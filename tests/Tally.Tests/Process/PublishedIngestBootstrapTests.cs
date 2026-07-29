using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Tally.Tests.Process;

/// <summary>
/// Regression proofs for the real published Program/bootstrap composition path.
/// Direct <c>TallyProcess</c> + hand-migrated store composition is not enough:
/// status must open a virgin ingest.db through Program.cs → IngestOperationBundle.
/// </summary>
[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class PublishedIngestBootstrapTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"tally-published-ingest-bootstrap-{Guid.NewGuid():N}");

    /// <summary>
    /// Fresh owner-only data root: published <c>tally ingest status --input -</c> must succeed
    /// without a prior preview (which historically migrated the schema as a side effect).
    /// </summary>
    [Fact]
    public async Task Published_ingest_status_on_fresh_data_root_succeeds_via_program_bootstrap()
    {
        const string envelope =
            """{"contractVersion":"1.0","actor":{"kind":"automation","label":"published-ingest-bootstrap"},"input":{"batchId":null,"limit":50,"cursor":null}}""";

        var result = await fixture.RunAsync(dataRoot, ["ingest", "status", "--input", "-"], envelope);

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            string.IsNullOrWhiteSpace(result.Stderr),
            "status must not surface host.unexpected (or any stderr) on a virgin data root");

        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("success", root.GetProperty("outcome").GetString());
        Assert.Equal("ingest.status", root.GetProperty("operationId").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);

        var payload = root.GetProperty("result");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("detail").ValueKind);
        Assert.Equal(0, payload.GetProperty("items").GetArrayLength());
        Assert.True(
            payload.GetProperty("nextCursor").ValueKind is JsonValueKind.Null or JsonValueKind.String,
            "nextCursor field must be present as null or opaque string");

        // Schema must have been applied by the published path (not left at user_version 0).
        var ingestDb = Path.Combine(dataRoot, "ingest", "ingest.db");
        Assert.True(File.Exists(ingestDb));
        await using var connection = new SqliteConnection($"Data Source={ingestDb};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(version >= 1, "ingest schema must be migrated by status bootstrap on a virgin store");

        // No live ledger path or statement path leakage in process output.
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/statements", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docs/statements", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Control: ledger account list remains healthy against the same disposable root/envelope shape
    /// (proves the defect was ingest-status composition, not a generic data-root failure).
    /// </summary>
    [Fact]
    public async Task Published_ledger_account_list_still_succeeds_on_same_disposable_root()
    {
        const string envelope =
            """{"contractVersion":"1.0","actor":{"kind":"automation","label":"published-ingest-bootstrap"},"input":{"status":null,"institutionName":null}}""";

        var result = await fixture.RunAsync(dataRoot, ["ledger", "account", "list", "--input", "-"], envelope);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("ledger.account.list", document.RootElement.GetProperty("operationId").GetString());
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(dataRoot);
        // Owner-only data root mirrors production host protections used by ingest/ledger open paths.
        File.SetUnixFileMode(dataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        return Task.CompletedTask;
    }
}
