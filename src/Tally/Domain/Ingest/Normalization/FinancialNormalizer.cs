using System.Globalization;
using Tally.Contracts.Ingest;

namespace Tally.Domain.Ingest.Normalization;

public enum SourceAccountKind { Asset, Liability }

public sealed record FinancialEvidence(string? Amount, string? CurrencyCode, string? Description, bool? BalanceIncreased, string? TransactionDate, string? PostingDate, string? YearlessMonthDay, StatementPeriod? StatementPeriod);

public sealed record NormalizedFinancialFacts(long SignedAmountMinor, string CurrencyCode, string TransactionDate, string? PostingDate, string OriginalDescription);

public sealed record FinancialNormalizationResult(SourceRecordDisposition Disposition, string ReasonCode, NormalizedFinancialFacts? Facts);

public static class FinancialNormalizer
{
    public static FinancialNormalizationResult Normalize(SourceAccountKind accountKind, FinancialEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Amount)) return Blocked("amount_missing");
        if (!TryParseMinorUnits(evidence.Amount, out var amount)) return Blocked("amount_invalid");
        if (evidence.CurrencyCode != "ZAR") return Blocked("currency_unsupported");
        if (string.IsNullOrWhiteSpace(evidence.Description)) return Blocked("description_missing");
        if (evidence.BalanceIncreased is null) return Blocked("sign_missing");
        if (!TryResolveDate(evidence, out var transactionDate)) return Blocked("transaction_date_ambiguous");
        if (evidence.PostingDate is not null && !IsCanonicalDate(evidence.PostingDate)) return Blocked("posting_date_invalid");

        if (amount == 0) return new(SourceRecordDisposition.ExcludedNonTransaction, "zero_movement", null);

        var balanceMovement = evidence.BalanceIncreased.Value ? amount : checked(-amount);
        var signed = accountKind == SourceAccountKind.Asset ? balanceMovement : checked(-balanceMovement);
        return new(SourceRecordDisposition.AcceptedCandidate, "accepted", new(signed, "ZAR", transactionDate, evidence.PostingDate, evidence.Description));
    }

    private static bool TryResolveDate(FinancialEvidence evidence, out string date)
    {
        if (evidence.TransactionDate is not null)
        {
            date = evidence.TransactionDate;
            return IsCanonicalDate(date);
        }

        date = string.Empty;
        if (evidence.YearlessMonthDay is null || evidence.StatementPeriod is null || evidence.YearlessMonthDay.Length != 5 || evidence.YearlessMonthDay[2] != '-') return false;
        if (!DateOnly.TryParseExact(evidence.StatementPeriod.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) || !DateOnly.TryParseExact(evidence.StatementPeriod.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || start > end) return false;
        var matches = new List<DateOnly>();
        for (var year = start.Year; year <= end.Year; year++)
            if (DateOnly.TryParseExact($"{year:D4}-{evidence.YearlessMonthDay}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var candidate) && candidate >= start && candidate <= end) matches.Add(candidate);
        if (matches.Count != 1) return false;
        date = matches[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static FinancialNormalizationResult Blocked(string reason) => new(SourceRecordDisposition.Blocked, reason, null);

    private static bool IsCanonicalDate(string value) => value.Length == 10 && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool TryParseMinorUnits(string value, out long minorUnits)
    {
        minorUnits = 0;
        if (value.Length == 0 || value[0] is '+' or '-') return false;
        const int start = 0;
        if (start == value.Length || (value[start] == '0' && value.Length > start + 1 && value[start + 1] != '.')) return false;
        var decimalIndex = value.IndexOf('.');
        if (decimalIndex >= 0 && (decimalIndex != value.Length - 3 || decimalIndex == start)) return false;
        var integralEnd = decimalIndex < 0 ? value.Length : decimalIndex;
        for (var index = start; index < integralEnd; index++) if (value[index] is < '0' or > '9') return false;
        if (decimalIndex >= 0)
            for (var index = decimalIndex + 1; index < value.Length; index++) if (value[index] is < '0' or > '9') return false;
        var digits = decimalIndex < 0
            ? string.Concat(value.AsSpan(start), "00")
            : string.Concat(value.AsSpan(start, decimalIndex - start), value.AsSpan(decimalIndex + 1));
        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var absolute)) return false;
        if (absolute > long.MaxValue) return false;
        minorUnits = (long)absolute;
        return true;
    }

}
