using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Normalization;
using Tally.Domain.Ingest.Reconciliation;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.Adapters;

public sealed class LayoutBStatementAdapterTests
{
    [Fact]
    public void Descriptor_is_the_exact_reviewed_layout_b_contract()
    {
        // TC-INGEST-LAYOUT-B-ADAPTER / DD-INGEST-FORMAT-ADAPTERS
        var adapter = new PdfTextLayoutBStatementAdapter();

        Assert.Equal("pdf-text-layout-b-v1", adapter.Descriptor.VariantId);
        Assert.Equal("application/pdf", adapter.Descriptor.SupportedMediaType);
        Assert.Equal(PdfExtractionLimits.PrivateFixture, adapter.Descriptor.HardLimits);
    }

    [Fact]
    public void Probe_exact_matches_managed_header_period_and_signed_row_structure()
    {
        // TC-INGEST-LAYOUT-B-ADAPTER
        var adapter = new PdfTextLayoutBStatementAdapter();
        var result = adapter.Probe(SyntheticLayoutB());

        Assert.Equal(VariantProbeOutcome.ExactMatch, result.Outcome);
        Assert.Contains("layout-b-managed-headers", result.StructuralEvidenceCodes);
        Assert.Contains("layout-b-period-dates", result.StructuralEvidenceCodes);
    }

