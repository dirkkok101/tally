using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
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
using Tally.Infrastructure.Storage.Dimensions;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Integration;

[SupportedOSPlatform("linux")]
public sealed class ReconciliationCrashAtomicityTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-reconciliation-crash-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private LedgerMutationExecutor executor = null!;
    private AccountStore accountStore = null!;
    private EvidenceStore evidenceStore = null!;
    private TransactionStore transactionStore = null!;
    private ReconciliationWriteStore writeStore = null!;
    private ReconciliationProjectionStore projectionStore = null!;
    private StatementAuthoritativeCorrectionCoordinator correctionCoordinator = null!;
    private StatementFixture statement = null!;

    [Fact]
    public void TC_LEDGER_RECONCILIATION_CRASH_ATOMICITY_exposes_every_base_write_boundary()
    {
        Assert.Equal(
            [
                ReconciliationWriteBoundary.StatementOnlyTransaction,
                ReconciliationWriteBoundary.Decision,
                ReconciliationWriteBoundary.DecisionAuthority,
                ReconciliationWriteBoundary.ConfirmingLink,
                ReconciliationWriteBoundary.Exception
            ],
            ReconciliationWriteStore.BaseWriteBoundaries);
    }

    [Theory]
    [InlineData(ReconciliationWriteBoundary.StatementOnlyTransaction)]
    [InlineData(ReconciliationWriteBoundary.Decision)]
    [InlineData(ReconciliationWriteBoundary.DecisionAuthority)]
    [InlineData(ReconciliationWriteBoundary.ConfirmingLink)]
    [InlineData(ReconciliationWriteBoundary.Exception)]
    public async Task TC_LEDGER_RECONCILIATION_CRASH_ATOMICITY_rolls_back_after_each_base_write_boundary(
        ReconciliationWriteBoundary crashAfter)
    {
        var before = await Counts();
        var request = new IdempotencyRequest(
            "1.0",
            "test.reconciliation.crash",
            "crash-" + crashAfter,
            "test:actor",
            JsonDocument.Parse($"{{\"boundary\":\"{crashAfter}\"}}").RootElement.Clone(),
            new("crash:" + statement.EvidenceId + ":" + crashAfter, "reconciliation_crash_test"));

        await Assert.ThrowsAsync<InjectedCrashException>(() => executor.ExecuteAsync(
            request,
            async (connection, transaction, token) =>
            {
                if (crashAfter == ReconciliationWriteBoundary.Exception)
                {
                    await WriteExceptionOutcome(connection, transaction, crashAfter, token);
                }
                else
                {
                    await WriteStatementOnlyOutcome(connection, transaction, crashAfter, token);
                }

                throw new InvalidOperationException("The requested crash boundary was not reached.");
            },
            CancellationToken.None));

        Assert.Equal(before, await Counts());
    }

    public static IEnumerable<object[]> CorrectionCrashBoundaries =>
        new[]
        {
            "replacement_fact", "decision", "supersession", "confirming_link",
            "pool", "payment", "correction", "idempotency"
        }.SelectMany(boundary => new[] { "before", "after" }.Select(timing => new object[] { boundary, timing }));

    [Theory]
    [MemberData(nameof(CorrectionCrashBoundaries))]
    public async Task TC_LEDGER_RECONCILIATION_CRASH_ATOMICITY_rolls_back_before_and_after_each_correction_boundary(
        string boundary,
        string timing)
    {
        var targetId = await SeedCorrectionTarget();
        var input = await CorrectionInput(targetId);
        var before = await Counts();
        await CreateCorrectionFailureTrigger(boundary, timing);

        await Assert.ThrowsAsync<SqliteException>(() => correctionCoordinator.HandleAsync(
            input,
            new("human", "correction-crash-test", "run-1"),
            $"correction-crash-{boundary}-{timing}",
            CancellationToken.None));

        Assert.Equal(before, await Counts());
        var target = await transactionStore.GetAsync(targetId, true, CancellationToken.None);
        Assert.Equal(TransactionLifecycleStatus.Active, target!.LifecycleStatus);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        executor = new(database, factory, new IdempotencyStore());
        accountStore = new(database, factory);
        evidenceStore = new(database, factory);
        transactionStore = new(database, factory);
        writeStore = new(evidenceStore, transactionStore);
        projectionStore = new(database, factory, evidenceStore, transactionStore);
        var decisionStore = new ReconciliationDecisionStore(database, factory, evidenceStore, transactionStore);
        var relationshipStore = new RelationshipStore(database, factory);
        var effectWriter = new StatementCorrectionEffectWriter(
            writeStore,
            decisionStore,
            transactionStore,
            new CategoryAllocationStore(database, factory),
            new PaymentAttributionStore(),
            new PaymentIdentityStore(database, factory),
            new PoolAssignmentStore(),
            relationshipStore);
        correctionCoordinator = new(
            executor,
            accountStore,
            projectionStore,
            writeStore,
            transactionStore,
            effectWriter);
        statement = await SeedStatement();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task WriteStatementOnlyOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationWriteBoundary crashAfter,
        CancellationToken cancellationToken)
    {
        var evidence = await writeStore.GetEvidenceAsync(connection, transaction, statement.EvidenceId, cancellationToken);
        Assert.True(ReconciliationDispositionPolicy.TryCreateStatementTransactionFact(statement.Fact, evidence!, out var fact));
        var transactionId = LedgerId.New().ToString();
        var decisionId = LedgerId.New().ToString();
        await writeStore.InsertStatementOnlyTransactionAsync(
            connection,
            transaction,
            transactionId,
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            fact!,
            "test:actor",
            At(3),
            cancellationToken);
        CrashAt(crashAfter, ReconciliationWriteBoundary.StatementOnlyTransaction);

        await writeStore.InsertDecisionAsync(
            connection,
            transaction,
            new(decisionId, statement.EvidenceId, transactionId, "statement_only", ManualReviewProjectionV1.PolicyId,
                ManualReviewProjectionV1.PolicyVersion, "crash boundary proof", "owner reviewed", "test:actor", At(3)),
            cancellationToken);
        CrashAt(crashAfter, ReconciliationWriteBoundary.Decision);

        await writeStore.InsertDecisionAuthorityAsync(
            connection,
            transaction,
            new(decisionId, "statement_only", transactionId, "statement:test-authority", At(3)),
            cancellationToken);
        CrashAt(crashAfter, ReconciliationWriteBoundary.DecisionAuthority);

        await writeStore.InsertConfirmingLinkAsync(
            connection,
            transaction,
            LedgerId.New().ToString(),
            statement.EvidenceId,
            transactionId,
            decisionId,
            "owner reviewed",
            "test:actor",
            At(3),
            cancellationToken);
        CrashAt(crashAfter, ReconciliationWriteBoundary.ConfirmingLink);
    }

    private async Task WriteExceptionOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReconciliationWriteBoundary crashAfter,
        CancellationToken cancellationToken)
    {
        var decisionId = LedgerId.New().ToString();
        await writeStore.InsertDecisionAsync(
            connection,
            transaction,
            new(decisionId, statement.EvidenceId, null, "exception", ManualReviewProjectionV1.PolicyId,
                ManualReviewProjectionV1.PolicyVersion, "crash boundary proof", "owner reviewed", "test:actor", At(3)),
            cancellationToken);
        await writeStore.InsertDecisionAuthorityAsync(
            connection,
            transaction,
            new(decisionId, "exception", null, "statement:test-authority", At(3)),
            cancellationToken);
        await writeStore.InsertExceptionAsync(
            connection,
            transaction,
            LedgerId.New().ToString(),
            statement.ScopeId,
            statement.EvidenceId,
            "exception",
            "TEST: owner reviewed",
            decisionId,
            "test:actor",
            At(3),
            cancellationToken);
        CrashAt(crashAfter, ReconciliationWriteBoundary.Exception);
    }

    private async Task<StatementFixture> SeedStatement()
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var accountId = LedgerId.New().ToString();
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Crash account", AccountType.Cheque, "****9001", "ZAR"), out var account, out _));
        await accountStore.InsertAsync(connection, transaction, accountId, LedgerId.New().ToString(), account!, "actor", At(0), CancellationToken.None);
        var observation = new EvidenceObservation(accountId, -1234, "ZAR", "2026-07-10", null, null, null, Digest());
        var input = new RegisterEvidenceInput(EvidenceKind.StatementRow, Digest(), "statement:crash", Digest(), observation);
        Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out _));
        var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, identity!, input, "actor", At(1), CancellationToken.None);
        var scopeId = LedgerId.New().ToString();
        await Execute(connection, transaction, """
            INSERT INTO statement_scope(scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at)
            VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', 'statement:crash', 'open', 'actor', $at);
            INSERT INTO statement_scope_evidence(scope_id, evidence_id) VALUES ($scopeId, $evidenceId);
            """, ("$scopeId", scopeId), ("$accountId", accountId), ("$at", At(1)), ("$evidenceId", evidence.EvidenceId));
        await transaction.CommitAsync();
        return new(evidence.EvidenceId, scopeId, new(accountId, "-12.34", "ZAR", "2026-07-10", null, "statement transaction"));
    }

    private async Task<string> SeedCorrectionTarget()
    {
        var input = new Tally.Contracts.Ledger.Transactions.RecordTransactionInput(
            statement.Fact.AccountId,
            "-12.30",
            "ZAR",
            statement.Fact.TransactionDate,
            null,
            "agent transaction",
            null,
            null,
            new(EvidenceKind.AgentCapture, Digest(), "capture:crash", null, null));
        Assert.True(TransactionFact.TryCreate(input, out var fact, out var error), error);
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
            At(2),
            "ubuntu",
            "test:actor",
            CancellationToken.None);
        await transaction.CommitAsync();
        return transactionId;
    }

    private async Task<ReconciliationApplyInput> CorrectionInput(string targetId)
    {
        var read = await projectionStore.ReadAsync(statement.EvidenceId, statement.ScopeId, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        var projection = ManualReviewProjectionV1.Project(read.Source!);
        var candidates = projection.ExactCandidates.Concat(projection.GuardCandidates)
            .Select(candidate => candidate.TransactionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(
            statement.EvidenceId,
            projection.EvidenceFingerprint,
            statement.ScopeId,
            projection.AdvisoryToken,
            ReconciliationApplyDisposition.CorrectExistingFromStatement,
            ReconciliationAuthorityKind.Owner,
            candidates,
            targetId,
            statement.Fact,
            null,
            "owner approved crash correction");
    }

    private async Task CreateCorrectionFailureTrigger(string boundary, string timing)
    {
        var (table, condition) = boundary switch
        {
            "replacement_fact" => ("transaction_fact", "WHEN NEW.original_description = 'statement transaction'"),
            "decision" => ("reconciliation_decision", string.Empty),
            "supersession" => ("transaction_lifecycle_event", "WHEN NEW.action = 'statement_authoritative_replacement'"),
            "confirming_link" => ("evidence_link_event", "WHEN NEW.role = 'confirming'"),
            "pool" => ("pool_assignment_event", "WHEN NEW.action = 'carry_forward'"),
            "payment" => ("transaction_attribution_event", "WHEN NEW.action IN ('carry_forward', 'initialize')"),
            "correction" => ("statement_correction", string.Empty),
            "idempotency" => ("idempotency_record", string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER fail_statement_correction_{boundary}_{timing}
            {timing.ToUpperInvariant()} INSERT ON {table} {condition}
            BEGIN SELECT RAISE(ABORT, 'injected statement correction crash'); END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyDictionary<string, long>> Counts()
    {
        var tables = new[]
        {
            "transaction_fact", "transaction_lifecycle_event", "transaction_attribution_event", "pool_assignment_event",
            "category_allocation_event", "reconciliation_decision", "reconciliation_decision_authority",
            "evidence_link_event", "reconciliation_exception", "statement_unknown_attribution_authority",
            "financial_relationship", "relationship_lifecycle_event", "statement_correction",
            "statement_correction_relationship_event", "idempotency_record", "logical_effect"
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

    private static void CrashAt(ReconciliationWriteBoundary actual, ReconciliationWriteBoundary expected)
    {
        if (actual == expected) throw new InjectedCrashException();
    }

    private static string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
    private static string At(int second) => $"2026-07-22T00:00:{second:D2}Z";
    private sealed record StatementFixture(string EvidenceId, string ScopeId, Tally.Contracts.Ledger.Reconciliation.AuthoritativeStatementFact Fact);
    private sealed class InjectedCrashException : Exception;
}
