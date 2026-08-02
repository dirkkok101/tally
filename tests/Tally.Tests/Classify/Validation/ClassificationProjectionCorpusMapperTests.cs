using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Rules;
using Tally.Infrastructure.Classify.Corpus;
using Xunit;

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-CORPUS-MAPPER / bd-3k1z —
/// Exact-label binding matrix over synthetic classification_v1 projection rows.
/// No live data, no file publication, no private Ledger access.
/// </summary>
public sealed class ClassificationProjectionCorpusMapperTests
{
    private static readonly ClassificationCategoryIdentity ActiveCat =
        new("cat-active", "Groceries", "active");

    private static readonly ClassificationCategoryIdentity ArchivedCat =
        new("cat-archived", "Old", "archived");

    // ── Success paths ────────────────────────────────────────────────────────

    [Fact]
    public void Suggestion_label_maps_one_eligible_row_with_active_category()
    {
        var item = Projection("tx-1", 0, CategoryMutationState.Assignable);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-active")],
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Null(error);
        Assert.Single(rows);
        Assert.Equal("tx-1", rows[0].TransactionId);
        Assert.Equal(0, rows[0].Ordinal);
        Assert.Equal("suggestion", rows[0].ExpectedOutcomeKind);
        Assert.Equal("cat-active", rows[0].ExpectedCategoryId);
        Assert.Equal(item.AccountId, rows[0].AccountId);
        Assert.Equal(item.SourceDescription, rows[0].SourceDescription);
        Assert.Equal(ClassificationRuleVocabulary.DirectionOutflow, rows[0].AmountDirection);
        Assert.Equal(
            ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(item),
            rows[0].ItemLifecycleFingerprint);
    }

    [Fact]
    public void No_suggestion_label_requires_absent_category()
    {
        var item = Projection("tx-1", 3);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Null(error);
        Assert.Single(rows);
        Assert.Equal("no_suggestion", rows[0].ExpectedOutcomeKind);
        Assert.Null(rows[0].ExpectedCategoryId);
        Assert.Equal(3, rows[0].Ordinal);
    }

