using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Infrastructure.Storage;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.Security;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class DiagnosticCanaryTests(PublishedTallyFixture fixture)
{
    [Theory]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("message")]
    [InlineData("recipient")]
    [InlineData("schedule")]
    [InlineData("delivery")]
    [InlineData("acknowledgement")]
    [InlineData("rawPayload")]
    [InlineData("providerCursor")]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_transport_fields_fail_without_disclosure_or_domain_effect(string field)
    {
        var canary = "PRIVATE_" + field.ToUpperInvariant() + "_CANARY";
        var input = "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"security-canary\"},\"input\":{\""
            + field + "\":\"" + canary + "\"}}";

        await WithDataRoot(async dataRoot =>
        {
            var result = await fixture.RunAsync(dataRoot, ["version", "--input", "-"], input);

            AssertSafeError(result, 3, "validation.invalid_input", canary, dataRoot);
            Assert.Equal(0, await CountAsync(dataRoot, "transaction_fact"));
            Assert.Equal(0, await CountAsync(dataRoot, "evidence_record"));
        });
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_malformed_json_does_not_echo_payload_or_parser_details()
    {
        const string canary = "PRIVATE_MALFORMED_JSON_CANARY";
        await WithDataRoot(async dataRoot =>
        {
            var result = await fixture.RunAsync(dataRoot, ["version", "--input", "-"], "{\"payload\":\"" + canary);

            AssertSafeError(result, 3, "validation.invalid_input", canary, "JsonException", "stack", dataRoot);
        });
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_oversized_value_fails_without_echo_or_domain_effect()
    {
        var canary = "PRIVATE_OVERSIZED_CANARY_" + new string('X', 1024 * 1024);
        var input = JsonSerializer.Serialize(new
        {
            contractVersion = "1.0",
            actor = new { kind = "automation", label = canary },
            input = new { }
        });

        await WithDataRoot(async dataRoot =>
        {
            var result = await fixture.RunAsync(dataRoot, ["version", "--input", "-"], input);

            AssertSafeError(result, 3, "validation.invalid_input", "PRIVATE_OVERSIZED_CANARY", dataRoot);
            Assert.Equal(0, await CountAsync(dataRoot, "transaction_fact"));
        });
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_full_account_identifier_is_rejected_without_echo()
    {
        const string canary = "4111111111111111";
        const string input = """
            {"contractVersion":"1.0","actor":{"kind":"automation","label":"security-canary"},"idempotencyKey":"full-identifier","input":{"institutionName":"Bank","displayName":"Private account","accountType":"cheque","maskedIdentifier":"4111111111111111","currencyCode":"ZAR"}}
            """;

        await WithDataRoot(async dataRoot =>
        {
            var result = await fixture.RunAsync(dataRoot, ["ledger", "account", "create", "--input", "-"], input);

            AssertSafeError(result, 3, "validation.invalid_input", canary, dataRoot);
            Assert.Equal(0, await CountAsync(dataRoot, "account"));
            Assert.Equal(0, await CountAsync(dataRoot, "idempotency_record"));
        });
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_unsafe_input_path_is_not_echoed()
    {
        const string canary = "/private/bank/PRIVATE_PATH_CANARY/message.eml";
        await WithDataRoot(async dataRoot =>
        {
            var result = await fixture.RunAsync(dataRoot, ["version", "--input", canary]);

            AssertSafeError(result, 2, "usage.invalid_input_path", canary, dataRoot);
        });
    }

    [Fact]
    public async Task TC_LEDGER_LOCAL_DATA_PROTECTION_corrupt_store_returns_only_safe_unexpected_metadata()
    {
        await WithDataRoot(async dataRoot =>
        {
            var first = await fixture.RunAsync(dataRoot, ["version", "--input", "-"], EmptyRequest());
            Assert.Equal(0, first.ExitCode);
            var database = await CurrentDatabaseAsync(dataRoot);
            await File.WriteAllTextAsync(database.DatabasePath, "PRIVATE_CORRUPT_STORE_CANARY");
            File.SetUnixFileMode(database.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var result = await fixture.RunAsync(dataRoot, ["version", "--input", "-"], EmptyRequest());

            AssertSafeError(result, 10, "host.unexpected", "PRIVATE_CORRUPT_STORE_CANARY", "SQLite", "stack", dataRoot);
        });
    }

    private static string EmptyRequest() =>
        "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"security-canary\"},\"input\":{}}";

    private static void AssertSafeError(PublishedTallyResult result, int exitCode, string code, params string[] canaries)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("system.process", document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(canary, result.Stderr, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task WithDataRoot(Func<string, Task> test)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "tally-security-canary-" + Guid.NewGuid().ToString("N"));
        try
        {
            await test(dataRoot);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        }
    }

    private static async Task<long> CountAsync(string dataRoot, string table)
    {
        var database = await CurrentDatabaseAsync(dataRoot);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<LedgerDb> CurrentDatabaseAsync(string dataRoot)
    {
        var generationId = (await File.ReadAllTextAsync(Path.Combine(dataRoot, "CURRENT"))).Trim();
        return new(dataRoot, generationId);
    }
}
