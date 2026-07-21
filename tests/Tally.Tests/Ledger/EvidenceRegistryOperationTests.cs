using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Evidence;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class EvidenceRegistryOperationTests : IAsyncLifetime
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-evidence-registry-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private TallyProcess process = null!;

    // DM-LEDGER-EVIDENCE-RECONCILIATION-CONTRACTS
    [Fact]
    public void Registry_exposes_typed_register_descriptor()
    {
        var descriptor = OperationRegistry.Create().Find("ledger.evidence.register")!;

        Assert.Equal(typeof(RegisterEvidenceInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(EvidenceRecordDetail), descriptor.ResultTypeInfo.Type);
        Assert.Equal("mutation", descriptor.Kind);
        Assert.True(descriptor.RequiresIdempotencyKey);
        Assert.Equal("EvidenceRegistryOperationModule.Register", descriptor.HandlerTarget);
    }

    // DM-LEDGER-EVIDENCE-RECONCILIATION-CONTRACTS
    [Fact]
    public void Registry_exposes_typed_get_descriptor()
    {
        var descriptor = OperationRegistry.Create().Find("ledger.evidence.get")!;

        Assert.Equal(typeof(GetEvidenceInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(EvidenceRecordDetail), descriptor.ResultTypeInfo.Type);
        Assert.Equal("query", descriptor.Kind);
        Assert.False(descriptor.RequiresIdempotencyKey);
        Assert.Equal("EvidenceRegistryOperationModule.Get", descriptor.HandlerTarget);
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Theory]
    [InlineData(EvidenceKind.AgentCapture)]
    [InlineData(EvidenceKind.StatementRow)]
    [InlineData(EvidenceKind.Receipt)]
    [InlineData(EvidenceKind.ExternalDocument)]
    [InlineData(EvidenceKind.OwnerAssertion)]
    public async Task Every_closed_evidence_kind_round_trips(EvidenceKind kind)
    {
        var registered = await RegisterAsync(new(kind, Digest, "opaque:reference", OtherFingerprint, null));
        var detail = SuccessDetail(registered);
        var fetched = await GetAsync(detail.EvidenceId);

        Assert.Equal(kind, detail.Kind);
        AssertEquivalent(detail, SuccessDetail(fetched));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact]
    public async Task Every_allowlisted_observation_field_round_trips_exactly()
    {
        var ids = await SeedObservationReferencesAsync();
        var observation = new EvidenceObservation(ids.AccountId, -12345, "ZAR", "2026-07-20", "2026-07-21", ids.InstrumentId, ids.CardholderId, OtherFingerprint);

        var detail = SuccessDetail(await RegisterAsync(new(EvidenceKind.AgentCapture, Digest, "opaque:reference", OtherFingerprint, observation)));

        Assert.Equal(observation, detail.Observation);
        Assert.Empty(detail.LinkHistory);
        Assert.StartsWith("automation:evidence-test", detail.RecordedBy, StringComparison.Ordinal);
        Assert.EndsWith("Z", detail.RecordedAt, StringComparison.Ordinal);
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Fact]
    public async Task Same_request_replay_returns_the_original_record()
    {
        var input = new RegisterEvidenceInput(EvidenceKind.AgentCapture, Digest, null, OtherFingerprint, null);
        var first = SuccessDetail(await RegisterAsync(input, "same-key"));
        var replay = SuccessDetail(await RegisterAsync(input, "same-key"));

        AssertEquivalent(first, replay);
        Assert.Equal(1L, await CountAsync("evidence_record"));
        Assert.Equal(1L, await CountAsync("idempotency_record"));
    }

    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Cross_key_logical_replay_returns_the_original_record()
    {
        var input = new RegisterEvidenceInput(EvidenceKind.StatementRow, Digest, null, OtherFingerprint, null);
        var first = SuccessDetail(await RegisterAsync(input, "first-key"));
        var replay = SuccessDetail(await RegisterAsync(input, "second-key"));

        AssertEquivalent(first, replay);
        Assert.Equal(1L, await CountAsync("evidence_record"));
        Assert.Equal(1L, await CountAsync("logical_effect"));
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Fact]
    public async Task Changed_same_key_replay_conflicts_and_preserves_the_original()
    {
        await RegisterAsync(new(EvidenceKind.Receipt, Digest, null, OtherFingerprint, null), "same-key");
        var conflict = await RegisterAsync(new(EvidenceKind.Receipt, Digest, null, new string('c', 64), null), "same-key");

        AssertError(conflict, 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1L, await CountAsync("evidence_record"));
    }

    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Changed_cross_key_logical_replay_conflicts_and_preserves_the_original()
    {
        await RegisterAsync(new(EvidenceKind.Receipt, Digest, null, OtherFingerprint, null), "first-key");
        var conflict = await RegisterAsync(new(EvidenceKind.Receipt, Digest, null, new string('c', 64), null), "second-key");

        AssertError(conflict, 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1L, await CountAsync("evidence_record"));
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Theory]
    [InlineData("short", null)]
    [InlineData(Digest, "not-a-fingerprint")]
    public async Task Invalid_fingerprints_fail_before_any_rows_are_retained(string digest, string? contentFingerprint)
    {
        var result = await RegisterAsync(new(EvidenceKind.AgentCapture, digest, null, contentFingerprint, null));

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
        Assert.Equal(0L, await CountAsync("idempotency_record"));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact]
    public async Task Numeric_unknown_evidence_kind_is_rejected_before_storage()
    {
        var input = $"{{\"kind\":99,\"logicalIdentityDigest\":\"{Digest}\"}}";
        var result = await RunRawAsync("ledger.evidence.register", input, "key");

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // NFR-LEDGER-LOCAL-PRIVACY
    [Theory]
    [InlineData("bank:123456789")]
    [InlineData("bearer:credential")]
    [InlineData("owner@example.com")]
    [InlineData("../private/document")]
    public async Task Unsafe_external_references_fail_before_storage(string reference)
    {
        var result = await RegisterAsync(new(EvidenceKind.ExternalDocument, Digest, reference, null, null));

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Theory]
    [InlineData(0, "ZAR", "2026-07-20")]
    [InlineData(100, "USD", "2026-07-20")]
    [InlineData(100, "ZAR", "20 July 2026")]
    public async Task Invalid_observation_values_fail_before_storage(long amount, string currency, string date)
    {
        var observation = new EvidenceObservation(null, amount, currency, date, null, null, null, null);
        var result = await RegisterAsync(new(EvidenceKind.AgentCapture, Digest, null, null, observation));

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact]
    public async Task Empty_observation_is_rejected_instead_of_creating_an_ambiguous_shape()
    {
        var observation = new EvidenceObservation(null, null, null, null, null, null, null, null);
        var result = await RegisterAsync(new(EvidenceKind.AgentCapture, Digest, null, null, observation));

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_observation"));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact]
    public async Task Unknown_observation_reference_returns_not_found_without_partial_rows()
    {
        var observation = new EvidenceObservation(LedgerId.New().ToString(), -100, "ZAR", "2026-07-20", null, null, null, null);
        var result = await RegisterAsync(new(EvidenceKind.AgentCapture, Digest, null, null, observation));

        AssertError(result, 4, "operation.not_found");
        Assert.Equal(0L, await CountAsync("evidence_record"));
        Assert.Equal(0L, await CountAsync("idempotency_record"));
    }

    // NFR-LEDGER-LOCAL-PRIVACY
    [Theory]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("providerCursor")]
    [InlineData("recipient")]
    [InlineData("rawPayload")]
    [InlineData("deliveryState")]
    public async Task Forbidden_provider_or_payload_fields_are_rejected_by_the_closed_schema(string field)
    {
        var input = $"{{\"kind\":\"agent_capture\",\"logicalIdentityDigest\":\"{Digest}\",\"{field}\":\"private\"}}";
        var result = await RunRawAsync("ledger.evidence.register", input, "key");

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Fact]
    public async Task Missing_idempotency_key_is_rejected_before_dispatch()
    {
        var input = JsonSerializer.SerializeToElement(new RegisterEvidenceInput(EvidenceKind.AgentCapture, Digest, null, null, null), LedgerJsonContext.Default.RegisterEvidenceInput);
        var result = await RunAsync("ledger.evidence.register", input, null);

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // DM-LEDGER-EVIDENCE-RECONCILIATION-CONTRACTS
    [Theory]
    [InlineData("{\"logicalIdentityDigest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("{\"kind\":\"agent_capture\"}")]
    public async Task Missing_required_evidence_fields_are_rejected_by_the_closed_schema(string input)
    {
        var result = await RunRawAsync("ledger.evidence.register", input, "key");

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // DM-LEDGER-EVIDENCE-RECONCILIATION-CONTRACTS
    [Fact]
    public async Task Feature_operation_without_an_input_envelope_is_a_validation_error()
    {
        var result = await process.RunAsync(["ledger", "evidence", "register"], null, CancellationToken.None);

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("evidence_record"));
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Fact]
    public async Task Get_rejects_invalid_identity_and_returns_not_found_for_an_unknown_valid_identity()
    {
        AssertError(await GetAsync("invalid"), 3, "validation.invalid_input");
        AssertError(await GetAsync(LedgerId.New().ToString()), 4, "operation.not_found");
    }

    // FR-LEDGER-EVIDENCE-REGISTRATION
    [Fact]
    public async Task Registration_alone_does_not_create_a_link_or_reconciliation_decision()
    {
        var detail = SuccessDetail(await RegisterAsync(new(EvidenceKind.StatementRow, Digest, null, OtherFingerprint, null)));

        Assert.Empty(detail.LinkHistory);
        Assert.Equal(0L, await CountAsync("evidence_link_event"));
        Assert.Equal(0L, await CountAsync("reconciliation_decision"));
        Assert.Equal(0L, await CountAsync("reconciliation_decision_authority"));
    }

    // DM-LEDGER-EVIDENCE-RECORD-LINK
    [Fact]
    public async Task Stored_evidence_tables_and_public_result_retain_only_the_allowlist()
    {
        var result = await RegisterAsync(new(EvidenceKind.AgentCapture, Digest, "opaque:reference", OtherFingerprint, null));
        await using var connection = await OpenAsync();
        var columns = await RowsAsync(connection, "SELECT name FROM pragma_table_xinfo('evidence_record') ORDER BY cid;");

        Assert.Equal("evidence_id,kind,logical_identity_digest,opaque_external_reference,content_fingerprint,recorded_by,recorded_at", string.Join(',', columns));
        foreach (var canary in new[] { "mailbox", "mime", "providerCursor", "recipient", "rawPayload", "delivery" })
        {
            Assert.DoesNotContain(canary, result.Stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<ProcessResult> RegisterAsync(RegisterEvidenceInput input, string key = "request-key") =>
        await RunAsync("ledger.evidence.register", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RegisterEvidenceInput), key);

    private async Task<ProcessResult> GetAsync(string evidenceId) =>
        await RunAsync("ledger.evidence.get", JsonSerializer.SerializeToElement(new GetEvidenceInput(evidenceId), LedgerJsonContext.Default.GetEvidenceInput), null);

    private async Task<ProcessResult> RunRawAsync(string operationId, string input, string? idempotencyKey)
    {
        using var inputDocument = JsonDocument.Parse(input);
        return await RunAsync(operationId, inputDocument.RootElement.Clone(), idempotencyKey);
    }

    private async Task<ProcessResult> RunAsync(string operationId, JsonElement input, string? idempotencyKey)
    {
        var request = new RequestEnvelope("1.0", new("automation", "evidence-test", "run-01"), input, idempotencyKey);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static EvidenceRecordDetail SuccessDetail(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Equal("success", envelope.Outcome);
        return JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.EvidenceRecordDetail)!;
    }

    private static void AssertEquivalent(EvidenceRecordDetail expected, EvidenceRecordDetail actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected, LedgerJsonContext.Default.EvidenceRecordDetail),
            JsonSerializer.Serialize(actual, LedgerJsonContext.Default.EvidenceRecordDetail));

    private static void AssertError(ProcessResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Equal("error", envelope.Outcome);
        Assert.Equal(errorCode, envelope.Error!.Code);
    }

    private async Task<(string AccountId, string InstrumentId, string CardholderId)> SeedObservationReferencesAsync()
    {
        var account = LedgerId.New().ToString();
        var instrument = LedgerId.New().ToString();
        var cardholder = LedgerId.New().ToString();
        const string at = "2026-07-21T00:00:00Z";
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, $"""
            INSERT INTO account VALUES ('{account}', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '{at}');
            INSERT INTO catalogue_lifecycle_event VALUES ('account-create', 'account', '{account}', 'create', NULL, 'Primary', 'primary', NULL, 'owner', '{at}', NULL);
            INSERT INTO payment_instrument VALUES ('{instrument}', '{account}', '1234', '{at}');
            INSERT INTO catalogue_lifecycle_event VALUES ('instrument-create', 'payment_instrument', '{instrument}', 'create', NULL, 'Card', 'card', NULL, 'owner', '{at}', NULL);
            INSERT INTO cardholder VALUES ('{cardholder}', '{at}');
            INSERT INTO catalogue_lifecycle_event VALUES ('cardholder-create', 'cardholder', '{cardholder}', 'create', NULL, 'Owner', 'owner', NULL, 'owner', '{at}', NULL);
            """);
        return (account, instrument, cardholder);
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync() =>
        await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> RowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }
}
