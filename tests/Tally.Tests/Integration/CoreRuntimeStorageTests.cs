using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Integration;

[SupportedOSPlatform("linux")]
public sealed class CoreRuntimeStorageTests : IAsyncLifetime
{
    private const string RecordedAt = "2026-07-21T00:00:00Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-core-runtime-{Guid.NewGuid():N}");
    private readonly HostArtifactProtection protection = new();
    private LedgerDb database = null!;

    // DM-LEDGER-STORE-GENERATION
    [Fact]
    public async Task Bootstrap_activates_one_current_generation_and_reuses_it()
    {
        var current = (await File.ReadAllTextAsync(Path.Combine(root, "CURRENT"))).Trim();
        var reopened = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);

        Assert.Equal(database.GenerationId, current);
        Assert.Equal(database.GenerationId, reopened.GenerationId);
        Assert.Single(Directory.GetDirectories(Path.Combine(root, "generations")));
    }

    // NFR-LEDGER-LOCAL-PRIVACY
    [Fact]
    public void Bootstrap_protects_every_authoritative_host_artifact()
    {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(database.GenerationDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.ManifestPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "CURRENT")));
    }

    // DD-LEDGER-EMBEDDED-STORAGE
    [Fact]
    public async Task Runtime_opens_the_current_schema_with_integrity_and_foreign_keys_intact()
    {
        await using var connection = await OpenAsync();

        Assert.Equal(CompleteLedgerSchema.CurrentVersion, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    // FR-LEDGER-EXACT-MONEY, FR-LEDGER-EFFECTIVE-DATE
    [Fact]
    public async Task Exact_money_and_effective_date_round_trip_through_the_current_store()
    {
        Assert.True(Money.TryParseTransactionAmount("123.45", out var amount, out _));
        Assert.True(EffectiveDate.TryParse("2026-07-20", out var transactionDate, out _));
        await using var connection = await OpenAsync();
        await SeedAccountAsync(connection);
        await ExecuteAsync(connection, """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ('transaction', 'account', $amount, 'ZAR', $date, '2026-07-21', 'Exact proof', $recordedAt, 'owner');
            """, ("$amount", amount.MinorUnits), ("$date", transactionDate.ToString()), ("$recordedAt", RecordedAt));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT signed_amount_minor, effective_date FROM transaction_fact WHERE transaction_id = 'transaction';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(amount, Money.FromMinorUnits(reader.GetInt64(0)));
        Assert.True(EffectiveDate.TryParse(reader.GetString(1), out var storedDate, out _));
        Assert.Equal(transactionDate, storedDate);
    }

    // FR-LEDGER-IDEMPOTENT-WRITES
    [Fact]
    public async Task Same_request_returns_the_original_result_and_one_domain_effect()
    {
        var calls = 0;
        var first = await RegisterEvidenceAsync("request", "digest", "fingerprint", "evidence-one", () => calls++);
        var replay = await RegisterEvidenceAsync("request", "digest", "fingerprint", "evidence-two", () => calls++);

        Assert.Equal("evidence-one", first.Value!.GetProperty("evidenceId").GetString());
        Assert.Equal("evidence-one", replay.Value!.GetProperty("evidenceId").GetString());
        Assert.Equal(1, calls);
        Assert.Equal(1L, await CountAsync("evidence_record"));
        Assert.Equal(1L, await CountAsync("idempotency_record"));
    }

    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Same_logical_evidence_under_another_key_returns_the_original_effect()
    {
        var calls = 0;
        await RegisterEvidenceAsync("first", "digest", "fingerprint", "evidence-one", () => calls++);
        var replay = await RegisterEvidenceAsync("second", "digest", "fingerprint", "evidence-two", () => calls++);

        Assert.Equal("evidence-one", replay.Value!.GetProperty("evidenceId").GetString());
        Assert.Equal(1, calls);
        Assert.Equal(1L, await CountAsync("evidence_record"));
        Assert.Equal(1L, await CountAsync("logical_effect"));
    }

    // FR-LEDGER-IDEMPOTENT-WRITES
    [Fact]
    public async Task Changed_replay_conflicts_without_a_second_domain_effect()
    {
        var calls = 0;
        await RegisterEvidenceAsync("request", "digest", "first", "evidence-one", () => calls++);
        var conflict = await RegisterEvidenceAsync("request", "digest", "changed", "evidence-two", () => calls++);

        Assert.Equal(LedgerMutationExecutor.ConflictCode, conflict.ErrorCode);
        Assert.Equal(1, calls);
        Assert.Equal(1L, await CountAsync("evidence_record"));
    }

    // TC-LEDGER-ATOMIC-CRASH-RECOVERY
    [Fact]
    public async Task Injected_mutation_crash_rolls_back_domain_and_identity_rows_then_allows_retry()
    {
        var executor = Executor();
        var request = Request("request", "digest", "fingerprint");
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(request, async (connection, transaction, cancellationToken) =>
        {
            await InsertEvidenceAsync(connection, transaction, "crashed", "digest", "fingerprint", cancellationToken);
            throw new InvalidOperationException("injected crash");
        }, CancellationToken.None));

        Assert.Equal(0L, await CountAsync("evidence_record"));
        Assert.Equal(0L, await CountAsync("idempotency_record"));
        var retry = await RegisterEvidenceAsync("request", "digest", "fingerprint", "recovered");
        Assert.True(retry.IsSuccess);
        Assert.Equal(1L, await CountAsync("evidence_record"));
        Assert.Equal(1L, await CountAsync("idempotency_record"));
    }

    public async Task InitializeAsync() => database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    private async Task<CommandResult<JsonElement>> RegisterEvidenceAsync(
        string requestKey,
        string digest,
        string fingerprint,
        string evidenceId,
        Action? onMutation = null)
    {
        return await Executor().ExecuteAsync(Request(requestKey, digest, fingerprint), async (connection, transaction, cancellationToken) =>
        {
            onMutation?.Invoke();
            await InsertEvidenceAsync(connection, transaction, evidenceId, digest, fingerprint, cancellationToken);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new { evidenceId }));
        }, CancellationToken.None);
    }

    private LedgerMutationExecutor Executor() => new(database, new LedgerConnectionFactory(protection), new IdempotencyStore());

    private static IdempotencyRequest Request(string requestKey, string digest, string fingerprint) => new(
        "1.0",
        "ledger.evidence.register",
        requestKey,
        "integration-test",
        JsonSerializer.SerializeToElement(new { digest, fingerprint }),
        new LogicalEffectIdentity("evidence:" + digest, "evidence"));

    private static async Task InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string evidenceId,
        string digest,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO evidence_record (
                evidence_id, kind, logical_identity_digest, opaque_external_reference,
                content_fingerprint, recorded_by, recorded_at)
            VALUES ($id, 'agent_capture', $digest, NULL, $fingerprint, 'integration-test', $recordedAt);
            """;
        command.Parameters.AddWithValue("$id", evidenceId);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$recordedAt", RecordedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync() =>
        await new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await OpenAsync();
        return await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {table};");
    }

    private static Task SeedAccountAsync(SqliteConnection connection) => ExecuteAsync(connection, $"""
        INSERT INTO account VALUES ('account', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '{RecordedAt}');
        INSERT INTO catalogue_lifecycle_event VALUES ('account-create', 'account', 'account', 'create', NULL, 'Primary', 'primary', NULL, 'owner', '{RecordedAt}', NULL);
        """);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql) =>
        Convert.ToString(await ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
