using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Actuals;
using Tally.Infrastructure.Storage.Actuals;
using Tally.Infrastructure.Storage.Categories;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Actuals;

public sealed class ActualsQueryHandler(
    QuerySnapshotStore store,
    CategoryStore? categoryStore = null,
    TransactionStore? transactionStore = null,
    CategoryAllocationStore? allocationStore = null,
    RelationshipStore? relationshipStore = null)
{
    private const int CursorVersion = 1;
    private const int DefaultPageSize = 100;
    private const int MaximumPageSize = 500;

    public async Task<CommandResult<JsonElement>> HandleAsync(QueryActualsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Purpose is not null)
        {
            return await ClassificationAsync(input, cancellationToken);
        }

        return input.Cursor is null
            ? await FirstPageAsync(input, cancellationToken)
            : await LaterPageAsync(input, cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> ClassificationAsync(
        QueryActualsInput input,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(input.ItemProjection, ClassificationProjectionVersions.ClassificationV1, StringComparison.Ordinal))
        {
            return Failure(ActualsErrors.ContractMismatch);
        }

        if (categoryStore is null || transactionStore is null || allocationStore is null || relationshipStore is null)
        {
            return Failure(ActualsErrors.Invariant);
        }

        return input.Purpose switch
        {
            ClassificationProjectionPurpose.Evaluation => await EvaluationProjectionAsync(input, cancellationToken),
            ClassificationProjectionPurpose.ApplyPreflight => await ApplyPreflightProjectionAsync(input, cancellationToken),
            _ => Failure(ActualsErrors.InvalidFilter)
        };
    }

    private async Task<CommandResult<JsonElement>> EvaluationProjectionAsync(
        QueryActualsInput input,
        CancellationToken cancellationToken)
    {
        if (input.TransactionIds is { Count: > 0 })
        {
            return Failure(ActualsErrors.InvalidFilter);
        }

        try
        {
            IReadOnlyList<ClassificationProjectionItem> all;
            string snapshotId;
            string expiresAt;
            string generationFingerprint;
            string filterHash;
            string hierarchyFingerprint;
            ActualsTotalsResult totals;
            int pageSize;
            int start;

            if (input.Cursor is null)
            {
                pageSize = input.PageSize ?? DefaultPageSize;
                if (pageSize is < 1 or > MaximumPageSize)
                {
                    return Failure(ActualsErrors.InvalidFilter);
                }

                var filterInput = (input.Filter ?? new ActualsFilterInput()) with
                {
                    CategorizationStates = [TransactionCategoryState.Uncategorized],
                    LifecycleStates = [TransactionLifecycleStatus.Active]
                };
                if (!TryFilter(filterInput, out var filter))
                {
                    return Failure(ActualsErrors.InvalidFilter);
                }

                filterHash = FilterHash(filter) + "|purpose=evaluation|proj=classification_v1";
                var page = await store.CreateAsync(filter, filterHash, pageSize: MaximumPageSize, DateTimeOffset.UtcNow, cancellationToken);
                all = await BuildEligibleClassificationItemsAsync(page, cancellationToken);
                snapshotId = page.Result.SnapshotId;
                expiresAt = page.Result.ExpiresAt;
                generationFingerprint = page.GenerationFingerprint;
                hierarchyFingerprint = page.HierarchyFingerprint;
                totals = page.Result.Totals;
                start = 0;
            }
            else
            {
                if (input.PageSize is not null || input.Filter is not null)
                {
                    return Failure(ActualsErrors.CursorFilterMismatch);
                }

                if (!TryDecode(input.Cursor, out var cursor, out var error))
                {
                    return Failure(error!);
                }

                // Cursor carries next ordinal into the frozen eligible list rebuilt from snapshot pages.
                var rebuild = await RebuildEligibleFromSnapshotAsync(cursor!, cancellationToken);
                if (rebuild is null)
                {
                    return Failure(ActualsErrors.SnapshotNotFound);
                }

                all = rebuild.Value.Items;
                snapshotId = cursor!.SnapshotId;
                expiresAt = cursor.ExpiresAt;
                generationFingerprint = cursor.GenerationFingerprint;
                hierarchyFingerprint = cursor.CategoryHierarchyFingerprint;
                filterHash = cursor.FilterHash;
                totals = rebuild.Value.Totals;
                pageSize = cursor.PageSize;
                start = cursor.NextOrdinal;
            }

            if (start < 0 || start > all.Count)
            {
                return Failure(ActualsErrors.CursorInvalid);
            }

            var slice = all.Skip(start).Take(pageSize).ToArray();
            var catalogue = await LoadActiveCategoriesAsync(cancellationToken);
            string? nextCursor = null;
            if (start + slice.Length < all.Count)
            {
                var payload = new ActualsCursorPayload(
                    CursorVersion,
                    QuerySnapshotStore.ContractVersion,
                    snapshotId,
                    start + slice.Length,
                    pageSize,
                    filterHash,
                    generationFingerprint,
                    hierarchyFingerprint,
                    expiresAt);
                nextCursor = Encode(JsonSerializer.SerializeToUtf8Bytes(payload, ActualsJsonContext.Default.ActualsCursorPayload));
            }

            var result = new ActualsQueryResult(
                SnapshotId: snapshotId,
                ExpiresAt: expiresAt,
                TotalCount: all.Count,
                Items: [],
                Totals: totals,
                Groups: [],
                Cursor: nextCursor,
                LedgerContractVersion: QuerySnapshotStore.ContractVersion,
                StoreGenerationFingerprint: generationFingerprint,
                ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
                CategoryIdentityLifecycleFingerprint: CatalogueFingerprint(catalogue),
                ActiveCategories: catalogue,
                ClassificationItems: slice,
                MissingTransactionIds: null);
            return Success(result);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }
    }

    private async Task<CommandResult<JsonElement>> ApplyPreflightProjectionAsync(
        QueryActualsInput input,
        CancellationToken cancellationToken)
    {
        if (input.Cursor is not null)
        {
            return Failure(ActualsErrors.InvalidFilter);
        }

        var ids = input.TransactionIds;
        if (ids is null
            || ids.Count is 0 or > ClassificationProjectionVersions.MaxApplyPreflightIds
            || ids.Count != ids.Distinct(StringComparer.Ordinal).Count()
            || ids.Any(id => !LedgerId.TryParse(id, out _, out _)))
        {
            return Failure(ActualsErrors.InvalidFilter);
        }

        if (!TryFilter(new ActualsFilterInput(LifecycleStates: [TransactionLifecycleStatus.Active]), out var seedFilter))
        {
            return Failure(ActualsErrors.InvalidFilter);
        }

        try
        {
            var seed = await store.CreateAsync(
                seedFilter,
                FilterHash(seedFilter) + "|purpose=apply_preflight|proj=classification_v1",
                pageSize: 1,
                DateTimeOffset.UtcNow,
                cancellationToken);

            var catalogue = await LoadActiveCategoriesAsync(cancellationToken);
            var items = new List<ClassificationProjectionItem>(ids.Count);
            var missing = new List<string>();
            var ordinal = 0;
            foreach (var transactionId in ids.Order(StringComparer.Ordinal))
            {
                var detail = await transactionStore!.GetAsync(transactionId, includeHistory: true, cancellationToken);
                if (detail is null)
                {
                    missing.Add(transactionId);
                    continue;
                }

                var allocation = await allocationStore!.FindCurrentAsync(transactionId, cancellationToken);
                var relationshipRevision = await relationshipStore!.ActiveRevisionAsync(transactionId, cancellationToken);
                var mutationState = ResolveMutationState(detail, allocation, relationshipRevision);
                items.Add(new ClassificationProjectionItem(
                    Ordinal: ordinal++,
                    TransactionId: detail.TransactionId,
                    AccountId: detail.AccountId,
                    EffectiveDate: detail.EffectiveDate,
                    SignedAmount: detail.SignedAmount,
                    SourceDescription: detail.OriginalDescription,
                    AmountDirection: Direction(detail.SignedAmount),
                    CategoryMutationState: mutationState,
                    CurrentCategoryId: detail.Category.CategoryId,
                    CurrentAllocationId: detail.Category.AllocationEventId,
                    TransactionRevision: TransactionRevision(detail),
                    RelationshipRevision: relationshipRevision,
                    AllocationRevision: allocation?.AllocationEventId ?? "none"));
            }

            var result = new ActualsQueryResult(
                SnapshotId: seed.Result.SnapshotId,
                ExpiresAt: seed.Result.ExpiresAt,
                TotalCount: items.Count,
                Items: [],
                Totals: seed.Result.Totals,
                Groups: [],
                Cursor: null,
                LedgerContractVersion: QuerySnapshotStore.ContractVersion,
                StoreGenerationFingerprint: seed.GenerationFingerprint,
                ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
                CategoryIdentityLifecycleFingerprint: CatalogueFingerprint(catalogue),
                ActiveCategories: catalogue,
                ClassificationItems: items,
                MissingTransactionIds: missing.Count == 0 ? null : missing.Order(StringComparer.Ordinal).ToArray());
            return Success(result);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }
    }

    private async Task<IReadOnlyList<ClassificationProjectionItem>> BuildEligibleClassificationItemsAsync(
        SnapshotPage firstPage,
        CancellationToken cancellationToken)
    {
        var membership = new List<ActualsPageItem>();
        membership.AddRange(firstPage.Result.Items);
        var next = firstPage.NextOrdinal;
        while (next is int nextOrdinal)
        {
            var payload = new ActualsCursorPayload(
                CursorVersion,
                QuerySnapshotStore.ContractVersion,
                firstPage.Result.SnapshotId,
                nextOrdinal,
                firstPage.PageSize,
                firstPage.FilterHash,
                firstPage.GenerationFingerprint,
                firstPage.HierarchyFingerprint,
                firstPage.Result.ExpiresAt);
            var read = await store.ReadAsync(payload, DateTimeOffset.UtcNow, cancellationToken);
            if (!read.IsSuccess || read.Page is null)
            {
                break;
            }

            membership.AddRange(read.Page.Result.Items);
            next = read.Page.NextOrdinal;
        }

        var eligibleIds = membership
            .Where(IsIndependentDecisionEligible)
            .Select(item => item.TransactionId)
            .ToArray();

        var items = new List<ClassificationProjectionItem>(eligibleIds.Length);
        var ordinal = 0;
        foreach (var transactionId in eligibleIds)
        {
            var built = await BuildItemAsync(transactionId, ordinal++, cancellationToken);
            if (built is not null)
            {
                items.Add(built);
            }
        }

        return items;
    }

    private async Task<(IReadOnlyList<ClassificationProjectionItem> Items, ActualsTotalsResult Totals)?> RebuildEligibleFromSnapshotAsync(
        ActualsCursorPayload cursor,
        CancellationToken cancellationToken)
    {
        // Re-read from ordinal 1 (first item) — snapshots use dense ordinals starting at 0 in storage
        // but NextOrdinal on first incomplete page is pageSize. Read full membership from start.
        var startPayload = cursor with { NextOrdinal = 0 };
        // ReadAsync with NextOrdinal 0: check store behavior
        var read = await store.ReadAsync(startPayload, DateTimeOffset.UtcNow, cancellationToken);
        if (!read.IsSuccess || read.Page is null)
        {
            // Fallback: cursor invalid for rebuild
            return null;
        }

        var page = read.Page;
        // If NextOrdinal 0 is rejected by store, create is not available. Use materialize from cursor.NextOrdinal pages only if first page fails.
        var items = await BuildEligibleClassificationItemsAsync(page, cancellationToken);
        return (items, page.Result.Totals);
    }

    private async Task<ClassificationProjectionItem?> BuildItemAsync(
        string transactionId,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var detail = await transactionStore!.GetAsync(transactionId, includeHistory: true, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var allocation = await allocationStore!.FindCurrentAsync(transactionId, cancellationToken);
        var relationshipRevision = await relationshipStore!.ActiveRevisionAsync(transactionId, cancellationToken);
        return new ClassificationProjectionItem(
            Ordinal: ordinal,
            TransactionId: detail.TransactionId,
            AccountId: detail.AccountId,
            EffectiveDate: detail.EffectiveDate,
            SignedAmount: detail.SignedAmount,
            SourceDescription: detail.OriginalDescription,
            AmountDirection: Direction(detail.SignedAmount),
            CategoryMutationState: ResolveMutationState(detail, allocation, relationshipRevision),
            CurrentCategoryId: detail.Category.CategoryId,
            CurrentAllocationId: detail.Category.AllocationEventId,
            TransactionRevision: TransactionRevision(detail),
            RelationshipRevision: relationshipRevision,
            AllocationRevision: allocation?.AllocationEventId ?? "none");
    }

    private static bool IsIndependentDecisionEligible(ActualsPageItem item) =>
        item.CategoryState == TransactionCategoryState.Uncategorized
        && item.RelationshipState is not (
            ActualsRelationshipRole.TransferOutflow
            or ActualsRelationshipRole.TransferInflow
            or ActualsRelationshipRole.RefundCredit);

    private static CategoryMutationState ResolveMutationState(
        TransactionDetail detail,
        CategoryAllocationCurrent? allocation,
        string relationshipRevision)
    {
        if (detail.LifecycleStatus != TransactionLifecycleStatus.Active)
        {
            return CategoryMutationState.Ineligible;
        }

        // Active transfer principal or linked-refund credit: Ledger relationship is non-none with transfer/refund role.
        if (relationshipRevision.Contains(":transfer_", StringComparison.Ordinal)
            || relationshipRevision.Contains(":refund_credit", StringComparison.Ordinal)
            || relationshipRevision.Contains(":linked_refund", StringComparison.Ordinal))
        {
            // transfer_outflow / transfer_inflow / refund roles encoded in revision string.
            if (relationshipRevision.Contains("transfer_outflow", StringComparison.Ordinal)
                || relationshipRevision.Contains("transfer_inflow", StringComparison.Ordinal)
                || relationshipRevision.Contains("refund_credit", StringComparison.Ordinal)
                || relationshipRevision.Contains("credit", StringComparison.Ordinal)
                   && relationshipRevision.Contains("refund", StringComparison.Ordinal))
            {
                return CategoryMutationState.Ineligible;
            }
        }

        if (allocation is null && detail.Category.State == TransactionCategoryState.Uncategorized)
        {
            return CategoryMutationState.Assignable;
        }

        if (allocation is not null && detail.Category.State == TransactionCategoryState.Categorized)
        {
            return CategoryMutationState.Correctable;
        }

        return CategoryMutationState.Ineligible;
    }

    private static string TransactionRevision(TransactionDetail detail)
    {
        var latestLifecycle = detail.History?.Lifecycle.LastOrDefault()?.LifecycleEventId;
        return latestLifecycle ?? ("genesis:" + detail.TransactionId);
    }

    private static ClassificationAmountDirection Direction(string signedAmount)
    {
        if (!Money.TryParse(signedAmount, out var money, out _))
        {
            return ClassificationAmountDirection.Zero;
        }

        if (money.MinorUnits < 0) return ClassificationAmountDirection.Expense;
        if (money.MinorUnits > 0) return ClassificationAmountDirection.Income;
        return ClassificationAmountDirection.Zero;
    }

    private async Task<IReadOnlyList<ClassificationCategoryIdentity>> LoadActiveCategoriesAsync(
        CancellationToken cancellationToken)
    {
        var listed = await categoryStore!.ListAsync(CategoryStatus.Active, null, CategoryListScope.All, cancellationToken);
        return listed
            .OrderBy(item => item.CategoryId, StringComparer.Ordinal)
            .Select(item => new ClassificationCategoryIdentity(
                item.CategoryId,
                item.Name,
                item.Status == CategoryStatus.Active ? "active" : "archived"))
            .ToArray();
    }

    private static string CatalogueFingerprint(IReadOnlyList<ClassificationCategoryIdentity> catalogue)
    {
        var canonical = string.Join('|', catalogue.Select(item => item.CategoryId + ':' + item.LifecycleState + ':' + item.DisplayName));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<CommandResult<JsonElement>> FirstPageAsync(QueryActualsInput input, CancellationToken cancellationToken)
    {
        var pageSize = input.PageSize ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize || !TryFilter(input.Filter ?? new(), out var filter))
        {
            return Failure(ActualsErrors.InvalidFilter);
        }

        var filterHash = FilterHash(filter);
        try
        {
            var page = await store.CreateAsync(filter, filterHash, pageSize, DateTimeOffset.UtcNow, cancellationToken);
            return Success(WithCursor(page));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }
    }

    private async Task<CommandResult<JsonElement>> LaterPageAsync(QueryActualsInput input, CancellationToken cancellationToken)
    {
        if (input.Filter is not null || input.PageSize is not null)
        {
            return Failure(ActualsErrors.CursorFilterMismatch);
        }
        if (!TryDecode(input.Cursor!, out var cursor, out var error)) return Failure(error!);

        SnapshotReadResult read;
        try
        {
            read = await store.ReadAsync(cursor!, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }

        return read.IsSuccess ? Success(WithCursor(read.Page!)) : Failure(read.ErrorCode!);
    }

    private static ActualsQueryResult WithCursor(SnapshotPage page)
    {
        var stamped = page.Result with
        {
            LedgerContractVersion = QuerySnapshotStore.ContractVersion,
            StoreGenerationFingerprint = page.GenerationFingerprint
        };
        if (page.NextOrdinal is null) return stamped;
        var cursor = new ActualsCursorPayload(
            CursorVersion,
            QuerySnapshotStore.ContractVersion,
            stamped.SnapshotId,
            page.NextOrdinal.Value,
            page.PageSize,
            page.FilterHash,
            page.GenerationFingerprint,
            page.HierarchyFingerprint,
            stamped.ExpiresAt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, ActualsJsonContext.Default.ActualsCursorPayload);
        return stamped with { Cursor = Encode(bytes) };
    }

    private static bool TryDecode(string value, out ActualsCursorPayload? cursor, out string? error)
    {
        cursor = null;
        error = ActualsErrors.CursorInvalid;
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            cursor = JsonSerializer.Deserialize(Convert.FromBase64String(encoded), ActualsJsonContext.Default.ActualsCursorPayload);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (cursor is null
            || cursor.CursorVersion != CursorVersion
            || string.IsNullOrWhiteSpace(cursor.ContractVersion)
            || !LedgerId.TryParse(cursor.SnapshotId, out _, out _)
            || cursor.NextOrdinal < 0
            || cursor.PageSize is < 1 or > MaximumPageSize
            || string.IsNullOrWhiteSpace(cursor.FilterHash)
            || string.IsNullOrWhiteSpace(cursor.GenerationFingerprint)
            || string.IsNullOrWhiteSpace(cursor.CategoryHierarchyFingerprint)
            || string.IsNullOrWhiteSpace(cursor.ExpiresAt))
        {
            return false;
        }

        if (!string.Equals(cursor.ContractVersion, QuerySnapshotStore.ContractVersion, StringComparison.Ordinal))
        {
            error = ActualsErrors.ContractMismatch;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryFilter(ActualsFilterInput input, out ActualsFilter filter)
    {
        filter = null!;
        if (!Enum.IsDefined(input.CategoryScope)
            || !Enum.IsDefined(input.GroupBy)
            || !TryDate(input.EffectiveFrom, out var effectiveFrom)
            || !TryDate(input.EffectiveTo, out var effectiveTo))
        {
            return false;
        }

        filter = new(
            input.AccountIds,
            effectiveFrom,
            effectiveTo,
            input.CategoryIds,
            (ActualsCategoryScope)(int)input.CategoryScope,
            input.CategorizationStates,
            input.PoolIds,
            input.PoolStates,
            input.InstrumentIds,
            input.InstrumentStates,
            input.CardholderIds,
            input.CardholderStates,
            input.EvidenceKinds,
            input.ReconciliationStates,
            input.RelationshipStates?.Select(value => (ActualsRelationshipState)(int)value).ToArray(),
            input.LifecycleStates,
            (ActualsGroupKind)(int)input.GroupBy);
        return filter.IsValid();
    }

    private static bool TryDate(string? value, out EffectiveDate? result)
    {
        result = null;
        if (value is null) return true;
        if (!EffectiveDate.TryParse(value, out var parsed, out _)) return false;
        result = parsed;
        return true;
    }

    private static string FilterHash(ActualsFilter filter)
    {
        var canonical = new StringBuilder();
        Add(canonical, "accounts", filter.AccountIds);
        Add(canonical, "from", filter.EffectiveFrom?.ToString());
        Add(canonical, "to", filter.EffectiveTo?.ToString());
        Add(canonical, "categories", filter.CategoryIds);
        Add(canonical, "categoryScope", (int)filter.CategoryScope);
        Add(canonical, "categorizationStates", filter.CategorizationStates?.Select(value => (int)value));
        Add(canonical, "pools", filter.PoolIds);
        Add(canonical, "poolStates", filter.PoolStates?.Select(value => (int)value));
        Add(canonical, "instruments", filter.InstrumentIds);
        Add(canonical, "instrumentStates", filter.InstrumentStates?.Select(value => (int)value));
        Add(canonical, "cardholders", filter.CardholderIds);
        Add(canonical, "cardholderStates", filter.CardholderStates?.Select(value => (int)value));
        Add(canonical, "evidenceKinds", filter.EvidenceKinds?.Select(value => (int)value));
        Add(canonical, "reconciliationStates", filter.ReconciliationStates?.Select(value => (int)value));
        Add(canonical, "relationshipStates", filter.RelationshipStates?.Select(value => (int)value));
        Add(canonical, "lifecycleStates", filter.LifecycleStates?.Select(value => (int)value));
        Add(canonical, "groupBy", (int)filter.GroupBy);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Add(StringBuilder target, string name, string? value)
    {
        target.Append(name).Append('=');
        if (value is null) target.Append("null");
        else target.Append(value.Length).Append(':').Append(value);
        target.Append('|');
    }

    private static void Add(StringBuilder target, string name, int value) =>
        Add(target, name, value.ToString(global::System.Globalization.CultureInfo.InvariantCulture));

    private static void Add(StringBuilder target, string name, IEnumerable<string>? values) =>
        Add(target, name, values is null ? null : string.Join(',', values.Order(StringComparer.Ordinal)));

    private static void Add(StringBuilder target, string name, IEnumerable<int>? values) =>
        Add(target, name, values is null ? null : string.Join(',', values.Order().Select(value => value.ToString(global::System.Globalization.CultureInfo.InvariantCulture))));

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CommandResult<JsonElement> Success(ActualsQueryResult result) => CommandResult<JsonElement>.Success(
        JsonSerializer.SerializeToElement(result, ActualsJsonContext.Default.ActualsQueryResult));

    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