    [Fact]
    public void Probe_no_matches_layout_a_running_balance_structure()
    {
        // DD-INGEST-FORMAT-ADAPTERS exclusivity
        var adapter = new PdfTextLayoutBStatementAdapter();
        var layoutA = GlyphOnlyEvidence(
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr");

        Assert.Equal(VariantProbeOutcome.NoMatch, adapter.Probe(layoutA).Outcome);
    }

    [Theory]
    [InlineData("missing-header")]
    [InlineData("duplicate-header")]
    [InlineData("missing-period")]
    [InlineData("missing-controls")]
    public void Probe_no_matches_drifted_or_ambiguous_structure(string scenario)
    {
        // TC-INGEST-LAYOUT-B-ADAPTER fail-closed
        var adapter = new PdfTextLayoutBStatementAdapter();
        var evidence = scenario switch
        {
            "missing-header" => SyntheticLayoutB(includeHeader: false),
            "duplicate-header" => SyntheticLayoutB(duplicateHeader: true),
            "missing-period" => SyntheticLayoutB(includePeriod: false),
            _ => SyntheticLayoutB(includeControls: false)
        };

        Assert.Equal(VariantProbeOutcome.NoMatch, adapter.Probe(evidence).Outcome);
    }

    [Fact]
    public void Extract_emits_ordered_signed_movements_and_exact_controls()
    {
        // FR-INGEST-FINANCIAL-NORMALIZATION / FR-INGEST-SOURCE-RECONCILIATION
        var adapter = new PdfTextLayoutBStatementAdapter();
        var account = Account("account-b", AccountClass.Asset, "ZAR");
        var statement = adapter.Extract(SyntheticLayoutB(), account);

        Assert.Equal(new StatementPeriod("2026-01-01", "2026-01-31"), statement.StatementPeriod);
        Assert.Equal(10_000, statement.OpeningEconomicBalanceMinor);
        Assert.Equal(12_000, statement.ClosingEconomicBalanceMinor);
        Assert.Equal(2, statement.OrderedRecords.Count);
        Assert.Equal(["10.00", "10.00"], statement.OrderedRecords.Select(record => record.FinancialEvidence.Amount));
        Assert.All(statement.OrderedRecords, record => Assert.True(record.FinancialEvidence.BalanceIncreased));
        Assert.All(statement.OrderedRecords, record => Assert.Null(record.RunningBalanceMinor));
        Assert.Equal(["First purchase", "Second purchase"], statement.OrderedRecords.Select(record => record.FinancialEvidence.Description));
    }

    [Fact]
    public void Extract_resolves_yearless_dates_only_when_unique_in_period()
    {
        // FR-INGEST-FINANCIAL-NORMALIZATION yearless rule
        var adapter = new PdfTextLayoutBStatementAdapter();
        var statement = adapter.Extract(SyntheticLayoutB(), Account("account-b", AccountClass.Asset, "ZAR"));

        Assert.Equal(["2026-01-02", "2026-01-03"], statement.OrderedRecords.Select(record => record.FinancialEvidence.TransactionDate));
    }

    [Fact]
    public void Extract_rejects_when_yearless_dates_are_ambiguous_across_the_period()
    {
        var adapter = new PdfTextLayoutBStatementAdapter();
        // Period spans two calendar years so 15 Mar is ambiguous (2025 and 2026).
        var evidence = SyntheticLayoutB(
            periodStart: "15 March 2025",
            periodEnd: "15 March 2026",
            rowDates: ["15 Mar", "16 Mar"]);

        Assert.Equal(VariantProbeOutcome.NoMatch, adapter.Probe(evidence).Outcome);
    }

    [Fact]
    public void Extract_fingerprint_excludes_selected_account_identity()
    {
        var adapter = new PdfTextLayoutBStatementAdapter();
        var evidence = SyntheticLayoutB();
        var first = adapter.Extract(evidence, Account("account-a", AccountClass.Asset, "ZAR"));
        var second = adapter.Extract(evidence, Account("account-b", AccountClass.Asset, "ZAR"));

        Assert.Equal(first.AccountEvidence.MetadataFingerprint, second.AccountEvidence.MetadataFingerprint);
        Assert.NotEqual(first.AccountEvidence.AccountId, second.AccountEvidence.AccountId);
    }

    [Fact]
    public void Extract_rejects_non_active_or_currency_mismatched_account()
    {
        var adapter = new PdfTextLayoutBStatementAdapter();
        Assert.Equal("INGEST-LAYOUT-B-ACCOUNT-MISMATCH", Assert.Throws<InvalidOperationException>(() =>
            adapter.Extract(SyntheticLayoutB(), Account("account-b", AccountClass.Asset, "USD"))).Message);
    }

    [Fact]
    public void Extract_is_deterministic_across_repeated_calls()
    {
        var adapter = new PdfTextLayoutBStatementAdapter();
        var evidence = SyntheticLayoutB();
        var account = Account("account-b", AccountClass.Asset, "ZAR");
        var first = adapter.Extract(evidence, account);
        var second = adapter.Extract(evidence, account);

        Assert.Equal(first.StatementPeriod, second.StatementPeriod);
        Assert.Equal(first.OpeningEconomicBalanceMinor, second.OpeningEconomicBalanceMinor);
        Assert.Equal(first.ClosingEconomicBalanceMinor, second.ClosingEconomicBalanceMinor);
        Assert.Equal(first.AccountEvidence.MetadataFingerprint, second.AccountEvidence.MetadataFingerprint);
        Assert.Equal(first.OrderedRecords.Count, second.OrderedRecords.Count);
        Assert.True(first.OrderedRecords.SequenceEqual(second.OrderedRecords));
    }

    [Fact]
    public void Probe_no_matches_when_description_band_is_ambiguous()
    {
        var adapter = new PdfTextLayoutBStatementAdapter();
        var evidence = SyntheticLayoutB(ambiguousDescription: true);
        Assert.Equal(VariantProbeOutcome.NoMatch, adapter.Probe(evidence).Outcome);
    }

    [Fact]
    public async Task Authorized_private_fixtures_select_layout_b_exclusively_when_injected()
    {
        // TC-INGEST-LAYOUT-B-ADAPTER / TC-INGEST-ADAPTER-GOLDEN-FIXTURES
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var adapter = new PdfTextLayoutBStatementAdapter();
        var extractor = new PdfStatementTextExtractor();
        var selectedCount = 0;
        foreach (var fixture in fixtureSet.Fixtures)
        {
            var extraction = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            Assert.Null(extraction.Error);
            var outcome = adapter.Probe(extraction.Evidence!).Outcome;
            if (outcome == VariantProbeOutcome.ExactMatch)
            {
                selectedCount++;
                Assert.False(string.Equals(fixture.VariantId, "pdf-text-layout-a-v1", StringComparison.Ordinal),
                    "Layout B must not exact-match a Layout A private fixture.");
                VerifyPrivateFixture(adapter, extraction.Evidence!, fixture);
            }
            else
            {
                Assert.Equal(VariantProbeOutcome.NoMatch, outcome);
            }
        }

        Assert.True(selectedCount > 0, "The authorized set supplied no structurally qualified Layout B fixture.");
    }

    private static void VerifyPrivateFixture(
        PdfTextLayoutBStatementAdapter adapter,
        PdfDocumentEvidence evidence,
        PrivateStatementFixture fixture)
    {
        var expected = fixture.Expected;
        var accountClass = InferAccountClass(expected);
        var currency = expected.GetProperty("accountEvidence").GetProperty("currency").GetString()!;
        var account = Account("private-fixture-account", accountClass, currency);
        var first = adapter.Extract(evidence, account);
        var second = adapter.Extract(evidence, account);
        var expectedPeriod = expected.GetProperty("statementPeriod");
        var expectedRecords = expected.GetProperty("orderedRecords").EnumerateArray().ToArray();

        Assert.True(first.StatementPeriod.StartDate == expectedPeriod.GetProperty("startDate").GetString() &&
            first.StatementPeriod.EndDate == expectedPeriod.GetProperty("endDate").GetString(),
            "Private Layout B statement period did not match the authorized expectation.");
        Assert.True(first.OrderedRecords.Count == expectedRecords.Length,
            "Private Layout B row accounting did not match the authorized expectation.");

        var normalized = new List<ReconciliationRecord>(expectedRecords.Length);
        var recordsMatch = true;
        for (var index = 0; index < expectedRecords.Length; index++)
        {
            var actual = first.OrderedRecords[index];
            var expectedRecord = expectedRecords[index];
            var result = FinancialNormalizer.Normalize(
                accountClass == AccountClass.Asset ? SourceAccountKind.Asset : SourceAccountKind.Liability,
                actual.FinancialEvidence);
            var signedMinor = result.Facts?.SignedAmountMinor ?? 0;
            var expectedSignedMinor = ParseExpectedMinor(expectedRecord.GetProperty("signedAmount").GetString()!);

            Assert.True(actual.RecordOrdinal == expectedRecord.GetProperty("order").GetInt32(),
                "Private Layout B record order did not match the authorized expectation.");
            Assert.True(actual.FinancialEvidence.CurrencyCode == expectedRecord.GetProperty("currency").GetString(),
                "Private Layout B record currency did not match the authorized expectation.");
            recordsMatch &= actual.FinancialEvidence.Description == expectedRecord.GetProperty("description").GetString();
            recordsMatch &= actual.FinancialEvidence.TransactionDate == expectedRecord.GetProperty("transactionDate").GetString();
            recordsMatch &= signedMinor == expectedSignedMinor;
            if (expectedRecord.TryGetProperty("runningBalance", out var running) && running.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                recordsMatch &= actual.RunningBalanceMinor == ParseExpectedMinor(running.GetString()!);
            }
            else
            {
                recordsMatch &= actual.RunningBalanceMinor is null;
            }

            normalized.Add(new(actual.SourceRecordId, signedMinor, actual.RunningBalanceMinor, actual.SourceControlMinor));
        }

        Assert.True(recordsMatch, "Private Layout B records did not match the authorized expectation.");
        var controls = expected.GetProperty("controls");
        var opening = ParseExpectedMinor(controls.GetProperty("openingEconomicBalance").GetString()!);
        var closing = ParseExpectedMinor(controls.GetProperty("closingEconomicBalance").GetString()!);
        var reconciliation = StatementReconciler.Reconcile(opening, closing, normalized);
        Assert.True(first.OpeningEconomicBalanceMinor == opening && first.ClosingEconomicBalanceMinor == closing && reconciliation.FullyReconciled,
            "Private Layout B controls did not reconcile to the authorized expectation.");
        Assert.True(first.OrderedRecords.SequenceEqual(second.OrderedRecords) &&
            first.AccountEvidence.MetadataFingerprint == second.AccountEvidence.MetadataFingerprint,
            "Private Layout B repeated extraction was not deterministic.");
        Assert.True(first.AccountEvidence.MetadataFingerprint.Length == 64 &&
            first.AccountEvidence.MetadataFingerprint.All(static c => char.IsAsciiHexDigitLower(c)),
            "Private Layout B metadata fingerprint was not a lowercase SHA-256 hex digest.");
    }

    private static AccountClass InferAccountClass(System.Text.Json.JsonElement expected)
    {
        // Prefer explicit accountKind when present.
        if (expected.GetProperty("accountEvidence").TryGetProperty("accountKind", out var kind))
        {
            var text = kind.GetString() ?? string.Empty;
            if (text.Contains("liability", StringComparison.OrdinalIgnoreCase))
            {
                return AccountClass.Liability;
            }

            if (text.Contains("asset", StringComparison.OrdinalIgnoreCase))
            {
                return AccountClass.Asset;
            }
        }

        return AccountClass.Asset;
    }

    private static long ParseExpectedMinor(string value) =>
        checked((long)(decimal.Parse(value, CultureInfo.InvariantCulture) * 100m));

    private static AccountDetail Account(string accountId, AccountClass accountClass, string currencyCode) => new(
        accountId,
        "institution",
        "display",
        accountClass == AccountClass.Asset ? AccountType.Cheque : AccountType.CreditCard,
        accountClass,
        "masked",
        currencyCode,
        AccountStatus.Active,
        "actor",
        "2026-01-01T00:00:00Z",
        null,
        []);

    private static PdfDocumentEvidence GlyphOnlyEvidence(params string[] lines)
    {
        var glyphs = new List<PdfGlyphEvidence>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var left = 20d;
            var bottom = 700d - (lineIndex * 20d);
            foreach (var character in string.Concat(lines[lineIndex], " "))
            {
                glyphs.Add(new PdfGlyphEvidence(character.ToString(), left, bottom, left + 5d, bottom + 10d, glyphs.Count, bottom, glyphs.Count));
                left += 5d;
            }
        }

        return new PdfDocumentEvidence("synthetic-a", 1, [new PdfPageEvidence(1, 612, 792, glyphs, [])]);
    }

