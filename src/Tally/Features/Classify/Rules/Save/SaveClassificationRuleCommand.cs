using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Rules.Save;

/// <summary>
/// classify.rule.save vertical slice (FR-CLASSIFY-RULE-LIFECYCLE / TASK-CLASSIFY-RULEBOOK-RULE-DRAFT-SAVE).
/// Appends an immutable owner-authored draft version after closed-grammar and active-category validation.
/// Never activates a rule set, never grants broad apply, and never mutates Ledger categories.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SaveClassificationRuleCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public SaveClassificationRuleCommand(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        LedgerContractClient ledger,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.ledger = ledger;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyRuleSaveResult>> HandleAsync(
        ClassifyRuleSaveRequest input,
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
            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!SaveClassificationRuleValidator.TryValidate(input, out var boundaryError))
        {
            return CommandResult<ClassifyRuleSaveResult>.Failure(boundaryError ?? ClassifyErrors.InvalidInput);
        }

        if (!ClassifyContractMapper.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var vocabularyInputs = ClassifyContractMapper.ToVocabularyInputs(input.Conditions);
        if (!ClassificationRuleVocabulary.TryValidateRule(vocabularyInputs, out var canonical, out var vocabularyError))
        {
            return CommandResult<ClassifyRuleSaveResult>.Failure(
                vocabularyError?.Code ?? ClassifyErrors.InvalidInput);
        }

        var ruleId = input.RuleId.Trim();
        var categoryId = input.CategoryId.Trim();
        var priorVersionId = string.IsNullOrWhiteSpace(input.PriorVersionId)
            ? null
            : input.PriorVersionId.Trim();
        var normalizationVersion = NormalizationDescriptor.V1.Version;

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var createdBy = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);

        // Fingerprint + probe BEFORE live category revalidation so a completed draft still replays
        // after later category archival or LEDGER outage (same pattern as Budget draft).
        var fingerprintElement = ClassifyContractMapper.ToRuleSaveFingerprintElement(
            ruleId,
            priorVersionId,
            categoryId,
            normalizationVersion,
            canonical,
            reason);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.RuleSave,
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

        var categoryValidation = await ValidateActiveCategoryAsync(categoryId, actor, cancellationToken);
        if (categoryValidation.ErrorCode is not null)
        {
            return CommandResult<ClassifyRuleSaveResult>.Failure(categoryValidation.ErrorCode);
        }

        var createdAt = timeProvider.GetUtcNow();
        var createdAtUtc = ClassifyContractMapper.FormatUtc(createdAt);
        var scopeHash = ClassifyContractMapper.ComputeScopeHash(canonical);
        var ruleVersionId = ClassifyContractMapper.NewRuleVersionId(createdAt);

        try
        {
            return await stateStore.ExecuteWriteAsync(
                async (connection, transaction, ct) =>
                {
                    // Re-check idempotency under BEGIN IMMEDIATE for concurrent first writers.
                    var existing = await idempotencyStore.FindAsync(connection, transaction, idempotencyKey, ct);
                    var lookup = idempotencyStore.Resolve(
                        existing,
                        ClassifyOperationIds.RuleSave,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint);
                    switch (lookup.Disposition)
                    {
                        case ClassifyIdempotencyDisposition.Replay:
                            return ReplayOrIntegrity(lookup.Record!);
                        case ClassifyIdempotencyDisposition.Conflict:
                            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.IdempotencyConflict);
                        case ClassifyIdempotencyDisposition.Miss:
                            break;
                        default:
                            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.Unexpected);
                    }

                    var activeBefore = await ruleStore.GetActiveRuleSetAsync(connection, transaction, ct);

                    if (priorVersionId is not null)
                    {
                        var prior = await ruleStore.GetRuleVersionAsync(connection, transaction, priorVersionId, ct);
                        if (prior is null)
                        {
                            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.RuleVersionNotFound);
                        }

                        if (!string.Equals(prior.RuleId, ruleId, StringComparison.Ordinal))
                        {
                            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.InvalidInput);
                        }
                    }

                    var existingRule = await ruleStore.GetRuleAsync(connection, transaction, ruleId, ct);
                    if (existingRule is null)
                    {
                        if (priorVersionId is not null)
                        {
                            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.RuleNotFound);
                        }

                        await ruleStore.InsertRuleAsync(
                            connection,
                            transaction,
                            new ClassifyRuleRow(ruleId, createdAtUtc, createdBy),
                            ct);
                    }

                    var versionRow = new ClassifyRuleVersionRow(
                        ruleVersionId,
                        ruleId,
                        priorVersionId,
                        normalizationVersion,
                        categoryId,
                        scopeHash,
                        ClassificationRuleStore.OriginOwnerAuthored,
                        SourceFeedbackId: null,
                        reason,
                        ClassificationRuleStore.LifecycleDraft,
                        BroadApplyAllowed: 0,
                        ValidationRunId: null,
                        createdAtUtc,
                        createdBy);

                    await ruleStore.InsertDraftVersionAsync(connection, transaction, versionRow, canonical, ct);

                    var activeAfter = await ruleStore.GetActiveRuleSetAsync(connection, transaction, ct);
                    if (!ActivePointerEquals(activeBefore, activeAfter))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: rule.save must not change active_rule_set.");
                    }

                    // Defensive: never leave a draft with broad apply or non-draft lifecycle.
                    var stored = await ruleStore.GetRuleVersionAsync(connection, transaction, ruleVersionId, ct)
                        ?? throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: Draft version disappeared after insert.");
                    if (!string.Equals(stored.LifecycleState, ClassificationRuleStore.LifecycleDraft, StringComparison.Ordinal)
                        || stored.BroadApplyAllowed != 0
                        || !string.Equals(stored.RuleOrigin, ClassificationRuleStore.OriginOwnerAuthored, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: Draft invariants violated after insert.");
                    }

                    var result = new ClassifyRuleSaveResult(
                        ClassifyOperationIds.ContractVersion,
                        ruleId,
                        ruleVersionId,
                        categoryId,
                        normalizationVersion);

                    await idempotencyStore.CommitAsync(
                        connection,
                        transaction,
                        new ClassifyOperationIdempotencyRow(
                            idempotencyKey,
                            ClassifyOperationIds.RuleSave,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint,
                            ClassifyContractMapper.SerializeRuleSaveResult(result),
                            createdAtUtc),
                        ct);

                    return CommandResult<ClassifyRuleSaveResult>.Success(result);
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal))
        {
            return CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyRuleSaveResult>?> TryProbeAsync(
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
                ClassifyOperationIds.RuleSave,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyRuleSaveResult> ReplayOrIntegrity(ClassifyOperationIdempotencyRow record)
    {
        var result = ClassifyContractMapper.TryDeserializeRuleSaveResult(record.TerminalResult);
        return result is null
            ? CommandResult<ClassifyRuleSaveResult>.Failure(ClassifyErrors.Integrity)
            : CommandResult<ClassifyRuleSaveResult>.Success(result);
    }

    private async Task<CategoryValidationResult> ValidateActiveCategoryAsync(
        string categoryId,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        // Full catalogue (status=null) so missing vs archived can be distinguished without private Ledger storage.
        var listed = await ledger.ListClassificationCategoriesAsync(
            CategoryContractVersions.Current,
            actor,
            cancellationToken,
            status: null);

        if (!listed.IsSuccess || listed.Value is null)
        {
            return CategoryValidationResult.Fail(ClassifyContractMapper.MapLedgerCategoryListError(listed.Error));
        }

        if (!string.Equals(
                listed.Value.LedgerContractVersion,
                CategoryContractVersions.Current,
                StringComparison.Ordinal))
        {
            return CategoryValidationResult.Fail(ClassifyErrors.LedgerIncompatible);
        }

        var match = listed.Value.Items.FirstOrDefault(
            item => string.Equals(item.CategoryId, categoryId, StringComparison.Ordinal));

        if (match is null)
        {
            // FR-CLASSIFY-RULE-LIFECYCLE: stable category-not-found.
            return CategoryValidationResult.Fail(ClassifyErrors.NotFound);
        }

        if (match.Status != CategoryStatus.Active)
        {
            // FR-CLASSIFY-RULE-LIFECYCLE: stable category-inactive (archived).
            return CategoryValidationResult.Fail(ClassifyErrors.Lifecycle);
        }

        if (!string.Equals(
                match.LedgerContractVersion,
                CategoryContractVersions.Current,
                StringComparison.Ordinal))
        {
            return CategoryValidationResult.Fail(ClassifyErrors.LedgerIncompatible);
        }

        return CategoryValidationResult.Ok();
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

    private sealed record CategoryValidationResult(string? ErrorCode)
    {
        public static CategoryValidationResult Ok() => new CategoryValidationResult(ErrorCode: null);
        public static CategoryValidationResult Fail(string errorCode) => new CategoryValidationResult(ErrorCode: errorCode);
    }
}
