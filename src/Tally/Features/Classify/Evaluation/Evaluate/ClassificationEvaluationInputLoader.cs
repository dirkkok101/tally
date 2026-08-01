using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Features.Classify.Contract;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Evaluation.Evaluate;

/// <summary>
/// Acquires one bounded, compatible, completely paged Ledger evaluation snapshot
/// (FR-CLASSIFY-ELIGIBLE-PROJECTION / TASK-CLASSIFY-RULEBOOK-EVALUATION-INPUT / bd-25a7).
/// Uses only the verified public <see cref="LedgerContractClient.QueryClassificationProjectionAsync"/>
/// with purpose=evaluation. Never opens Ledger storage, never writes CLASSIFY state, never mutates Ledger,
/// never streams partial pages into evaluation, and discards buffers on any failure.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationEvaluationInputLoader
{
    /// <summary>NFR-CLASSIFY-BOUNDED-EVALUATION / C11 published evaluation transaction bound.</summary>
    public const long MaxTransactionCount = ClassifyOperationModule.V1Limits.MaxTransactionCount;

    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public ClassificationEvaluationInputLoader(
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Load a complete evaluation-purpose classification_v1 snapshot or a stable pre-state failure.
    /// Propagates <paramref name="cancellationToken"/> to every page request performed by the client.
    /// On any failure the method returns no input and retains no page payload.
    /// </summary>
    public async Task<CommandResult<ClassificationEvaluationInput>> LoadAsync(
        SafeActor? actor,
        CancellationToken cancellationToken,
        string contractVersion = ActualsContractVersions.Current,
        int? pageSize = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassificationEvaluationInput>.Failure(ClassifyErrors.Unexpected);
        }

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassificationEvaluationInput>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(contractVersion))
        {
            return CommandResult<ClassificationEvaluationInput>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        // Only evaluation purpose is in scope for this loader (apply_preflight is a different surface).
        LedgerContractResult<ActualsQueryResult> projection;
        try
        {
            projection = await ledger.QueryClassificationProjectionAsync(
                ClassificationProjectionPurpose.Evaluation,
                contractVersion.Trim(),
                actor,
                cancellationToken,
                transactionIds: null,
                pageSize: pageSize,
                itemProjection: ClassificationProjectionVersions.ClassificationV1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Discard any partial acquisition — client holds no CLASSIFY state.
            return CommandResult<ClassificationEvaluationInput>.Failure(ClassifyErrors.Unexpected);
        }

        if (!projection.IsSuccess || projection.Value is null)
        {
            return CommandResult<ClassificationEvaluationInput>.Failure(
                MapProjectionFailure(projection.Error, projection.ExitCode));
        }

        var validationError = ValidateAcquiredProjection(
            projection.Value,
            timeProvider.GetUtcNow(),
            MaxTransactionCount);
        if (validationError is not null)
        {
            // Explicit discard: do not return or cache the page payload.
            return CommandResult<ClassificationEvaluationInput>.Failure(validationError);
        }

        var input = BuildInput(projection.Value);
        // projection local falls out of scope; only the immutable input remains for the command boundary.
        return CommandResult<ClassificationEvaluationInput>.Success(input);
    }

    /// <summary>
    /// Pure descriptor and membership validation for a completely acquired evaluation projection.
    /// Returns a stable CLASSIFY error code, or null when the snapshot may cross the evaluation boundary.
    /// </summary>
    public static string? ValidateAcquiredProjection(
        ActualsQueryResult page,
        DateTimeOffset nowUtc,
        long maxTransactionCount = MaxTransactionCount)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(page.LedgerContractVersion)
            || !string.Equals(page.LedgerContractVersion, ActualsContractVersions.Current, StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        if (!string.Equals(page.ProjectionVersion, ClassificationProjectionVersions.ClassificationV1, StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        if (string.IsNullOrWhiteSpace(page.SnapshotId)
            || string.IsNullOrWhiteSpace(page.ExpiresAt)
            || string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint)
            || page.StoreGenerationFingerprint.Length != 64
            || string.IsNullOrWhiteSpace(page.CategoryIdentityLifecycleFingerprint)
            || page.CategoryIdentityLifecycleFingerprint.Length != 64)
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        if (page.Cursor is not null)
        {
            // Incomplete acquisition must never cross the evaluation boundary.
            return ClassifyErrors.Stale;
        }

        if (!TryParseExpiresAt(page.ExpiresAt, out var expiresAt))
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        if (nowUtc >= expiresAt)
        {
            return ClassifyErrors.Stale;
        }

        if (page.TotalCount < 0)
        {
            return ClassifyErrors.Integrity;
        }

        // Exact limit is accepted; one-over-limit fails before any evaluation input is published.
        if (page.TotalCount > maxTransactionCount)
        {
            return ClassifyErrors.ResourceLimit;
        }

        var items = page.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>();
        if (items.Count != page.TotalCount)
        {
            return ClassifyErrors.Integrity;
        }

        if (page.ActiveCategories is null)
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        // Ordinal accounting: 0..N-1 exactly once, frozen order.
        var seenOrdinals = new HashSet<int>();
        var seenTx = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Ordinal != i)
            {
                // Missing, duplicate, or out-of-order ordinal relative to buffer position.
                return ClassifyErrors.Integrity;
            }

            if (!seenOrdinals.Add(item.Ordinal))
            {
                return ClassifyErrors.Integrity;
            }

            if (string.IsNullOrWhiteSpace(item.TransactionId)
                || string.IsNullOrWhiteSpace(item.AccountId)
                || string.IsNullOrWhiteSpace(item.TransactionRevision)
                || string.IsNullOrWhiteSpace(item.RelationshipRevision)
                || string.IsNullOrWhiteSpace(item.AllocationRevision))
            {
                return ClassifyErrors.Integrity;
            }

            if (!seenTx.Add(item.TransactionId))
            {
                return ClassifyErrors.Integrity;
            }
        }

        if (seenOrdinals.Count != page.TotalCount)
        {
            return ClassifyErrors.Integrity;
        }

        return null;
    }

    /// <summary>
    /// Build the immutable evaluation input from a validated complete projection.
    /// Copies public projection fields only; does not open CLASSIFY storage.
    /// </summary>
    public static ClassificationEvaluationInput BuildInput(ActualsQueryResult page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var items = Array.AsReadOnly((page.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>())
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .Select(CloneItem)
            .ToArray());
        var categories = Array.AsReadOnly((page.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
            .OrderBy(c => c.CategoryId, StringComparer.Ordinal)
            .Select(c => new ClassificationCategoryIdentity(c.CategoryId, c.DisplayName, c.LifecycleState))
            .ToArray());

        var orderedItemsFingerprint = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
            items.Select(i => (
                i.Ordinal,
                i.TransactionId,
                ComputeItemLifecycleFingerprint(i))));

        var snapshotFingerprint = ComputeSnapshotFingerprint(
            page.LedgerContractVersion,
            page.ProjectionVersion!,
            page.StoreGenerationFingerprint!,
            page.SnapshotId,
            page.ExpiresAt,
            page.CategoryIdentityLifecycleFingerprint!,
            orderedItemsFingerprint,
            items);

        return new ClassificationEvaluationInput(
            LedgerContractVersion: page.LedgerContractVersion,
            ProjectionVersion: page.ProjectionVersion!,
            SnapshotId: page.SnapshotId,
            SnapshotExpiresAt: page.ExpiresAt,
            StoreGenerationFingerprint: page.StoreGenerationFingerprint!,
            CategoryLifecycleFingerprint: page.CategoryIdentityLifecycleFingerprint!,
            OrderedItemsFingerprint: orderedItemsFingerprint,
            SnapshotFingerprint: snapshotFingerprint,
            TotalCount: page.TotalCount,
            Items: items,
            ActiveCategories: categories);
    }

    /// <summary>
    /// Canonical byte sequence over ordered membership and snapshot identity for equivalence proofs.
    /// Excludes no private paths; includes only public projection fields needed for stability.
    /// </summary>
    public static byte[] ToCanonicalBytes(ClassificationEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder(256 + (input.Items.Count * 96));
        sb.Append("ledgerContractVersion=").Append(input.LedgerContractVersion).Append('\n');
        sb.Append("projectionVersion=").Append(input.ProjectionVersion).Append('\n');
        sb.Append("snapshotId=").Append(input.SnapshotId).Append('\n');
        sb.Append("snapshotExpiresAt=").Append(input.SnapshotExpiresAt).Append('\n');
        sb.Append("storeGenerationFingerprint=").Append(input.StoreGenerationFingerprint).Append('\n');
        sb.Append("categoryLifecycleFingerprint=").Append(input.CategoryLifecycleFingerprint).Append('\n');
        sb.Append("orderedItemsFingerprint=").Append(input.OrderedItemsFingerprint).Append('\n');
        sb.Append("snapshotFingerprint=").Append(input.SnapshotFingerprint).Append('\n');
        sb.Append("totalCount=").Append(input.TotalCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var item in input.Items)
        {
            sb.Append(item.Ordinal.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(item.TransactionId).Append('\t')
                .Append(item.AccountId).Append('\t')
                .Append(item.EffectiveDate).Append('\t')
                .Append(item.SignedAmount).Append('\t')
                .Append(item.AmountDirection.ToString()).Append('\t')
                .Append(item.CategoryMutationState.ToString()).Append('\t')
                .Append(item.CurrentCategoryId ?? string.Empty).Append('\t')
                .Append(item.CurrentAllocationId ?? string.Empty).Append('\t')
                .Append(item.TransactionRevision).Append('\t')
                .Append(item.RelationshipRevision).Append('\t')
                .Append(item.AllocationRevision).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string ComputeItemLifecycleFingerprint(ClassificationProjectionItem item) =>
        CanonicalClassificationHasher.HashParts(
            item.TransactionRevision,
            item.RelationshipRevision,
            item.AllocationRevision);

    private static string ComputeSnapshotFingerprint(
        string ledgerContractVersion,
        string projectionVersion,
        string storeGenerationFingerprint,
        string snapshotId,
        string snapshotExpiresAt,
        string categoryLifecycleFingerprint,
        string orderedItemsFingerprint,
        IReadOnlyList<ClassificationProjectionItem> items) =>
        CanonicalClassificationHasher.HashParts(
            ledgerContractVersion,
            projectionVersion,
            storeGenerationFingerprint,
            snapshotId,
            snapshotExpiresAt,
            categoryLifecycleFingerprint,
            orderedItemsFingerprint,
            items.Count.ToString(CultureInfo.InvariantCulture));

    private static ClassificationProjectionItem CloneItem(ClassificationProjectionItem item) =>
        new(
            item.Ordinal,
            item.TransactionId,
            item.AccountId,
            item.EffectiveDate,
            item.SignedAmount,
            item.SourceDescription,
            item.AmountDirection,
            item.CategoryMutationState,
            item.CurrentCategoryId,
            item.CurrentAllocationId,
            item.TransactionRevision,
            item.RelationshipRevision,
            item.AllocationRevision);

    private static bool TryParseExpiresAt(string expiresAt, out DateTimeOffset parsed)
    {
        parsed = default;
        return DateTimeOffset.TryParse(
            expiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);
    }

    private static string MapProjectionFailure(ProcessError? error, int exitCode)
    {
        if (error is null)
        {
            return exitCode is 7 or 9
                ? ClassifyErrors.LedgerUnavailable
                : ClassifyErrors.LedgerUnavailable;
        }

        if (string.Equals(error.Category, "compatibility", StringComparison.Ordinal)
            || string.Equals(error.Code, "contract.incompatible", StringComparison.Ordinal)
            || string.Equals(error.Code, ClassifyErrors.LedgerIncompatible, StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        if (string.Equals(error.Category, "conflict", StringComparison.Ordinal)
            || string.Equals(error.Code, ClassifyErrors.Stale, StringComparison.Ordinal)
            || (error.Code?.Contains("stale", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.Code?.Contains("expired", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.Code?.Contains("cursor", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return ClassifyErrors.Stale;
        }

        if (string.Equals(error.Category, "integrity", StringComparison.Ordinal)
            || string.Equals(error.Code, ClassifyErrors.Integrity, StringComparison.Ordinal)
            || (error.Code?.Contains("integrity", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.Message?.Contains("ordinal", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.Message?.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.Message?.Contains("membership", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return ClassifyErrors.Integrity;
        }

        if (string.Equals(error.Code, "host.unavailable", StringComparison.Ordinal)
            || string.Equals(error.Category, "host", StringComparison.Ordinal)
            || string.Equals(error.Code, ClassifyErrors.LedgerUnavailable, StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerUnavailable;
        }

        return ClassifyErrors.LedgerUnavailable;
    }
}

/// <summary>
/// Bounded immutable evaluation input produced only after complete compatible projection acquisition.
/// Contains public projection fields and deterministic fingerprints — no CLASSIFY run id and no private paths.
/// Source descriptions are present solely as public Ledger projection fields for the evaluate command boundary;
/// the loader retains no additional page buffers after <see cref="ClassificationEvaluationInputLoader.LoadAsync"/> returns.
/// </summary>
public sealed record ClassificationEvaluationInput(
    string LedgerContractVersion,
    string ProjectionVersion,
    string SnapshotId,
    string SnapshotExpiresAt,
    string StoreGenerationFingerprint,
    string CategoryLifecycleFingerprint,
    string OrderedItemsFingerprint,
    string SnapshotFingerprint,
    int TotalCount,
    IReadOnlyList<ClassificationProjectionItem> Items,
    IReadOnlyList<ClassificationCategoryIdentity> ActiveCategories);
