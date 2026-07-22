using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Domain.Ledger.Evidence;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Reconciliation;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class ReconciliationProjectionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-reconciliation-projection-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private AccountStore accountStore = null!;
    private EvidenceStore evidenceStore = null!;
    private TransactionStore transactionStore = null!;
    private ReconciliationProjectionHandler handler = null!;
    private ReconciliationProjectionOperationModule module = null!;
    private int accountSequence;

    // FR-LEDGER-RECONCILIATION-PROJECTION and TC-LEDGER-RECONCILIATION-PROJECTION-CONTRACT
    [Theory]
    [InlineData("none", ReconciliationProjectionOutcome.NoCandidate, 0, 0)]
    [InlineData("exact", ReconciliationProjectionOutcome.UniqueCandidate, 1, 0)]
    [InlineData("amount-guard", ReconciliationProjectionOutcome.GuardOnly, 0, 1)]
    [InlineData("sign-guard", ReconciliationProjectionOutcome.GuardOnly, 0, 1)]
    [InlineData("date-guard", ReconciliationProjectionOutcome.GuardOnly, 0, 1)]
    [InlineData("multiple-exact", ReconciliationProjectionOutcome.Ambiguous, 2, 0)]
    [InlineData("exact-and-guard", ReconciliationProjectionOutcome.Ambiguous, 1, 1)]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_classifies_exact_guard_and_ambiguous_matrices(
        string scenario,
        ReconciliationProjectionOutcome expectedOutcome,
        int expectedExact,
        int expectedGuards)
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        switch (scenario)
        {
            case "exact":
                await SeedTransaction(accountId, -1234, "2026-07-10");
                break;
            case "amount-guard":
                await SeedTransaction(accountId, -9999, "2026-07-10");
                break;
            case "sign-guard":
                await SeedTransaction(accountId, 1234, "2026-07-10");
                break;
            case "date-guard":
                await SeedTransaction(accountId, -1234, "2026-07-11");
                break;
            case "multiple-exact":
                await SeedTransaction(accountId, -1234, "2026-07-10");
                await SeedTransaction(accountId, -1234, "2026-07-10");
                break;
            case "exact-and-guard":
                await SeedTransaction(accountId, -1234, "2026-07-10");
                await SeedTransaction(accountId, -5678, "2026-07-10");
                break;
        }

        var result = Success(await Project(evidence));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedExact, result.ExactCandidates.Count);
        Assert.Equal(expectedGuards, result.GuardCandidates.Count);
        Assert.True(result.AdvisoryOnly);
        Assert.False(result.GrantsAutomaticAuthority);
    }

    // TC-LEDGER-RECONCILIATION-PROJECTION-CONTRACT
    [Fact]
    public async Task TC_LEDGER_RECONCILIATION_PROJECTION_CONTRACT_reports_complete_exact_and_guard_comparison_basis()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -9999, "2026-07-11");

        var result = Success(await Project(evidence));
        var exact = Assert.Single(result.ExactCandidates);
        var guard = Assert.Single(result.GuardCandidates);

        Assert.Equal(ReconciliationCandidateKind.Exact, exact.Kind);
        Assert.Equal(new(true, true, true, true), exact.Basis);
        Assert.Equal([ReconciliationCandidateReason.ExactCompatible], exact.Reasons);
        Assert.Equal(ReconciliationCandidateKind.Guard, guard.Kind);
        Assert.Equal(new(true, true, false, false), guard.Basis);
        Assert.Equal(
            [ReconciliationCandidateReason.SignedAmountDiffers, ReconciliationCandidateReason.EffectiveDateDiffers],
            guard.Reasons);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_membership_order_and_token_are_repeatable()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -9999, "2026-07-11");
        await SeedTransaction(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -5678, "2026-07-12");

        var first = Success(await Project(evidence));
        var second = Success(await Project(evidence));

        Assert.Equal(first.AdvisoryToken, second.AdvisoryToken);
        Assert.Equal(
            JsonSerializer.Serialize(first, ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult),
            JsonSerializer.Serialize(second, ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult));
        Assert.Equal(first.GuardCandidates.OrderBy(x => x.TransactionId).ToArray(), first.GuardCandidates);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Theory]
    [InlineData("void")]
    [InlineData("superseded")]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_excludes_inactive_transactions_with_stable_reason(string action)
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        var transactionId = await SeedTransaction(accountId, -1234, "2026-07-10");
        await Terminate(transactionId, action);

        var result = Success(await Project(evidence));

        Assert.Empty(result.ExactCandidates);
        AssertExclusion(result, ReconciliationExclusionReason.InactiveTransaction, 1);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_excludes_wrong_account_without_returning_its_identity()
    {
        var accountId = await SeedAccount();
        var otherAccountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        var excludedId = await SeedTransaction(otherAccountId, -1234, "2026-07-10");

        var result = Success(await Project(evidence));

        AssertExclusion(result, ReconciliationExclusionReason.WrongAccount, 1);
        Assert.DoesNotContain(excludedId, JsonSerializer.Serialize(result, ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult), StringComparison.Ordinal);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_excludes_out_of_scope_transactions()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -1234, "2026-08-01");

        var result = Success(await Project(evidence));

        AssertExclusion(result, ReconciliationExclusionReason.OutsideStatementScope, 1);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_excludes_transactions_with_current_reconciliation_decisions()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        var transactionId = await SeedTransaction(accountId, -1234, "2026-07-10");
        await SeedDecision(transactionId);

        var result = Success(await Project(evidence));

        AssertExclusion(result, ReconciliationExclusionReason.AlreadyReconciled, 1);
        Assert.Empty(result.ExactCandidates);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_excludes_actively_statement_confirmed_transactions()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        var transactionId = await SeedTransaction(accountId, -1234, "2026-07-10");
        await SeedDecision(transactionId, confirmingLink: true);

        var result = Success(await Project(evidence));

        AssertExclusion(result, ReconciliationExclusionReason.ActiveStatementConfirmation, 1);
        Assert.Empty(result.ExactCandidates);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_rejects_non_statement_evidence()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10", EvidenceKind.Receipt);

        AssertError(await Project(evidence), ReconciliationProjectionErrors.StatementEvidenceRequired);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public void FR_LEDGER_RECONCILIATION_PROJECTION_surfaces_currency_conflict_without_a_candidate()
    {
        var source = new ReconciliationProjectionSource(
            new("evidence", Digest(), "account", -1234, "ZAR", "2026-07-10", false, false),
            new("scope", "account", "2026-07-01", "2026-07-31"),
            [new("transaction", "account", -1234, "USD", "2026-07-10", true, false, false)]);

        var result = ManualReviewProjectionV1.Project(source);

        AssertExclusion(result, ReconciliationExclusionReason.CurrencyConflict, 1);
        Assert.Empty(result.ExactCandidates);
        Assert.Empty(result.GuardCandidates);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_returns_stable_not_found_errors()
    {
        var unknownEvidence = new StatementFixture(LedgerId.New().ToString(), LedgerId.New().ToString());
        AssertError(await Project(unknownEvidence), ReconciliationProjectionErrors.EvidenceNotFound);

        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        AssertError(
            await handler.HandleAsync(
                new(evidence.EvidenceId, LedgerId.New().ToString(), ManualReviewProjectionV1.PolicyId, ManualReviewProjectionV1.PolicyVersion),
                CancellationToken.None),
            ReconciliationProjectionErrors.ScopeNotFound);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_rejects_scope_account_conflict()
    {
        var accountId = await SeedAccount();
        var otherAccountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(
            otherAccountId,
            new(accountId, -1234, "ZAR", "2026-07-10", null, null, null, null));

        AssertError(await Project(evidence), ReconciliationProjectionErrors.ScopeConflict);
    }

    // FR-LEDGER-RECONCILIATION-PROJECTION
    [Theory]
    [InlineData("account")]
    [InlineData("currency")]
    [InlineData("amount")]
    [InlineData("date")]
    public async Task FR_LEDGER_RECONCILIATION_PROJECTION_requires_every_exact_comparison_field(string missing)
    {
        var accountId = await SeedAccount();
        var observation = new EvidenceObservation(
            missing == "account" ? null : accountId,
            missing == "amount" ? null : -1234,
            missing == "currency" ? null : "ZAR",
            missing == "date" ? null : "2026-07-10",
            null, null, null, null);
        var evidence = await SeedStatementEvidence(accountId, observation);

        AssertError(await Project(evidence), ReconciliationProjectionErrors.IncompleteObservation);
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Theory]
    [InlineData("unknown-policy", ManualReviewProjectionV1.PolicyVersion)]
    [InlineData(ManualReviewProjectionV1.PolicyId, "2.0")]
    public async Task DM_LEDGER_RECONCILIATION_HISTORY_rejects_unsupported_policy_identity(string policyId, string policyVersion)
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");

        AssertError(await Project(evidence, policyId, policyVersion), ReconciliationProjectionErrors.UnsupportedPolicy);
    }

    // NFR-LEDGER-LOCAL-PRIVACY and FR-LEDGER-RECONCILIATION-PROJECTION
    [Theory]
    [InlineData("provider")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("rawPayload")]
    [InlineData("sender")]
    [InlineData("description")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_closed_projection_input_rejects_provider_and_payload_fields(string field)
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        var before = await MutationCounts();
        var input = JsonSerializer.SerializeToElement(new GetReconciliationCandidatesInput(evidence.EvidenceId, evidence.ScopeId, ManualReviewProjectionV1.PolicyId, ManualReviewProjectionV1.PolicyVersion));
        var json = input.GetRawText().TrimEnd('}') + $",\"{field}\":\"forbidden\"}}";

        var result = await module.CandidatesAsync(new(JsonDocument.Parse(json).RootElement.Clone(), null, null), CancellationToken.None);

        AssertError(result, ReconciliationProjectionErrors.InvalidInput);
        Assert.Equal(before, await MutationCounts());
    }

    // DM-LEDGER-RECONCILIATION-HISTORY
    [Fact]
    public async Task DM_LEDGER_RECONCILIATION_HISTORY_projection_token_is_advisory_and_binds_evidence_scope_policy_and_candidates()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -1234, "2026-07-10");

        var result = Success(await Project(evidence));

        Assert.Equal(evidence.EvidenceId, result.EvidenceId);
        Assert.Equal(evidence.ScopeId, result.ScopeId);
        Assert.Equal(ManualReviewProjectionV1.PolicyId, result.PolicyId);
        Assert.Equal(ManualReviewProjectionV1.PolicyVersion, result.PolicyVersion);
        Assert.Matches("^[a-f0-9]{64}$", result.AdvisoryToken);
        Assert.True(result.AdvisoryOnly);
        Assert.False(result.GrantsAutomaticAuthority);
    }

    // TC-LEDGER-RECONCILIATION-PROJECTION-CONTRACT
    [Fact]
    public async Task TC_LEDGER_RECONCILIATION_PROJECTION_CONTRACT_query_performs_zero_writes()
    {
        var accountId = await SeedAccount();
        var evidence = await SeedStatementEvidence(accountId, -1234, "2026-07-10");
        await SeedTransaction(accountId, -1234, "2026-07-10");
        var before = await MutationCounts();

        await Project(evidence);

        Assert.Equal(before, await MutationCounts());
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        accountStore = new(database, factory);
        evidenceStore = new(database, factory);
        transactionStore = new(database, factory);
        var projectionStore = new ReconciliationProjectionStore(database, factory, evidenceStore, transactionStore);
        handler = new(projectionStore);
        module = new(handler);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<string> SeedAccount()
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var accountId = LedgerId.New().ToString();
        var suffix = (1000 + Interlocked.Increment(ref accountSequence)).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Account " + suffix, AccountType.Cheque, "****" + suffix, "ZAR"), out var account, out _));
        await accountStore.InsertAsync(connection, transaction, accountId, LedgerId.New().ToString(), account!, "actor", At(0), CancellationToken.None);
        await transaction.CommitAsync();
        return accountId;
    }

    private async Task<string> SeedTransaction(string accountId, long amount, string date)
    {
        var input = new RecordTransactionInput(
            accountId, Money.FromMinorUnits(amount).ToString(), "ZAR", date, null, "private source description",
            null, null, new(EvidenceKind.AgentCapture, Digest(), null, null, null));
        Assert.True(TransactionFact.TryCreate(input, out var fact, out _));
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await transactionStore.InsertFactAndDefaultsAsync(
            connection, transaction, transactionId, LedgerId.New().ToString(), null, LedgerId.New().ToString(),
            fact!, At(1), "ubuntu", "actor", CancellationToken.None);
        await transaction.CommitAsync();
        return transactionId;
    }

    private Task<StatementFixture> SeedStatementEvidence(string accountId, long amount, string date, EvidenceKind kind = EvidenceKind.StatementRow) =>
        SeedStatementEvidence(accountId, new(accountId, amount, "ZAR", date, null, null, null, null), kind);

    private async Task<StatementFixture> SeedStatementEvidence(string scopeAccountId, EvidenceObservation observation, EvidenceKind kind = EvidenceKind.StatementRow)
    {
        var input = new RegisterEvidenceInput(kind, Digest(), null, Digest(), observation);
        Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out _));
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var detail = await evidenceStore.RegisterInitialAsync(connection, transaction, identity!, input, "actor", At(2), CancellationToken.None);
        var scopeId = LedgerId.New().ToString();
        await Execute(connection, transaction, """
            INSERT INTO statement_scope(scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at)
            VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', 'manifest:private', 'open', 'actor', $at);
            INSERT INTO statement_scope_evidence(scope_id, evidence_id) VALUES ($scopeId, $evidenceId);
            """, ("$scopeId", scopeId), ("$accountId", scopeAccountId), ("$at", At(2)), ("$evidenceId", detail.EvidenceId));
        await transaction.CommitAsync();
        return new(detail.EvidenceId, scopeId);
    }

    private async Task Terminate(string transactionId, string action)
    {
        var detail = (await transactionStore.GetAsync(transactionId, false, CancellationToken.None))!;
        var replacementId = action == "superseded"
            ? await SeedTransaction(detail.AccountId, -9999, "2026-08-01")
            : null;
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Execute(connection, transaction, """
            INSERT INTO transaction_lifecycle_event(
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $action, $replacementId, NULL, 'test lifecycle', 'actor', $at);
            """, ("$eventId", LedgerId.New().ToString()), ("$transactionId", transactionId), ("$action", action),
            ("$replacementId", replacementId ?? (object)DBNull.Value), ("$at", At(3)));
        await transaction.CommitAsync();
    }

    private async Task SeedDecision(string transactionId, bool confirmingLink = false)
    {
        var transaction = (await transactionStore.GetAsync(transactionId, false, CancellationToken.None))!;
        var evidence = await SeedStatementEvidence(transaction.AccountId, -1234, transaction.EffectiveDate);
        await using var connection = await Open();
        await using var dbTransaction = connection.BeginTransaction();
        var decisionId = LedgerId.New().ToString();
        await Execute(connection, dbTransaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $transactionId, 'deterministic_match', 'test-policy', '1.0',
                    'exact', 1, 'test decision', 'actor', $at, NULL);
            """, ("$decisionId", decisionId), ("$evidenceId", evidence.EvidenceId), ("$transactionId", transactionId), ("$at", At(4)));
        if (confirmingLink)
        {
            await Execute(connection, dbTransaction, """
                INSERT INTO evidence_link_event(
                    link_event_id, evidence_id, transaction_id, role, action, decision_id,
                    reason, recorded_by, recorded_at, previous_link_event_id)
                VALUES ($linkId, $evidenceId, $transactionId, 'confirming', 'link', $decisionId,
                        'test confirmation', 'actor', $at, NULL);
                """, ("$linkId", LedgerId.New().ToString()), ("$evidenceId", evidence.EvidenceId), ("$transactionId", transactionId), ("$decisionId", decisionId), ("$at", At(4)));
        }

        await dbTransaction.CommitAsync();
    }

    private Task<CommandResult<JsonElement>> Project(
        StatementFixture evidence,
        string policyId = ManualReviewProjectionV1.PolicyId,
        string policyVersion = ManualReviewProjectionV1.PolicyVersion) =>
        handler.HandleAsync(new(evidence.EvidenceId, evidence.ScopeId, policyId, policyVersion), CancellationToken.None);

    private static ReconciliationProjectionResult Success(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult)!;
    }

    private static void AssertError(CommandResult<JsonElement> result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.ErrorCode);
    }

    private static void AssertExclusion(ReconciliationProjectionResult result, ReconciliationExclusionReason reason, int count) =>
        Assert.Contains(result.Exclusions, item => item.Reason == reason && item.Count == count);

    private async Task<IReadOnlyDictionary<string, long>> MutationCounts()
    {
        var tables = new[]
        {
            "transaction_fact", "evidence_record", "evidence_link_event", "reconciliation_decision",
            "coverage_entry", "reconciliation_exception", "idempotency_record", "logical_effect", "query_snapshot"
        };
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await Open();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            result.Add(table, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        return result;
    }

    private async Task<SqliteConnection> Open() => await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
    private static string At(int second) => $"2026-07-22T00:00:{second:D2}Z";
    private sealed record StatementFixture(string EvidenceId, string ScopeId);
}
