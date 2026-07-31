using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Budget.Periods;

namespace Tally.Integration.Ledger;

public sealed record LedgerContractResult<T>(int ExitCode, T? Value, ProcessError? Error, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

public sealed class LedgerContractClient(OperationRegistry registry, TallyProcess process)
{
    private const string AccountGet = "ledger.account.get";
    private const string TransactionRecord = "ledger.transaction.record";
    private const string TransactionGet = "ledger.transaction.get";
    private const string CategoryList = "ledger.category.list";
    private const string CategoryGet = "ledger.category.get";
    private const string ActualsQuery = "ledger.actuals.query";
    private const string CategoryAssign = "ledger.transaction.category.assign";
    private const string CategoryCorrect = "ledger.transaction.category.correct";

    public Task<LedgerContractResult<AccountDetail>> GetAccountAsync(
        string accountId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            AccountGet,
            contractVersion,
            actor,
            new GetAccountInput(accountId),
            null,
            LedgerJsonContext.Default.GetAccountInput,
            LedgerJsonContext.Default.AccountDetail,
            cancellationToken);

    public Task<LedgerContractResult<TransactionDetail>> RecordTransactionAsync(
        FrozenLedgerRecordRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.OperationId, TransactionRecord, StringComparison.Ordinal))
        {
            return Task.FromResult(Incompatible<TransactionDetail>());
        }

        return ExecuteAsync(
            request.OperationId,
            request.LedgerContractVersion,
            request.Actor,
            request.Input,
            request.IdempotencyKey,
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail,
            cancellationToken);
    }

    public Task<LedgerContractResult<TransactionDetail>> GetTransactionAsync(
        string transactionId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            TransactionGet,
            contractVersion,
            actor,
            new GetTransactionInput(transactionId, IncludeHistory: false),
            null,
            LedgerJsonContext.Default.GetTransactionInput,
            LedgerJsonContext.Default.TransactionDetail,
            cancellationToken);

    /// <summary>
    /// BUDGET category catalogue evidence via released <c>ledger.category.list</c>
    /// (DM-BUDGET-LEDGER-COMPOSITION-CONTRACT).
    /// </summary>
    public Task<LedgerContractResult<CategoryListResult>> ListBudgetCategoriesAsync(
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken,
        CategoryStatus? status = null)
    {
        if (!IsCompatible(CategoryList, contractVersion, typeof(ListCategoriesInput), typeof(CategoryListResult)))
        {
            return Task.FromResult(BudgetIncompatible<CategoryListResult>());
        }

        return ExecuteAsync(
            CategoryList,
            contractVersion,
            actor,
            new ListCategoriesInput(Status: status),
            null,
            LedgerJsonContext.Default.ListCategoriesInput,
            LedgerJsonContext.Default.CategoryListResult,
            cancellationToken);
    }

    /// <summary>
    /// BUDGET category identity and lifecycle evidence via released <c>ledger.category.get</c>
    /// (DM-BUDGET-LEDGER-COMPOSITION-CONTRACT).
    /// </summary>
    public Task<LedgerContractResult<CategoryDetail>> GetBudgetCategoryAsync(
        string categoryId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken,
        bool includeHistory = false)
    {
        if (!IsCompatible(CategoryGet, contractVersion, typeof(GetCategoryInput), typeof(CategoryDetail)))
        {
            return Task.FromResult(BudgetIncompatible<CategoryDetail>());
        }

        return ExecuteAsync(
            CategoryGet,
            contractVersion,
            actor,
            new GetCategoryInput(categoryId, includeHistory),
            null,
            LedgerJsonContext.Default.GetCategoryInput,
            LedgerJsonContext.Default.CategoryDetail,
            cancellationToken);
    }

    /// <summary>
    /// BUDGET period actuals: maps half-open <see cref="BudgetPeriod"/> to LEDGER inclusive dates,
    /// drains every page under one snapshot/generation, and returns the complete set or no partial.
    /// </summary>
    public async Task<LedgerContractResult<ActualsQueryResult>> QueryBudgetActualsAsync(
        BudgetPeriod period,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken,
        int? pageSize = null)
    {
        if (!IsCompatible(ActualsQuery, contractVersion, typeof(QueryActualsInput), typeof(ActualsQueryResult)))
        {
            return BudgetIncompatible<ActualsQueryResult>();
        }

        var effectiveFrom = period.FormatStartInclusive();
        var effectiveTo = period.EndExclusive.AddDays(-1)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var filter = new ActualsFilterInput(
            EffectiveFrom: effectiveFrom,
            EffectiveTo: effectiveTo,
            LifecycleStates: [TransactionLifecycleStatus.Active]);

        var first = await ExecuteAsync(
            ActualsQuery,
            contractVersion,
            actor,
            new QueryActualsInput(filter, pageSize),
            null,
            ActualsJsonContext.Default.QueryActualsInput,
            ActualsJsonContext.Default.ActualsQueryResult,
            cancellationToken);

        if (!first.IsSuccess || first.Value is null)
        {
            // Expected Ledger failure or incompatibility — no partial position evidence.
            return first;
        }

        if (!string.Equals(first.Value.LedgerContractVersion, contractVersion, StringComparison.Ordinal))
        {
            return BudgetIntegrity<ActualsQueryResult>(
                "Budget actuals pages do not carry the requested Ledger contract version.");
        }

        // Iteration cap from the first page's evidence: a well-behaved drain never needs more.
        var effectivePageSize = Math.Max(1, pageSize ?? first.Value.Items.Count);
        var maxPages = (first.Value.TotalCount / effectivePageSize) + 2;

        var pages = new List<ActualsQueryResult> { first.Value };
        var cursor = first.Value.Cursor;
        while (cursor is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pages.Count >= maxPages)
            {
                return BudgetIntegrity<ActualsQueryResult>(
                    "Budget actuals pagination exceeded the bounded page count for the reported total.");
            }

            var next = await ExecuteAsync(
                ActualsQuery,
                contractVersion,
                actor,
                new QueryActualsInput(Cursor: cursor),
                null,
                ActualsJsonContext.Default.QueryActualsInput,
                ActualsJsonContext.Default.ActualsQueryResult,
                cancellationToken);

            if (!next.IsSuccess || next.Value is null)
            {
                // Drop any prior pages — no partial position on expiry/cursor/generation failure.
                return new(next.ExitCode, default, next.Error, next.StandardError);
            }

            pages.Add(next.Value);
            cursor = next.Value.Cursor;
        }

        var anchor = pages[0];
        for (var i = 1; i < pages.Count; i++)
        {
            var page = pages[i];
            if (!string.Equals(page.SnapshotId, anchor.SnapshotId, StringComparison.Ordinal)
                || !string.Equals(page.StoreGenerationFingerprint, anchor.StoreGenerationFingerprint, StringComparison.Ordinal)
                || !string.Equals(page.LedgerContractVersion, anchor.LedgerContractVersion, StringComparison.Ordinal)
                || page.TotalCount != anchor.TotalCount
                || !string.Equals(page.Totals.BudgetActual, anchor.Totals.BudgetActual, StringComparison.Ordinal)
                || !string.Equals(page.Totals.NetAccountMovement, anchor.Totals.NetAccountMovement, StringComparison.Ordinal)
                || !string.Equals(page.Totals.ExternalSpend, anchor.Totals.ExternalSpend, StringComparison.Ordinal))
            {
                return BudgetIntegrity<ActualsQueryResult>(
                    "Budget actuals pages do not share one snapshot and generation evidence.");
            }
        }

        var items = pages.SelectMany(page => page.Items).ToArray();
        if (items.Length != anchor.TotalCount)
        {
            return BudgetIntegrity<ActualsQueryResult>(
                "Budget actuals page membership does not match the full-set total count.");
        }

        if (items.Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            return BudgetIntegrity<ActualsQueryResult>(
                "Budget actuals pages returned a duplicated transaction member.");
        }

        var ordinals = items.Select(item => item.Ordinal).Order().ToArray();
        if (!ordinals.SequenceEqual(Enumerable.Range(0, anchor.TotalCount)))
        {
            return BudgetIntegrity<ActualsQueryResult>(
                "Budget actuals ordinals are incomplete or duplicated across pages.");
        }

        var startInclusive = period.StartInclusive;
        var endInclusive = period.EndExclusive.AddDays(-1);
        foreach (var item in items)
        {
            if (!DateOnly.TryParseExact(
                    item.EffectiveDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var effectiveDate)
                || effectiveDate < startInclusive
                || effectiveDate > endInclusive)
            {
                return BudgetIntegrity<ActualsQueryResult>(
                    "Budget actuals returned a member outside the requested period window.");
            }
        }

        var complete = anchor with
        {
            Items = items,
            Cursor = null
        };
        return new(0, complete, null, first.StandardError);
    }

    /// <summary>
    /// CLASSIFY purpose-scoped projection via released <c>ledger.actuals.query</c>
    /// (DM-CLASSIFY-LEDGER-PROJECTION-CONTRACT). Discovers a compatible descriptor before any read,
    /// drains every page under one frozen snapshot, and returns complete classification membership
    /// with exact ordinal accounting or no partial result.
    /// </summary>
    public async Task<LedgerContractResult<ActualsQueryResult>> QueryClassificationProjectionAsync(
        ClassificationProjectionPurpose purpose,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? transactionIds = null,
        int? pageSize = null,
        string itemProjection = ClassificationProjectionVersions.ClassificationV1)
    {
        if (!IsCompatible(ActualsQuery, contractVersion, typeof(QueryActualsInput), typeof(ActualsQueryResult)))
        {
            return Incompatible<ActualsQueryResult>();
        }

        if (!string.Equals(itemProjection, ClassificationProjectionVersions.ClassificationV1, StringComparison.Ordinal))
        {
            return Incompatible<ActualsQueryResult>();
        }

        var firstInput = new QueryActualsInput(
            Purpose: purpose,
            ItemProjection: itemProjection,
            TransactionIds: transactionIds,
            PageSize: pageSize);

        var first = await ExecuteAsync(
            ActualsQuery,
            contractVersion,
            actor,
            firstInput,
            null,
            ActualsJsonContext.Default.QueryActualsInput,
            ActualsJsonContext.Default.ActualsQueryResult,
            cancellationToken);

        if (!first.IsSuccess || first.Value is null)
        {
            return first;
        }

        if (!string.Equals(first.Value.LedgerContractVersion, contractVersion, StringComparison.Ordinal)
            || !string.Equals(first.Value.ProjectionVersion, ClassificationProjectionVersions.ClassificationV1, StringComparison.Ordinal))
        {
            return ClassifyIntegrity<ActualsQueryResult>(
                "Classification projection pages do not carry the requested classification contract identity.");
        }

        // apply_preflight is a single coherent page in the released contract; still drain if a cursor appears.
        var classificationCount = first.Value.ClassificationItems?.Count ?? 0;
        var effectivePageSize = Math.Max(1, pageSize ?? Math.Max(classificationCount, 1));
        var maxPages = (first.Value.TotalCount / effectivePageSize) + 2;

        var pages = new List<ActualsQueryResult> { first.Value };
        var cursor = first.Value.Cursor;
        while (cursor is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pages.Count >= maxPages)
            {
                return ClassifyIntegrity<ActualsQueryResult>(
                    "Classification projection pagination exceeded the bounded page count for the reported total.");
            }

            var next = await ExecuteAsync(
                ActualsQuery,
                contractVersion,
                actor,
                new QueryActualsInput(
                    Purpose: purpose,
                    ItemProjection: itemProjection,
                    Cursor: cursor),
                null,
                ActualsJsonContext.Default.QueryActualsInput,
                ActualsJsonContext.Default.ActualsQueryResult,
                cancellationToken);

            if (!next.IsSuccess || next.Value is null)
            {
                // Drop prior pages — no partial evaluation/preflight on expiry/cursor/generation failure.
                return new(next.ExitCode, default, next.Error, next.StandardError);
            }

            pages.Add(next.Value);
            cursor = next.Value.Cursor;
        }

        var anchor = pages[0];
        for (var i = 1; i < pages.Count; i++)
        {
            var page = pages[i];
            if (!string.Equals(page.SnapshotId, anchor.SnapshotId, StringComparison.Ordinal)
                || !string.Equals(page.StoreGenerationFingerprint, anchor.StoreGenerationFingerprint, StringComparison.Ordinal)
                || !string.Equals(page.LedgerContractVersion, anchor.LedgerContractVersion, StringComparison.Ordinal)
                || !string.Equals(page.ProjectionVersion, anchor.ProjectionVersion, StringComparison.Ordinal)
                || !string.Equals(page.CategoryIdentityLifecycleFingerprint, anchor.CategoryIdentityLifecycleFingerprint, StringComparison.Ordinal)
                || page.TotalCount != anchor.TotalCount)
            {
                return ClassifyIntegrity<ActualsQueryResult>(
                    "Classification projection pages do not share one frozen snapshot and catalogue identity.");
            }
        }

        var classificationItems = pages
            .SelectMany(page => page.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>())
            .ToArray();
        if (classificationItems.Length != anchor.TotalCount)
        {
            return ClassifyIntegrity<ActualsQueryResult>(
                "Classification projection membership does not match the full-set total count.");
        }

        if (classificationItems.Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count()
            != classificationItems.Length)
        {
            return ClassifyIntegrity<ActualsQueryResult>(
                "Classification projection pages returned a duplicated transaction member.");
        }

        var ordinals = classificationItems.Select(item => item.Ordinal).ToArray();
        if (!ordinals.SequenceEqual(Enumerable.Range(0, anchor.TotalCount)))
        {
            return ClassifyIntegrity<ActualsQueryResult>(
                "Classification projection ordinals are incomplete, duplicated, or out of frozen order.");
        }

        var complete = anchor with
        {
            ClassificationItems = classificationItems,
            ActiveCategories = anchor.ActiveCategories,
            Cursor = null
        };
        return new(0, complete, null, first.StandardError);
    }

    /// <summary>
    /// CLASSIFY category display/catalogue evidence via released <c>ledger.category.list</c>.
    /// </summary>
    public Task<LedgerContractResult<CategoryListResult>> ListClassificationCategoriesAsync(
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken,
        CategoryStatus? status = CategoryStatus.Active)
    {
        if (!IsCompatible(CategoryList, contractVersion, typeof(ListCategoriesInput), typeof(CategoryListResult)))
        {
            return Task.FromResult(Incompatible<CategoryListResult>());
        }

        return ExecuteAsync(
            CategoryList,
            contractVersion,
            actor,
            new ListCategoriesInput(Status: status),
            null,
            LedgerJsonContext.Default.ListCategoriesInput,
            LedgerJsonContext.Default.CategoryListResult,
            cancellationToken);
    }

    /// <summary>
    /// CLASSIFY category assignment via released <c>ledger.transaction.category.assign</c>.
    /// Preserves frozen idempotency key, mutation preconditions, cancellation, and Ledger errors.
    /// </summary>
    public Task<LedgerContractResult<CategoryAllocationResult>> AssignCategoryAsync(
        AssignCategoryInput input,
        string contractVersion,
        SafeActor actor,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsCompatible(CategoryAssign, contractVersion, typeof(AssignCategoryInput), typeof(CategoryAllocationResult)))
        {
            return Task.FromResult(Incompatible<CategoryAllocationResult>());
        }

        return ExecuteAsync(
            CategoryAssign,
            contractVersion,
            actor,
            input,
            idempotencyKey,
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult,
            cancellationToken);
    }

    /// <summary>
    /// CLASSIFY category correction via released <c>ledger.transaction.category.correct</c>.
    /// Preserves frozen idempotency key, expected allocation/revisions, cancellation, and Ledger errors.
    /// </summary>
    public Task<LedgerContractResult<CategoryAllocationResult>> CorrectCategoryAsync(
        CorrectCategoryInput input,
        string contractVersion,
        SafeActor actor,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsCompatible(CategoryCorrect, contractVersion, typeof(CorrectCategoryInput), typeof(CategoryAllocationResult)))
        {
            return Task.FromResult(Incompatible<CategoryAllocationResult>());
        }

        return ExecuteAsync(
            CategoryCorrect,
            contractVersion,
            actor,
            input,
            idempotencyKey,
            LedgerJsonContext.Default.CorrectCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult,
            cancellationToken);
    }

    private async Task<LedgerContractResult<TResult>> ExecuteAsync<TInput, TResult>(
        string operationId,
        string contractVersion,
        SafeActor actor,
        TInput input,
        string? idempotencyKey,
        JsonTypeInfo<TInput> inputType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
    {
        var descriptor = registry.Find(operationId);
        if (descriptor is null
            || descriptor.RequestTypeInfo.Type != typeof(TInput)
            || descriptor.ResultTypeInfo.Type != typeof(TResult)
            || !SupportsVersion(descriptor, contractVersion))
        {
            return Incompatible<TResult>();
        }

        var inputElement = JsonSerializer.SerializeToElement(input, inputType);
        var request = new RequestEnvelope(contractVersion, actor, inputElement, idempotencyKey);
        var requestJson = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = descriptor.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Concat(["--input", "-"])
            .ToArray();
        var processResult = await process.RunAsync(arguments, requestJson, cancellationToken);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)
            ?? throw new InvalidOperationException("The public Ledger executor returned no result envelope.");

        if (processResult.ExitCode != 0)
        {
            return new(processResult.ExitCode, default, envelope.Error, processResult.Stderr);
        }

        if (envelope.Outcome != "success" || envelope.Result is null)
        {
            throw new InvalidOperationException("The public Ledger executor returned an invalid success envelope.");
        }

        var value = JsonSerializer.Deserialize(envelope.Result.Value, resultType)
            ?? throw new InvalidOperationException("The public Ledger executor returned no typed result.");
        return new(processResult.ExitCode, value, null, processResult.Stderr);
    }

    private bool IsCompatible(string operationId, string contractVersion, Type requestType, Type resultType)
    {
        var descriptor = registry.Find(operationId);
        return descriptor is not null
            && descriptor.RequestTypeInfo.Type == requestType
            && descriptor.ResultTypeInfo.Type == resultType
            && SupportsVersion(descriptor, contractVersion);
    }

    private static bool SupportsVersion(OperationDescriptor descriptor, string contractVersion) =>
        Version.TryParse(contractVersion, out var requested)
        && Version.TryParse(descriptor.MinimumContractVersion, out var minimum)
        && Version.TryParse(descriptor.MaximumContractVersion, out var maximum)
        && requested >= minimum
        && requested <= maximum;

    private static LedgerContractResult<T> Incompatible<T>() => new(
        7,
        default,
        new ProcessError("contract.incompatible", "compatibility", "The Ledger contract version or operation is not supported."),
        "tally: contract.incompatible");

    private static LedgerContractResult<T> BudgetIncompatible<T>() => new(
        7,
        default,
        new ProcessError(
            BudgetErrors.LedgerIncompatible,
            "compatibility",
            "The Ledger contract version or operation is not supported for BUDGET composition."),
        $"tally: {BudgetErrors.LedgerIncompatible}");

    private static LedgerContractResult<T> BudgetIntegrity<T>(string message) => new(
        8,
        default,
        new ProcessError(BudgetErrors.Integrity, "integrity", message),
        $"tally: {BudgetErrors.Integrity}");

    /// <summary>
    /// Client-side frozen-page accounting failure. Does not rewrite downstream Ledger error codes.
    /// </summary>
    private static LedgerContractResult<T> ClassifyIntegrity<T>(string message) => new(
        8,
        default,
        new ProcessError("operation.review_required", "integrity", message),
        "tally: operation.review_required");
}
