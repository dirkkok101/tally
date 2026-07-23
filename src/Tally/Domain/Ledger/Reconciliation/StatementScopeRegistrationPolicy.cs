using Tally.Contracts.Ledger.Reconciliation;

namespace Tally.Domain.Ledger.Reconciliation;

public static class StatementScopeRegistrationPolicy
{
    public static bool TryNormalize(RegisterReconciliationScopeInput input, out NormalizedStatementScopeRegistration? normalized, out string? error)
    {
        normalized = null;
        error = ReconciliationScopeErrors.InvalidInput;
        if (!LedgerId.TryParse(input.AccountId, out _, out _)
            || !EffectiveDate.TryParse(input.PeriodStart, out var start, out _)
            || !EffectiveDate.TryParse(input.PeriodEnd, out var end, out _)
            || string.CompareOrdinal(start!.ToString(), end!.ToString()) > 0
            || !Text(input.ManifestOpaqueReference, out var manifest)
            || input.EvidenceIds is null || input.EvidenceIds.Count is 0 or > 10000)
            return false;

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in input.EvidenceIds)
            if (!LedgerId.TryParse(id, out _, out _) || !ids.Add(id)) return false;
        normalized = new(input.AccountId, start.ToString(), end.ToString(), manifest, ids.ToArray());
        error = null;
        return true;
    }

    private static bool Text(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 512 && normalized.All(character => !char.IsControl(character));
    }
}

public sealed record NormalizedStatementScopeRegistration(
    string AccountId, string PeriodStart, string PeriodEnd, string ManifestOpaqueReference, IReadOnlyList<string> EvidenceIds)
{
    public RegisterReconciliationScopeInput CanonicalInput() => new(AccountId, PeriodStart, PeriodEnd, ManifestOpaqueReference, EvidenceIds);
}
