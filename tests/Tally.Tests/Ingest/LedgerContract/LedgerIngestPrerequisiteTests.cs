using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Ingest.LedgerContract;

// Covers TC-INGEST-LEDGER-PUBLIC-CONFORMANCE and DD-INGEST-LEDGER-PUBLIC-INTEGRATION.
// INGEST consumes LEDGER only through OperationRegistry descriptors, the shared process
// executor, and Contracts/Ledger records. Host composition (LedgerRuntimeBootstrap and
// LedgerServices) is the runtime host's responsibility, not part of the INGEST seam, so it
// is confined to InitializeAsync below.
[SupportedOSPlatform("linux")]
public sealed class LedgerIngestPrerequisiteTests : IAsyncLifetime
{
    private const string AccountGet = "ledger.account.get";
    private const string TransactionRecord = "ledger.transaction.record";
    private const string TransactionGet = "ledger.transaction.get";

    private static readonly string[] RequiredOperationIds = [AccountGet, TransactionRecord, TransactionGet];

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-ledger-{Guid.NewGuid():N}");
    private TallyProcess process = null!;

    public static TheoryData<string> RequiredOperations => new(RequiredOperationIds);

    // DM-LEDGER-OPERATION-DESCRIPTOR
    [Theory]
    [MemberData(nameof(RequiredOperations))]
    public void Required_operation_publishes_a_concrete_versioned_source_generated_contract(string operationId)
    {
        var descriptor = Assert.IsType<OperationDescriptor>(OperationRegistry.Create().Find(operationId));

        Assert.NotEqual(typeof(JsonElement), descriptor.RequestTypeInfo.Type);
        Assert.NotEqual(typeof(JsonElement), descriptor.ResultTypeInfo.Type);
        Assert.NotEqual("FoundationOperationHandler", descriptor.HandlerTarget);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
        Assert.Equal(descriptor.Kind == "mutation", descriptor.RequiresIdempotencyKey);
    }

    // DD-INGEST-LEDGER-PUBLIC-INTEGRATION
    [Fact]
    public void Ingest_depends_on_exactly_three_public_ledger_operations()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(3, RequiredOperationIds.Length);
        Assert.Equal(3, RequiredOperationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(RequiredOperationIds, operationId => Assert.NotNull(registry.Find(operationId)));
        Assert.Equal(typeof(GetAccountInput), registry.Find(AccountGet)!.RequestTypeInfo.Type);
        Assert.Equal(typeof(AccountDetail), registry.Find(AccountGet)!.ResultTypeInfo.Type);
        Assert.Equal(typeof(RecordTransactionInput), registry.Find(TransactionRecord)!.RequestTypeInfo.Type);
        Assert.Equal(typeof(GetTransactionInput), registry.Find(TransactionGet)!.RequestTypeInfo.Type);
        Assert.Equal(typeof(TransactionDetail), registry.Find(TransactionRecord)!.ResultTypeInfo.Type);
        Assert.Equal(typeof(TransactionDetail), registry.Find(TransactionGet)!.ResultTypeInfo.Type);
    }

    // NFR-INGEST-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public void Required_operations_publish_the_stable_errors_and_exit_codes_ingest_handles()
    {
        var registry = OperationRegistry.Create();

        AssertError(registry, AccountGet, "LEDGER-ACCOUNT-NOT-FOUND", 4);
        AssertError(registry, TransactionGet, "LEDGER-TRANSACTION-NOT-FOUND", 4);
        AssertError(registry, TransactionRecord, "LEDGER-ACCOUNT-NOT-FOUND", 4);
        AssertError(registry, TransactionRecord, "LEDGER-IDEMPOTENCY-001", 5);
        foreach (var operationId in RequiredOperationIds)
        {
            AssertError(registry, operationId, "contract.incompatible", 7);
            AssertError(registry, operationId, "validation.invalid_input", 3);
        }

        Assert.DoesNotContain(registry.Find(AccountGet)!.ToSchema().Errors, error => error.Code == "LEDGER-IDEMPOTENCY-001");
        Assert.DoesNotContain(registry.Find(TransactionGet)!.ToSchema().Errors, error => error.Code == "LEDGER-IDEMPOTENCY-001");
    }

