using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Normalization;
using Tally.Domain.Ingest.Reconciliation;
using Tally.Infrastructure.Ingest.Pdf;
using Xunit;

namespace Tally.Tests.Ingest.Fixtures;

/// <summary>
/// Owner-only regenerator for <c>docs/statements/.ingest-fixture-manifest.json</c>.
/// Gated by <c>TALLY_INGEST_REGENERATE_PRIVATE_MANIFEST=1</c>. Emits only structural counts.
/// Periods and row facts are derived from product extraction evidence — never from FNB filenames.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateFixtureManifestRegenerator
{
    public const string RegenerateEnvironmentVariable = "TALLY_INGEST_REGENERATE_PRIVATE_MANIFEST";

    [Fact]
    public async Task Regenerate_authorized_private_manifest_when_requested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var fixtureRoot = Path.Combine(repositoryRoot, "docs", "statements");
        var inventoryPath = Path.Combine(fixtureRoot, ".fixture-inventory.json");
        var manifestPath = Path.Combine(fixtureRoot, ".ingest-fixture-manifest.json");
        Assert.True(File.Exists(inventoryPath), "PRIVATE-FIXTURE-INVENTORY-MISSING");

        using var inventoryDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(inventoryPath));
        var inventoryFixtures = inventoryDocument.RootElement.GetProperty("fixtures");
        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, inventoryFixtures.GetArrayLength());

        var extractor = new PdfStatementTextExtractor();
        var registry = StatementAdapterRegistry.CreateDefault();
        var fixturesNode = new JsonArray();
        var layoutA = 0;
        var layoutB = 0;
        var fullyReconciled = 0;
        var totalRecords = 0;

        foreach (var item in inventoryFixtures.EnumerateArray())
        {
            var sourcePath = item.GetProperty("sourcePath").GetString()!;
            var expectedSha = item.GetProperty("sourceSha256").GetString()!;
            var accountRole = item.GetProperty("accountRole").GetString()!;
            var absolute = Path.GetFullPath(Path.Combine(repositoryRoot, sourcePath));
            var bytes = await File.ReadAllBytesAsync(absolute);
            var actualSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.Equal(expectedSha, actualSha);

            var extraction = await extractor.ExtractAsync(
                System.Collections.Immutable.ImmutableArray.Create(bytes),
                PdfExtractionLimits.PrivateFixture,
                CancellationToken.None);
            Assert.Null(extraction.Error);
            Assert.NotNull(extraction.Evidence);

            var selection = registry.Select(extraction.Evidence!);
            if (selection.Status != AdapterSelectionStatus.ExclusiveMatch || selection.Adapter is null)
            {
                // Structural-only diagnostic: role + digest prefix + page/glyph counts + probe codes.
                var pages = extraction.Evidence!.Pages.Count;
                var glyphs = extraction.Evidence.Pages.Sum(p => p.OrderedGlyphs.Count);
                var probes = registry.Adapters
                    .Select(adapter =>
                    {
                        var probe = adapter.Probe(extraction.Evidence!);
                        return $"{adapter.Descriptor.VariantId}:{probe.Outcome}:{probe.StructuralEvidenceCodes.Count}";
                    })
                    .ToArray();
                throw new Xunit.Sdk.XunitException(
                    $"PRIVATE-FIXTURE-NO-MATCH role={accountRole} sha={expectedSha[..12]} pages={pages} glyphs={glyphs} probes=[{string.Join(',', probes)}]");
            }

            var accountClass = AccountClassForRole(accountRole);
            var accountKind = AccountKindForRole(accountRole);
            var account = new AccountDetail(
                "private-fixture-account",
                "institution",
                "display",
                accountClass == AccountClass.Asset ? AccountType.Cheque : AccountType.CreditCard,
                accountClass,
                "masked",
                "ZAR",
                AccountStatus.Active,
                "actor",
                "2026-01-01T00:00:00Z",
                null,
                []);

            var statement = selection.Adapter!.Extract(extraction.Evidence!, account);
            var variantId = selection.Adapter.Descriptor.VariantId;
            if (variantId == "pdf-text-layout-a-v1") layoutA++;
            else if (variantId == "pdf-text-layout-b-v1") layoutB++;
            else Assert.Fail("unexpected-variant");

            // Periods must come from parsed statement evidence, never opaque FNB filename tokens.
            Assert.False(string.IsNullOrWhiteSpace(statement.StatementPeriod.StartDate));
            Assert.False(string.IsNullOrWhiteSpace(statement.StatementPeriod.EndDate));

            var accountKindSource = SourceAccountKindFor(accountClass);
            var ordered = new JsonArray();
            var normalized = new List<ReconciliationRecord>(statement.OrderedRecords.Count);
            long signedMovementTotal = 0;
            var hasRunning = true;
            for (var index = 0; index < statement.OrderedRecords.Count; index++)
            {
                var record = statement.OrderedRecords[index];
                var norm = FinancialNormalizer.Normalize(accountKindSource, record.FinancialEvidence);
                var signedMinor = norm.Facts?.SignedAmountMinor ?? 0;
                signedMovementTotal = checked(signedMovementTotal + signedMinor);
                if (record.RunningBalanceMinor is null) hasRunning = false;

                var description = record.DescriptionEvidenceKind == DescriptionEvidenceKind.SourceText
                    ? record.FinancialEvidence.Description ?? string.Empty
                    : string.Empty;

                ordered.Add(new JsonObject
                {
                    ["order"] = record.RecordOrdinal,
                    ["sourceRecordId"] = record.SourceRecordId,
                    ["transactionDate"] = record.FinancialEvidence.TransactionDate,
                    ["description"] = description,
                    ["signedAmount"] = FormatMinor(signedMinor),
                    ["runningBalance"] = record.RunningBalanceMinor is null
                        ? null
                        : JsonValue.Create(FormatMinor(record.RunningBalanceMinor.Value)),
                    ["currency"] = record.FinancialEvidence.CurrencyCode
                });
                normalized.Add(new(record.SourceRecordId, signedMinor, record.RunningBalanceMinor, record.SourceControlMinor));
            }

            totalRecords += statement.OrderedRecords.Count;
            Assert.NotNull(statement.OpeningEconomicBalanceMinor);
            Assert.NotNull(statement.ClosingEconomicBalanceMinor);
            var reconciliation = StatementReconciler.Reconcile(
                statement.OpeningEconomicBalanceMinor,
                statement.ClosingEconomicBalanceMinor,
                normalized);
            if (reconciliation.FullyReconciled) fullyReconciled++;

            var allRowsAccounted = reconciliation.Controls.Any(c =>
                c.Name == "record_accounting" && c.State == ReconciliationControlState.Satisfied);
            var balanceEquation = reconciliation.Controls.Any(c =>
                c.Name == "opening_to_closing" && c.State == ReconciliationControlState.Satisfied);
            var runningSatisfied = !reconciliation.Controls.Any(c =>
                c.Name.StartsWith("running:", StringComparison.Ordinal) &&
                c.State == ReconciliationControlState.Mismatched);
            // Manifest historical shape: allRunningBalanceTransitionsSatisfied is false when any running control is Unavailable.
            var allRunningTransitions = hasRunning && runningSatisfied &&
                !reconciliation.Controls.Any(c =>
                    c.Name.StartsWith("running:", StringComparison.Ordinal) &&
                    c.State == ReconciliationControlState.Unavailable);

            // FNB archive historically flags permissionEncrypted; Discovery does not.
            var permissionEncrypted = string.Equals(accountRole, "fnb", StringComparison.Ordinal);

            var expected = new JsonObject
            {
                ["statementPeriod"] = new JsonObject
                {
                    ["startDate"] = statement.StatementPeriod.StartDate,
                    ["endDate"] = statement.StatementPeriod.EndDate
                },
                ["accountEvidence"] = new JsonObject
                {
                    ["accountKind"] = accountKind,
                    ["currency"] = "ZAR",
                    ["metadataFingerprint"] = statement.AccountEvidence.MetadataFingerprint,
                    ["permissionEncrypted"] = permissionEncrypted
                },
                ["orderedRecords"] = ordered,
                ["controls"] = new JsonObject
                {
                    ["sourceRowCount"] = statement.OrderedRecords.Count,
                    ["openingEconomicBalance"] = FormatMinor(statement.OpeningEconomicBalanceMinor!.Value),
                    ["closingEconomicBalance"] = FormatMinor(statement.ClosingEconomicBalanceMinor!.Value),
                    ["signedMovementTotal"] = FormatMinor(signedMovementTotal),
                    ["hasRunningBalances"] = hasRunning,
                    ["allRowsAccounted"] = allRowsAccounted,
                    ["allRunningBalanceTransitionsSatisfied"] = allRunningTransitions,
                    ["balanceEquationSatisfied"] = balanceEquation
                }
            };

            fixturesNode.Add(new JsonObject
            {
                ["sourcePath"] = sourcePath,
                ["sourceSha256"] = expectedSha,
                ["variantId"] = variantId,
                ["expected"] = expected
            });
        }

        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, fixturesNode.Count);
        Assert.Equal(2, (layoutA > 0 ? 1 : 0) + (layoutB > 0 ? 1 : 0));
        Assert.True(layoutA >= 1 && layoutB >= 1);

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["fixtures"] = fixturesNode
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, json + "\n");
        File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Structural-only console evidence (no paths, no private values).
        Console.WriteLine(
            $"PRIVATE_MANIFEST_REGENERATED fixtures={fixturesNode.Count} layoutA={layoutA} layoutB={layoutB} records={totalRecords} fullyReconciled={fullyReconciled}");
    }

    private static string AccountKindForRole(string role) => role switch
    {
        "fnb" => "asset-deposit",
        "discovery-purple-transaction-account" => "asset-deposit",
        "discovery-purple-card" => "liability-credit",
        _ => throw new InvalidOperationException("PRIVATE-FIXTURE-ROLE-UNKNOWN")
    };

    private static AccountClass AccountClassForRole(string role) =>
        AccountKindForRole(role).StartsWith("liability", StringComparison.Ordinal)
            ? AccountClass.Liability
            : AccountClass.Asset;

    private static SourceAccountKind SourceAccountKindFor(AccountClass accountClass) =>
        accountClass == AccountClass.Asset ? SourceAccountKind.Asset : SourceAccountKind.Liability;

    private static string FormatMinor(long minorUnits) =>
        (minorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
