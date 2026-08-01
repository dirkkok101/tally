using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Evaluation;
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
            if (input.Cursor is null)
            {
                var pageSize = input.PageSize ?? DefaultPageSize;
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

                var filterHash = FilterHash(filter) + "|purpose=evaluation|proj=classification_v1";
                var created = await store.CreateClassificationSnapshotAsync(
                    filter,
                    filterHash,
                    pageSize,
                    DateTimeOffset.UtcNow,
                    MaterializeEvaluationFreezeAsync,
                    cancellationToken);
                return Success(ClassificationPage(created, startOrdinal: 0));
            }

            if (input.PageSize is not null || input.Filter is not null)
            {
                return Failure(ActualsErrors.CursorFilterMismatch);
            }

            // Public cursor validation: NextOrdinal >= 1 (never weakens ordinary actuals TryDecode).
            if (!TryDecode(input.Cursor, out var cursor, out var error))
            {
                return Failure(error!);
            }

            var read = await store.ReadClassificationSnapshotAsync(cursor!, DateTimeOffset.UtcNow, cancellationToken);
            if (!read.IsSuccess || read.Page is null)
            {
                return Failure(read.ErrorCode ?? ActualsErrors.SnapshotNotFound);
            }

            return Success(ClassificationPage(read.Page, startOrdinal: cursor!.NextOrdinal));
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

        try
        {
            var orderedIds = ids.Order(StringComparer.Ordinal).ToArray();
            var filterHash = "purpose=apply_preflight|proj=classification_v1|ids="
                + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', orderedIds))));

            var created = await store.CreateClassificationSnapshotAsync(
                membershipFilter: null,
                filterHash,
                pageSize: Math.Max(orderedIds.Length, 1),
                DateTimeOffset.UtcNow,
                (connection, transaction, _, token) =>
                    MaterializePreflightFreezeAsync(connection, transaction, orderedIds, token),
                cancellationToken);

            // Preflight is a single coherent page (no cursor).
            return Success(ClassificationPage(created, startOrdinal: 0, emitCursor: false));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(ActualsErrors.SnapshotBusy);
        }
    }

    private async Task<ClassificationFrozenPayload> MaterializeEvaluationFreezeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ActualsItem>? membership,
        CancellationToken cancellationToken)
    {
        var ordered = membership ?? Array.Empty<ActualsItem>();
        var eligible = ordered.Where(IsIndependentDecisionEligible).ToArray();
        var items = new List<ClassificationProjectionItem>(eligible.Length);
        var ordinal = 0;
        foreach (var member in eligible)
        {
            var built = await BuildItemAsync(connection, transaction, member.TransactionId, ordinal++, cancellationToken);
            if (built is null)
            {
                // Membership promised the row; absence mid-transaction is an invariant failure.
                throw new InvalidOperationException(ActualsErrors.Invariant);
            }

            items.Add(built);
        }

        var catalogue = await LoadActiveCategoriesAsync(connection, transaction, cancellationToken);
        var totals = TotalsFromMembership(ordered);
        return new ClassificationFrozenPayload(
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CatalogueFingerprint: CatalogueFingerprint(catalogue),
            ActiveCategories: catalogue,
            Items: items,
            MissingTransactionIds: null,
            Totals: totals);
    }

    private async Task<ClassificationFrozenPayload> MaterializePreflightFreezeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> orderedIds,
        CancellationToken cancellationToken)
    {
        var items = new List<ClassificationProjectionItem>(orderedIds.Count);
        var missing = new List<string>();
        var ordinal = 0;
        foreach (var transactionId in orderedIds)
        {
            var detail = await transactionStore!.GetAsync(connection, transaction, transactionId, includeHistory: true, cancellationToken);
            if (detail is null)
            {
                missing.Add(transactionId);
                continue;
            }

            var allocation = await allocationStore!.FindCurrentAsync(connection, transaction, transactionId, cancellationToken);
            var relationshipRevision = await relationshipStore!.ActiveRevisionAsync(connection, transaction, transactionId, cancellationToken);
            items.Add(new ClassificationProjectionItem(
                Ordinal: ordinal++,
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
                AllocationRevision: allocation?.AllocationEventId ?? "none"));
        }

        var catalogue = await LoadActiveCategoriesAsync(connection, transaction, cancellationToken);
        return new ClassificationFrozenPayload(
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CatalogueFingerprint: CatalogueFingerprint(catalogue),
            ActiveCategories: catalogue,
            Items: items,
            MissingTransactionIds: missing.Count == 0 ? null : missing.ToArray(),
            Totals: new ActualsTotalsResult("0.00", "0.00", "0.00"));
    }

    private async Task<ClassificationProjectionItem?> BuildItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var detail = await transactionStore!.GetAsync(connection, transaction, transactionId, includeHistory: true, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var allocation = await allocationStore!.FindCurrentAsync(connection, transaction, transactionId, cancellationToken);
        var relationshipRevision = await relationshipStore!.ActiveRevisionAsync(connection, transaction, transactionId, cancellationToken);
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

    private static ActualsQueryResult ClassificationPage(
        ClassificationSnapshotCreateResult created,
        int startOrdinal,
        bool emitCursor = true)
    {
        var frozen = created.Frozen;
        if (startOrdinal < 0 || startOrdinal > frozen.Items.Count)
        {
            // Caller validates cursor; defensive empty page is never returned as success from handlers.
            throw new InvalidOperationException(ActualsErrors.CursorInvalid);
        }

        var slice = frozen.Items.Skip(startOrdinal).Take(created.PageSize).ToArray();
        string? nextCursor = null;
        if (emitCursor && startOrdinal + slice.Length < frozen.Items.Count)
        {
            var payload = new ActualsCursorPayload(
                CursorVersion,
                QuerySnapshotStore.ContractVersion,
                created.SnapshotId,
                startOrdinal + slice.Length,
                created.PageSize,
                created.FilterHash,
                created.GenerationFingerprint,
                created.HierarchyFingerprint,
                created.ExpiresAt);
            nextCursor = Encode(JsonSerializer.SerializeToUtf8Bytes(payload, ActualsJsonContext.Default.ActualsCursorPayload));
        }

        return new ActualsQueryResult(
            SnapshotId: created.SnapshotId,
            ExpiresAt: created.ExpiresAt,
            TotalCount: frozen.Items.Count,
            Items: [],
            Totals: frozen.Totals,
            Groups: [],
            Cursor: nextCursor,
            LedgerContractVersion: QuerySnapshotStore.ContractVersion,
            StoreGenerationFingerprint: created.GenerationFingerprint,
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CategoryIdentityLifecycleFingerprint: frozen.CatalogueFingerprint,
            ActiveCategories: frozen.ActiveCategories,
            ClassificationItems: slice,
            MissingTransactionIds: frozen.MissingTransactionIds);
    }

    private static bool IsIndependentDecisionEligible(ActualsItem item) =>
        item.LifecycleStatus == TransactionLifecycleStatus.Active
        && item.CategoryState == TransactionCategoryState.Uncategorized
        && item.RelationshipState is not (
            ActualsRelationshipState.TransferOutflow
            or ActualsRelationshipState.TransferInflow
            or ActualsRelationshipState.RefundCredit);

    private static CategoryMutationState ResolveMutationState(
        TransactionDetail detail,
        CategoryAllocationCurrent? allocation,
        string relationshipRevision)
    {
        if (detail.LifecycleStatus != TransactionLifecycleStatus.Active)
        {
            return CategoryMutationState.Ineligible;
        }

        if (relationshipRevision.Contains("transfer_outflow", StringComparison.Ordinal)
            || relationshipRevision.Contains("transfer_inflow", StringComparison.Ordinal)
            || relationshipRevision.Contains("refund_credit", StringComparison.Ordinal))
        {
            return CategoryMutationState.Ineligible;
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var listed = await categoryStore!.ListAsync(
            connection,
            transaction,
            CategoryStatus.Active,
            null,
            CategoryListScope.All,
            cancellationToken);
        return listed
            .OrderBy(item => item.CategoryId, StringComparer.Ordinal)
            .Select(item => new ClassificationCategoryIdentity(
                item.CategoryId,
                item.Name,
                item.Status == CategoryStatus.Active ? "active" : "archived"))
            .ToArray();
    }

    /// <summary>
    /// classification_v1 CategoryIdentityLifecycleFingerprint / catalogue fingerprint.
    /// Must match <see cref="EvaluationFingerprint.ComputeCategoryLifecycleFingerprint"/>:
    /// identity + lifecycle only (display name excluded so renames stay valid), ordered by category id.
    /// </summary>
    private static string CatalogueFingerprint(IReadOnlyList<ClassificationCategoryIdentity> catalogue) =>
        EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
            catalogue.Select(item => (item.CategoryId, item.LifecycleState)));

    private static ActualsTotalsResult TotalsFromMembership(IReadOnlyList<ActualsItem> membership)
    {
        var calculation = ActualsCalculator.Calculate(membership, ActualsGroupKind.None);
        return new ActualsTotalsResult(
            calculation.Totals.NetAccountMovement.ToString(),
            calculation.Totals.ExternalSpend.ToString(),
            calculation.Totals.BudgetActual.ToString());
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

    /// <summary>
    /// Public actuals/classification cursor validation. NextOrdinal must be &gt;= 1 so ordinary
    /// actuals cursors are never weakened to resume at storage ordinal 0.
    /// Classification later pages also use NextOrdinal &gt;= 1 into the frozen item list.
    /// </summary>
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
            || cursor.NextOrdinal < 1
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
