using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Migrations.V001;
using Xunit;

namespace Tally.Tests.Application;

[SupportedOSPlatform("linux")]
public sealed class LedgerMutationExecutorTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-idempotency-{Guid.NewGuid():N}");
    private readonly HostArtifactProtection protection = new();
    private LedgerDb database = null!;

    // TC-LEDGER-IDEMPOTENT-WRITES-CONTRACT
    [Fact] public async Task Same_request_returns_the_original_stable_result() { var calls = new Counter(); var first = await ExecuteAsync("key", "actor", "{\"a\":1}", (_, _, _) => Task.FromResult(CommandResult<JsonElement>.Success(Json("{\"id\":\"one\"}"))), calls); var replay = await ExecuteAsync("key", "actor", "{\"a\":1}", (_, _, _) => Task.FromResult(CommandResult<JsonElement>.Success(Json("{\"id\":\"two\"}"))), calls); Assert.Equal("one", first.Value!.GetProperty("id").GetString()); Assert.Equal("one", replay.Value!.GetProperty("id").GetString()); Assert.Equal(1, calls.Value); }
    // TC-LEDGER-IDEMPOTENT-WRITES-CONTRACT
    [Fact] public async Task Normalized_property_order_replays_the_original_result() { var calls = new Counter(); await ExecuteAsync("key", "actor", "{\"a\":1,\"b\":2}", Success, calls); var replay = await ExecuteAsync("key", "actor", "{\"b\":2,\"a\":1}", Success, calls); Assert.True(replay.IsSuccess); Assert.Equal(1, calls.Value); }
    // TC-LEDGER-IDEMPOTENT-WRITES-CONTRACT
    [Fact] public async Task Changed_payload_conflicts_without_second_effect() { var calls = new Counter(); await ExecuteAsync("key", "actor", "{\"a\":1}", Success, calls); var result = await ExecuteAsync("key", "actor", "{\"a\":2}", Success, calls); Assert.Equal("LEDGER-IDEMPOTENCY-001", result.ErrorCode); Assert.Equal(1, calls.Value); }
    // TC-LEDGER-IDEMPOTENT-WRITES-CONTRACT
    [Fact] public async Task Changed_actor_conflicts_without_second_effect() { var calls = new Counter(); await ExecuteAsync("key", "first", "{}", Success, calls); var result = await ExecuteAsync("key", "second", "{}", Success, calls); Assert.Equal("LEDGER-IDEMPOTENCY-001", result.ErrorCode); Assert.Equal(1, calls.Value); }
    // TC-LEDGER-IDEMPOTENT-WRITES-CONTRACT
    [Fact] public async Task Changed_operation_conflicts_without_second_effect() { var calls = new Counter(); await ExecuteAsync("key", "actor", "{}", Success, calls, operationId: "one"); var result = await ExecuteAsync("key", "actor", "{}", Success, calls, operationId: "two"); Assert.Equal("LEDGER-IDEMPOTENCY-001", result.ErrorCode); Assert.Equal(1, calls.Value); }
    // TC-LEDGER-IDEMPOTENT-WRITES-CONTRACT
    [Fact] public async Task Changed_contract_version_conflicts_without_second_effect() { var calls = new Counter(); await ExecuteAsync("key", "actor", "{}", Success, calls, contractVersion: "1"); var result = await ExecuteAsync("key", "actor", "{}", Success, calls, contractVersion: "2"); Assert.Equal("LEDGER-IDEMPOTENCY-001", result.ErrorCode); Assert.Equal(1, calls.Value); }
    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact] public async Task Same_logical_effect_under_another_key_replays_original_outcome() { var calls = new Counter(); await ExecuteAsync("one", "actor", "{}", Success, calls, logical: new("evidence:digest", "evidence")); var replay = await ExecuteAsync("two", "actor", "{}", Success, calls, logical: new("evidence:digest", "evidence")); Assert.True(replay.IsSuccess); Assert.Equal(1, calls.Value); }
    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact] public async Task Changed_logical_effect_content_conflicts() { var calls = new Counter(); await ExecuteAsync("one", "actor", "{\"fact\":1}", Success, calls, logical: new("evidence:digest", "evidence")); var conflict = await ExecuteAsync("two", "actor", "{\"fact\":2}", Success, calls, logical: new("evidence:digest", "evidence")); Assert.Equal("LEDGER-IDEMPOTENCY-001", conflict.ErrorCode); Assert.Equal(1, calls.Value); }
    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact] public async Task Changed_logical_effect_type_conflicts() { var calls = new Counter(); await ExecuteAsync("one", "actor", "{}", Success, calls, logical: new("decision:evidence:scope:root", "decision")); var conflict = await ExecuteAsync("two", "actor", "{}", Success, calls, logical: new("decision:evidence:scope:root", "evidence")); Assert.Equal("LEDGER-IDEMPOTENCY-001", conflict.ErrorCode); Assert.Equal(1, calls.Value); }
    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact] public async Task Validation_failure_does_not_consume_request_identity() { var calls = new Counter(); var invalid = await ExecuteAsync("key", "actor", "{}", (_, _, _) => Task.FromResult(CommandResult<JsonElement>.Failure("validation.invalid")), calls); var success = await ExecuteAsync("key", "actor", "{}", Success, calls); Assert.Equal("validation.invalid", invalid.ErrorCode); Assert.True(success.IsSuccess); Assert.Equal(2, calls.Value); }
    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact] public async Task Busy_failure_does_not_consume_logical_identity() { var calls = new Counter(); var logical = new LogicalEffectIdentity("evidence:digest", "evidence"); await ExecuteAsync("one", "actor", "{}", (_, _, _) => Task.FromResult(CommandResult<JsonElement>.Failure("storage.busy")), calls, logical); var success = await ExecuteAsync("two", "actor", "{}", Success, calls, logical); Assert.True(success.IsSuccess); Assert.Equal(2, calls.Value); }
    // FR-LEDGER-IDEMPOTENT-WRITES
    [Fact] public async Task Invalid_identity_returns_stable_validation_without_running_the_mutation() { var calls = new Counter(); var result = await ExecuteAsync(" ", "actor", "{}", Success, calls); Assert.Equal("validation.invalid_input", result.ErrorCode); Assert.Equal(0, calls.Value); }
    // FR-LEDGER-IDEMPOTENT-WRITES
    [Fact] public async Task Invalid_logical_identity_returns_stable_validation_without_running_the_mutation() { var calls = new Counter(); var result = await ExecuteAsync("key", "actor", "{}", Success, calls, logical: new(" ", "evidence")); Assert.Equal("validation.invalid_input", result.ErrorCode); Assert.Equal(0, calls.Value); }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact] public async Task Real_sqlite_busy_failure_does_not_consume_the_identity() { await using var lockConnection = await OpenAsync(); await using var writeLock = lockConnection.BeginTransaction(deferred: false); var calls = new Counter(); var busy = await ExecuteAsync("key", "actor", "{}", Success, calls); Assert.Equal("operation.conflict", busy.ErrorCode); Assert.Equal(0, calls.Value); await writeLock.RollbackAsync(); var retry = await ExecuteAsync("key", "actor", "{}", Success, calls); Assert.True(retry.IsSuccess); Assert.Equal(1, calls.Value); }
    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact] public async Task Unexpected_sqlite_failure_is_not_mislabeled_as_busy() { var calls = new Counter(); await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("key", "actor", "{}", async (connection, transaction, _) => { await ExecuteAsync(connection, "INSERT INTO table_that_does_not_exist VALUES (1);", transaction); return CommandResult<JsonElement>.Success(Json("{}")); }, calls)); Assert.Equal(1, calls.Value); }
    // NFR-LEDGER-ATOMIC-DURABLE-MUTATIONS
    [Fact] public async Task Domain_failure_rolls_back_domain_changes_and_identities_together() { var calls = new Counter(); var result = await ExecuteAsync("key", "actor", "{}", async (connection, transaction, _) => { await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS effect_probe (id INTEGER);", transaction); await ExecuteAsync(connection, "INSERT INTO effect_probe VALUES (1);", transaction); return CommandResult<JsonElement>.Failure("validation.invalid"); }, calls); Assert.Equal("validation.invalid", result.ErrorCode); await using var connection = await OpenAsync(); Assert.Equal(0L, await CountAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'effect_probe';")); }
    // TC-LEDGER-ATOMIC-CRASH-RECOVERY
    [Fact] public async Task Thrown_mutation_failure_rolls_back_and_leaves_the_identity_reusable() { var calls = new Counter(); await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync("key", "actor", "{}", async (connection, transaction, _) => { await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS effect_probe (id INTEGER);", transaction); await ExecuteAsync(connection, "INSERT INTO effect_probe VALUES (1);", transaction); throw new InvalidOperationException("injected"); }, calls)); var retry = await ExecuteAsync("key", "actor", "{}", Success, calls); Assert.True(retry.IsSuccess); Assert.Equal(2, calls.Value); await using var connection = await OpenAsync(); Assert.Equal(0L, await CountAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'effect_probe';")); }

    public async Task InitializeAsync() { database = new LedgerDb(root, Guid.NewGuid().ToString("N")); await using var connection = await OpenAsync(); await new LedgerSchemaFragmentRegistry([new V001StorageSchema()], [V001StorageSchema.FragmentName]).ApplyAsync(connection, CancellationToken.None); }
    public Task DisposeAsync() { if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }
    private async Task<CommandResult<JsonElement>> ExecuteAsync(string key, string actor, string input, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<CommandResult<JsonElement>>> mutation, Counter calls, LogicalEffectIdentity? logical = null, string operationId = "ledger.evidence.register", string contractVersion = "1") { var executor = new LedgerMutationExecutor(database, new LedgerConnectionFactory(protection), new IdempotencyStore()); return await executor.ExecuteAsync(new IdempotencyRequest(contractVersion, operationId, key, actor, Json(input), logical), async (connection, transaction, cancellationToken) => { calls.Value++; return await mutation(connection, transaction, cancellationToken); }, CancellationToken.None); }
    private static Task<CommandResult<JsonElement>> Success(SqliteConnection _, SqliteTransaction __, CancellationToken ___) => Task.FromResult(CommandResult<JsonElement>.Success(Json("{\"id\":\"one\"}")));
    private async Task<SqliteConnection> OpenAsync() => await new LedgerConnectionFactory(protection).OpenAsync(database, 1, CancellationToken.None);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction transaction) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; await command.ExecuteNonQueryAsync(); }
    private static async Task<long> CountAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); }
    private sealed class Counter { public int Value { get; set; } }
}
