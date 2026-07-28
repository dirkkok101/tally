using System.Runtime.Versioning;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Features.Budget.Contract;
using Tally.Integration.Ledger;

namespace Tally.Features.Budget.Categories;

/// <summary>
/// Single policy for BUDGET category lifecycle evidence (bd-b5fl).
/// Modes:
/// <list type="bullet">
/// <item><see cref="Mode.ValidateActive"/> — draft/activate: every ID must be Active; fail closed.</item>
/// <item><see cref="Mode.EnrichSupplemental"/> — revision get: historical IDs may be Archived or Unknown
/// (true not-found only); host/compat failures fail the request, never fabricate Unknown.</item>
/// <item><see cref="Mode.ResolveKnown"/> — position/insights: unknown IDs fail Integrity; Active+Archived only.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetCategoryEvidenceResolver(LedgerContractClient ledger)
{
    public enum Mode
    {
        ValidateActive,
        EnrichSupplemental,
        ResolveKnown
    }

    public async Task<BudgetCategoryEvidenceResult> ResolveAsync(
        IReadOnlyList<string> categoryIds,
        Mode mode,
        SafeActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        ArgumentNullException.ThrowIfNull(actor);
        cancellationToken.ThrowIfCancellationRequested();

        var orderedIds = categoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (orderedIds.Length == 0)
        {
            // Empty plans cite the executable's released category contract version (compatibility),
            // not a fabricated LEDGER observation of categories.
            return BudgetCategoryEvidenceResult.Ok(CategoryContractVersions.Current, []);
        }

        var listed = await ledger.ListBudgetCategoriesAsync(
            CategoryContractVersions.Current,
            actor,
            cancellationToken);

        if (!listed.IsSuccess || listed.Value is null)
        {
            return BudgetCategoryEvidenceResult.Fail(
                BudgetContractMapper.MapLedgerCompositionError(listed.Error));
        }

        if (!string.Equals(
                listed.Value.LedgerContractVersion,
                CategoryContractVersions.Current,
                StringComparison.Ordinal))
        {
            return BudgetCategoryEvidenceResult.Fail(BudgetErrors.LedgerIncompatible);
        }

        var byId = listed.Value.Items.ToDictionary(i => i.CategoryId, StringComparer.Ordinal);
        var evidence = new List<CategoryLifecycleEvidence>(orderedIds.Length);
        string? citedVersion = listed.Value.LedgerContractVersion;

        foreach (var categoryId in orderedIds)
        {
            if (byId.TryGetValue(categoryId, out var summary))
            {
                var lifecycle = BudgetContractMapper.MapLifecycle(summary.Status);
                if (mode == Mode.ValidateActive && lifecycle != CategoryLifecycleStatus.Active)
                {
                    return BudgetCategoryEvidenceResult.Fail(BudgetErrors.CategoryInactive);
                }

                if (mode == Mode.ResolveKnown && lifecycle == CategoryLifecycleStatus.Unknown)
                {
                    return BudgetCategoryEvidenceResult.Fail(BudgetErrors.Integrity);
                }

                if (!string.Equals(summary.LedgerContractVersion, CategoryContractVersions.Current, StringComparison.Ordinal))
                {
                    return BudgetCategoryEvidenceResult.Fail(BudgetErrors.LedgerIncompatible);
                }

                evidence.Add(new CategoryLifecycleEvidence(
                    summary.CategoryId,
                    summary.Name,
                    lifecycle,
                    summary.LedgerContractVersion));
                continue;
            }

            // Not on the catalogue page — precise get for unknown vs host failure.
            var got = await ledger.GetBudgetCategoryAsync(
                categoryId,
                CategoryContractVersions.Current,
                actor,
                cancellationToken);

            if (!got.IsSuccess || got.Value is null)
            {
                if (mode == Mode.EnrichSupplemental)
                {
                    // True not-found only → Unknown historical evidence. Host/compat fail closed.
                    if (IsTrueNotFound(got.Error))
                    {
                        evidence.Add(new CategoryLifecycleEvidence(
                            categoryId,
                            CurrentDisplayName: null,
                            CategoryLifecycleStatus.Unknown,
                            CategoryContractVersions.Current));
                        continue;
                    }

                    return BudgetCategoryEvidenceResult.Fail(
                        BudgetContractMapper.MapLedgerCompositionError(got.Error));
                }

                if (mode == Mode.ValidateActive)
                {
                    return BudgetCategoryEvidenceResult.Fail(BudgetContractMapper.MapMissingCategory(got.Error));
                }

                // ResolveKnown: unknown identity is integrity (cannot classify actuals).
                if (IsTrueNotFound(got.Error))
                {
                    return BudgetCategoryEvidenceResult.Fail(BudgetErrors.Integrity);
                }

                return BudgetCategoryEvidenceResult.Fail(
                    BudgetContractMapper.MapLedgerCompositionError(got.Error));
            }

            var detailLifecycle = BudgetContractMapper.MapLifecycle(got.Value.Status);
            if (mode == Mode.ValidateActive && detailLifecycle != CategoryLifecycleStatus.Active)
            {
                return BudgetCategoryEvidenceResult.Fail(BudgetErrors.CategoryInactive);
            }

            if (mode == Mode.ResolveKnown && detailLifecycle == CategoryLifecycleStatus.Unknown)
            {
                return BudgetCategoryEvidenceResult.Fail(BudgetErrors.Integrity);
            }

            if (!string.Equals(
                    got.Value.LedgerContractVersion,
                    CategoryContractVersions.Current,
                    StringComparison.Ordinal))
            {
                return BudgetCategoryEvidenceResult.Fail(BudgetErrors.LedgerIncompatible);
            }

            citedVersion ??= got.Value.LedgerContractVersion;
            evidence.Add(new CategoryLifecycleEvidence(
                got.Value.CategoryId,
                got.Value.Name,
                detailLifecycle,
                got.Value.LedgerContractVersion));
        }

        return BudgetCategoryEvidenceResult.Ok(
            citedVersion ?? CategoryContractVersions.Current,
            evidence);
    }

    private static bool IsTrueNotFound(ProcessError? error) =>
        error is not null
        && (string.Equals(error.Category, "not_found", StringComparison.Ordinal)
            || string.Equals(error.Code, "LEDGER-CATEGORY-NOT-FOUND", StringComparison.Ordinal)
            || string.Equals(error.Code, BudgetErrors.CategoryUnknown, StringComparison.Ordinal)
            || string.Equals(error.Code, BudgetErrors.NotFound, StringComparison.Ordinal));
}

public sealed record BudgetCategoryEvidenceResult(
    string? ErrorCode,
    string CategoryContractVersion,
    IReadOnlyList<CategoryLifecycleEvidence> Evidence)
{
    public static BudgetCategoryEvidenceResult Ok(
        string categoryContractVersion,
        IReadOnlyList<CategoryLifecycleEvidence> evidence) =>
        new(null, categoryContractVersion, evidence);

    public static BudgetCategoryEvidenceResult Fail(string errorCode) =>
        new(errorCode, CategoryContractVersions.Current, []);
}