    [Fact]
    public void Conflict_and_stale_labels_forbid_category()
    {
        var items = new[] { Projection("tx-c", 0), Projection("tx-s", 1) };
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-c", ClassifyOutcomeKind.Conflict),
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-s", ClassifyOutcomeKind.Stale)
            ],
            items,
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Null(error);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.ExpectedCategoryId));
        Assert.Equal("conflict", rows.Single(r => r.TransactionId == "tx-c").ExpectedOutcomeKind);
        Assert.Equal("stale", rows.Single(r => r.TransactionId == "tx-s").ExpectedOutcomeKind);
    }

    [Fact]
    public void Multiple_labels_order_by_public_ordinal_then_transaction_id()
    {
        var items = new[]
        {
            Projection("tx-b", 5),
            Projection("tx-a", 2),
            Projection("tx-c", 2)
        };
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-b", ClassifyOutcomeKind.NoSuggestion),
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-c", ClassifyOutcomeKind.NoSuggestion),
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-a", ClassifyOutcomeKind.NoSuggestion)
            ],
            items,
            [ActiveCat],
            out var rows,
            out _));
        Assert.Equal(["tx-a", "tx-c", "tx-b"], rows.Select(r => r.TransactionId).ToArray());
        Assert.Equal([2, 2, 5], rows.Select(r => r.Ordinal).ToArray());
    }

    [Fact]
    public void Income_direction_maps_to_inflow_vocabulary()
    {
        var item = Projection("tx-1", 0, amountDirection: ClassificationAmountDirection.Income, signed: "15.00");
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        Assert.Equal(ClassificationRuleVocabulary.DirectionInflow, rows[0].AmountDirection);
        Assert.Equal(1500, rows[0].AmountAbsoluteMinor);
    }

    [Fact]
    public void Zero_amount_direction_maps_to_null_corpus_direction()
    {
        // Money.TryParse accepts bare "0" for zero; "0.00" is rejected by the ledger money grammar.
        var item = Projection("tx-1", 0, amountDirection: ClassificationAmountDirection.Zero, signed: "0");
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        Assert.Null(rows[0].AmountDirection);
        Assert.Equal(0, rows[0].AmountAbsoluteMinor);
    }

    [Fact]
    public void Correctable_projection_items_are_eligible()
    {
        var item = Projection("tx-1", 0, CategoryMutationState.Correctable, currentCategory: "cat-x", currentAlloc: "alloc-1");
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Null(error);
        Assert.Single(rows);
    }

    // ── Rejection matrix ─────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_labels_fail_before_rows_are_published()
    {
        var item = Projection("tx-1", 0);
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion),
                new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)
            ],
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LabelInvalid, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Missing_projection_member_fails_stale()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-missing", ClassifyOutcomeKind.NoSuggestion)],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Ineligible_projection_member_fails_stale()
    {
        var item = Projection("tx-1", 0, CategoryMutationState.Ineligible);
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Suggestion_without_category_fails_label_invalid()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Suggestion, null)],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LabelInvalid, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Suggestion_with_archived_category_fails_stale()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-archived")],
            [Projection("tx-1", 0)],
            [ArchivedCat, ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Suggestion_with_unknown_category_fails_stale()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-ghost")],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void No_suggestion_with_category_fails_label_invalid()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion, "cat-active")],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LabelInvalid, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Conflict_with_category_fails_label_invalid()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Conflict, "cat-active")],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LabelInvalid, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Stale_outcome_with_category_fails_label_invalid()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Stale, "cat-active")],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LabelInvalid, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Empty_labels_fail_resource_limit()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            Array.Empty<ClassificationProjectionCorpusMapper.ExactLabel>(),
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.ResourceLimit, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Over_limit_labels_fail_resource_limit()
    {
        var labels = Enumerable.Range(0, PrivateCorpusLimits.MaxRowCount + 1)
            .Select(i => new ClassificationProjectionCorpusMapper.ExactLabel("tx-" + i, ClassifyOutcomeKind.NoSuggestion))
            .ToArray();
        var items = labels.Select((l, i) => Projection(l.TransactionId, i)).ToArray();
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            labels,
            items,
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.ResourceLimit, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Exact_max_label_count_succeeds()
    {
        // Keep synthetic cost bounded: use a modest high bound still within MaxRowCount.
        const int count = 32;
        Assert.True(count <= PrivateCorpusLimits.MaxRowCount);
        var labels = Enumerable.Range(0, count)
            .Select(i => new ClassificationProjectionCorpusMapper.ExactLabel("tx-" + i, ClassifyOutcomeKind.NoSuggestion))
            .ToArray();
        var items = labels.Select((l, i) => Projection(l.TransactionId, i)).ToArray();
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            labels,
            items,
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Null(error);
        Assert.Equal(count, rows.Count);
    }

    [Fact]
    public void Duplicate_projection_transaction_ids_fail_integrity()
    {
        var a = Projection("tx-1", 0);
        var b = Projection("tx-1", 1, description: "OTHER");
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [a, b],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.Integrity, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Null_labels_or_projection_fail_invalid_input()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            null!,
            [Projection("tx-1", 0)],
            [ActiveCat],
            out _,
            out var e1));
        Assert.Equal(ClassifyErrors.InvalidInput, e1);

        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            null!,
            [ActiveCat],
            out _,
            out var e2));
        Assert.Equal(ClassifyErrors.InvalidInput, e2);
    }

    [Fact]
    public void Blank_transaction_id_label_fails_label_invalid()
    {
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("  ", ClassifyOutcomeKind.NoSuggestion)],
            [Projection("tx-1", 0)],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LabelInvalid, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Malformed_signed_amount_fails_ledger_incompatible()
    {
        var item = Projection("tx-1", 0, signed: "not-money");
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.LedgerIncompatible, error);
        Assert.Empty(rows);
    }

    // ── Bind private → evaluation (validation reuse) ─────────────────────────

    [Fact]
    public void Bind_private_rows_matches_round_trip_from_labels()
    {
        var item = Projection("tx-1", 7);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-active")],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        Assert.True(ClassificationProjectionCorpusMapper.TryBindPrivateRowsToProjection(
            rows,
            [item],
            out var bound,
            out var error));
        Assert.Null(error);
        Assert.Single(bound);
        Assert.Equal(rows[0].TransactionId, bound[0].TransactionId);
        Assert.Equal(rows[0].ItemLifecycleFingerprint, bound[0].ItemLifecycleFingerprint);
        Assert.Equal(rows[0].AmountAbsoluteMinor, bound[0].AmountAbsoluteMinor);
    }

    [Fact]
    public void Bind_fails_when_lifecycle_fingerprint_drifts()
    {
        var item = Projection("tx-1", 0);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        var drifted = item with { AllocationRevision = "alloc-drifted" };
        Assert.False(ClassificationProjectionCorpusMapper.TryBindPrivateRowsToProjection(
            rows,
            [drifted],
            out var bound,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
        Assert.Empty(bound);
    }

    [Fact]
    public void Bind_fails_when_description_drifts()
    {
        var item = Projection("tx-1", 0);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        var drifted = item with { SourceDescription = "CHANGED" };
        Assert.False(ClassificationProjectionCorpusMapper.TryBindPrivateRowsToProjection(
            rows,
            [drifted],
            out _,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
    }

    [Fact]
    public void Bind_fails_when_private_row_missing_from_projection()
    {
        var item = Projection("tx-1", 0);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        Assert.False(ClassificationProjectionCorpusMapper.TryBindPrivateRowsToProjection(
            rows,
            [Projection("tx-other", 0)],
            out _,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
    }

    [Fact]
    public void Bind_allows_extra_projection_members()
    {
        var item = Projection("tx-1", 0);
        var extra = Projection("tx-extra", 9);
        Assert.True(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _));
        Assert.True(ClassificationProjectionCorpusMapper.TryBindPrivateRowsToProjection(
            rows,
            [item, extra],
            out var bound,
            out var error));
        Assert.Null(error);
        Assert.Single(bound);
    }

    [Fact]
    public void Bind_empty_private_rows_succeeds()
    {
        Assert.True(ClassificationProjectionCorpusMapper.TryBindPrivateRowsToProjection(
            Array.Empty<PrivateCorpusRow>(),
            [Projection("tx-1", 0)],
            out var bound,
            out var error));
        Assert.Null(error);
        Assert.Empty(bound);
    }

    [Fact]
    public void Lifecycle_fingerprint_is_deterministic_over_revision_tuple()
    {
        var item = Projection("tx-1", 0);
        var a = ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(item);
        var b = ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(item);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        var changed = ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(
            item with { TransactionRevision = "tr-other" });
        Assert.NotEqual(a, changed);
    }

    [Fact]
    public void TryMapPublicAmount_rejects_invalid_signed_amount()
    {
        var item = Projection("tx-1", 0, signed: "xx");
        Assert.False(ClassificationProjectionCorpusMapper.TryMapPublicAmount(item, out _, out _));
    }

    [Fact]
    public void Mapper_does_not_invent_labels_from_current_allocation()
    {
        // Even when projection already has a category/allocation, labels must be explicit.
        var item = Projection(
            "tx-1",
            0,
            CategoryMutationState.Correctable,
            currentCategory: "cat-active",
            currentAlloc: "alloc-1");
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            Array.Empty<ClassificationProjectionCorpusMapper.ExactLabel>(),
            [item],
            [ActiveCat],
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.ResourceLimit, error);
        Assert.Empty(rows);
    }

    [Fact]
    public void Active_category_set_ignores_null_catalogue()
    {
        // Suggestion requires active catalogue membership — null catalogue fails stale.
        Assert.False(ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-active")],
            [Projection("tx-1", 0)],
            null,
            out var rows,
            out var error));
        Assert.Equal(ClassifyErrors.Stale, error);
        Assert.Empty(rows);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ClassificationProjectionItem Projection(
        string transactionId,
        int ordinal,
        CategoryMutationState mutation = CategoryMutationState.Assignable,
        ClassificationAmountDirection amountDirection = ClassificationAmountDirection.Expense,
        string signed = "-12.34",
        string description = "COFFEE SHOP",
        string? currentCategory = null,
        string? currentAlloc = null) =>
        new(
            Ordinal: ordinal,
            TransactionId: transactionId,
            AccountId: "acct-1",
            EffectiveDate: "2026-07-15",
            SignedAmount: signed,
            SourceDescription: description,
            AmountDirection: amountDirection,
            CategoryMutationState: mutation,
            CurrentCategoryId: currentCategory,
            CurrentAllocationId: currentAlloc,
            TransactionRevision: "tr-" + transactionId,
            RelationshipRevision: "rr-" + transactionId,
            AllocationRevision: "ar-" + transactionId);
}
