using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Rules.Activate;

/// <summary>
/// classify.rule.activate vertical slice (FR-CLASSIFY-RULE-LIFECYCLE / TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE).
/// Requires exact current completed validation evidence, zero incorrect/unexplained/drift canaries,
/// active category identity, and explicit owner actor/reason. Atomically creates an immutable
/// rule-set version, members, lifecycle events, and active pointer. Never mutates rule versions
/// in place, never auto-activates, and never mutates Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ActivateClassificationRuleCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly ClassificationValidationStore validationStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public ActivateClassificationRuleCommand(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        ClassificationValidationStore validationStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(validationStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.validationStore = validationStore;
        this.ruleSetStore = ruleSetStore;
        this.ledger = ledger;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyRuleActivateResult>> HandleAsync(
        ClassifyRuleActivateRequest input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (!RuleLifecyclePolicy.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.InvalidInput);
        }

        if (string.IsNullOrWhiteSpace(input.ValidationId))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var validationId = input.ValidationId.Trim();
        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var requestedBroadApply = input.BroadApplyAllowed;

        var fingerprintElement = BuildFingerprintElement(validationId, requestedBroadApply, reason);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.RuleActivate,
            ClassifyOperationIds.ContractVersion,
            actorKind,
            actorLabel,
            actorRunId,
            fingerprintElement);

        var probed = await TryProbeAsync(idempotencyKey, requestFingerprint, cancellationToken);
        if (probed is not null)
        {
            return probed;
        }

        // Load validation evidence and active pointer before live category revalidation so
        // completed activations can still replay after later catalogue drift.
        ClassificationValidationRunRow? run;
        ClassificationValidationReportRow? report;
        ClassifyActiveRuleSetPointer? activeBefore;
        IReadOnlyList<ClassifyRuleVersionRow> allVersions;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            run = await validationStore.GetRunAsync(connection, null, validationId, cancellationToken);
            report = await validationStore.GetReportAsync(connection, null, validationId, cancellationToken);
            activeBefore = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            allVersions = await ruleSetStore.ListAllRuleVersionsAsync(connection, null, cancellationToken);
        }

        var evidenceError = RuleLifecyclePolicy.ValidateActivationEvidence(run, report);
        if (evidenceError is not null)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(evidenceError);
        }

        var resolveError = RuleLifecyclePolicy.TryResolveCandidatesByFingerprint(
            allVersions,
            run!.CandidateFingerprint,
            out var candidates);
        if (resolveError is not null)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(resolveError);
        }

        if (candidates.Count == 0)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.RuleVersionNotFound);
        }

        // Live category revalidation (public Ledger client only) — rename preserves identity.
        var listed = await ledger.ListClassificationCategoriesAsync(
            CategoryContractVersions.Current,
            actor,
            cancellationToken,
            status: null);
        if (!listed.IsSuccess || listed.Value is null)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(
                ClassifyContractMapper.MapLedgerCategoryListError(listed.Error));
        }

        if (!string.Equals(
                listed.Value.LedgerContractVersion,
                CategoryContractVersions.Current,
                StringComparison.Ordinal))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.LedgerIncompatible);
        }

        var activeCategories = listed.Value.Items
            .Where(i => i.Status == CategoryStatus.Active)
            .ToArray();
        var activeCategoryIds = activeCategories
            .Select(i => i.CategoryId)
            .ToHashSet(StringComparer.Ordinal);

        var categoryError = RuleLifecyclePolicy.ValidateActiveCategoryIdentity(
            candidates.Select(c => c.CategoryId).ToArray(),
            activeCategoryIds);
        if (categoryError is not null)
        {
            // Distinguish missing identity (not in full catalogue) for stable not-found.
            var fullIds = listed.Value.Items
                .Select(i => i.CategoryId)
                .ToHashSet(StringComparer.Ordinal);
            if (candidates.Any(c => !fullIds.Contains(c.CategoryId)))
            {
                return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.NotFound);
            }

            return CommandResult<ClassifyRuleActivateResult>.Failure(categoryError);
        }

        var currentCategoryFingerprint = EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
            activeCategories.Select(i => (i.CategoryId, "active")));
        var currencyError = RuleLifecyclePolicy.ValidateCategoryFingerprintCurrency(
            run.CategoryLifecycleFingerprint,
            currentCategoryFingerprint);
        if (currencyError is not null)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(currencyError);
        }

        var broadApplyAllowed = RuleLifecyclePolicy.AuthorizeBroadApply(
            requestedBroadApply,
            report!,
            evidenceError: null);
        var broadError = RuleLifecyclePolicy.ValidateBroadApplyRequest(requestedBroadApply, broadApplyAllowed);
        if (broadError is not null)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(broadError);
        }

        // Normalization must remain the validated one for every candidate.
        if (candidates.Any(c => !string.Equals(
                c.NormalizationVersion,
                run.NormalizationVersion,
                StringComparison.Ordinal))
            || !string.Equals(
                run.NormalizationVersion,
                NormalizationDescriptor.V1.Version,
                StringComparison.Ordinal))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        var activatedAt = timeProvider.GetUtcNow();
        var activatedAtUtc = ClassifyContractMapper.FormatUtc(activatedAt);
        var ruleSetVersionId = ClassifyContractMapper.NewRuleVersionId(activatedAt);
        var activateEventId = ClassifyContractMapper.NewRuleVersionId(activatedAt.AddTicks(1));
        var supersedeEventId = activeBefore is null
            ? null
            : ClassifyContractMapper.NewRuleVersionId(activatedAt.AddTicks(2));

        try
        {
            return await stateStore.ExecuteWriteAsync(
                async (connection, transaction, ct) =>
                {
                    var existing = await idempotencyStore.FindAsync(connection, transaction, idempotencyKey, ct);
                    var lookup = idempotencyStore.Resolve(
                        existing,
                        ClassifyOperationIds.RuleActivate,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint);
                    switch (lookup.Disposition)
                    {
                        case ClassifyIdempotencyDisposition.Replay:
                            return ReplayOrIntegrity(lookup.Record!);
                        case ClassifyIdempotencyDisposition.Conflict:
                            return CommandResult<ClassifyRuleActivateResult>.Failure(
                                ClassifyErrors.IdempotencyConflict);
                        case ClassifyIdempotencyDisposition.Miss:
                            break;
                        default:
                            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Unexpected);
                    }

                    // Re-load evidence under write lock — fail closed if concurrent drift.
                    var liveRun = await validationStore.GetRunAsync(connection, transaction, validationId, ct);
                    var liveReport = await validationStore.GetReportAsync(connection, transaction, validationId, ct);
                    var liveEvidenceError = RuleLifecyclePolicy.ValidateActivationEvidence(liveRun, liveReport);
                    if (liveEvidenceError is not null)
                    {
                        return CommandResult<ClassifyRuleActivateResult>.Failure(liveEvidenceError);
                    }

                    var liveActive = await ruleSetStore.GetActiveRuleSetAsync(connection, transaction, ct);
                    if (!ActivePointerEquals(activeBefore, liveActive))
                    {
                        return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Conflict);
                    }

                    var liveVersions = await ruleSetStore.ListAllRuleVersionsAsync(connection, transaction, ct);
                    var liveResolveError = RuleLifecyclePolicy.TryResolveCandidatesByFingerprint(
                        liveVersions,
                        liveRun!.CandidateFingerprint,
                        out var liveCandidates);
                    if (liveResolveError is not null)
                    {
                        return CommandResult<ClassifyRuleActivateResult>.Failure(liveResolveError);
                    }

                    // Candidate identity must still match the pre-write resolution.
                    if (!CandidateSetEquals(candidates, liveCandidates))
                    {
                        return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Stale);
                    }

                    var memberIds = liveCandidates
                        .Select(c => c.RuleVersionId)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();

                    var versionRow = new ClassifyRuleSetVersionRow(
                        ruleSetVersionId,
                        liveActive?.RuleSetVersionId,
                        liveRun.NormalizationVersion,
                        validationId,
                        reason,
                        activatedAtUtc,
                        actorText);

                    var events = new List<ClassifyRuleLifecycleEventRow>(2);
                    if (liveActive is not null && supersedeEventId is not null)
                    {
                        events.Add(new ClassifyRuleLifecycleEventRow(
                            supersedeEventId,
                            liveActive.RuleSetVersionId,
                            RuleLifecyclePolicy.StateActive,
                            RuleLifecyclePolicy.StateSuperseded,
                            ruleSetVersionId,
                            reason,
                            actorText,
                            activatedAtUtc));
                    }

                    events.Add(new ClassifyRuleLifecycleEventRow(
                        activateEventId,
                        ruleSetVersionId,
                        liveActive?.RuleSetVersionId,
                        RuleLifecyclePolicy.ActivationResultingState(broadApplyAllowed),
                        ReplacementId: null,
                        reason,
                        actorText,
                        activatedAtUtc));

                    // Also attribute each activated rule-version membership without mutating the row.
                    foreach (var memberId in memberIds)
                    {
                        events.Add(new ClassifyRuleLifecycleEventRow(
                            ClassifyContractMapper.NewRuleVersionId(timeProvider.GetUtcNow()),
                            memberId,
                            RuleLifecyclePolicy.StateDraft,
                            RuleLifecyclePolicy.ActivationResultingState(broadApplyAllowed),
                            ruleSetVersionId,
                            reason,
                            actorText,
                            activatedAtUtc));
                    }

                    await ruleSetStore.ActivateRuleSetAsync(
                        connection,
                        transaction,
                        versionRow,
                        memberIds,
                        events,
                        ct);

                    // Defensive: rule_version rows must remain immutable drafts (no in-place mutation).
                    foreach (var memberId in memberIds)
                    {
                        var stored = await ruleStore.GetRuleVersionAsync(connection, transaction, memberId, ct)
                            ?? throw new InvalidOperationException(
                                $"{ClassifyErrors.Integrity}: activated member disappeared.");
                        if (!string.Equals(stored.LifecycleState, ClassificationRuleStore.LifecycleDraft, StringComparison.Ordinal)
                            && !string.Equals(stored.LifecycleState, RuleLifecyclePolicy.StateDraft, StringComparison.Ordinal))
                        {
                            // Draft rows stay draft; active authority is membership + events only.
                        }
                    }

                    var activeAfter = await ruleSetStore.GetActiveRuleSetAsync(connection, transaction, ct);
                    if (activeAfter is null
                        || !string.Equals(activeAfter.RuleSetVersionId, ruleSetVersionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: active_rule_set pointer was not installed.");
                    }

                    var result = new ClassifyRuleActivateResult(
                        ClassifyOperationIds.ContractVersion,
                        ruleSetVersionId,
                        validationId,
                        broadApplyAllowed);

                    await idempotencyStore.CommitAsync(
                        connection,
                        transaction,
                        new ClassifyOperationIdempotencyRow(
                            idempotencyKey,
                            ClassifyOperationIds.RuleActivate,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint,
                            SerializeResult(result),
                            activatedAtUtc),
                        ct);

                    return CommandResult<ClassifyRuleActivateResult>.Success(result);
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal)
            || ex.Message.Contains("active_rule_set", StringComparison.Ordinal)
            || ex.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyRuleActivateResult>?> TryProbeAsync(
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);
        await using var transaction = stateStore.BeginImmediate(connection);
        try
        {
            var existing = await idempotencyStore.FindAsync(connection, transaction, idempotencyKey, cancellationToken);
            var lookup = idempotencyStore.Resolve(
                existing,
                ClassifyOperationIds.RuleActivate,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyRuleActivateResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyRuleActivateResult);
            return result is null
                ? CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyRuleActivateResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyRuleActivateResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyRuleActivateResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyRuleActivateResult);

    private static JsonElement BuildFingerprintElement(
        string validationId,
        bool broadApplyAllowed,
        string reason)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("broadApplyAllowed", broadApplyAllowed);
            writer.WriteString("reason", reason);
            writer.WriteString("validationId", validationId);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static bool ActivePointerEquals(
        ClassifyActiveRuleSetPointer? before,
        ClassifyActiveRuleSetPointer? after)
    {
        if (before is null && after is null)
        {
            return true;
        }

        if (before is null || after is null)
        {
            return false;
        }

        return before.SingletonId == after.SingletonId
            && string.Equals(before.RuleSetVersionId, after.RuleSetVersionId, StringComparison.Ordinal)
            && before.ActivationEpoch == after.ActivationEpoch;
    }

    private static bool CandidateSetEquals(
        IReadOnlyList<ClassifyRuleVersionRow> left,
        IReadOnlyList<ClassifyRuleVersionRow> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftIds = left.Select(v => v.RuleVersionId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var rightIds = right.Select(v => v.RuleVersionId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < leftIds.Length; i++)
        {
            if (!string.Equals(leftIds[i], rightIds[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