    private static PdfDocumentEvidence SyntheticLayoutB(
        bool includeHeader = true,
        bool duplicateHeader = false,
        bool includePeriod = true,
        bool includeControls = true,
        bool ambiguousDescription = false,
        string periodStart = "01 January 2026",
        string periodEnd = "31 January 2026",
        string[]? rowDates = null)
    {
        rowDates ??= ["02 Jan", "03 Jan"];
        // Column anchors: Date@20, Details@120, Amount@320 → band [53, 270]
        const double dateX = 20d;
        const double detailsX = 120d;
        const double amountX = 320d;
        var managed = new List<PdfManagedLineEvidence>();
        var glyphs = new List<PdfGlyphEvidence>();
        var bottom = 700d;

        void AddManaged(string text, double left, double lineBottom, int block, int line)
        {
            managed.Add(new(block, line, text, left, lineBottom, left + (text.Length * 5d), lineBottom + 10d));
            var cursor = left;
            foreach (var character in text)
            {
                glyphs.Add(new PdfGlyphEvidence(
                    character.ToString(),
                    cursor,
                    lineBottom,
                    cursor + 5d,
                    lineBottom + 10d,
                    glyphs.Count,
                    lineBottom,
                    glyphs.Count));
                cursor += 5d;
            }
        }

        AddManaged("Account Card ****9999", 20d, bottom, 0, 0);
        bottom -= 20d;
        if (includePeriod)
        {
            AddManaged($"Statement period {periodStart} {periodEnd}", 20d, bottom, 0, 1);
            bottom -= 20d;
        }

        if (includeControls)
        {
            AddManaged("Opening balance 100.00", 20d, bottom, 0, 2);
            bottom -= 20d;
            AddManaged("Closing balance 120.00", 20d, bottom, 0, 3);
            bottom -= 20d;
        }

        if (includeHeader)
        {
            // Place header tokens at the governed column anchors.
            var headerBottom = bottom;
            managed.Add(new(0, 4, "Date Details Amount", dateX, headerBottom, amountX + 40d, headerBottom + 10d));
            PlaceToken(glyphs, "Date", dateX, headerBottom);
            PlaceToken(glyphs, "Details", detailsX, headerBottom);
            PlaceToken(glyphs, "Amount", amountX, headerBottom);
            bottom -= 20d;
            if (duplicateHeader)
            {
                managed.Add(new(0, 5, "Date Details Amount", dateX, bottom, amountX + 40d, bottom + 10d));
                PlaceToken(glyphs, "Date", dateX, bottom);
                PlaceToken(glyphs, "Details", detailsX, bottom);
                PlaceToken(glyphs, "Amount", amountX, bottom);
                bottom -= 20d;
            }
        }

        for (var index = 0; index < rowDates.Length; index++)
        {
            var description = index == 0 ? "First purchase" : "Second purchase";
            var rowBottom = bottom;
            var rowText = $"{rowDates[index]} {description} 10.00";
            managed.Add(new(1, index, rowText, dateX, rowBottom, amountX + 40d, rowBottom + 10d));
            // Date token
            PlaceToken(glyphs, rowDates[index], dateX, rowBottom);
            // Description inside band
            PlaceToken(glyphs, description, detailsX + 10d, rowBottom);
            if (ambiguousDescription)
            {
                // Two distinct nonempty bands, both compact-substrings of the managed row.
                PlaceToken(glyphs, "First", detailsX + 10d, rowBottom - 2d);
                PlaceToken(glyphs, "purchase", detailsX + 40d, rowBottom - 4d);
            }

            // Amount at amount column
            PlaceToken(glyphs, "10.00", amountX, rowBottom);
            bottom -= 20d;
        }

        return new PdfDocumentEvidence("synthetic-b", 1, [new PdfPageEvidence(1, 612, 792, glyphs, managed)]);
    }

    private static void PlaceToken(List<PdfGlyphEvidence> glyphs, string text, double left, double bottom)
    {
        var cursor = left;
        foreach (var character in text)
        {
            glyphs.Add(new PdfGlyphEvidence(
                character.ToString(),
                cursor,
                bottom,
                cursor + 5d,
                bottom + 10d,
                glyphs.Count,
                bottom,
                glyphs.Count));
            cursor += 5d;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Tally.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
