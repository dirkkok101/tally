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
public sealed class ReconciliationCoverageOperationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-reconciliation-coverage-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private AccountStore accountStore = null!;
    private EvidenceStore evidenceStore = null!;
    private TransactionStore transactionStore = null!;
    private CompleteStatementCoverageHandler completeHandler = null!;
    private GetStatementCoverageHandler getHandler = null!;
    private ReconciliationCoverageOperationModule module = null!;
    private int sequence;

    [Fact]
    public void FR_LEDGER_RECONCILIATION_COVERAGE_normalizes_the_closed_completion_contract()
    {
        var input = new CompleteStatementCoverageInput(
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            "2026-07-01",
            "2026-07-31",
            "statement:manifest",
            [LedgerId.New().ToString(), LedgerId.New().ToString()],
            StatementCoveragePolicy.PolicyId,
            StatementCoveragePolicy.PolicyVersion);

        var accepted = StatementCoveragePolicy.TryNormalize(input, out var normalized, out var error);

        Assert.True(accepted, error);
        Assert.NotNull(normalized);
        Assert.Equal(normalized.ExpectedEvidenceIds.Order(StringComparer.Ordinal), normalized.ExpectedEvidenceIds);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_incomplete_scope_cannot_create_absence()
    {
        var prior = await SeedTransaction(await SeedAccount(), -1234, "2026-07-10", At(1));
        var scope = await SeedScope(prior.AccountId, "open");
        var before = await MutationCounts();

        var result = await Complete(scope);

        AssertError(result, ReconciliationCoverageErrors.ScopeIncomplete);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_requires_one_durable_outcome_per_statement_row()
    {
        var scope = await SeedScope(await SeedAccount(), "completed");
        var before = await MutationCounts();

        var result = await Complete(scope);

        AssertError(result, ReconciliationCoverageErrors.MissingOutcome);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("scope", ReconciliationCoverageErrors.InvalidInput)]
    [InlineData("account", ReconciliationCoverageErrors.InvalidInput)]
    [InlineData("period", ReconciliationCoverageErrors.InvalidInput)]
    [InlineData("manifest", ReconciliationCoverageErrors.InvalidInput)]
    [InlineData("duplicate_evidence", ReconciliationCoverageErrors.InvalidInput)]
    [InlineData("policy", ReconciliationCoverageErrors.PolicyUnsupported)]
    public void FR_LEDGER_RECONCILIATION_COVERAGE_rejects_invalid_or_unsupported_contracts(
        string scenario,
        string expectedError)
    {
        var evidenceId = LedgerId.New().ToString();
        var input = new CompleteStatementCoverageInput(
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            "2026-07-01",
            "2026-07-31",
            "statement:manifest",
            [evidenceId],
            StatementCoveragePolicy.PolicyId,
            StatementCoveragePolicy.PolicyVersion) with
        {
            ScopeId = scenario == "scope" ? "invalid" : LedgerId.New().ToString(),
            AccountId = scenario == "account" ? "invalid" : LedgerId.New().ToString(),
            PeriodStart = scenario == "period" ? "2026-08-01" : "2026-07-01",
            PeriodEnd = "2026-07-31",
            ManifestOpaqueReference = scenario == "manifest" ? " " : "statement:manifest",
            ExpectedEvidenceIds = scenario == "duplicate_evidence" ? [evidenceId, evidenceId] : [evidenceId],
            PolicyId = scenario == "policy" ? "unsupported" : StatementCoveragePolicy.PolicyId
        };

        var accepted = StatementCoveragePolicy.TryNormalize(input, out var normalized, out var error);

        Assert.False(accepted);
        Assert.Null(normalized);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("replaced", ReconciliationCoverageErrors.ScopeInactive)]
    [InlineData("account", ReconciliationCoverageErrors.ScopeConflict)]
    [InlineData("period", ReconciliationCoverageErrors.ScopeConflict)]
    [InlineData("manifest", ReconciliationCoverageErrors.ScopeConflict)]
    [InlineData("evidence", ReconciliationCoverageErrors.EvidenceSetChanged)]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_revalidates_the_complete_scope_inside_the_writer(
        string scenario,
        string expectedError)
    {
        var scope = await SeedScope(await SeedAccount(), scenario == "replaced" ? "replaced" : "completed");
        var input = CoverageInput(scope) with
        {
            AccountId = scenario == "account" ? LedgerId.New().ToString() : scope.AccountId,
            PeriodEnd = scenario == "period" ? "2026-07-30" : scope.PeriodEnd,
            ManifestOpaqueReference = scenario == "manifest" ? "different" : scope.ManifestReference,
            ExpectedEvidenceIds = scenario == "evidence" ? [LedgerId.New().ToString()] : scope.EvidenceIds
        };
        var before = await MutationCounts();

        var result = await Complete(scope, input: input);

        AssertError(result, expectedError);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("date")]
    [InlineData("kind")]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_rejects_incompatible_scope_evidence(string scenario)
    {
        var accountId = await SeedAccount();
        var otherAccountId = scenario == "account" ? await SeedAccount() : accountId;
        var scope = await SeedScope(
            accountId,
            "completed",
            evidenceAccountId: otherAccountId,
            evidenceDate: scenario == "date" ? "2026-08-01" : null,
            evidenceKind: scenario == "kind" ? EvidenceKind.Receipt : EvidenceKind.StatementRow);
        var before = await MutationCounts();

        var result = await Complete(scope);

        AssertError(result, ReconciliationCoverageErrors.ScopeConflict);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_rejects_a_decision_from_another_scope()
    {
        var scope = await SeedScope(await SeedAccount(), "completed");
        await SeedDecision(scope, "exception", authorityScopeId: LedgerId.New().ToString());
        var before = await MutationCounts();

        var result = await Complete(scope);

        AssertError(result, ReconciliationCoverageErrors.ScopeConflict);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("confirmed", StatementCoverageOutcome.ConfirmedExisting)]
    [InlineData("corrected", StatementCoverageOutcome.CorrectedFromStatement)]
    [InlineData("statement_only", StatementCoverageOutcome.StatementOnly)]
    [InlineData("ambiguous", StatementCoverageOutcome.Ambiguous)]
    [InlineData("exception", StatementCoverageOutcome.Exception)]
    [InlineData("owner_confirmed", StatementCoverageOutcome.OwnerConfirmedMatch)]
    public async Task TC_LEDGER_RECONCILIATION_COVERAGE_CONTRACT_maps_every_statement_row_class(
        string scenario,
        StatementCoverageOutcome expected)
    {
        var accountId = await SeedAccount();
        TransactionFixture? prior = null;
        if (scenario is "confirmed" or "corrected" or "owner_confirmed")
            prior = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var scope = await SeedScope(accountId, "completed");
        TransactionFixture? active = scenario switch
        {
            "corrected" or "statement_only" => await SeedTransaction(accountId, -1234, "2026-07-10", At(3)),
            "confirmed" or "owner_confirmed" => prior,
            _ => null
        };
        await SeedDecision(scope, scenario, prior, active);

        var summary = Success(await Complete(scope));

        var row = Assert.Single(summary.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.StatementRow);
        Assert.Equal(expected, row.Outcome);
        Assert.Equal(scope.EvidenceIds[0], row.StableId);
        Assert.Equal(expected, Assert.Single(summary.History, item => item.Kind == StatementCoverageMemberKind.StatementRow).Outcome);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_records_each_unconfirmed_eligible_transaction_as_absent()
    {
        var accountId = await SeedAccount();
        var absent = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var scope = await SeedScope(accountId, "completed");
        await SeedDecision(scope, "ambiguous");

        var summary = Success(await Complete(scope));

        Assert.Equal(1, summary.EvidenceCount);
        Assert.Equal(1, summary.EligibleTransactionCount);
        var transaction = Assert.Single(summary.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.EligibleTransaction);
        Assert.Equal(absent.TransactionId, transaction.StableId);
        Assert.Equal(StatementCoverageOutcome.RecordedAbsentFromStatement, transaction.Outcome);
        Assert.Equal(StatementCoveragePolicy.RecordedAbsentReason, transaction.Reason);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM coverage_entry WHERE outcome = 'recorded_absent_from_statement';"));
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_does_not_promote_a_post_scope_match_to_prior_membership()
    {
        var accountId = await SeedAccount();
        var scope = await SeedScope(accountId, "completed");
        var target = await SeedTransaction(accountId, -1234, "2026-07-10", At(3));
        await SeedDecision(scope, "owner_confirmed", active: target);

        var summary = Success(await Complete(scope));

        Assert.Equal(0, summary.EligibleTransactionCount);
        Assert.DoesNotContain(summary.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.EligibleTransaction);
        Assert.Equal(StatementCoverageOutcome.OwnerConfirmedMatch, Assert.Single(summary.CurrentMembers).Outcome);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_same_key_returns_the_original_coverage()
    {
        var scope = await SeedCompletedAmbiguousScope();

        var first = Success(await Complete(scope, key: "same-key"));
        var replay = Success(await Complete(scope, key: "same-key"));

        Assert.Equal(
            JsonSerializer.Serialize(first, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary),
            JsonSerializer.Serialize(replay, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary));
        Assert.Equal(first.History.Count, await Scalar("SELECT COUNT(*) FROM coverage_entry;"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_cross_key_exact_replay_returns_one_effect()
    {
        var scope = await SeedCompletedAmbiguousScope();

        var first = Success(await Complete(scope, key: "first-key"));
        var replay = Success(await Complete(scope, key: "second-key"));

        Assert.Equal(first.CompletedAt, replay.CompletedAt);
        Assert.Equal(first.History.Count, await Scalar("SELECT COUNT(*) FROM coverage_entry;"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM logical_effect WHERE effect_type = 'statement_coverage_completion';"));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_cross_key_completion_conflicts_without_mutation()
    {
        var scope = await SeedCompletedAmbiguousScope();
        Success(await Complete(scope, key: "first-key"));
        var before = await MutationCounts();
        var changed = CoverageInput(scope) with { ManifestOpaqueReference = "changed" };

        var result = await Complete(scope, key: "second-key", input: changed);

        AssertError(result, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_get_returns_the_durable_summary()
    {
        var scope = await SeedCompletedAmbiguousScope();
        var completed = Success(await Complete(scope));

        var queried = Success(await Get(scope.ScopeId));

        Assert.Equal(
            JsonSerializer.Serialize(completed, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary),
            JsonSerializer.Serialize(queried, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_requires_actor_and_idempotency_key(bool hasActor, bool hasKey)
    {
        var scope = await SeedCompletedAmbiguousScope();
        var before = await MutationCounts();

        var result = await completeHandler.HandleAsync(
            CoverageInput(scope),
            hasActor ? new("owner", "dirk") : null,
            hasKey ? "key" : null,
            CancellationToken.None);

        AssertError(result, ReconciliationCoverageErrors.InvalidInput);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("post_scope")]
    [InlineData("outside_period")]
    [InlineData("wrong_account")]
    [InlineData("inactive")]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_does_not_infer_false_absence(string scenario)
    {
        var accountId = await SeedAccount();
        var otherAccountId = scenario == "wrong_account" ? await SeedAccount() : accountId;
        var transaction = await SeedTransaction(
            otherAccountId,
            -1234,
            scenario == "outside_period" ? "2026-08-01" : "2026-07-10",
            scenario == "post_scope" ? At(3) : At(1));
        if (scenario == "inactive") await Void(transaction.TransactionId);
        var scope = await SeedScope(accountId, "completed");
        await SeedDecision(scope, "exception");

        var summary = Success(await Complete(scope));

        Assert.Equal(0, summary.EligibleTransactionCount);
        Assert.DoesNotContain(summary.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.EligibleTransaction);
        Assert.Equal(0, await Scalar("SELECT COUNT(*) FROM coverage_entry WHERE outcome = 'recorded_absent_from_statement';"));
    }

    [Fact]
    public async Task TC_LEDGER_RECONCILIATION_COVERAGE_CONTRACT_counts_exact_row_and_transaction_membership()
    {
        var accountId = await SeedAccount();
        var confirmed = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var absent = await SeedTransaction(accountId, -2222, "2026-07-11", At(1));
        var scope = await SeedScope(accountId, "completed", rows: 2);
        await SeedDecision(scope, "confirmed", active: confirmed, evidenceIndex: 0);
        await SeedDecision(scope, "exception", evidenceIndex: 1);

        var summary = Success(await Complete(scope));

        Assert.Equal(2, summary.EvidenceCount);
        Assert.Equal(2, summary.EligibleTransactionCount);
        Assert.Equal(4, summary.CurrentMembers.Count);
        Assert.Equal(1, Count(summary, StatementCoverageMemberKind.StatementRow, StatementCoverageOutcome.ConfirmedExisting));
        Assert.Equal(1, Count(summary, StatementCoverageMemberKind.StatementRow, StatementCoverageOutcome.Exception));
        Assert.Equal(1, Count(summary, StatementCoverageMemberKind.EligibleTransaction, StatementCoverageOutcome.StatementReconciled));
        Assert.Equal(1, Count(summary, StatementCoverageMemberKind.EligibleTransaction, StatementCoverageOutcome.RecordedAbsentFromStatement));
        Assert.Contains(summary.CurrentMembers, item => item.StableId == absent.TransactionId);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_rejects_one_transaction_confirmed_by_two_rows()
    {
        var accountId = await SeedAccount();
        var target = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var scope = await SeedScope(accountId, "completed", rows: 2);
        await SeedDecision(scope, "confirmed", active: target, evidenceIndex: 0);
        await SeedDecision(scope, "owner_confirmed", active: target, evidenceIndex: 1, addLink: false);
        var before = await MutationCounts();

        var result = await Complete(scope);

        AssertError(result, ReconciliationCoverageErrors.DuplicateTransactionOutcome);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_later_owner_confirmation_changes_current_not_history()
    {
        var accountId = await SeedAccount();
        var target = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var scope = await SeedScope(accountId, "completed");
        var initial = await SeedDecision(scope, "ambiguous");
        var completed = Success(await Complete(scope));

        await AppendOwnerConfirmation(scope, initial.DecisionId, target);
        var current = Success(await Get(scope.ScopeId));

        Assert.Equal(StatementCoverageOutcome.Ambiguous, Assert.Single(completed.History, item => item.Kind == StatementCoverageMemberKind.StatementRow).Outcome);
        Assert.Equal(StatementCoverageOutcome.Ambiguous, Assert.Single(current.History, item => item.Kind == StatementCoverageMemberKind.StatementRow).Outcome);
        Assert.Equal(StatementCoverageOutcome.OwnerConfirmedMatch, Assert.Single(current.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.StatementRow).Outcome);
        Assert.Equal(StatementCoverageOutcome.StatementReconciled, Assert.Single(current.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.EligibleTransaction).Outcome);
        Assert.Equal(completed.History.Count, current.History.Count);
        Assert.Equal(
            TransactionReconciliationState.OwnerConfirmedMatch,
            (await transactionStore.GetAsync(target.TransactionId, false, CancellationToken.None))!.ReconciliationState);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_later_statement_correction_changes_current_not_history()
    {
        var accountId = await SeedAccount();
        var prior = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var scope = await SeedScope(accountId, "completed");
        var initial = await SeedDecision(scope, "confirmed", active: prior);
        var completed = Success(await Complete(scope));
        var replacement = await SeedTransaction(accountId, -1200, "2026-07-10", At(4));

        await AppendCorrection(scope, initial, prior, replacement);
        var current = Success(await Get(scope.ScopeId));

        var row = Assert.Single(current.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.StatementRow);
        Assert.Equal(StatementCoverageOutcome.CorrectedFromStatement, row.Outcome);
        Assert.Equal(prior.TransactionId, row.PriorTransactionId);
        Assert.Equal(replacement.TransactionId, row.ActiveTransactionId);
        Assert.Equal(StatementCoverageOutcome.ConfirmedExisting, Assert.Single(current.History, item => item.Kind == StatementCoverageMemberKind.StatementRow).Outcome);
        Assert.Equal(StatementCoverageOutcome.StatementReconciled, Assert.Single(current.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.EligibleTransaction).Outcome);
        Assert.Equal(completed.History.Count, current.History.Count);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_owner_link_replacement_moves_current_transaction_coverage()
    {
        var accountId = await SeedAccount();
        var original = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var replacement = await SeedTransaction(accountId, -1234, "2026-07-10", At(1));
        var scope = await SeedScope(accountId, "completed");
        var initial = await SeedDecision(scope, "confirmed", active: original);
        Success(await Complete(scope));

        await AppendOwnerReplacement(scope, initial, original, replacement);
        var current = Success(await Get(scope.ScopeId));

        var transactions = current.CurrentMembers
            .Where(item => item.Kind == StatementCoverageMemberKind.EligibleTransaction)
            .ToDictionary(item => item.StableId, StringComparer.Ordinal);
        Assert.Equal(StatementCoverageOutcome.RecordedAbsentFromStatement, transactions[original.TransactionId].Outcome);
        Assert.Equal(StatementCoverageOutcome.StatementReconciled, transactions[replacement.TransactionId].Outcome);
        Assert.Equal(StatementCoverageOutcome.OwnerConfirmedMatch, Assert.Single(current.CurrentMembers, item => item.Kind == StatementCoverageMemberKind.StatementRow).Outcome);
        Assert.Equal(
            TransactionReconciliationState.RecordedAbsentFromStatement,
            (await transactionStore.GetAsync(original.TransactionId, false, CancellationToken.None))!.ReconciliationState);
        Assert.Equal(
            TransactionReconciliationState.OwnerConfirmedMatch,
            (await transactionStore.GetAsync(replacement.TransactionId, false, CancellationToken.None))!.ReconciliationState);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_coverage_history_is_append_only(string action)
    {
        var scope = await SeedCompletedAmbiguousScope();
        Success(await Complete(scope));
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = action == "update"
            ? "UPDATE coverage_entry SET reason = 'changed' WHERE scope_id = $scopeId;"
            : "DELETE FROM coverage_entry WHERE scope_id = $scopeId;";
        command.Parameters.AddWithValue("$scopeId", scope.ScopeId);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_coverage_contract_rejects_unknown_payload_fields()
    {
        var scope = await SeedCompletedAmbiguousScope();
        var json = JsonSerializer.Serialize(CoverageInput(scope), ReconciliationCoverageJsonContext.Default.CompleteStatementCoverageInput);
        json = json.TrimEnd('}') + ",\"rawStatement\":\"forbidden\"}";

        var result = await module.HandleAsync(
            ReconciliationCoverageOperationModule.CompleteOperationId,
            new(JsonDocument.Parse(json).RootElement.Clone(), new("owner", "dirk"), "privacy-key"),
            CancellationToken.None);

        AssertError(result, ReconciliationCoverageErrors.InvalidInput);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_COVERAGE_get_rejects_invalid_or_uncompleted_scope()
    {
        AssertError(await Get("invalid"), ReconciliationCoverageErrors.InvalidInput);
        var scope = await SeedScope(await SeedAccount(), "completed");
        AssertError(await Get(scope.ScopeId), ReconciliationCoverageErrors.NotFound);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        accountStore = new(database, factory);
        evidenceStore = new(database, factory);
        transactionStore = new(database, factory);
        var store = new ReconciliationCoverageStore(database, factory, transactionStore);
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        completeHandler = new(executor, store);
        getHandler = new(store);
        module = new(completeHandler, getHandler);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<CommandResult<JsonElement>> Complete(
        ScopeFixture scope,
        string key = "coverage-key",
        CompleteStatementCoverageInput? input = null) =>
        await completeHandler.HandleAsync(
            input ?? CoverageInput(scope),
            new("owner", "dirk", "coverage-run"),
            key,
            CancellationToken.None);

    private Task<CommandResult<JsonElement>> Get(string scopeId) =>
        getHandler.HandleAsync(new(scopeId), CancellationToken.None);

    private static CompleteStatementCoverageInput CoverageInput(ScopeFixture scope) => new(
        scope.ScopeId,
        scope.AccountId,
        scope.PeriodStart,
        scope.PeriodEnd,
        scope.ManifestReference,
        scope.EvidenceIds,
        StatementCoveragePolicy.PolicyId,
        StatementCoveragePolicy.PolicyVersion);

    private async Task<ScopeFixture> SeedCompletedAmbiguousScope()
    {
        var scope = await SeedScope(await SeedAccount(), "completed");
        await SeedDecision(scope, "ambiguous");
        return scope;
    }

    private async Task<ScopeFixture> SeedScope(
        string accountId,
        string status,
        int rows = 1,
        string? evidenceAccountId = null,
        string? evidenceDate = null,
        EvidenceKind evidenceKind = EvidenceKind.StatementRow)
    {
        var scopeId = LedgerId.New().ToString();
        var evidenceIds = new List<string>();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        for (var index = 0; index < rows; index++)
        {
            var observation = new EvidenceObservation(
                evidenceAccountId ?? accountId,
                -1234 - index,
                "ZAR",
                evidenceDate ?? $"2026-07-{10 + index:D2}",
                null,
                null,
                null,
                Digest());
            var input = new RegisterEvidenceInput(evidenceKind, Digest(), "statement:row", Digest(), observation);
            Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out _));
            var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, identity!, input, "actor", At(2), CancellationToken.None);
            evidenceIds.Add(evidence.EvidenceId);
        }

        await Execute(connection, transaction, """
            INSERT INTO statement_scope(scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at)
            VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', 'statement:manifest', $status, 'owner:dirk', $at);
            """, ("$scopeId", scopeId), ("$accountId", accountId), ("$status", status), ("$at", At(2)));
        foreach (var evidenceId in evidenceIds)
        {
            await Execute(connection, transaction,
                "INSERT INTO statement_scope_evidence(scope_id, evidence_id) VALUES ($scopeId, $evidenceId);",
                ("$scopeId", scopeId), ("$evidenceId", evidenceId));
        }

        await transaction.CommitAsync();
        return new(scopeId, accountId, "2026-07-01", "2026-07-31", "statement:manifest", evidenceIds);
    }

    private async Task<string> SeedAccount()
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var accountId = LedgerId.New().ToString();
        var suffix = (1000 + Interlocked.Increment(ref sequence)).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Coverage " + suffix, AccountType.Cheque, "****" + suffix, "ZAR"), out var account, out _));
        await accountStore.InsertAsync(connection, transaction, accountId, LedgerId.New().ToString(), account!, "actor", At(0), CancellationToken.None);
        await transaction.CommitAsync();
        return accountId;
    }

    private async Task<TransactionFixture> SeedTransaction(string accountId, long amount, string date, string recordedAt)
    {
        var input = new RecordTransactionInput(
            accountId,
            Money.FromMinorUnits(amount).ToString(),
            "ZAR",
            date,
            null,
            "agent-captured transaction",
            null,
            null,
            new(EvidenceKind.AgentCapture, Digest(), null, null, null));
        Assert.True(TransactionFact.TryCreate(input, out var fact, out _));
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await transactionStore.InsertFactAndDefaultsAsync(
            connection,
            transaction,
            transactionId,
            LedgerId.New().ToString(),
            null,
            LedgerId.New().ToString(),
            fact!,
            recordedAt,
            "ubuntu",
            "actor",
            CancellationToken.None);
        await transaction.CommitAsync();
        return new(transactionId, accountId);
    }

    private async Task<DecisionFixture> SeedDecision(
        ScopeFixture scope,
        string scenario,
        TransactionFixture? prior = null,
        TransactionFixture? active = null,
        int evidenceIndex = 0,
        bool addLink = true,
        string? authorityScopeId = null)
    {
        var evidenceId = scope.EvidenceIds[evidenceIndex];
        var decisionId = LedgerId.New().ToString();
        var (baseDisposition, detailDisposition, deterministic) = scenario switch
        {
            "confirmed" => ("deterministic_match", "confirmed_existing", 1),
            "corrected" => ("replaced", "corrected_from_statement", 0),
            "statement_only" => ("statement_only", "statement_only", 0),
            "ambiguous" => ("ambiguous", "ambiguous", 0),
            "exception" => ("exception", "exception", 0),
            "owner_confirmed" => ("owner_confirmed", "owner_confirmed_match", 0),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var activeId = active?.TransactionId;
        var priorId = scenario == "corrected" ? prior?.TransactionId : null;
        var reason = scenario + " coverage outcome";
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Execute(connection, transaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $activeId, $baseDisposition, 'test-policy', '1.0',
                    'coverage-test', $deterministic, $reason, 'owner:dirk', $at, NULL);
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, $detailDisposition, $priorId, $activeId,
                    $authority, $basis, 'v2', $at);
            """,
            ("$decisionId", decisionId),
            ("$evidenceId", evidenceId),
            ("$activeId", activeId ?? (object)DBNull.Value),
            ("$baseDisposition", baseDisposition),
            ("$deterministic", deterministic),
            ("$reason", reason),
            ("$at", At(3)),
            ("$detailDisposition", detailDisposition),
            ("$priorId", priorId ?? (object)DBNull.Value),
            ("$authority", deterministic == 1 ? "deterministic_policy" : "owner"),
            ("$basis", "scope:" + (authorityScopeId ?? scope.ScopeId) + "|evidence:test"));

        if (scenario == "corrected")
        {
            await Execute(connection, transaction, """
                INSERT INTO transaction_lifecycle_event(
                    lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                    reconciliation_decision_id, reason, actor, occurred_at)
                VALUES ($eventId, $priorId, 'statement_authoritative_replacement', $activeId,
                        $decisionId, $reason, 'owner:dirk', $at);
                """,
                ("$eventId", LedgerId.New().ToString()),
                ("$priorId", priorId!),
                ("$activeId", activeId!),
                ("$decisionId", decisionId),
                ("$reason", reason),
                ("$at", At(3)));
        }

        if (addLink && activeId is not null)
        {
            await Execute(connection, transaction, """
                INSERT INTO evidence_link_event(
                    link_event_id, evidence_id, transaction_id, role, action, decision_id,
                    reason, recorded_by, recorded_at, previous_link_event_id)
                VALUES ($linkId, $evidenceId, $activeId, 'confirming', 'link', $decisionId,
                        $reason, 'owner:dirk', $at, NULL);
                """,
                ("$linkId", LedgerId.New().ToString()),
                ("$evidenceId", evidenceId),
                ("$activeId", activeId),
                ("$decisionId", decisionId),
                ("$reason", reason),
                ("$at", At(3)));
        }

        if (scenario is "ambiguous" or "exception")
        {
            await Execute(connection, transaction, """
                INSERT INTO reconciliation_exception(
                    exception_id, scope_id, evidence_id, transaction_id, disposition,
                    reason, active_decision_id, recorded_by, recorded_at)
                VALUES ($exceptionId, $scopeId, $evidenceId, NULL, $disposition,
                        $reason, $decisionId, 'owner:dirk', $at);
                """,
                ("$exceptionId", LedgerId.New().ToString()),
                ("$scopeId", scope.ScopeId),
                ("$evidenceId", evidenceId),
                ("$disposition", scenario),
                ("$reason", reason),
                ("$decisionId", decisionId),
                ("$at", At(3)));
        }

        await transaction.CommitAsync();
        return new(decisionId, evidenceId, activeId);
    }

    private async Task AppendOwnerConfirmation(
        ScopeFixture scope,
        string previousDecisionId,
        TransactionFixture target)
    {
        var decisionId = LedgerId.New().ToString();
        var evidenceId = scope.EvidenceIds[0];
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Execute(connection, transaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $targetId, 'owner_confirmed', 'owner-decision', '1.0',
                    'coverage-later-confirm', 0, 'owner confirmed later', 'owner:dirk', $at, $previousDecisionId);
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'owner_confirmed_match', NULL, $targetId,
                    'owner', $basis, 'v2', $at);
            INSERT INTO evidence_link_event(
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $targetId, 'confirming', 'link', $decisionId,
                    'owner confirmed later', 'owner:dirk', $at, NULL);
            """,
            ("$decisionId", decisionId),
            ("$evidenceId", evidenceId),
            ("$targetId", target.TransactionId),
            ("$previousDecisionId", previousDecisionId),
            ("$basis", "scope:" + scope.ScopeId + "|evidence:test"),
            ("$linkId", LedgerId.New().ToString()),
            ("$at", At(5)));
        await transaction.CommitAsync();
    }

    private async Task AppendCorrection(
        ScopeFixture scope,
        DecisionFixture previous,
        TransactionFixture prior,
        TransactionFixture replacement)
    {
        var decisionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var previousLinkId = await Text(connection, transaction,
            "SELECT link_event_id FROM evidence_active_confirming_target WHERE evidence_id = $evidenceId;",
            ("$evidenceId", previous.EvidenceId));
        await Execute(connection, transaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', 'statement-correction', '1.0',
                    'coverage-later-correction', 0, 'statement corrected later', 'owner:dirk', $at, $previousDecisionId);
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'corrected_from_statement', $priorId, $replacementId,
                    'owner', $basis, 'v2', $at);
            INSERT INTO transaction_lifecycle_event(
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($lifecycleId, $priorId, 'statement_authoritative_replacement', $replacementId,
                    $decisionId, 'statement corrected later', 'owner:dirk', $at);
            INSERT INTO evidence_link_event(
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $replacementId, 'confirming', 'replace', $decisionId,
                    'statement corrected later', 'owner:dirk', $at, $previousLinkId);
            """,
            ("$decisionId", decisionId),
            ("$evidenceId", previous.EvidenceId),
            ("$replacementId", replacement.TransactionId),
            ("$priorId", prior.TransactionId),
            ("$previousDecisionId", previous.DecisionId),
            ("$basis", "scope:" + scope.ScopeId + "|evidence:test"),
            ("$lifecycleId", LedgerId.New().ToString()),
            ("$linkId", LedgerId.New().ToString()),
            ("$previousLinkId", previousLinkId),
            ("$at", At(5)));
        await transaction.CommitAsync();
    }

    private async Task AppendOwnerReplacement(
        ScopeFixture scope,
        DecisionFixture previous,
        TransactionFixture prior,
        TransactionFixture replacement)
    {
        var decisionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var previousLinkId = await Text(connection, transaction,
            "SELECT link_event_id FROM evidence_active_confirming_target WHERE evidence_id = $evidenceId;",
            ("$evidenceId", previous.EvidenceId));
        await Execute(connection, transaction, """
            INSERT INTO reconciliation_decision(
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', 'owner-decision', '1.0',
                    'coverage-owner-replacement', 0, 'owner replaced match', 'owner:dirk', $at, $previousDecisionId);
            INSERT INTO reconciliation_decision_authority(
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'replaced', $priorId, $replacementId,
                    'owner', $basis, 'v2', $at);
            INSERT INTO evidence_link_event(
                link_event_id, evidence_id, transaction_id, role, action, decision_id,
                reason, recorded_by, recorded_at, previous_link_event_id)
            VALUES ($linkId, $evidenceId, $replacementId, 'confirming', 'replace', $decisionId,
                    'owner replaced match', 'owner:dirk', $at, $previousLinkId);
            """,
            ("$decisionId", decisionId),
            ("$evidenceId", previous.EvidenceId),
            ("$replacementId", replacement.TransactionId),
            ("$priorId", prior.TransactionId),
            ("$previousDecisionId", previous.DecisionId),
            ("$basis", "scope:" + scope.ScopeId + "|evidence:test"),
            ("$linkId", LedgerId.New().ToString()),
            ("$previousLinkId", previousLinkId),
            ("$at", At(5)));
        await transaction.CommitAsync();
    }

    private async Task Void(string transactionId)
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Execute(connection, transaction, """
            INSERT INTO transaction_lifecycle_event(
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, 'void', NULL, NULL, 'test void', 'owner:dirk', $at);
            """,
            ("$eventId", LedgerId.New().ToString()),
            ("$transactionId", transactionId),
            ("$at", At(5)));
        await transaction.CommitAsync();
    }

    private async Task<IReadOnlyDictionary<string, long>> MutationCounts()
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "coverage_entry", "reconciliation_exception", "idempotency_record", "logical_effect" })
            result.Add(table, await Scalar($"SELECT COUNT(*) FROM {table};"));
        return result;
    }

    private async Task<long> Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Text(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (string)(await command.ExecuteScalarAsync())!;
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

    private static void AssertError(CommandResult<JsonElement> result, string expected)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorCode);
    }

    private static StatementCoverageSummary Success(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary)!;
    }

    private static int Count(
        StatementCoverageSummary summary,
        StatementCoverageMemberKind kind,
        StatementCoverageOutcome outcome) =>
        summary.Counts.Single(item => item.Kind == kind && item.Outcome == outcome).Count;

    private static string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
    private static string At(int second) => $"2026-07-22T00:00:{second:D2}Z";
    private sealed record ScopeFixture(string ScopeId, string AccountId, string PeriodStart, string PeriodEnd, string ManifestReference, IReadOnlyList<string> EvidenceIds);
    private sealed record TransactionFixture(string TransactionId, string AccountId);
    private sealed record DecisionFixture(string DecisionId, string EvidenceId, string? ActiveTransactionId);
}