    // DD-INGEST-LEDGER-PUBLIC-INTEGRATION
    [Fact]
    public void Required_operation_contracts_expose_only_published_contract_types()
    {
        var registry = OperationRegistry.Create();

        foreach (var operationId in RequiredOperationIds)
        {
            var descriptor = registry.Find(operationId)!;

            Assert.StartsWith("Tally.Contracts.", descriptor.RequestTypeInfo.Type.Namespace, StringComparison.Ordinal);
            Assert.StartsWith("Tally.Contracts.", descriptor.ResultTypeInfo.Type.Namespace, StringComparison.Ordinal);
        }
    }

    // NFR-INGEST-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public void Required_operation_schemas_expose_no_private_storage_surface()
    {
        var registry = OperationRegistry.Create();

        foreach (var operationId in RequiredOperationIds)
        {
            var schema = registry.Find(operationId)!.ToSchema();

            foreach (var forbidden in new[] { "sqlite", "connectionString", "ledgerDb", "dataRoot", "sql", "handler", "rawPayload" })
            {
                Assert.DoesNotContain(forbidden, schema.RequestSchema, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(forbidden, schema.ResultSchema, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // DM-LEDGER-ACCOUNT-CATEGORY-CONTRACTS
    [Fact]
    public async Task Account_get_returns_the_account_facts_ingest_requires()
    {
        var created = await CreateAccountAsync();

        var fetched = AccountSuccess(await RunAsync(
            AccountGet,
            JsonSerializer.SerializeToElement(new GetAccountInput(created.AccountId), LedgerJsonContext.Default.GetAccountInput),
            null));

        Assert.Equal(created.AccountId, fetched.AccountId);
        Assert.Equal(AccountClass.Asset, fetched.AccountClass);
        Assert.Equal("ZAR", fetched.CurrencyCode);
        Assert.Equal(AccountStatus.Active, fetched.Status);
        using var schema = JsonDocument.Parse(OperationRegistry.Create().Find(AccountGet)!.ToSchema().ResultSchema);
        Assert.Equal(
            new[] { "active", "archived" },
            schema.RootElement.GetProperty("properties").GetProperty("status").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT
    [Fact]
    public async Task Record_and_get_round_trip_every_immutable_commit_fact()
    {
        var account = await CreateAccountAsync();
        var input = RecordInput(account.AccountId);

        var recorded = await RecordAsync(input, "commit-key");
        var fetched = await GetTransactionAsync(recorded.TransactionId);

        Assert.Equal(account.AccountId, fetched.AccountId);
        Assert.Equal("-12.34", fetched.SignedAmount);
        Assert.Equal("ZAR", fetched.CurrencyCode);
        Assert.Equal("2026-07-01", fetched.TransactionDate);
        Assert.Equal("2026-07-03", fetched.PostingDate);
        Assert.Equal("Owner-safe statement line", fetched.OriginalDescription);
        Assert.Equal(TransactionLifecycleStatus.Active, fetched.LifecycleStatus);
        Assert.False(string.IsNullOrWhiteSpace(fetched.EffectiveDate));
        Assert.False(string.IsNullOrWhiteSpace(fetched.RecordedByOsIdentity));
        Assert.EndsWith("Z", fetched.RecordedAt, StringComparison.Ordinal);
        AssertEquivalent(recorded, fetched);
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT
    [Fact]
    public async Task Statement_provenance_round_trips_through_initial_evidence()
    {
        var account = await CreateAccountAsync();

        var recorded = await RecordAsync(RecordInput(account.AccountId), "provenance-key");
        var fetched = await GetTransactionAsync(recorded.TransactionId);

        var evidence = Assert.Single(fetched.Evidence);
        Assert.Equal(EvidenceKind.StatementRow, evidence.Kind);
        Assert.Equal(Digest('a'), evidence.LogicalIdentityDigest);
        Assert.Equal("statement:page-1-line-7", evidence.OpaqueExternalReference);
        Assert.Equal(Digest('b'), evidence.ContentFingerprint);
        Assert.False(string.IsNullOrWhiteSpace(evidence.EvidenceId));
        Assert.False(string.IsNullOrWhiteSpace(evidence.LinkEventId));
    }

    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Replayed_record_with_equivalent_input_returns_the_prior_logical_result()
    {
        var account = await CreateAccountAsync();
        var input = RecordInput(account.AccountId);

        var first = await RecordAsync(input, "replay-key");
        var replay = await RecordAsync(input, "replay-key");

        AssertEquivalent(first, replay);
        AssertEquivalent(first, await GetTransactionAsync(first.TransactionId));
    }

    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task Same_key_with_changed_input_conflicts_and_preserves_the_original()
    {
        var account = await CreateAccountAsync();
        var first = await RecordAsync(RecordInput(account.AccountId), "conflict-key");

        var conflict = await RunAsync(
            TransactionRecord,
            JsonSerializer.SerializeToElement(RecordInput(account.AccountId) with { SignedAmount = "-99.00" }, LedgerJsonContext.Default.RecordTransactionInput),
            "conflict-key");

        Assert.Equal(5, conflict.ExitCode);
        var envelope = JsonSerializer.Deserialize(conflict.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Equal("error", envelope.Outcome);
        Assert.Equal("LEDGER-IDEMPOTENCY-001", envelope.Error!.Code);
        AssertEquivalent(first, await GetTransactionAsync(first.TransactionId));
    }

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private static string Digest(char character) => new(character, 64);

    private static RecordTransactionInput RecordInput(string accountId) => new(
        accountId, "-12.34", "ZAR", "2026-07-01", "2026-07-03", "Owner-safe statement line", null, null,
        new(EvidenceKind.StatementRow, Digest('a'), "statement:page-1-line-7", Digest('b'), null));

    private async Task<AccountDetail> CreateAccountAsync() => AccountSuccess(await RunAsync(
        "ledger.account.create",
        JsonSerializer.SerializeToElement(new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1234", "ZAR"), LedgerJsonContext.Default.CreateAccountInput),
        "account-key"));

    private async Task<TransactionDetail> RecordAsync(RecordTransactionInput input, string idempotencyKey) => TransactionSuccess(await RunAsync(
        TransactionRecord,
        JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput),
        idempotencyKey));

    private async Task<TransactionDetail> GetTransactionAsync(string transactionId) => TransactionSuccess(await RunAsync(
        TransactionGet,
        JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId), LedgerJsonContext.Default.GetTransactionInput),
        null));

    private async Task<ProcessResult> RunAsync(string operationId, JsonElement input, string? idempotencyKey)
    {
        var request = new RequestEnvelope("1.0", new("automation", "ingest-prerequisite", "run-01"), input, idempotencyKey);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static AccountDetail AccountSuccess(ProcessResult result) =>
        JsonSerializer.Deserialize(SuccessResult(result), LedgerJsonContext.Default.AccountDetail)!;

    private static TransactionDetail TransactionSuccess(ProcessResult result) =>
        JsonSerializer.Deserialize(SuccessResult(result), LedgerJsonContext.Default.TransactionDetail)!;

    private static JsonElement SuccessResult(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Equal("success", envelope.Outcome);
        return envelope.Result!.Value;
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT freezes the immutable commit facts only; the optional
    // history projection is not one of them, and record returns it while get omits it unless asked.
    private static void AssertEquivalent(TransactionDetail expected, TransactionDetail actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected with { History = null }, LedgerJsonContext.Default.TransactionDetail),
            JsonSerializer.Serialize(actual with { History = null }, LedgerJsonContext.Default.TransactionDetail));

    private static void AssertError(OperationRegistry registry, string operationId, string code, int exitCode) =>
        Assert.Contains(registry.Find(operationId)!.ToSchema().Errors, error => error.Code == code && error.ExitCode == exitCode);
}
