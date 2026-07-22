using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Domain.Ledger.Reconciliation;

public static class StatementCoveragePolicy
{
    public const string PolicyId = "statement-coverage-v1";
    public const string PolicyVersion = "1.0";
    public const string RecordedAbsentReason = "not_found_in_completed_statement_scope";

    public static bool TryNormalize(
        CompleteStatementCoverageInput input,
        out NormalizedStatementCoverage? normalized,
        out string? error)
    {
        normalized = null;
        error = ReconciliationCoverageErrors.InvalidInput;
        if (!LedgerId.TryParse(input.ScopeId, out _, out _)
            || !LedgerId.TryParse(input.AccountId, out _, out _)
            || !EffectiveDate.TryParse(input.PeriodStart, out var periodStart, out _)
            || !EffectiveDate.TryParse(input.PeriodEnd, out var periodEnd, out _)
            || string.CompareOrdinal(periodStart!.ToString(), periodEnd!.ToString()) > 0
            || !TryText(input.ManifestOpaqueReference, 512, out var manifestReference)
            || input.ExpectedEvidenceIds is null
            || input.ExpectedEvidenceIds.Count is 0 or > 10000)
        {
            return false;
        }

        if (!string.Equals(input.PolicyId, PolicyId, StringComparison.Ordinal)
            || !string.Equals(input.PolicyVersion, PolicyVersion, StringComparison.Ordinal))
        {
            error = ReconciliationCoverageErrors.PolicyUnsupported;
            return false;
        }

        var evidenceIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var evidenceId in input.ExpectedEvidenceIds)
        {
            if (!LedgerId.TryParse(evidenceId, out _, out _) || !evidenceIds.Add(evidenceId)) return false;
        }

        normalized = new(
            input.ScopeId,
            input.AccountId,
            periodStart.ToString(),
            periodEnd.ToString(),
            manifestReference,
            evidenceIds.ToArray(),
            PolicyId,
            PolicyVersion);
        error = null;
        return true;
    }

    public static bool TryNormalizeGet(GetStatementCoverageInput input, out string? scopeId)
    {
        scopeId = null;
        if (!LedgerId.TryParse(input.ScopeId, out _, out _)) return false;
        scopeId = input.ScopeId;
        return true;
    }

    public static string? ValidateScope(
        NormalizedStatementCoverage input,
        StatementCoverageScope scope,
        IReadOnlyList<string> evidenceIds)
    {
        if (scope.Status == "open") return ReconciliationCoverageErrors.ScopeIncomplete;
        if (scope.Status != "completed") return ReconciliationCoverageErrors.ScopeInactive;
        if (!string.Equals(input.AccountId, scope.AccountId, StringComparison.Ordinal)
            || !string.Equals(input.PeriodStart, scope.PeriodStart, StringComparison.Ordinal)
            || !string.Equals(input.PeriodEnd, scope.PeriodEnd, StringComparison.Ordinal)
            || !string.Equals(input.ManifestOpaqueReference, scope.ManifestOpaqueReference, StringComparison.Ordinal))
        {
            return ReconciliationCoverageErrors.ScopeConflict;
        }

        return input.ExpectedEvidenceIds.SequenceEqual(evidenceIds, StringComparer.Ordinal)
            ? null
            : ReconciliationCoverageErrors.EvidenceSetChanged;
    }

    public static string? ValidateRows(
        string scopeId,
        IReadOnlyList<StatementCoverageDecision> decisions,
        IReadOnlySet<string> eligibleTransactionIds)
    {
        if (decisions.Any(decision => decision.DecisionId is null))
            return ReconciliationCoverageErrors.MissingOutcome;
        var expectedAuthorityPrefix = "scope:" + scopeId + "|";
        if (decisions.Any(decision => decision.StatementAuthorityBasis is null
            || !decision.StatementAuthorityBasis.StartsWith(expectedAuthorityPrefix, StringComparison.Ordinal)))
        {
            return ReconciliationCoverageErrors.ScopeConflict;
        }

        var coveredEligible = decisions
            .SelectMany(CoveredTransactionIds)
            .Where(eligibleTransactionIds.Contains)
            .ToArray();
        return coveredEligible.Length == coveredEligible.Distinct(StringComparer.Ordinal).Count()
            ? null
            : ReconciliationCoverageErrors.DuplicateTransactionOutcome;
    }

    public static IEnumerable<string> CoveredTransactionIds(StatementCoverageDecision decision)
    {
        if (decision.ActiveTransactionId is not null) yield return decision.ActiveTransactionId;
        if (decision.Outcome == StatementCoverageOutcome.CorrectedFromStatement
            && decision.PriorTransactionId is not null)
        {
            yield return decision.PriorTransactionId;
        }
    }

    private static bool TryText(string? value, int maximum, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maximum
            && normalized.All(character => !char.IsControl(character));
    }
}

public sealed record NormalizedStatementCoverage(
    string ScopeId,
    string AccountId,
    string PeriodStart,
    string PeriodEnd,
    string ManifestOpaqueReference,
    IReadOnlyList<string> ExpectedEvidenceIds,
    string PolicyId,
    string PolicyVersion)
{
    public CompleteStatementCoverageInput CanonicalInput() => new(
        ScopeId,
        AccountId,
        PeriodStart,
        PeriodEnd,
        ManifestOpaqueReference,
        ExpectedEvidenceIds,
        PolicyId,
        PolicyVersion);
}

public sealed record StatementCoverageScope(
    string ScopeId,
    string AccountId,
    string PeriodStart,
    string PeriodEnd,
    string ManifestOpaqueReference,
    string Status,
    string CreatedAt);

public sealed record StatementCoverageDecision(
    string EvidenceId,
    string? DecisionId,
    string? PriorTransactionId,
    string? ActiveTransactionId,
    StatementCoverageOutcome Outcome,
    string Reason,
    string? DecidedAt,
    string? StatementAuthorityBasis);
