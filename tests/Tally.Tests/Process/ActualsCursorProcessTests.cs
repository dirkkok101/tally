using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Features.Ledger.Actuals;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Actuals;
using Xunit;

namespace Tally.Tests.Process;

[SupportedOSPlatform("linux")]
public sealed class ActualsCursorProcessTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-actuals-cursor-" + Guid.NewGuid().ToString("N"));
    private LedgerDb database = null!;
    private TallyProcess process = null!;
    private ActualsOperationModule module = null!;

    [Fact]
    public void Descriptor_is_typed_versioned_query_without_idempotency()
    {
        var descriptor = Assert.Single(module.Descriptors);

        Assert.Equal(ActualsOperationModule.OperationId, descriptor.OperationId);
        Assert.Equal("query", descriptor.Kind);
        Assert.False(descriptor.RequiresIdempotencyKey);
        Assert.Equal(typeof(QueryActualsInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(ActualsQueryResult), descriptor.ResultTypeInfo.Type);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
        Assert.Contains(descriptor.DomainErrors!, error => error.Code == ActualsErrors.CursorInvalid && error.ExitCode == 7);
    }

    [Fact]
    public async Task Later_page_dispatch_accepts_cursor_only_and_returns_same_snapshot()
    {
        var first = await FirstPage();

        var second = await DispatchSuccess(new(Cursor: first.Cursor));

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.Totals, second.Totals);
        Assert.Equal(first.Groups, second.Groups);
        Assert.Single(second.Items);
        Assert.Null(second.Cursor);
    }

    [Fact]
    public async Task Cursor_with_resubmitted_filters_fails_as_filter_mismatch()
    {
        var cursor = (await FirstPage()).Cursor;

        var result = await Dispatch(new(new(), Cursor: cursor));

        Assert.False(result.IsSuccess);
        Assert.Equal(ActualsErrors.CursorFilterMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task FR_LEDGER_SNAPSHOT_PAGINATION_published_invalid_cursor_uses_the_declared_compatibility_exit()
    {
        var result = await RunProcess(new(Cursor: "not-base64"));

        AssertProcessError(result, 7, ActualsErrors.CursorInvalid);
    }

    [Fact]
    public async Task FR_LEDGER_SNAPSHOT_PAGINATION_published_resubmitted_filters_use_the_declared_compatibility_exit()
    {
        var cursor = (await FirstPage()).Cursor;

        var result = await RunProcess(new(new(), Cursor: cursor));

        AssertProcessError(result, 7, ActualsErrors.CursorFilterMismatch);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("e30")]
    [InlineData("")]
    public async Task Malformed_or_incomplete_cursor_has_one_stable_compatibility_error(string cursor)
    {
        var result = await Dispatch(new(Cursor: cursor));

        Assert.False(result.IsSuccess);
        Assert.Equal(ActualsErrors.CursorInvalid, result.ErrorCode);
    }

    [Theory]
    [InlineData("cursorVersion", "99", "LEDGER-SNAPSHOT-CURSOR-INVALID")]
    [InlineData("contractVersion", "2.0", "LEDGER-SNAPSHOT-CONTRACT-MISMATCH")]
    [InlineData("filterHash", "altered", "LEDGER-SNAPSHOT-FILTER-MISMATCH")]
    [InlineData("generationFingerprint", "altered", "LEDGER-SNAPSHOT-GENERATION-MISMATCH")]
    [InlineData("categoryHierarchyFingerprint", "altered", "LEDGER-SNAPSHOT-HIERARCHY-MISMATCH")]
    [InlineData("expiresAt", "2000-01-01T00:00:00Z", "LEDGER-SNAPSHOT-EXPIRED")]
    public async Task Altered_cursor_metadata_fails_without_a_partial_page(string property, string value, string expectedError)
    {
        var cursor = Tamper((await FirstPage()).Cursor!, property, value);

        var result = await Dispatch(new(Cursor: cursor));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.ErrorCode);
    }

    [Fact]
    public async Task Unknown_snapshot_identity_returns_not_found_without_live_fallback()
    {
        var cursor = Tamper((await FirstPage()).Cursor!, "snapshotId", LedgerId.New().ToString());

        var result = await Dispatch(new(Cursor: cursor));

        Assert.False(result.IsSuccess);
        Assert.Equal(ActualsErrors.SnapshotNotFound, result.ErrorCode);
    }

    [Theory]
    [InlineData("agentmail")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("whatsapp")]
    [InlineData("recipient")]
    [InlineData("deliveryretry")]
    [InlineData("rawpayload")]
    public void Public_actuals_schema_is_provider_and_transport_neutral(string forbidden)
    {
        var schema = JsonSerializer.Serialize(module.Descriptors.Single().ToSchema(), LedgerJsonContext.Default.OperationSchema);

        Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_unchanged_first_page_queries_have_byte_stable_contract_shape_and_totals()
    {
        var first = await FirstPage();
        var second = await FirstPage();

        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Equal(
            JsonSerializer.Serialize(first.Items.ToArray(), ActualsJsonContext.Default.ActualsPageItemArray),
            JsonSerializer.Serialize(second.Items.ToArray(), ActualsJsonContext.Default.ActualsPageItemArray));
        Assert.Equal(first.Totals, second.Totals);
        Assert.Equal(first.Groups, second.Groups);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new(OperationRegistry.Create(), LedgerServices.Create(database));
        var factory = new LedgerConnectionFactory(new HostArtifactProtection());
        module = new(new(new QuerySnapshotStore(database, factory)));

        var account = await RunSuccess(
            "ledger.account.create",
            new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1111", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail,
            "account");
        await Record(account.AccountId, "-1", "2026-07-01", "first");
        await Record(account.AccountId, "-2", "2026-07-02", "second");
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private Task<ActualsQueryResult> FirstPage() => DispatchSuccess(new(new ActualsFilterInput(GroupBy: ActualsGrouping.PoolCategory), 1));

    private async Task<CommandResult<JsonElement>> Dispatch(QueryActualsInput input)
    {
        var json = JsonSerializer.SerializeToElement(input, ActualsJsonContext.Default.QueryActualsInput);
        return await module.HandleAsync(ActualsOperationModule.OperationId, new(json, null, null), CancellationToken.None);
    }

    private async Task<ActualsQueryResult> DispatchSuccess(QueryActualsInput input)
    {
        var result = await Dispatch(input);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value, ActualsJsonContext.Default.ActualsQueryResult)!;
    }

    private async Task<ProcessResult> RunProcess(QueryActualsInput input)
    {
        var request = new RequestEnvelope(
            "1.0",
            new("automation", "actuals-process-test"),
            JsonSerializer.SerializeToElement(input, ActualsJsonContext.Default.QueryActualsInput),
            null);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        return await process.RunAsync(["ledger", "actuals", "query", "--input", "-"], body, CancellationToken.None);
    }

    private static void AssertProcessError(ProcessResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + errorCode, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private Task<TransactionDetail> Record(string accountId, string amount, string date, string description)
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(description)));
        return RunSuccess(
            "ledger.transaction.record",
            new RecordTransactionInput(accountId, amount, "ZAR", date, null, description, null, null, new(EvidenceKind.AgentCapture, digest, null, null, null)),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail,
            "record-" + description);
    }

    private async Task<TResult> RunSuccess<TInput, TResult>(
        string operationId,
        TInput input,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType,
        string key)
    {
        var request = new RequestEnvelope("1.0", new("human", "actuals-process-test"), JsonSerializer.SerializeToElement(input, inputType), key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        var result = await process.RunAsync(arguments, body, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var response = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(response.Result!.Value, resultType)!;
    }

    private static string Tamper(string cursor, string property, string value)
    {
        var bytes = Decode(cursor);
        var node = JsonNode.Parse(bytes)!.AsObject();
        node[property] = value;
        return Encode(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private static byte[] Decode(string value)
    {
        var encoded = value.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        return Convert.FromBase64String(encoded);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
