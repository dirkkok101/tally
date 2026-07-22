using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
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
public sealed class StatementCorrectionPrerequisiteTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-statement-prerequisite-{Guid.NewGuid():N}");

    [Fact]
    public void TC_LEDGER_STATEMENT_RECONCILIATION_CONTRACT_base_apply_module_retains_its_public_boundary()
    {
        var method = Assert.Single(typeof(ReconciliationApplyOperationModule)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

        Assert.Equal(nameof(ReconciliationApplyOperationModule.ApplyAsync), method.Name);
        Assert.Equal(typeof(Task<CommandResult<JsonElement>>), method.ReturnType);
        Assert.Equal(
            [typeof(OperationRequest), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal("ledger.reconciliation.apply", ReconciliationApplyOperationModule.OperationId);
    }

    [Fact]
    public void DM_LEDGER_RECONCILIATION_HISTORY_effect_writer_retains_one_transaction_scoped_boundary()
    {
        var method = Assert.Single(typeof(StatementCorrectionEffectWriter)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

        Assert.Equal(nameof(StatementCorrectionEffectWriter.AppendAsync), method.Name);
        Assert.Equal(typeof(Task<StatementCorrectionEffectResult>), method.ReturnType);
        Assert.Equal(
            [typeof(SqliteConnection), typeof(SqliteTransaction), typeof(StatementCorrectionEffectWrite), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(LedgerDb));
    }

    [Fact]
    public void DM_LEDGER_RECONCILIATION_HISTORY_base_and_correction_writers_share_the_caller_transaction_shape()
    {
        var baseWrites = new[]
        {
            nameof(ReconciliationWriteStore.InsertStatementOnlyTransactionAsync),
            nameof(ReconciliationWriteStore.InsertDecisionAsync),
            nameof(ReconciliationWriteStore.InsertDecisionAuthorityAsync),
            nameof(ReconciliationWriteStore.InsertConfirmingLinkAsync),
            nameof(ReconciliationWriteStore.InsertExceptionAsync)
        };

        foreach (var name in baseWrites.Append(nameof(StatementCorrectionEffectWriter.AppendAsync)))
        {
            var owner = name == nameof(StatementCorrectionEffectWriter.AppendAsync)
                ? typeof(StatementCorrectionEffectWriter)
                : typeof(ReconciliationWriteStore);
            var method = Assert.Single(owner.GetMethods(BindingFlags.Instance | BindingFlags.Public), candidate => candidate.Name == name);
            Assert.Equal(typeof(SqliteConnection), method.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(SqliteTransaction), method.GetParameters()[1].ParameterType);
        }
    }

    [Fact]
    public void DM_LEDGER_RECONCILIATION_HISTORY_effect_result_and_conflict_error_are_stable()
    {
        Assert.Equal("LEDGER-RECONCILIATION-CORRECTION-CONFLICT", StatementCorrectionEffectErrors.Conflict);
        Assert.Equal(
            [
                typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string),
                typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
                typeof(PaymentAttributionCarryForwardResolution?), typeof(IReadOnlyList<string>)
            ],
            typeof(StatementCorrectionEffectResult).GetConstructors().Single().GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TC_LEDGER_STATEMENT_RECONCILIATION_CONTRACT_producers_compose_without_financial_effects(bool upgradeFromV1)
    {
        var seam = await ComposeAsync(upgradeFromV1);

        Assert.NotNull(seam.ApplyModule);
        Assert.NotNull(seam.EffectWriter);
        await using var connection = await seam.Factory.OpenAsync(seam.Database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, """
            SELECT
                (SELECT COUNT(*) FROM transaction_fact) +
                (SELECT COUNT(*) FROM reconciliation_decision) +
                (SELECT COUNT(*) FROM reconciliation_decision_authority) +
                (SELECT COUNT(*) FROM evidence_link_event) +
                (SELECT COUNT(*) FROM category_allocation_event) +
                (SELECT COUNT(*) FROM pool_assignment_event) +
                (SELECT COUNT(*) FROM transaction_attribution_event) +
                (SELECT COUNT(*) FROM financial_relationship) +
                (SELECT COUNT(*) FROM statement_correction) +
                (SELECT COUNT(*) FROM statement_correction_relationship_event);
            """));
    }

    [Theory]
    [InlineData("reconciliation_decision_authority")]
    [InlineData("statement_unknown_attribution_authority")]
    [InlineData("statement_correction")]
    [InlineData("statement_correction_relationship_event")]
    public async Task DM_LEDGER_RECONCILIATION_HISTORY_fresh_and_upgraded_schemas_expose_each_v002_target(string table)
    {
        await using var fresh = await OpenCurrentSchemaAsync("fresh-" + table, upgradeFromV1: false);
        await using var upgraded = await OpenCurrentSchemaAsync("upgraded-" + table, upgradeFromV1: true);

        Assert.Equal(1L, await TableExistsAsync(fresh, table));
        Assert.Equal(1L, await TableExistsAsync(upgraded, table));
    }

    [Fact]
    public async Task DM_LEDGER_RECONCILIATION_HISTORY_fresh_and_upgraded_schemas_retain_producer_foreign_keys()
    {
        var expectedCorrectionTargets = new[]
        {
            "category_allocation_event",
            "pool_assignment_event",
            "reconciliation_decision",
            "reconciliation_decision_authority",
            "transaction_attribution_event",
            "transaction_fact",
            "transaction_lifecycle_event"
        };
        var expectedRelationshipTargets = new[] { "relationship_lifecycle_event", "statement_correction" };

        await using var fresh = await OpenCurrentSchemaAsync("fresh-foreign-keys", upgradeFromV1: false);
        await using var upgraded = await OpenCurrentSchemaAsync("upgraded-foreign-keys", upgradeFromV1: true);

        Assert.Equal(expectedCorrectionTargets, await ForeignKeyTargetsAsync(fresh, "statement_correction"));
        Assert.Equal(expectedCorrectionTargets, await ForeignKeyTargetsAsync(upgraded, "statement_correction"));
        Assert.Equal(expectedRelationshipTargets, await ForeignKeyTargetsAsync(fresh, "statement_correction_relationship_event"));
        Assert.Equal(expectedRelationshipTargets, await ForeignKeyTargetsAsync(upgraded, "statement_correction_relationship_event"));
    }

    [Fact]
    public async Task DM_LEDGER_RECONCILIATION_HISTORY_v001_alone_fails_closed_when_the_effect_target_is_absent()
    {
        await using var connection = await OpenSchemaAsync("v1-only");
        await CompleteLedgerSchema.CreateV1().ApplyAsync(connection, CancellationToken.None);

        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            ScalarLongAsync(connection, "SELECT COUNT(*) FROM statement_correction;"));

        Assert.Contains("no such table", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task DM_LEDGER_RECONCILIATION_HISTORY_rejects_a_transaction_from_another_connection_without_effects()
    {
        var seam = await ComposeAsync(upgradeFromV1: false);
        await using var ownerConnection = await seam.Factory.OpenAsync(seam.Database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var otherConnection = await seam.Factory.OpenAsync(seam.Database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = ownerConnection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => seam.EffectWriter.AppendAsync(
            otherConnection,
            transaction,
            CreateWrite(),
            CancellationToken.None));

        await transaction.RollbackAsync();
        Assert.Equal(0L, await ScalarLongAsync(otherConnection, "SELECT COUNT(*) FROM statement_correction;"));
        Assert.Equal(0L, await ScalarLongAsync(otherConnection, "SELECT COUNT(*) FROM transaction_fact;"));
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<ComposedSeam> ComposeAsync(bool upgradeFromV1)
    {
        var database = new LedgerDb(root, Guid.NewGuid().ToString("N"));
        var factory = new LedgerConnectionFactory(new HostArtifactProtection());
        await using (var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None))
        {
            if (upgradeFromV1) await CompleteLedgerSchema.CreateV1().ApplyAsync(connection, CancellationToken.None);
            await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);
        }

        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var accountStore = new AccountStore(database, factory);
        var evidenceStore = new EvidenceStore(database, factory);
        var transactionStore = new TransactionStore(database, factory);
        var writeStore = new ReconciliationWriteStore(evidenceStore, transactionStore);
        var projectionStore = new ReconciliationProjectionStore(database, factory, evidenceStore, transactionStore);
        var applyModule = new ReconciliationApplyOperationModule(
            new ReconciliationApplyHandler(executor, accountStore, projectionStore, writeStore));
        var effectWriter = new StatementCorrectionEffectWriter(
            writeStore,
            new ReconciliationDecisionStore(database, factory, evidenceStore, transactionStore),
            transactionStore,
            new CategoryAllocationStore(database, factory),
            new PaymentAttributionStore(),
            new PaymentIdentityStore(database, factory),
            new PoolAssignmentStore(),
            new RelationshipStore(database, factory));
        return new(database, factory, applyModule, effectWriter);
    }

    private async Task<SqliteConnection> OpenCurrentSchemaAsync(string name, bool upgradeFromV1)
    {
        var connection = await OpenSchemaAsync(name);
        if (upgradeFromV1) await CompleteLedgerSchema.CreateV1().ApplyAsync(connection, CancellationToken.None);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);
        return connection;
    }

    private async Task<SqliteConnection> OpenSchemaAsync(string name)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, name + ".db")}");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static StatementCorrectionEffectWrite CreateWrite()
    {
        var accountId = LedgerId.New().ToString();
        var evidenceInput = new RegisterEvidenceInput(
            EvidenceKind.StatementRow,
            Digest('a'),
            "statement:seam",
            Digest('b'),
            new(accountId, -1234, "ZAR", "2026-07-01", null, null, null, Digest('c')));
        Assert.True(TransactionFact.TryCreate(
            new(accountId, "-12.34", "ZAR", "2026-07-01", null, "Statement seam transaction", null, null, evidenceInput),
            out var fact,
            out var error), error);
        return new(
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            LedgerId.New().ToString(),
            fact!,
            null,
            null,
            "manual-review-v1",
            "1.0",
            "prerequisite seam",
            "scope:seam|evidence:test",
            "prerequisite seam",
            "test:statement-prerequisite",
            "2026-07-22T00:00:00Z",
            []);
    }

    private static async Task<string[]> ForeignKeyTargetsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT \"table\" FROM pragma_foreign_key_list('{table}') ORDER BY \"table\";";
        await using var reader = await command.ExecuteReaderAsync();
        var targets = new List<string>();
        while (await reader.ReadAsync()) targets.Add(reader.GetString(0));
        return [.. targets];
    }

    private static async Task<long> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Digest(char value) => new(value, 64);

    private sealed record ComposedSeam(
        LedgerDb Database,
        LedgerConnectionFactory Factory,
        ReconciliationApplyOperationModule ApplyModule,
        StatementCorrectionEffectWriter EffectWriter);
}
