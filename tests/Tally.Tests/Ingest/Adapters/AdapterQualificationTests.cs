using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Normalization;
using Tally.Domain.Ingest.Reconciliation;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.Adapters;

// TC-INGEST-VARIANT-QUALIFICATION-CONTRACT / FR-INGEST-VARIANT-QUALIFICATION
// TC-INGEST-ADAPTER-GOLDEN-FIXTURES / DD-INGEST-FORMAT-ADAPTERS
public sealed class AdapterQualificationTests
{
    [Fact]
    public void Registry_contains_exactly_layout_a_and_layout_b_in_deterministic_order()
    {
        // TC-INGEST-ADAPTER-GOLDEN-FIXTURES / DD-INGEST-FORMAT-ADAPTERS
        var registry = StatementAdapterRegistry.CreateDefault();

        Assert.Equal(2, registry.Adapters.Count);
        Assert.IsType<PdfTextLayoutAStatementAdapter>(registry.Adapters[0]);
        Assert.IsType<PdfTextLayoutBStatementAdapter>(registry.Adapters[1]);
        Assert.Equal(["pdf-text-layout-a-v1", "pdf-text-layout-b-v1"], registry.Descriptors.Select(d => d.VariantId));
    }

    [Fact]
    public void Registry_rejects_missing_or_extra_adapters()
    {
        Assert.Throws<ArgumentException>(() => new StatementAdapterRegistry([new PdfTextLayoutAStatementAdapter()]));
        Assert.Throws<ArgumentException>(() => new StatementAdapterRegistry(
        [
            new PdfTextLayoutAStatementAdapter(),
            new PdfTextLayoutBStatementAdapter(),
            new PdfTextLayoutAStatementAdapter()
        ]));
    }

