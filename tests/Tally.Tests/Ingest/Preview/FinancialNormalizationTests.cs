using Tally.Contracts.Ingest;
using Tally.Domain.Ingest.Normalization;
using Xunit;

namespace Tally.Tests.Ingest.Preview;

public sealed class FinancialNormalizationTests
{
    // TC-INGEST-FINANCIAL-NORMALIZATION-CONTRACT / FR-INGEST-FINANCIAL-NORMALIZATION
    [Theory]
    [InlineData(SourceAccountKind.Asset, true, "12.34", 1234)]
    [InlineData(SourceAccountKind.Asset, false, "12.34", -1234)]
    [InlineData(SourceAccountKind.Liability, true, "12.34", -1234)]
    [InlineData(SourceAccountKind.Liability, false, "12.34", 1234)]
    public void FR_INGEST_FINANCIAL_NORMALIZATION_owner_economic_sign_is_exact(SourceAccountKind kind, bool increased, string amount, long expected)
    {
        var result = FinancialNormalizer.Normalize(kind, Evidence(amount, increased));

        Assert.Equal(SourceRecordDisposition.AcceptedCandidate, result.Disposition);
        Assert.Equal(expected, result.Facts!.SignedAmountMinor);
        Assert.Equal("ZAR", result.Facts.CurrencyCode);
    }

    // TC-INGEST-FINANCIAL-NORMALIZATION-CONTRACT
    [Theory]
    [InlineData("0", "zero_movement")]
    [InlineData("0.00", "zero_movement")]
    [InlineData("-1.00", "amount_invalid")]
    [InlineData("1.2", "amount_invalid")]
    [InlineData("1.234", "amount_invalid")]
    public void TC_INGEST_FINANCIAL_NORMALIZATION_zero_or_lossy_movement_is_not_a_candidate(string amount, string reason)
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, Evidence(amount, true));

        Assert.Equal(reason == "zero_movement" ? SourceRecordDisposition.ExcludedNonTransaction : SourceRecordDisposition.Blocked, result.Disposition);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Null(result.Facts);
    }

    // FR-INGEST-FINANCIAL-NORMALIZATION
    [Theory]
    [InlineData(null, "ZAR", "description", true, "2026-07-01", "amount_missing")]
    [InlineData("1.00", "USD", "description", true, "2026-07-01", "currency_unsupported")]
    [InlineData("1.00", "ZAR", "", true, "2026-07-01", "description_missing")]
    [InlineData("1.00", "ZAR", "description", null, "2026-07-01", "sign_missing")]
    [InlineData("1.00", "ZAR", "description", true, null, "transaction_date_ambiguous")]
    public void FR_INGEST_FINANCIAL_NORMALIZATION_missing_or_ambiguous_facts_fail_closed(string? amount, string currency, string description, bool? increased, string? transactionDate, string reason)
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, new(amount, currency, description, increased, transactionDate, null, null, null));

        Assert.Equal(SourceRecordDisposition.Blocked, result.Disposition);
        Assert.Equal(reason, result.ReasonCode);
    }

    // FR-INGEST-FINANCIAL-NORMALIZATION
    [Theory]
    [InlineData("07-01", "2026-06-30", "2026-07-02", "2026-07-01")]
    [InlineData("02-29", "2024-02-28", "2024-03-01", "2024-02-29")]
    public void FR_INGEST_FINANCIAL_NORMALIZATION_yearless_date_resolves_only_inside_explicit_period(string yearless, string start, string end, string expected)
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, new("1.00", "ZAR", "D", true, null, null, yearless, new(start, end)));

        Assert.Equal(expected, result.Facts!.TransactionDate);
    }

    // FR-INGEST-FINANCIAL-NORMALIZATION
    [Theory]
    [InlineData("07-01", "2025-01-01", "2026-12-31")]
    [InlineData("02-29", "2025-01-01", "2025-12-31")]
    public void FR_INGEST_FINANCIAL_NORMALIZATION_ambiguous_or_absent_yearless_date_blocks(string yearless, string start, string end)
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, new("1.00", "ZAR", "D", true, null, null, yearless, new(start, end)));

        Assert.Equal(SourceRecordDisposition.Blocked, result.Disposition);
        Assert.Equal("transaction_date_ambiguous", result.ReasonCode);
    }

    // DM-INGEST-IMPORT-MANIFEST
    [Fact]
    public void DM_INGEST_IMPORT_MANIFEST_posting_date_is_optional_and_distinct_from_effective_date()
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, Evidence("1.00", true) with { PostingDate = "2026-07-02" });

        Assert.Equal("2026-07-01", result.Facts!.TransactionDate);
        Assert.Equal("2026-07-02", result.Facts.PostingDate);
    }

    // FR-INGEST-FINANCIAL-NORMALIZATION / DM-INGEST-IMPORT-MANIFEST: supplied source facts are preserved even when equal.
    [Fact]
    public void FR_INGEST_FINANCIAL_NORMALIZATION_supplied_equal_posting_date_remains_distinct_source_fact()
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, Evidence("1.00", true) with { PostingDate = "2026-07-01" });

        Assert.Equal("2026-07-01", result.Facts!.TransactionDate);
        Assert.Equal("2026-07-01", result.Facts.PostingDate);
    }

    // FR-INGEST-FINANCIAL-NORMALIZATION / DM-INGEST-IMPORT-MANIFEST
    [Fact]
    public void FR_INGEST_FINANCIAL_NORMALIZATION_absent_posting_date_remains_optional()
    {
        var result = FinancialNormalizer.Normalize(SourceAccountKind.Asset, Evidence("1.00", true));

        Assert.Null(result.Facts!.PostingDate);
    }

    private static FinancialEvidence Evidence(string? amount, bool? increased) => new(amount, "ZAR", "Description", increased, "2026-07-01", null, null, null);
}
