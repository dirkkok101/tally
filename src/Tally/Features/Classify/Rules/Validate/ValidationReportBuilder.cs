using System.Globalization;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Features.Classify.Rules.Validate;

/// <summary>
/// Pure aggregate validation report construction
/// (DM-CLASSIFY-VALIDATION-RUN / FR-CLASSIFY-RULE-VALIDATION).
/// Compares engine outcomes to expected labels without retaining private payloads.
/// </summary>
public static class ValidationReportBuilder
{
    /// <summary>
    /// Build an aggregate-only report from ordered engine outcomes and private expected labels.
    /// Expected labels are consumed only for canary arithmetic and never written to the report.
    /// </summary>
    public static BuiltValidationReport Build(
        string validationRunId,
        IReadOnlyList<PrivateCorpusRow> corpusRows,
        ClassificationEvaluationResult evaluation,
        int ownerDecisionCountBefore = 0,
        int ownerDecisionCountAfter = 0,
        double? ownerMinutesBefore = null,
        double? ownerMinutesAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationRunId);
        ArgumentNullException.ThrowIfNull(corpusRows);
        ArgumentNullException.ThrowIfNull(evaluation);

        var totalRows = corpusRows.Count;
        var outcomes = evaluation.Outcomes;
        var accountedRows = outcomes.Count;

        // Exact once accounting: engine emits one outcome per input row when ordinals are contiguous.
        if (accountedRows != totalRows)
        {
            // Still produce a non-activating report; eligibility will fail.
        }

        var expectedByOrdinal = corpusRows.ToDictionary(r => r.Ordinal);
        var incorrect = 0;
        var unexplainedConflicts = 0;
        var drift = 0;

        foreach (var outcome in outcomes)
        {
            if (outcome.Kind == ClassificationOutcomeKind.Stale)
            {
                drift++;
            }

            if (!expectedByOrdinal.TryGetValue(outcome.Ordinal, out var expected))
            {
                // Unexpected ordinal — treat as drift canary (membership change).
                drift++;
                continue;
            }

            if (IsIncorrectApplication(outcome, expected))
            {
                incorrect++;
            }

            if (IsUnexplainedConflict(outcome, expected))
            {
                unexplainedConflicts++;
            }
        }

        var suggestionCount = evaluation.SuggestionCount;
        var noSuggestionCount = evaluation.NoSuggestionCount;
        var conflictCount = evaluation.ConflictCount;
        var staleCount = evaluation.StaleCount;
        var coverageBasisPoints = totalRows == 0
            ? 0
            : (int)Math.Min(10_000L, (long)suggestionCount * 10_000L / totalRows);

        var activationEligible =
            totalRows == accountedRows
            && incorrect == 0
            && unexplainedConflicts == 0
            && drift == 0
            && totalRows == suggestionCount + noSuggestionCount + conflictCount + staleCount;

        var reportFingerprint = CanonicalClassificationHasher.HashParts(
            validationRunId,
            totalRows.ToString(CultureInfo.InvariantCulture),
            accountedRows.ToString(CultureInfo.InvariantCulture),
            suggestionCount.ToString(CultureInfo.InvariantCulture),
            noSuggestionCount.ToString(CultureInfo.InvariantCulture),
            conflictCount.ToString(CultureInfo.InvariantCulture),
            staleCount.ToString(CultureInfo.InvariantCulture),
            coverageBasisPoints.ToString(CultureInfo.InvariantCulture),
            drift.ToString(CultureInfo.InvariantCulture),
            incorrect.ToString(CultureInfo.InvariantCulture),
            unexplainedConflicts.ToString(CultureInfo.InvariantCulture),
            ownerDecisionCountBefore.ToString(CultureInfo.InvariantCulture),
            ownerDecisionCountAfter.ToString(CultureInfo.InvariantCulture),
            ownerMinutesBefore?.ToString(CultureInfo.InvariantCulture),
            ownerMinutesAfter?.ToString(CultureInfo.InvariantCulture));

        var row = new ClassificationValidationReportRow(
            validationRunId,
            totalRows,
            accountedRows,
            suggestionCount,
            noSuggestionCount,
            conflictCount,
            staleCount,
            coverageBasisPoints,
            drift,
            incorrect,
            unexplainedConflicts,
            ownerDecisionCountBefore,
            ownerDecisionCountAfter,
            ownerMinutesBefore,
            ownerMinutesAfter,
            reportFingerprint);

        return new BuiltValidationReport(row, activationEligible);
    }

    /// <summary>
    /// Byte-stable fingerprint over ordered expected labels only (not raw descriptions/amounts).
    /// </summary>
    public static string ComputeExpectedOutcomeFingerprint(IReadOnlyList<PrivateCorpusRow> rows) =>
        CanonicalClassificationHasher.HashOrderedLines(
            rows
                .OrderBy(r => r.Ordinal)
                .ThenBy(r => r.TransactionId, StringComparer.Ordinal)
                .Select(r => string.Concat(
                    r.Ordinal.ToString(CultureInfo.InvariantCulture),
                    '\t',
                    r.ExpectedOutcomeKind ?? "",
                    '\t',
                    r.ExpectedCategoryId ?? "")));

    /// <summary>
    /// Byte-stable fingerprint over immutable candidate rule versions (ids + scope + category + origin).
    /// </summary>
    public static string ComputeCandidateFingerprint(
        IReadOnlyList<(string RuleVersionId, string CategoryId, string ScopeHash, string NormalizationVersion, string RuleOrigin)> candidates) =>
        CanonicalClassificationHasher.HashOrderedLines(
            candidates
                .OrderBy(c => c.RuleVersionId, StringComparer.Ordinal)
                .Select(c => string.Concat(
                    c.RuleVersionId, '\t',
                    c.CategoryId, '\t',
                    c.ScopeHash, '\t',
                    c.NormalizationVersion, '\t',
                    c.RuleOrigin)));

    private static bool IsIncorrectApplication(ClassificationOutcome outcome, PrivateCorpusRow expected)
    {
        // Incorrect application: engine selected a category that disagrees with an expected label.
        if (outcome.Kind == ClassificationOutcomeKind.Suggestion)
        {
            if (string.Equals(expected.ExpectedOutcomeKind, "no_suggestion", StringComparison.Ordinal)
                || string.Equals(expected.ExpectedOutcomeKind, "conflict", StringComparison.Ordinal)
                || string.Equals(expected.ExpectedOutcomeKind, "stale", StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(expected.ExpectedCategoryId)
                && !string.Equals(outcome.CategoryId, expected.ExpectedCategoryId, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(expected.ExpectedOutcomeKind, "suggestion", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(expected.ExpectedCategoryId)
                && !string.Equals(outcome.CategoryId, expected.ExpectedCategoryId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Expected a specific category suggestion but got none.
        if (string.Equals(expected.ExpectedOutcomeKind, "suggestion", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(expected.ExpectedCategoryId)
            && outcome.Kind != ClassificationOutcomeKind.Suggestion)
        {
            return true;
        }

        return false;
    }

    private static bool IsUnexplainedConflict(ClassificationOutcome outcome, PrivateCorpusRow expected)
    {
        if (outcome.Kind != ClassificationOutcomeKind.Conflict)
        {
            return false;
        }

        // Declared expected conflict is explained (still counted in conflict_count, not unexplained).
        if (string.Equals(expected.ExpectedOutcomeKind, "conflict", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}

/// <summary>Aggregate report plus activation eligibility (never grants authority by itself).</summary>
public sealed record BuiltValidationReport(
    ClassificationValidationReportRow Report,
    bool ActivationEligible);
