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

public sealed class LayoutAStatementAdapterTests
{
    [Fact]
    public void Descriptor_is_the_exact_reviewed_layout_a_contract()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();

        Assert.Equal("pdf-text-layout-a-v1", adapter.Descriptor.VariantId);
        Assert.Equal("application/pdf", adapter.Descriptor.SupportedMediaType);
        Assert.Equal(PdfExtractionLimits.PrivateFixture, adapter.Descriptor.HardLimits);
    }

    [Fact]
    public void Extract_contract_uses_public_account_detail_and_normalization_ready_evidence()
    {
        var method = typeof(IStatementAdapter).GetMethod(nameof(IStatementAdapter.Extract));

        Assert.NotNull(method);
        Assert.Equal(typeof(AccountDetail), method.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(FinancialEvidence), typeof(SourceRecordEvidence).GetProperty(nameof(SourceRecordEvidence.FinancialEvidence))!.PropertyType);
        Assert.Equal(typeof(StatementPeriod), typeof(ExtractedStatement).GetProperty(nameof(ExtractedStatement.StatementPeriod))!.PropertyType);
        Assert.NotNull(typeof(StatementAccountEvidence).GetProperty("MetadataFingerprint"));
    }

    [Fact]
    public void Probe_exact_matches_only_explicit_period_running_balance_structure()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();

        var result = adapter.Probe(Evidence(
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr"));

        Assert.Equal(VariantProbeOutcome.ExactMatch, result.Outcome);
        Assert.Equal(["layout-a-explicit-period", "layout-a-running-balance-transitions"], result.StructuralEvidenceCodes);
    }

    [Theory]
    [InlineData("missing-period")]
    [InlineData("missing-controls")]
    [InlineData("single-row")]
    [InlineData("amount-only")]
    public void Probe_no_matches_drifted_or_insufficient_structure(string scenario)
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var evidence = scenario switch
        {
            "missing-period" => Evidence("Opening balance 100.00Cr", "Closing balance 120.00Cr", "Date Description Amount Balance", "01 Jan First row 10.00Cr 110.00Cr", "02 Jan Second row 10.00Cr 120.00Cr"),
            "missing-controls" => Evidence("01 January 2026 31 January 2026", "Date Description Amount Balance", "01 Jan First row 10.00Cr 110.00Cr", "02 Jan Second row 10.00Cr 120.00Cr"),
            "single-row" => Evidence("01 January 2026 31 January 2026", "Opening balance 100.00Cr", "Closing balance 110.00Cr", "Date Description Amount Balance", "01 Jan First row 10.00Cr 110.00Cr"),
            _ => Evidence("01 January 2026 31 January 2026", "Opening balance 100.00Cr", "Closing balance 120.00Cr", "Date Description Amount Balance", "01 Jan First row 10.00", "02 Jan Second row 20.00")
        };

        Assert.Equal(VariantProbeOutcome.NoMatch, adapter.Probe(evidence).Outcome);
    }

    [Fact]
    public void Extract_emits_normalization_ready_rows_and_exact_controls()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var account = Account("account-a", AccountClass.Asset, "ZAR");
        var statement = adapter.Extract(Evidence(
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr"), account);

        Assert.Equal(new StatementPeriod("2026-01-01", "2026-01-31"), statement.StatementPeriod);
        Assert.Equal(10_000, statement.OpeningEconomicBalanceMinor);
        Assert.Equal(12_000, statement.ClosingEconomicBalanceMinor);
        Assert.Equal(2, statement.OrderedRecords.Count);
        Assert.All(statement.OrderedRecords, record => Assert.Equal("10.00", record.FinancialEvidence.Amount));
        Assert.Equal([11_000L, 12_000L], statement.OrderedRecords.Select(record => record.RunningBalanceMinor));
        Assert.All(statement.OrderedRecords, record => Assert.True(record.FinancialEvidence.BalanceIncreased));
    }

    [Fact]
    public void Extract_fingerprint_excludes_selected_account_identity()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var evidence = Evidence(
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr");

        var first = adapter.Extract(evidence, Account("account-a", AccountClass.Asset, "ZAR"));
        var second = adapter.Extract(evidence, Account("account-b", AccountClass.Asset, "ZAR"));

        Assert.Equal(first.AccountEvidence.MetadataFingerprint, second.AccountEvidence.MetadataFingerprint);
        Assert.NotEqual(first.AccountEvidence.AccountId, second.AccountEvidence.AccountId);
    }

    [Fact]
    public void Extract_fingerprint_uses_the_canonical_layout_a_metadata_contract()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var account = Account("account-a", AccountClass.Asset, "ZAR");
        var first = adapter.Extract(Evidence(
            "  ACCOUNT   Card ****1234 999.99  ",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr"), account);
        var second = adapter.Extract(Evidence(
            "account card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr"), account);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("1.0.0\naccount card ****1234")));

        Assert.Equal(expected, first.AccountEvidence.MetadataFingerprint);
        Assert.Equal(expected, second.AccountEvidence.MetadataFingerprint);
    }

    [Fact]
    public void Extract_rejects_a_non_active_or_currency_mismatched_account()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var evidence = Evidence(
            "Account Card ****1234",
            "Statement period 01 January 2026 31 January 2026",
            "Opening balance 100.00Cr",
            "Closing balance 120.00Cr",
            "Date Description Amount Balance",
            "01 Jan First row 10.00Cr 110.00Cr",
            "02 Jan Second row 10.00Cr 120.00Cr");

        Assert.Equal("INGEST-LAYOUT-A-ACCOUNT-MISMATCH", Assert.Throws<InvalidOperationException>(() =>
            adapter.Extract(evidence, Account("account-a", AccountClass.Asset, "USD"))).Message);
    }

    [Fact]
    public void Extract_assigns_separate_baseline_descriptions_only_with_unique_row_band_ownership()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var statement = adapter.Extract(Evidence(
            ("Account Card ****1234", 20d, 700d),
            ("Statement period 01 January 2026 31 January 2026", 20d, 680d),
            ("Opening balance 100.00Cr", 20d, 660d),
            ("Closing balance 120.00Cr", 20d, 640d),
            ("Date Description Amount Balance", 20d, 620d),
            ("First row", 55d, 606d),
            ("01 Jan             10.00Cr 110.00Cr", 20d, 600d),
            ("02 Jan             10.00Cr 120.00Cr", 20d, 560d),
            ("Second row", 55d, 554d)), Account("account-a", AccountClass.Asset, "ZAR"));

        Assert.Equal(["First row", "Second row"], statement.OrderedRecords.Select(record => record.FinancialEvidence.Description));
        Assert.All(statement.OrderedRecords, record => Assert.Contains('\n', record.OriginalTextEvidence));
    }

    [Fact]
    public void Probe_no_matches_missing_or_ambiguous_separate_baseline_description_ownership()
    {
        var adapter = new PdfTextLayoutAStatementAdapter();
        var ambiguous = Evidence(
            ("Account Card ****1234", 20d, 700d),
            ("Statement period 01 January 2026 31 January 2026", 20d, 680d),
            ("Opening balance 100.00Cr", 20d, 660d),
            ("Closing balance 120.00Cr", 20d, 640d),
            ("Date Description Amount Balance", 20d, 620d),
            ("First candidate", 55d, 612d),
            ("Second candidate", 55d, 606d),
            ("01 Jan             10.00Cr 110.00Cr", 20d, 600d),
            ("02 Jan Second row 10.00Cr 120.00Cr", 20d, 560d));

        Assert.Equal(VariantProbeOutcome.NoMatch, adapter.Probe(ambiguous).Outcome);
    }

    [Fact]
    public async Task Authorized_private_fixtures_select_layout_a_exclusively_when_injected()
    {
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var adapter = new PdfTextLayoutAStatementAdapter();
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
                VerifyPrivateFixture(adapter, extraction.Evidence!, fixture);
            }
            else
            {
                Assert.Equal(VariantProbeOutcome.NoMatch, outcome);
            }
        }

        Assert.True(selectedCount > 0, "The authorized set supplied no structurally qualified Layout A fixture.");
    }

    private static void VerifyPrivateFixture(
        PdfTextLayoutAStatementAdapter adapter,
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
            "Private Layout A statement period did not match the authorized expectation.");
        var expectedFingerprint = expected.GetProperty("accountEvidence").GetProperty("metadataFingerprint").GetString()!;
        Assert.True(first.OrderedRecords.Count == expectedRecords.Length,
            "Private Layout A row accounting did not match the authorized expectation.");

        var normalized = new List<ReconciliationRecord>(expectedRecords.Length);
        var sourceRecordIdsMatch = true;
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
            var expectedRunningMinor = ParseExpectedMinor(expectedRecord.GetProperty("runningBalance").GetString()!);

            Assert.True(actual.RecordOrdinal == expectedRecord.GetProperty("order").GetInt32(),
                "Private Layout A record order did not match the authorized expectation.");
            Assert.True(actual.FinancialEvidence.CurrencyCode == expectedRecord.GetProperty("currency").GetString(),
                "Private Layout A record currency did not match the authorized expectation.");
            recordsMatch &= actual.FinancialEvidence.Description == expectedRecord.GetProperty("description").GetString();
            recordsMatch &= actual.FinancialEvidence.TransactionDate == expectedRecord.GetProperty("transactionDate").GetString();
            recordsMatch &= signedMinor == expectedSignedMinor;
            recordsMatch &= actual.RunningBalanceMinor == expectedRunningMinor;
            sourceRecordIdsMatch &= actual.SourceRecordId == expectedRecord.GetProperty("sourceRecordId").GetString();
            normalized.Add(new(actual.SourceRecordId, signedMinor, actual.RunningBalanceMinor, actual.SourceControlMinor));
        }

        Assert.True(recordsMatch, "Private Layout A records did not match the authorized expectation.");
        Assert.True(sourceRecordIdsMatch,
            "Private Layout A source record identity did not match the authorized expectation.");
        Assert.True(first.AccountEvidence.MetadataFingerprint == expectedFingerprint,
            "Private Layout A metadata fingerprint did not match the authorized expectation.");

        var controls = expected.GetProperty("controls");
        var opening = ParseExpectedMinor(controls.GetProperty("openingEconomicBalance").GetString()!);
        var closing = ParseExpectedMinor(controls.GetProperty("closingEconomicBalance").GetString()!);
        var reconciliation = StatementReconciler.Reconcile(opening, closing, normalized);
        Assert.True(first.OpeningEconomicBalanceMinor == opening && first.ClosingEconomicBalanceMinor == closing && reconciliation.FullyReconciled,
            "Private Layout A controls did not reconcile to the authorized expectation.");
        Assert.True(StatementsAreEquivalent(first, second),
            "Private Layout A repeated extraction was not deterministic.");
    }

    private static AccountClass InferAccountClass(System.Text.Json.JsonElement expected)
    {
        var controls = expected.GetProperty("controls");
        var previous = ParseExpectedMinor(controls.GetProperty("openingEconomicBalance").GetString()!);
        var asset = true;
        var liability = true;
        foreach (var record in expected.GetProperty("orderedRecords").EnumerateArray())
        {
            var running = ParseExpectedMinor(record.GetProperty("runningBalance").GetString()!);
            var signed = ParseExpectedMinor(record.GetProperty("signedAmount").GetString()!);
            asset &= signed == running - previous;
            liability &= signed == -(running - previous);
            previous = running;
        }

        Assert.True(asset != liability, "Private Layout A account class evidence was ambiguous.");
        return asset ? AccountClass.Asset : AccountClass.Liability;
    }

    private static long ParseExpectedMinor(string value) =>
        checked((long)(decimal.Parse(value, CultureInfo.InvariantCulture) * 100m));

    private static bool StatementsAreEquivalent(ExtractedStatement first, ExtractedStatement second) =>
        first == second ||
        (first.Variant == second.Variant &&
         first.StatementPeriod == second.StatementPeriod &&
         first.AccountEvidence == second.AccountEvidence &&
         first.OpeningEconomicBalanceMinor == second.OpeningEconomicBalanceMinor &&
         first.ClosingEconomicBalanceMinor == second.ClosingEconomicBalanceMinor &&
         first.OrderedRecords.SequenceEqual(second.OrderedRecords) &&
         first.AdvertisedControls.SequenceEqual(second.AdvertisedControls));

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

    private static PdfDocumentEvidence Evidence(params string[] lines)
    {
        var glyphs = new List<PdfGlyphEvidence>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var left = 20d;
            var bottom = 700d - (lineIndex * 20d);
            foreach (var character in string.Concat(lines[lineIndex], " "))
            {
                glyphs.Add(new PdfGlyphEvidence(character.ToString(), left, bottom, left + 5d, bottom + 10d, glyphs.Count));
                left += 5d;
            }
        }

        return new PdfDocumentEvidence("synthetic", 1, [new PdfPageEvidence(1, 612, 792, glyphs)]);
    }

    private static PdfDocumentEvidence Evidence(params (string Text, double Left, double Bottom)[] lines)
    {
        var glyphs = new List<PdfGlyphEvidence>();
        foreach (var line in lines)
        {
            var left = line.Left;
            foreach (var character in string.Concat(line.Text, " "))
            {
                glyphs.Add(new PdfGlyphEvidence(character.ToString(), left, line.Bottom, left + 5d, line.Bottom + 10d, glyphs.Count));
                left += 5d;
            }
        }

        return new PdfDocumentEvidence("synthetic", 1, [new PdfPageEvidence(1, 612, 792, glyphs)]);
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
