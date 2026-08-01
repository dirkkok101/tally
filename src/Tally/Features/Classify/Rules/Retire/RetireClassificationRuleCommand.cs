using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Rules.Retire;

/// <summary>
/// classify.rule.retire vertical slice (FR-CLASSIFY-RULE-LIFECYCLE / TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE).
/// Retires an active rule version by creating an attributable successor rule set without that member.
/// All prior rule-set versions, members, and rule versions are retained. Never mutates rows in place
/// and never mutates Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RetireClassificationRuleCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public RetireClassificationRuleCommand(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.ruleSetStore = ruleSetStore;
        this.ledger = ledger;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyRuleRetireResult>> HandleAsync(
        ClassifyRuleRetireRequest input,
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
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (!RuleLifecyclePolicy.TryNormalizeReason(input.Reason, out var reason))
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.InvalidInput);
        }

        if (string.IsNullOrWhiteSpace(input.RuleVersionId))
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.InvalidInput);
        }

        var ruleVersionId = input.RuleVersionId.Trim();
        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);

        var fingerprintElement = BuildFingerprintElement(ruleVersionId, reason);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.RuleRetire,
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

        ClassifyRuleVersionRow? targetVersion;
        ClassifyActiveRuleSetPointer? activeBefore;
        IReadOnlyList<string> activeMembers;
        ClassifyRuleSetVersionRow? activeSetVersion;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            targetVersion = await ruleStore.GetRuleVersionAsync(connection, null, ruleVersionId, cancellationToken);
            if (targetVersion is null)
            {
                return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.RuleVersionNotFound);
            }

            activeBefore = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            if (activeBefore is null)
            {
                return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Lifecycle);
            }

            activeMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
                connection, null, activeBefore.RuleSetVersionId, cancellationToken);
            activeSetVersion = await ruleSetStore.GetRuleSetVersionAsync(
                connection, null, activeBefore.RuleSetVersionId, cancellationToken);
        }

        var membershipError = RuleLifecyclePolicy.ValidateRetirementMembership(
            ruleVersionId,
            activeMembers.ToHashSet(StringComparer.Ordinal));
        if (membershipError is not null)
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(membershipError);
        }

        // Remaining members must still reference active category identities (archive fail-closed).
        var remainingIds = RuleLifecyclePolicy.SuccessorMembersAfterRetirement(activeMembers, ruleVersionId);
        if (remainingIds.Count > 0)
        {
            var remainingCategoryIds = new List<string>(remainingIds.Count);
            await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
            {
                foreach (var memberId in remainingIds)
                {
                    var member = await ruleStore.GetRuleVersionAsync(connection, null, memberId, cancellationToken);
                    if (member is null)
                    {
                        return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.RuleVersionNotFound);
                    }

                    remainingCategoryIds.Add(member.CategoryId);
                }
            }

            var listed = await ledger.ListClassificationCategoriesAsync(
                CategoryContractVersions.Current,
                actor,
                cancellationToken,
                status: null);
            if (!listed.IsSuccess || listed.Value is null)
            {
                return CommandResult<ClassifyRuleRetireResult>.Failure(
                    ClassifyContractMapper.MapLedgerCategoryListError(listed.Error));
            }

            var activeCategoryIds = listed.Value.Items
                .Where(i => i.Status == CategoryStatus.Active)
                .Select(i => i.CategoryId)
                .ToHashSet(StringComparer.Ordinal);
            var categoryError = RuleLifecyclePolicy.ValidateActiveCategoryIdentity(
                remainingCategoryIds,
                activeCategoryIds);
            if (categoryError is not null)
            {
                return CommandResult<ClassifyRuleRetireResult>.Failure(categoryError);
            }
        }

        var retiredAt = timeProvider.GetUtcNow();
        var retiredAtUtc = ClassifyContractMapper.FormatUtc(retiredAt);
        var successorId = ClassifyContractMapper.NewRuleVersionId(retiredAt);
        var retireEventId = ClassifyContractMapper.NewRuleVersionId(retiredAt.AddTicks(1));
        var successorEventId = ClassifyContractMapper.NewRuleVersionId(retiredAt.AddTicks(2));
        var supersedeEventId = ClassifyContractMapper.NewRuleVersionId(retiredAt.AddTicks(3));

        // Retirement successors re-use the prior set's validation evidence reference so history
        // stays attributable without inventing new corpus authority.
        var validationRunId = activeSetVersion?.ValidationRunId
            ?? throw new InvalidOperationException(
                $"{ClassifyErrors.Integrity}: active rule set lacks validation attribution.");
        var normalizationVersion = activeSetVersion?.NormalizationVersion
            ?? NormalizationDescriptor.V1.Version;

        try
        {
            return await stateStore.ExecuteWriteAsync(
                async (connection, transaction, ct) =>
                {
                    var existing = await idempotencyStore.FindAsync(connection, transaction, idempotencyKey, ct);
                    var lookup = idempotencyStore.Resolve(
                        existing,
                        ClassifyOperationIds.RuleRetire,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint);
                    switch (lookup.Disposition)
                    {
                        case ClassifyIdempotencyDisposition.Replay:
                            return ReplayOrIntegrity(lookup.Record!);
                        case ClassifyIdempotencyDisposition.Conflict:
                            return CommandResult<ClassifyRuleRetireResult>.Failure(
                                ClassifyErrors.IdempotencyConflict);
                        case ClassifyIdempotencyDisposition.Miss:
                            break;
                        default:
                            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Unexpected);
                    }

                    var liveTarget = await ruleStore.GetRuleVersionAsync(connection, transaction, ruleVersionId, ct);
                    if (liveTarget is null)
                    {
                        return CommandResult<ClassifyRuleRetireResult>.Failure(
                            ClassifyErrors.RuleVersionNotFound);
                    }

                    var liveActive = await ruleSetStore.GetActiveRuleSetAsync(connection, transaction, ct);
                    if (liveActive is null
                        || !string.Equals(
                            liveActive.RuleSetVersionId,
                            activeBefore!.RuleSetVersionId,
                            StringComparison.Ordinal))
                    {
                        return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Conflict);
                    }

                    var liveMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
                        connection, transaction, liveActive.RuleSetVersionId, ct);
                    var liveMembershipError = RuleLifecyclePolicy.ValidateRetirementMembership(
                        ruleVersionId,
                        liveMembers.ToHashSet(StringComparer.Ordinal));
                    if (liveMembershipError is not null)
                    {
                        return CommandResult<ClassifyRuleRetireResult>.Failure(liveMembershipError);
                    }

                    var successorMembers = RuleLifecyclePolicy.SuccessorMembersAfterRetirement(
                        liveMembers,
                        ruleVersionId);

                    var successor = new ClassifyRuleSetVersionRow(
                        successorId,
                        liveActive.RuleSetVersionId,
                        normalizationVersion,
                        validationRunId,
                        reason,
                        retiredAtUtc,
                        actorText);

                    var events = new List<ClassifyRuleLifecycleEventRow>
                    {
                        new(
                            supersedeEventId,
                            liveActive.RuleSetVersionId,
                            RuleLifecyclePolicy.StateActive,
                            RuleLifecyclePolicy.StateSuperseded,
                            successorId,
                            reason,
                            actorText,
                            retiredAtUtc),
                        new(
                            retireEventId,
                            ruleVersionId,
                            RuleLifecyclePolicy.StateActive,
                            RuleLifecyclePolicy.StateRetired,
                            successorId,
                            reason,
                            actorText,
                            retiredAtUtc),
                        new(
                            successorEventId,
                            successorId,
                            liveActive.RuleSetVersionId,
                            RuleLifecyclePolicy.StateActive,
                            ReplacementId: null,
                            reason,
                            actorText,
                            retiredAtUtc)
                    };

                    await ruleSetStore.RetireIntoSuccessorAsync(
                        connection,
                        transaction,
                        successor,
                        successorMembers,
                        events,
                        ct);

                    // Prior rule version row must remain byte-stable (immutable history).
                    var retained = await ruleStore.GetRuleVersionAsync(connection, transaction, ruleVersionId, ct)
                        ?? throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: retired rule version disappeared.");
                    if (!string.Equals(retained.RuleVersionId, liveTarget.RuleVersionId, StringComparison.Ordinal)
                        || !string.Equals(retained.ScopeHash, liveTarget.ScopeHash, StringComparison.Ordinal)
                        || !string.Equals(retained.CategoryId, liveTarget.CategoryId, StringComparison.Ordinal)
                        || !string.Equals(retained.Reason, liveTarget.Reason, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: rule_version was mutated during retirement.");
                    }

                    // Prior rule set must still exist with original members.
                    var priorMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
                        connection, transaction, liveActive.RuleSetVersionId, ct);
                    if (!priorMembers.Contains(ruleVersionId, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: prior rule-set membership was lost.");
                    }

                    var activeAfter = await ruleSetStore.GetActiveRuleSetAsync(connection, transaction, ct);
                    if (activeAfter is null
                        || !string.Equals(activeAfter.RuleSetVersionId, successorId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: successor active pointer was not installed.");
                    }

                    var result = new ClassifyRuleRetireResult(
                        ClassifyOperationIds.ContractVersion,
                        ruleVersionId,
                        successorId);

                    await idempotencyStore.CommitAsync(
                        connection,
                        transaction,
                        new ClassifyOperationIdempotencyRow(
                            idempotencyKey,
                            ClassifyOperationIds.RuleRetire,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint,
                            SerializeResult(result),
                            retiredAtUtc),
                        ct);

                    return CommandResult<ClassifyRuleRetireResult>.Success(result);
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal)
            || ex.Message.Contains("active_rule_set", StringComparison.Ordinal)
            || ex.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyRuleRetireResult>?> TryProbeAsync(
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
                ClassifyOperationIds.RuleRetire,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyRuleRetireResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyRuleRetireResult);
            return result is null
                ? CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyRuleRetireResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyRuleRetireResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyRuleRetireResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyRuleRetireResult);

    private static JsonElement BuildFingerprintElement(string ruleVersionId, string reason)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("reason", reason);
            writer.WriteString("ruleVersionId", ruleVersionId);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
