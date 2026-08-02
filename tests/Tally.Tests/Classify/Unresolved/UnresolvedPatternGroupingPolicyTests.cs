using System.Reflection;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Classify.Unresolved;
using Xunit;

namespace Tally.Tests.Classify.Unresolved;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-POLICY / bd-elq8 —
/// Pure grouping, ordering, top-N, overflow, fingerprint, and disclosure proofs.
/// Synthetic values only; no Ledger/SQLite/live data.
/// </summary>
public sealed class UnresolvedPatternGroupingPolicyTests
{
    private const string Norm = "normalization_v1";

    // ── Grouping / key equality ──────────────────────────────────────────────

    [Fact]
    public void Identical_keys_merge_counts_and_checked_totals()
    {
        var rows = new[]
        {
            Row("coffee", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -100),
            Row("coffee", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -250),
            Row("coffee", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -50)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, topN: 10, minimumCount: 2, out var result, out var error));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Single(result!.Groups);
        var g = result.Groups[0];
        Assert.Equal(3, g.TransactionCount);
        Assert.Equal(-400, g.CheckedSignedAmountMinorTotal);
        Assert.Equal(400, g.CheckedAbsoluteAmountMinorTotal);
        Assert.Equal("coffee", g.NormalizedDescription);
        Assert.Equal(1, g.Rank);
    }

    [Fact]
    public void Different_description_account_or_direction_split_groups()
    {
        var rows = new[]
        {
            Row("coffee", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -10),
            Row("tea", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -10),
            Row("coffee", "acct-b", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -10),
            Row("coffee", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Income, 10),
            Row("coffee", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -20),
            Row("tea", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -5)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var result, out _));
        // Groups with count>=2: coffee/acct-a/expense (2), tea/acct-a/expense (2)
        Assert.Equal(2, result!.DistinctGroupCount);
        Assert.Equal(2, result.ReturnedGroupCount);
        Assert.Equal(4, result.CandidateRowCount);
        Assert.Equal(2, result.BelowMinimumRowCount); // coffee/acct-b and coffee/income each count 1
    }

    [Fact]
    public void Ordinal_equality_is_case_and_culture_sensitive()
    {
        var rows = new[]
        {
            Row("Coffee", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("coffee", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("Coffee", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("coffee", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var result, out _));
        Assert.Equal(2, result!.DistinctGroupCount);
        Assert.All(result.Groups, g => Assert.Equal(2, g.TransactionCount));
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    [Fact]
    public void Orders_by_count_desc_then_description_account_direction_ordinal()
    {
        var rows = new[]
        {
            Row("zeta", "acct-b", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("zeta", "acct-b", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("alpha", "acct-b", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("alpha", "acct-b", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("alpha", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("alpha", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("alpha", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Income, 1),
            Row("alpha", "acct-a", UnresolvedPatternGroupingPolicy.AmountDirections.Income, 1),
            // higher count group
            Row("mid", "acct-z", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("mid", "acct-z", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("mid", "acct-z", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var result, out _));
        Assert.Equal(5, result!.Groups.Count);
        Assert.Equal("mid", result.Groups[0].NormalizedDescription);
        Assert.Equal(3, result.Groups[0].TransactionCount);
        Assert.Equal(1, result.Groups[0].Rank);
        // Remaining count=2 groups ordered by description, account, direction
        Assert.Equal("alpha", result.Groups[1].NormalizedDescription);
        Assert.Equal("acct-a", result.Groups[1].AccountId);
        Assert.Equal(UnresolvedPatternGroupingPolicy.AmountDirections.Expense, result.Groups[1].AmountDirection);
        Assert.Equal("alpha", result.Groups[2].NormalizedDescription);
        Assert.Equal("acct-a", result.Groups[2].AccountId);
        Assert.Equal(UnresolvedPatternGroupingPolicy.AmountDirections.Income, result.Groups[2].AmountDirection);
        Assert.Equal("alpha", result.Groups[3].NormalizedDescription);
        Assert.Equal("acct-b", result.Groups[3].AccountId);
        Assert.Equal("zeta", result.Groups[4].NormalizedDescription);
    }

    [Fact]
    public void Input_permutation_does_not_change_order_or_fingerprints()
    {
        var baseRows = new[]
        {
            Row("b", "a2", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -30),
            Row("a", "a1", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -10),
            Row("a", "a1", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -20),
            Row("b", "a2", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -40),
            Row("b", "a2", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -5)
        };
        var shuffled = baseRows.Reverse().ToArray();
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(baseRows, 10, 2, out var r1, out _));
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(shuffled, 10, 2, out var r2, out _));
        Assert.Equal(r1!.ReportFingerprint, r2!.ReportFingerprint);
        Assert.Equal(
            r1.Groups.Select(g => g.GroupFingerprint).ToArray(),
            r2.Groups.Select(g => g.GroupFingerprint).ToArray());
        Assert.Equal(
            r1.Groups.Select(g => (g.NormalizedDescription, g.TransactionCount, g.CheckedSignedAmountMinorTotal)).ToArray(),
            r2.Groups.Select(g => (g.NormalizedDescription, g.TransactionCount, g.CheckedSignedAmountMinorTotal)).ToArray());
    }

    // ── Top-N / minimumCount ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void Top_n_accepts_exact_bounds(int topN)
    {
        var rows = Enumerable.Range(0, 4)
            .SelectMany(i => new[]
            {
                Row("g" + i, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
                Row("g" + i, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
            })
            .ToArray();
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, topN, 2, out var result, out var error));
        Assert.Null(error);
        Assert.Equal(Math.Min(topN, 4), result!.ReturnedGroupCount);
        Assert.Equal(4, result.DistinctGroupCount);
        Assert.Equal(result.DistinctGroupCount - result.ReturnedGroupCount, result.OmittedGroupCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(-1)]
    public void Top_n_rejects_one_under_and_one_over(int topN)
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [Row("a", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)],
            topN,
            2,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.ResourceLimit, error);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(500)]
    public void Minimum_count_accepts_exact_bounds(int minimumCount)
    {
        var rows = Enumerable.Range(0, minimumCount)
            .Select(_ => Row("x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1))
            .ToArray();
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, minimumCount, out var result, out var error));
        Assert.Null(error);
        Assert.Equal(1, result!.DistinctGroupCount);
        Assert.Equal(minimumCount, result.CandidateRowCount);
        Assert.Equal(0, result.BelowMinimumRowCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(501)]
    public void Minimum_count_rejects_one_under_and_one_over(int minimumCount)
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [Row("a", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
             Row("a", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)],
            10,
            minimumCount,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.ResourceLimit, error);
        Assert.Null(result);
    }

    [Fact]
    public void Groups_below_minimum_count_are_excluded_from_candidates()
    {
        var rows = new[]
        {
            Row("keep", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("keep", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("drop", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var result, out _));
        Assert.Single(result!.Groups);
        Assert.Equal("keep", result.Groups[0].NormalizedDescription);
        Assert.Equal(2, result.CandidateRowCount);
        Assert.Equal(1, result.BelowMinimumRowCount);
        Assert.Equal(3, result.NoSuggestionOutcomeCount);
        Assert.Equal(3, result.JoinedRowCount);
        Assert.Equal(result.NoSuggestionOutcomeCount, result.JoinedRowCount);
        Assert.Equal(result.JoinedRowCount, result.CandidateRowCount + result.BelowMinimumRowCount);
    }

    [Fact]
    public void Top_n_truncation_sets_omitted_group_count()
    {
        var rows = Enumerable.Range(0, 5)
            .SelectMany(i => new[]
            {
                Row("g" + i, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
                Row("g" + i, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
            })
            .ToArray();
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, topN: 2, minimumCount: 2, out var result, out _));
        Assert.Equal(5, result!.DistinctGroupCount);
        Assert.Equal(2, result.ReturnedGroupCount);
        Assert.Equal(3, result.OmittedGroupCount);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(1, result.Groups[0].Rank);
        Assert.Equal(2, result.Groups[1].Rank);
    }

    // ── Fingerprints ─────────────────────────────────────────────────────────

    [Fact]
    public void Identical_inputs_yield_identical_group_and_report_fingerprints()
    {
        var rows = new[]
        {
            Row("shop", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -100),
            Row("shop", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -50)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var a, out _));
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var b, out _));
        Assert.Equal(a!.Groups[0].GroupFingerprint, b!.Groups[0].GroupFingerprint);
        Assert.Equal(a.ReportFingerprint, b.ReportFingerprint);
        Assert.Equal(64, a.Groups[0].GroupFingerprint.Length);
        Assert.Equal(64, a.ReportFingerprint.Length);
    }

    [Fact]
    public void Group_fingerprint_changes_when_count_or_totals_change()
    {
        var a = UnresolvedPatternFingerprint.ForGroup(
            Norm, "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 2, -30, 30);
        var b = UnresolvedPatternFingerprint.ForGroup(
            Norm, "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 3, -30, 30);
        var c = UnresolvedPatternFingerprint.ForGroup(
            Norm, "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 2, -31, 31);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Group_fingerprint_changes_when_key_dimension_changes()
    {
        var baseFp = UnresolvedPatternFingerprint.ForGroup(
            Norm, "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 2, -10, 10);
        Assert.NotEqual(
            baseFp,
            UnresolvedPatternFingerprint.ForGroup(
                Norm, "y", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 2, -10, 10));
        Assert.NotEqual(
            baseFp,
            UnresolvedPatternFingerprint.ForGroup(
                Norm, "x", "other", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 2, -10, 10));
        Assert.NotEqual(
            baseFp,
            UnresolvedPatternFingerprint.ForGroup(
                Norm, "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Income, 2, -10, 10));
    }

    [Fact]
    public void Report_fingerprint_changes_when_top_n_bound_changes()
    {
        var rows = Enumerable.Range(0, 4)
            .SelectMany(i => new[]
            {
                Row("g" + i, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
                Row("g" + i, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
            })
            .ToArray();
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 2, 2, out var a, out _));
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 3, 2, out var b, out _));
        Assert.NotEqual(a!.ReportFingerprint, b!.ReportFingerprint);
    }

    // ── Failures: invalid input / overflow / no partial groups ───────────────

    [Fact]
    public void Empty_normalization_version_fails_with_no_partial_groups()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [new UnresolvedPatternGroupingPolicy.JoinedRow("", "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)],
            10,
            2,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.InvalidInput, error);
        Assert.Null(result);
    }

    [Fact]
    public void Whitespace_normalization_version_fails()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [new UnresolvedPatternGroupingPolicy.JoinedRow("  ", "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)],
            10,
            2,
            out _,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.InvalidInput, error);
    }

    [Fact]
    public void Invalid_amount_direction_fails()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [Row("x", "acct", "sideways", -1)],
            10,
            2,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.InvalidInput, error);
        Assert.Null(result);
    }

    [Fact]
    public void Empty_account_id_fails()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [new UnresolvedPatternGroupingPolicy.JoinedRow(Norm, "x", "", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)],
            10,
            2,
            out _,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.InvalidInput, error);
    }

    [Fact]
    public void Null_normalized_description_fails()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [new UnresolvedPatternGroupingPolicy.JoinedRow(Norm, null!, "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)],
            10,
            2,
            out _,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.InvalidInput, error);
    }

    [Fact]
    public void Mixed_normalization_versions_fail_integrity()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [
                new UnresolvedPatternGroupingPolicy.JoinedRow("norm_a", "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
                new UnresolvedPatternGroupingPolicy.JoinedRow("norm_b", "x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
            ],
            10,
            2,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.Integrity, error);
        Assert.Null(result);
    }

    [Fact]
    public void Null_rows_argument_fails()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(null!, 10, 2, out var result, out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.InvalidInput, error);
        Assert.Null(result);
    }

    [Fact]
    public void Checked_signed_total_overflow_fails_with_no_partial_groups()
    {
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [
                Row("x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, long.MaxValue),
                Row("x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, 1)
            ],
            10,
            2,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.ResourceLimit, error);
        Assert.Null(result);
    }

    [Fact]
    public void Checked_absolute_total_handles_long_min_value_without_throw()
    {
        // Abs(long.MinValue) is replaced by long.MaxValue in policy; two MinValue would overflow abs sum.
        Assert.False(UnresolvedPatternGroupingPolicy.TryGroup(
            [
                Row("x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, long.MinValue),
                Row("x", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, long.MinValue)
            ],
            10,
            2,
            out var result,
            out var error));
        Assert.Equal(UnresolvedPatternGroupingPolicy.ErrorCodes.ResourceLimit, error);
        Assert.Null(result);
    }

    [Fact]
    public void Empty_row_list_succeeds_with_zero_groups()
    {
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(
            Array.Empty<UnresolvedPatternGroupingPolicy.JoinedRow>(),
            10,
            2,
            out var result,
            out var error));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Empty(result!.Groups);
        Assert.Equal(0, result.NoSuggestionOutcomeCount);
        Assert.Equal(0, result.JoinedRowCount);
        Assert.Equal(0, result.CandidateRowCount);
        Assert.Equal(0, result.BelowMinimumRowCount);
        Assert.Equal(0, result.DistinctGroupCount);
        Assert.Equal(0, result.ReturnedGroupCount);
        Assert.Equal(0, result.OmittedGroupCount);
        Assert.Equal(64, result.ReportFingerprint.Length);
    }

    // ── Disclosure / privacy shape ───────────────────────────────────────────

    [Fact]
    public void Group_type_exposes_no_transaction_id_path_rule_or_feedback_members()
    {
        var names = typeof(UnresolvedPatternGroupingPolicy.Group)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Group.NormalizedDescription), names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Group.AccountId), names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Group.AmountDirection), names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Group.TransactionCount), names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Group.GroupFingerprint), names);
        Assert.DoesNotContain("TransactionId", names);
        Assert.DoesNotContain("TransactionIds", names);
        Assert.DoesNotContain("SourcePath", names);
        Assert.DoesNotContain("Path", names);
        Assert.DoesNotContain("RuleVersionId", names);
        Assert.DoesNotContain("ProposalId", names);
        Assert.DoesNotContain("ActivationId", names);
        Assert.DoesNotContain("FeedbackId", names);
        Assert.DoesNotContain("Artifact", names);
        Assert.DoesNotContain("CorpusPath", names);
    }

    [Fact]
    public void Joined_row_type_exposes_no_transaction_id_or_source_path()
    {
        var names = typeof(UnresolvedPatternGroupingPolicy.JoinedRow)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("TransactionId", names);
        Assert.DoesNotContain("SourceDescription", names);
        Assert.DoesNotContain("Path", names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.JoinedRow.NormalizedDescription), names);
    }

    [Fact]
    public void Success_result_has_no_rule_authority_or_durable_artifact_fields()
    {
        var names = typeof(UnresolvedPatternGroupingPolicy.Success)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("RuleSetVersionId", names);
        Assert.DoesNotContain("ProposalId", names);
        Assert.DoesNotContain("ActivationId", names);
        Assert.DoesNotContain("ArtifactPath", names);
        Assert.DoesNotContain("Persist", names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Success.ReportFingerprint), names);
        Assert.Contains(nameof(UnresolvedPatternGroupingPolicy.Success.Groups), names);
    }

    [Fact]
    public void Accepts_closed_rule_vocabulary_direction_aliases()
    {
        var rows = new[]
        {
            Row("x", "acct", ClassificationRuleVocabulary.DirectionOutflow, -10),
            Row("x", "acct", ClassificationRuleVocabulary.DirectionOutflow, -20)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var result, out var error));
        Assert.Null(error);
        Assert.Single(result!.Groups);
        Assert.Equal(ClassificationRuleVocabulary.DirectionOutflow, result.Groups[0].AmountDirection);
    }

    [Fact]
    public void Accounting_identity_holds_for_mixed_minimum_partition()
    {
        var rows = new[]
        {
            Row("a", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("a", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("a", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("b", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("c", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1),
            Row("c", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Expense, -1)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, topN: 1, minimumCount: 2, out var result, out _));
        Assert.Equal(6, result!.NoSuggestionOutcomeCount);
        Assert.Equal(6, result.JoinedRowCount);
        Assert.Equal(5, result.CandidateRowCount); // a:3 + c:2
        Assert.Equal(1, result.BelowMinimumRowCount); // b:1
        Assert.Equal(2, result.DistinctGroupCount);
        Assert.Equal(1, result.ReturnedGroupCount);
        Assert.Equal(1, result.OmittedGroupCount);
        Assert.Equal(result.JoinedRowCount, result.CandidateRowCount + result.BelowMinimumRowCount);
        Assert.Equal(result.DistinctGroupCount, result.ReturnedGroupCount + result.OmittedGroupCount);
    }

    [Fact]
    public void Zero_direction_is_valid_closed_vocabulary()
    {
        var rows = new[]
        {
            Row("z", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Zero, 0),
            Row("z", "acct", UnresolvedPatternGroupingPolicy.AmountDirections.Zero, 0)
        };
        Assert.True(UnresolvedPatternGroupingPolicy.TryGroup(rows, 10, 2, out var result, out _));
        Assert.Single(result!.Groups);
        Assert.Equal(0, result.Groups[0].CheckedSignedAmountMinorTotal);
        Assert.Equal(0, result.Groups[0].CheckedAbsoluteAmountMinorTotal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static UnresolvedPatternGroupingPolicy.JoinedRow Row(
        string description,
        string accountId,
        string direction,
        long signedMinor) =>
        new(Norm, description, accountId, direction, signedMinor);
}
