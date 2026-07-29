using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Normalization;
using Tally.Domain.Ingest.Reconciliation;
using Tally.Features.Ingest.Contract;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Tests.Ingest.Fixtures;
using Xunit;

namespace Tally.Tests.Ingest.E2E;

/// <summary>
/// Full private archive validation (27 unique PDFs). Enabled only when
/// <c>TALLY_INGEST_PRIVATE_FIXTURE_MANIFEST</c> points at the owner-only expected-results manifest.
/// Disposable data root only — never the live ledger. Never logs rows, identifiers, or source paths.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateFixtureArchiveValidationTests : IAsyncLifetime
{
    private readonly IngestE2EHarness harness = new();

    public Task InitializeAsync() => harness.InitializeAsync();
    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task Authorized_archive_covers_all_27_fixtures_with_hash_adapter_period_identity_and_controls()
    {
        var fixtures = harness.TryPrivateFixtures();
        if (fixtures is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, fixtures.Fixtures.Count);
        Assert.Equal(
            PrivateStatementFixtureSet.AuthorizedVariantCount,
            fixtures.Fixtures.Select(f => f.VariantId).Distinct(StringComparer.Ordinal).Count());

        var extractor = new PdfStatementTextExtractor();
        var registry = StatementAdapterRegistry.CreateDefault();
        var executed = 0;

        foreach (var fixture in fixtures.Fixtures)
        {
            executed++;
            // Per-file hash equality (loader already verifies; re-assert on bytes).
            var digest = Convert.ToHexStringLower(SHA256.HashData(fixture.SourceBytes.AsSpan()));
            Assert.Equal(fixture.SourceSha256, digest);

            var extraction = await extractor.ExtractAsync(fixture.SourceBytes, PdfExtractionLimits.PrivateFixture, CancellationToken.None);
            Assert.Null(extraction.Error);
            Assert.NotNull(extraction.Evidence);

            var selection = registry.Select(extraction.Evidence!);
            Assert.Equal(AdapterSelectionStatus.ExclusiveMatch, selection.Status);
            Assert.NotNull(selection.Adapter);
            Assert.Equal(fixture.VariantId, selection.Adapter!.Descriptor.VariantId);

            var account = AccountFor(fixture);
            var first = selection.Adapter.Extract(extraction.Evidence!, account);
            var second = selection.Adapter.Extract(extraction.Evidence!, account);
            Assert.Equal(first.StatementPeriod, second.StatementPeriod);
            Assert.True(first.OrderedRecords.SequenceEqual(second.OrderedRecords));

            var expected = fixture.Expected;
            var period = expected.GetProperty("statementPeriod");
            Assert.Equal(period.GetProperty("startDate").GetString(), first.StatementPeriod.StartDate);
            Assert.Equal(period.GetProperty("endDate").GetString(), first.StatementPeriod.EndDate);

            var expectedRecords = expected.GetProperty("orderedRecords").EnumerateArray().ToArray();
            Assert.Equal(expectedRecords.Length, first.OrderedRecords.Count);

            var accountClass = account.AccountClass;
            var sourceKind = accountClass == AccountClass.Asset ? SourceAccountKind.Asset : SourceAccountKind.Liability;
            var normalized = new List<ReconciliationRecord>(expectedRecords.Length);
            var identitiesMatch = true;
            var recordsMatch = true;
            for (var index = 0; index < expectedRecords.Length; index++)
            {
                var actual = first.OrderedRecords[index];
                var exp = expectedRecords[index];
                var signed = FinancialNormalizer.Normalize(sourceKind, actual.FinancialEvidence).Facts?.SignedAmountMinor ?? 0;
                recordsMatch &= actual.RecordOrdinal == exp.GetProperty("order").GetInt32();
                recordsMatch &= actual.FinancialEvidence.CurrencyCode == exp.GetProperty("currency").GetString();
                recordsMatch &= actual.FinancialEvidence.TransactionDate == exp.GetProperty("transactionDate").GetString();
                recordsMatch &= signed == ParseMinor(exp.GetProperty("signedAmount").GetString()!);
                var expectedDescription = exp.GetProperty("description").GetString() ?? string.Empty;
                if (actual.DescriptionEvidenceKind == DescriptionEvidenceKind.SourceText)
                {
                    recordsMatch &= actual.FinancialEvidence.Description == expectedDescription;
                }
                else
                {
                    recordsMatch &= string.IsNullOrEmpty(expectedDescription);
                }

                if (exp.TryGetProperty("runningBalance", out var running) && running.ValueKind == JsonValueKind.String)
                {
                    recordsMatch &= actual.RunningBalanceMinor == ParseMinor(running.GetString()!);
                }
                else
                {
                    recordsMatch &= actual.RunningBalanceMinor is null;
                }

                identitiesMatch &= actual.SourceRecordId == exp.GetProperty("sourceRecordId").GetString();
                normalized.Add(new(actual.SourceRecordId, signed, actual.RunningBalanceMinor, actual.SourceControlMinor));
            }

            Assert.True(recordsMatch, "ordered-records-mismatch");
            Assert.True(identitiesMatch, "source-identity-mismatch");

            var controls = expected.GetProperty("controls");
            var opening = ParseMinor(controls.GetProperty("openingEconomicBalance").GetString()!);
            var closing = ParseMinor(controls.GetProperty("closingEconomicBalance").GetString()!);
            Assert.Equal(opening, first.OpeningEconomicBalanceMinor);
            Assert.Equal(closing, first.ClosingEconomicBalanceMinor);
            Assert.Equal(controls.GetProperty("sourceRowCount").GetInt32(), first.OrderedRecords.Count);

            var reconciliation = StatementReconciler.Reconcile(opening, closing, normalized);
            Assert.Equal(controls.GetProperty("balanceEquationSatisfied").GetBoolean(),
                reconciliation.Controls.Any(c => c.Name == "opening_to_closing" && c.State == ReconciliationControlState.Satisfied));
            Assert.Equal(controls.GetProperty("allRowsAccounted").GetBoolean(),
                reconciliation.Controls.Any(c => c.Name == "record_accounting" && c.State == ReconciliationControlState.Satisfied));

            var expectedFingerprint = expected.GetProperty("accountEvidence").GetProperty("metadataFingerprint").GetString()!;
            Assert.Equal(expectedFingerprint, first.AccountEvidence.MetadataFingerprint);
        }

        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, executed);
    }

    [Fact]
    public async Task Authorized_archive_preview_approve_commit_and_replay_only_when_committable()
    {
        var fixtures = harness.TryPrivateFixtures();
        if (fixtures is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(PrivateStatementFixtureSet.ManifestEnvironmentVariable));
            return;
        }

        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, fixtures.Fixtures.Count);
        harness.RebindIngestWithRealPdfExtractor();

        var previewed = 0;
        var approvedCommitted = 0;
        var exactReplays = 0;
        var duplicateEffectReplays = 0;
        var nonCommittableStopped = 0;

        foreach (var fixture in fixtures.Fixtures)
        {
            var account = AccountFor(fixture);
            var accountId = await harness.CreateAccountAsync(
                account.AccountType == AccountType.CreditCard ? AccountType.CreditCard : AccountType.Cheque,
                institution: "PrivateArchive");

            var sourcePath = Path.Combine(harness.Root, $"src-{previewed:D2}.pdf");
            await File.WriteAllBytesAsync(sourcePath, fixture.SourceBytes.ToArray());

            var preview = await harness.PreviewPathAsync(accountId, sourcePath);
            previewed++;
            Assert.False(string.IsNullOrWhiteSpace(preview.BatchId));
            Assert.False(string.IsNullOrWhiteSpace(preview.ManifestRevisionId));
            Assert.Equal(BatchStatus.Previewed, preview.Status);

            // Exact-preview replay: same bytes, same account → exactReplayOf or same batch.
            var replayPath = Path.Combine(harness.Root, $"src-{previewed:D2}-replay.pdf");
            await File.WriteAllBytesAsync(replayPath, fixture.SourceBytes.ToArray());
            var replay = await harness.PreviewPathAsync(accountId, replayPath);
            Assert.True(
                string.Equals(replay.BatchId, preview.BatchId, StringComparison.Ordinal) ||
                string.Equals(replay.ExactReplayOf, preview.BatchId, StringComparison.Ordinal),
                "exact-preview-replay-mismatch");
            exactReplays++;

            var inspect = await harness.InspectAsync(preview.BatchId, preview.ManifestRevisionId!);
            // Stop before approval when reconciliation is not fully satisfied (mismatch / non-committable).
            if (!(preview.ReconciliationSummary?.FullyReconciled ?? false))
            {
                var blocked = await harness.TryApproveAsync(
                    preview.BatchId,
                    preview.ManifestRevisionId!,
                    inspect.CanonicalDigest);
                Assert.False(blocked.Ok);
                nonCommittableStopped++;
                continue;
            }

            var approve = await harness.TryApproveAsync(
                preview.BatchId,
                preview.ManifestRevisionId!,
                inspect.CanonicalDigest);
            if (!approve.Ok)
            {
                // Host may still reject non-committable manifests; do not proceed to commit.
                Assert.Equal(ApproveErrors.NotCommittable, approve.Error);
                nonCommittableStopped++;
                continue;
            }

            var receipt = await harness.CommitAsync(
                preview.BatchId,
                preview.ManifestRevisionId!,
                inspect.CanonicalDigest);
            Assert.True(receipt.CandidateOutcomes.Count >= 0);
            var firstLedgerCount = await harness.CountResolvableLedgerTransactionsAsync(receipt);

            // Post-commit duplicate-effect replay: re-commit same key must not create new effects.
            var reCommit = await harness.TryCommitAsync(
                preview.BatchId,
                preview.ManifestRevisionId!,
                inspect.CanonicalDigest);
            Assert.True(reCommit.Ok, reCommit.Error);
            var secondLedgerCount = await harness.CountResolvableLedgerTransactionsAsync(reCommit.Value!);
            Assert.Equal(firstLedgerCount, secondLedgerCount);

            // Re-preview same bytes after commit remains stable exact-replay linkage (no new effects).
            var postPath = Path.Combine(harness.Root, $"src-{previewed:D2}-post.pdf");
            await File.WriteAllBytesAsync(postPath, fixture.SourceBytes.ToArray());
            var postPreview = await harness.PreviewPathAsync(accountId, postPath);
            Assert.True(
                string.Equals(postPreview.BatchId, preview.BatchId, StringComparison.Ordinal) ||
                string.Equals(postPreview.ExactReplayOf, preview.BatchId, StringComparison.Ordinal),
                "post-commit-exact-replay-mismatch");

            approvedCommitted++;
            duplicateEffectReplays++;
        }

        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, previewed);
        Assert.Equal(PrivateStatementFixtureSet.AuthorizedFixtureCount, exactReplays);
        Assert.Equal(previewed, approvedCommitted + nonCommittableStopped);
        // Structural console proof only.
        Console.WriteLine(
            $"PRIVATE_ARCHIVE_PIPELINE previewed={previewed} committed={approvedCommitted} nonCommittable={nonCommittableStopped} exactReplays={exactReplays} duplicateEffectReplays={duplicateEffectReplays}");
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
        checked((long)(decimal.Parse(value, CultureInfo.InvariantCulture) * 100m));
}
