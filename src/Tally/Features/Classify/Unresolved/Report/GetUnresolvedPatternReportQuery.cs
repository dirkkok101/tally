using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Unresolved;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Unresolved.Report;

/// <summary>
/// classify.unresolved.report vertical slice
/// (FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT / DD-CLASSIFY-UNRESOLVED-REPORT-BOUNDARY / bd-3ciw).
/// Loads retained no_suggestion identities, joins each exactly once to a fresh evaluation-purpose
/// classification_v1 projection, groups via <see cref="UnresolvedPatternGroupingPolicy"/>, and
/// returns a complete ephemeral report or a typed null-result error. Never writes CLASSIFY or Ledger
/// state; never logs descriptions, amounts, accounts, transaction IDs, tokens, or private paths.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetUnresolvedPatternReportQuery
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly ClassificationUnresolvedStore unresolvedStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public GetUnresolvedPatternReportQuery(
        ClassifyStateStore stateStore,
        ClassificationEvaluationStore evaluationStore,
        ClassificationUnresolvedStore unresolvedStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(unresolvedStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.evaluationStore = evaluationStore;
        this.unresolvedStore = unresolvedStore;
        this.ruleSetStore = ruleSetStore;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyUnresolvedReportResult>> HandleAsync(
        ClassifyUnresolvedReportRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (!ClassifyOperatorErgonomicsContracts.TryValidate(input, out var validationError)
            || validationError is not null)
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(
                validationError ?? ClassifyErrors.InvalidInput);
        }

        var evaluationId = input.EvaluationId.Trim();
        var topN = input.TopN;
        var minimumCount = input.MinimumCount;
        var accountFilter = string.IsNullOrWhiteSpace(input.AccountId) ? null : input.AccountId.Trim();
        var directionFilter = input.AmountDirection;

        ClassifyEvaluationRunRow? run;
        IReadOnlyList<ClassificationUnresolvedStore.NoSuggestionIdentity> identities;
        string? activeRuleSetVersionId;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            run = await evaluationStore.GetRunAsync(connection, null, evaluationId, cancellationToken);
            if (run is null)
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.EvaluationNotFound);
            }

            if (string.Equals(
                    run.LifecycleState,
                    ClassifyContractMapper.EvaluationLifecycleAbandoned,
                    StringComparison.Ordinal))
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Lifecycle);
            }

            if (!string.Equals(
                    run.LifecycleState,
                    ClassifyContractMapper.EvaluationLifecycleCompleted,
                    StringComparison.Ordinal))
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Lifecycle);
            }

            // Retention / accounting integrity on the retained evaluation envelope.
            if (run.SuggestionCount + run.NoSuggestionCount + run.ConflictCount + run.StaleCount
                != run.InputCount
                || run.InputCount < 0
                || run.NoSuggestionCount < 0)
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
            }

            identities = await unresolvedStore.ListNoSuggestionIdentitiesAsync(
                connection, null, evaluationId, cancellationToken);
            var counted = await unresolvedStore.CountNoSuggestionIdentitiesAsync(
                connection, null, evaluationId, cancellationToken);
            if (counted != identities.Count || counted != run.NoSuggestionCount)
            {
                // Retention gap or desynchronized counters — fail closed, zero writes.
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
            }

            var active = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            if (active is null)
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(
                    ClassifyErrors.ActiveRuleSetNotFound);
            }

            activeRuleSetVersionId = active.RuleSetVersionId;
        }

        if (!TryParseExpiresAt(run.SnapshotExpiresAt, out var retainedExpiresAt))
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
        }

        var now = timeProvider.GetUtcNow();
        if (now >= retainedExpiresAt)
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Stale);
        }

        // Fresh complete evaluation-purpose classification_v1 projection via public client only.
        cancellationToken.ThrowIfCancellationRequested();
        var projection = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            actor,
            cancellationToken);
        if (!projection.IsSuccess || projection.Value is null)
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(
                MapLedgerError(projection.Error?.Code));
        }

        var page = projection.Value;
        if (string.IsNullOrWhiteSpace(page.ProjectionVersion)
            || string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint)
            || string.IsNullOrWhiteSpace(page.LedgerContractVersion)
            || string.IsNullOrWhiteSpace(page.SnapshotId))
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.LedgerIncompatible);
        }

        if (!string.Equals(
                page.ProjectionVersion,
                ClassificationProjectionVersions.ClassificationV1,
                StringComparison.Ordinal)
            || !string.Equals(page.LedgerContractVersion, run.LedgerContractVersion, StringComparison.Ordinal)
            || !string.Equals(page.ProjectionVersion, run.ProjectionVersion, StringComparison.Ordinal))
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.LedgerIncompatible);
        }

        if (!string.Equals(
                page.StoreGenerationFingerprint,
                run.StoreGenerationFingerprint,
                StringComparison.Ordinal))
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Stale);
        }

        var items = page.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>();
        var byTx = new Dictionary<string, ClassificationProjectionItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.TransactionId))
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
            }

            if (!byTx.TryAdd(item.TransactionId, item))
            {
                // Duplicate projection row — fail closed.
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
            }
        }

        var categoryLifecycleFingerprint =
            !string.IsNullOrWhiteSpace(page.CategoryIdentityLifecycleFingerprint)
                ? page.CategoryIdentityLifecycleFingerprint!
                : EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
                    (page.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
                        .Select(c => (c.CategoryId, c.LifecycleState)));

        // Optional: detect archived catalogue evidence when join needs active membership (none for no_sug).
        // Archived lifecycle on retained outcome binding uses item lifecycle drift below.

        var joined = new List<UnresolvedPatternGroupingPolicy.JoinedRow>(identities.Count);
        var seenTx = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenTx.Add(identity.TransactionId))
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
            }

            if (!byTx.TryGetValue(identity.TransactionId, out var publicItem))
            {
                // Unmatched fresh row for a retained identity.
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Stale);
            }

            var currentLifecycle = ClassifyContractMapper.ComputeItemLifecycleFingerprint(publicItem);
            if (!string.Equals(
                    currentLifecycle,
                    identity.ItemLifecycleFingerprint,
                    StringComparison.Ordinal))
            {
                // Lifecycle drift / archived evidence / revision change.
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Stale);
            }

            if (!ClassifyContractMapper.TryMapSignedAmountMinor(publicItem, out var signedMinor, out var amountError))
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(
                    amountError ?? ClassifyErrors.LedgerIncompatible);
            }

            if (!NormalizerV1.TryNormalize(publicItem.SourceDescription, out var normalized, out _))
            {
                return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.ResourceLimit);
            }

            var direction = ClassifyContractMapper.FormatUnresolvedAmountDirection(publicItem.AmountDirection);

            // Optional request filters (public scope only).
            if (accountFilter is not null
                && !string.Equals(publicItem.AccountId, accountFilter, StringComparison.Ordinal))
            {
                continue;
            }

            if (directionFilter is not null
                && publicItem.AmountDirection != directionFilter.Value)
            {
                continue;
            }

            joined.Add(new UnresolvedPatternGroupingPolicy.JoinedRow(
                run.NormalizationVersion,
                normalized,
                publicItem.AccountId,
                direction,
                signedMinor));
        }

        // Accounting: every retained no_suggestion identity must join exactly once when unfiltered.
        // With filters, joined count may be lower; still fail if we skipped due to missing join above.
        if (accountFilter is null && directionFilter is null && joined.Count != identities.Count)
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
        }

        if (!UnresolvedPatternGroupingPolicy.TryGroup(
                joined,
                topN,
                minimumCount,
                out var grouped,
                out var groupError)
            || grouped is null)
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(
                groupError ?? ClassifyErrors.Integrity);
        }

        // When unfiltered, policy noSuggestionCount must equal retained identity count.
        if (accountFilter is null
            && directionFilter is null
            && grouped.NoSuggestionOutcomeCount != identities.Count)
        {
            return CommandResult<ClassifyUnresolvedReportResult>.Failure(ClassifyErrors.Integrity);
        }

        var evaluationFingerprint = ClassifyContractMapper
            .ToRetainedEvaluationFingerprint(run)
            .CanonicalHash;
        var orderedItemsFingerprint = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
            items
                .OrderBy(i => i.Ordinal)
                .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
                .Select(i => (
                    i.Ordinal,
                    i.TransactionId,
                    ClassifyContractMapper.ComputeItemLifecycleFingerprint(i))));
        var projectionFingerprint = ClassifyContractMapper.ComputeUnresolvedProjectionFingerprint(
            page.LedgerContractVersion!,
            page.ProjectionVersion!,
            page.StoreGenerationFingerprint!,
            page.SnapshotId,
            categoryLifecycleFingerprint,
            orderedItemsFingerprint);
        var ruleSetFingerprint = ClassifyContractMapper.RuleSetFingerprint(activeRuleSetVersionId!);

        var result = ClassifyContractMapper.ToUnresolvedReportResult(
            evaluationId,
            evaluationFingerprint,
            projectionFingerprint,
            categoryLifecycleFingerprint,
            ruleSetFingerprint,
            grouped);

        // Privacy: result must never re-introduce transaction IDs from identities (mapper omits them).
        return CommandResult<ClassifyUnresolvedReportResult>.Success(result);
    }

    private static string MapLedgerError(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? ClassifyErrors.LedgerUnavailable
            : code switch
            {
                _ when code.Contains("incompatible", StringComparison.OrdinalIgnoreCase) =>
                    ClassifyErrors.LedgerIncompatible,
                _ when code.Contains("unavailable", StringComparison.OrdinalIgnoreCase) =>
                    ClassifyErrors.LedgerUnavailable,
                _ => ClassifyErrors.LedgerUnavailable
            };

    private static bool TryParseExpiresAt(string raw, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out expiresAt);
    }
}
