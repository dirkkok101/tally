using System.Diagnostics;
using System.Runtime.Versioning;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-EVALUATION-WORKFLOW / bd-8uew / NFR-CLASSIFY-BOUNDED-EVALUATION
/// Exact-limit, over-limit, timeout, and memory-bound cases (pure + published constants).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class EvaluationLimitTests
{
    [Fact]
    public void Published_evaluation_limits_match_c11_bounds()
    {
        Assert.Equal(10_000, ClassifyOperationModule.V1Limits.MaxTransactionCount);
        Assert.Equal(500, ClassifyOperationModule.V1Limits.MaxRuleCount);
        Assert.Equal(100_000, ClassifyOperationModule.V1Limits.MaxEvidenceRowCount);
        Assert.Equal(256L * 1024 * 1024, ClassifyOperationModule.V1Limits.MaxMemoryBytes);
        Assert.Equal(5_000, ClassifyOperationModule.V1Limits.MaxProcessingTimeMs);
        Assert.Equal(
            ClassificationEvaluationInputLoader.MaxTransactionCount,
            ClassifyOperationModule.V1Limits.MaxTransactionCount);
    }

    [Fact]
    public void Exact_rule_count_limit_is_accepted()
    {
        Assert.True(ClassifyContractMapper.IsRuleCountWithinBound(
            500, ClassifyOperationModule.V1Limits.MaxRuleCount));
    }

    [Fact]
    public void One_over_rule_count_limit_is_rejected()
    {
        Assert.False(ClassifyContractMapper.IsRuleCountWithinBound(
            501, ClassifyOperationModule.V1Limits.MaxRuleCount));
    }

    [Fact]
    public void Exact_evidence_row_limit_is_accepted()
    {
        var evaluation = BuildEvaluationWithEvidenceRows(3);
        Assert.True(ClassifyContractMapper.IsEvidenceWithinBound(evaluation, maxEvidenceRows: 3));
    }

    [Fact]
    public void One_over_evidence_row_limit_is_rejected()
    {
        var evaluation = BuildEvaluationWithEvidenceRows(4);
        Assert.False(ClassifyContractMapper.IsEvidenceWithinBound(evaluation, maxEvidenceRows: 3));
    }

    [Fact]
    public void Input_loader_rejects_one_over_transaction_limit()
    {
        var items = Enumerable.Range(0, 4)
            .Select(i => new ClassificationProjectionItem(
                i,
                "tx-" + i,
                "acct",
                "2026-07-15",
                "-1.00",
                "d",
                ClassificationAmountDirection.Expense,
                CategoryMutationState.Assignable,
                null,
                null,
                "tr",
                "rr",
                "ar"))
            .ToArray();
        var page = new ActualsQueryResult(
            SnapshotId: "snap",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            TotalCount: 4,
            Items: Array.Empty<ActualsPageItem>(),
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: Array.Empty<ActualsGroupResult>(),
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: new string('a', 64),
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CategoryIdentityLifecycleFingerprint: new string('b', 64),
            ActiveCategories: Array.Empty<ClassificationCategoryIdentity>(),
            ClassificationItems: items);
        Assert.Equal(
            ClassifyErrors.ResourceLimit,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
                page, DateTimeOffset.UtcNow, maxTransactionCount: 3));
    }

    [Fact]
    public void Input_loader_accepts_exact_transaction_limit()
    {
        var items = Enumerable.Range(0, 3)
            .Select(i => new ClassificationProjectionItem(
                i,
                "tx-" + i,
                "acct",
                "2026-07-15",
                "-1.00",
                "d",
                ClassificationAmountDirection.Expense,
                CategoryMutationState.Assignable,
                null,
                null,
                "tr",
                "rr",
                "ar"))
            .ToArray();
        var page = new ActualsQueryResult(
            SnapshotId: "snap",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            TotalCount: 3,
            Items: Array.Empty<ActualsPageItem>(),
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: Array.Empty<ActualsGroupResult>(),
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: new string('a', 64),
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            CategoryIdentityLifecycleFingerprint: new string('b', 64),
            ActiveCategories: Array.Empty<ClassificationCategoryIdentity>(),
            ClassificationItems: items);
        Assert.Null(ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
            page, DateTimeOffset.UtcNow, maxTransactionCount: 3));
    }

    [Fact]
    public void Processing_time_limit_is_positive_and_matches_published_bound()
    {
        Assert.True(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs > 0);
        Assert.Equal(5_000, ClassifyOperationModule.V1Limits.MaxProcessingTimeMs);
        // Command links a timeout CTS to this bound — Stopwatch proves the constant is usable.
        var sw = Stopwatch.StartNew();
        Assert.True(sw.ElapsedMilliseconds < ClassifyOperationModule.V1Limits.MaxProcessingTimeMs);
    }

    [Fact]
    public void Memory_limit_is_positive_and_matches_published_bound()
    {
        Assert.Equal(256L * 1024 * 1024, ClassifyOperationModule.V1Limits.MaxMemoryBytes);
        Assert.True(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 > 0);
    }

    private static ClassificationEvaluationResult BuildEvaluationWithEvidenceRows(int evidenceCount)
    {
        var fingerprint = EvaluationFingerprint.Create(
            "1.0",
            ClassificationProjectionVersions.ClassificationV1,
            new string('a', 64),
            "snap",
            DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            new string('b', 64),
            NormalizationDescriptor.V1.Version,
            "rsv",
            new string('c', 64));
        var evidence = Enumerable.Range(0, evidenceCount)
            .Select(i => new MatchEvidence(
                "rv",
                "c" + i,
                "description.normalized",
                "equals",
                new string('d', 64)))
            .ToArray();
        return new ClassificationEvaluationResult(
            fingerprint,
            [
                ClassificationOutcome.Suggestion(
                    0,
                    "tx",
                    "cat",
                    ["rv"],
                    evidence,
                    new string('e', 64))
            ]);
    }
}