    [Fact]
    public async Task Private_fixtures_select_exactly_one_adapter_when_injected()
    {
        // FR-INGEST-VARIANT-QUALIFICATION
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var registry = StatementAdapterRegistry.CreateDefault();
        var extractor = new PdfStatementTextExtractor();
        var selected = 0;
        foreach (var fixture in fixtureSet.Fixtures)
        {
            var extraction = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            Assert.Null(extraction.Error);
            var selection = registry.Select(extraction.Evidence!);
            Assert.Equal(AdapterSelectionStatus.ExclusiveMatch, selection.Status);
            Assert.NotNull(selection.Adapter);
            selected++;
            Assert.True(
                selection.Adapter.Descriptor.VariantId == "pdf-text-layout-a-v1" ||
                selection.Adapter.Descriptor.VariantId == "pdf-text-layout-b-v1",
                "Selection produced an unexpected public variant id.");
        }

        Assert.Equal(3, selected);
        Assert.Equal(2, fixtureSet.Fixtures.Select(f => f.VariantId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Private_fixtures_are_deterministic_through_extractor_and_selected_adapter()
    {
        var fixtureSet = PrivateStatementFixtureSet.TryLoadFromEnvironment(FindRepositoryRoot());
        if (fixtureSet is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        var registry = StatementAdapterRegistry.CreateDefault();
        var extractor = new PdfStatementTextExtractor();
        foreach (var fixture in fixtureSet.Fixtures)
        {
            var firstEvidence = (await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None)).Evidence!;
            var secondEvidence = (await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None)).Evidence!;
            var firstSelection = registry.Select(firstEvidence);
            var secondSelection = registry.Select(secondEvidence);
            Assert.Equal(AdapterSelectionStatus.ExclusiveMatch, firstSelection.Status);
            Assert.Equal(firstSelection.Adapter!.Descriptor.VariantId, secondSelection.Adapter!.Descriptor.VariantId);

            var account = AccountFor(fixture);
            var first = firstSelection.Adapter.Extract(firstEvidence, account);
            var second = secondSelection.Adapter!.Extract(secondEvidence, account);
            Assert.Equal(first.StatementPeriod, second.StatementPeriod);
            Assert.Equal(first.OpeningEconomicBalanceMinor, second.OpeningEconomicBalanceMinor);
            Assert.Equal(first.ClosingEconomicBalanceMinor, second.ClosingEconomicBalanceMinor);
            Assert.Equal(first.OrderedRecords.Count, second.OrderedRecords.Count);
            Assert.True(first.OrderedRecords.SequenceEqual(second.OrderedRecords));
            Assert.Equal(Digest(first), Digest(second));
            AssertControls(fixture, first, account.AccountClass);
        }
    }

    [Fact]
    public void Selection_is_no_match_when_structure_is_unsupported()
    {
        var registry = StatementAdapterRegistry.CreateDefault();
        var empty = new PdfDocumentEvidence(
            "unsupported",
            1,
            [new PdfPageEvidence(1, 612, 792, [], [])]);

        Assert.Equal(AdapterSelectionStatus.NoMatch, registry.Select(empty).Status);
    }

    private static void AssertControls(PrivateStatementFixture fixture, ExtractedStatement statement, AccountClass accountClass)
    {
        var expected = fixture.Expected;
        var expectedRecords = expected.GetProperty("orderedRecords").EnumerateArray().ToArray();
        Assert.Equal(expectedRecords.Length, statement.OrderedRecords.Count);
        var opening = ParseMinor(expected.GetProperty("controls").GetProperty("openingEconomicBalance").GetString()!);
        var closing = ParseMinor(expected.GetProperty("controls").GetProperty("closingEconomicBalance").GetString()!);
        Assert.Equal(opening, statement.OpeningEconomicBalanceMinor);
        Assert.Equal(closing, statement.ClosingEconomicBalanceMinor);

        var normalized = new List<ReconciliationRecord>();
        for (var index = 0; index < expectedRecords.Length; index++)
        {
            var actual = statement.OrderedRecords[index];
            var result = FinancialNormalizer.Normalize(
                accountClass == AccountClass.Asset ? SourceAccountKind.Asset : SourceAccountKind.Liability,
                actual.FinancialEvidence);
            var signed = result.Facts?.SignedAmountMinor ?? 0;
            Assert.Equal(ParseMinor(expectedRecords[index].GetProperty("signedAmount").GetString()!), signed);
            normalized.Add(new(actual.SourceRecordId, signed, actual.RunningBalanceMinor, actual.SourceControlMinor));
        }

        Assert.True(StatementReconciler.Reconcile(opening, closing, normalized).FullyReconciled);
    }

    private static AccountDetail AccountFor(PrivateStatementFixture fixture)
    {
        var kind = fixture.Expected.GetProperty("accountEvidence").GetProperty("accountKind").GetString() ?? string.Empty;
        var accountClass = kind.Contains("liability", StringComparison.OrdinalIgnoreCase)
            ? AccountClass.Liability
            : AccountClass.Asset;
        var currency = fixture.Expected.GetProperty("accountEvidence").GetProperty("currency").GetString()!;
        return new(
            "private-fixture-account",
            "institution",
            "display",
            accountClass == AccountClass.Asset ? AccountType.Cheque : AccountType.CreditCard,
            accountClass,
            "masked",
            currency,
            AccountStatus.Active,
            "actor",
            "2026-01-01T00:00:00Z",
            null,
            []);
    }

    private static long ParseMinor(string value) =>
        checked((long)(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * 100m));

    private static string Digest(ExtractedStatement statement)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
        Append(statement.Variant.VariantId);
        Append(statement.StatementPeriod.StartDate);
        Append(statement.StatementPeriod.EndDate);
        Append(statement.OpeningEconomicBalanceMinor?.ToString() ?? string.Empty);
        Append(statement.ClosingEconomicBalanceMinor?.ToString() ?? string.Empty);
        foreach (var record in statement.OrderedRecords)
        {
            Append(record.SourceRecordId);
            Append(record.FinancialEvidence.Amount ?? string.Empty);
            Append(record.FinancialEvidence.Description ?? string.Empty);
            Append(record.FinancialEvidence.TransactionDate ?? string.Empty);
            Append(record.DescriptionEvidenceKind.ToString());
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
