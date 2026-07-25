using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Ingest.LedgerContract;

[SupportedOSPlatform("linux")]
public sealed class LedgerContractClientTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-ingest-client-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "ingest-client", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient client = null!;

    // DM-LEDGER-ACCOUNT-CATEGORY-CONTRACTS
    [Fact]
    public async Task GetAccount_returns_the_released_account_detail_unchanged()
    {
        var created = await CreateAccountAsync();
        var expected = await GetAccountDirectAsync(created.AccountId);

        var result = await client.GetAccountAsync(created.AccountId, "1.0", actor, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            JsonSerializer.Serialize(expected, LedgerJsonContext.Default.AccountDetail),
            JsonSerializer.Serialize(result.Value, LedgerJsonContext.Default.AccountDetail));
        Assert.Null(result.Error);
        Assert.Equal(0, result.ExitCode);
    }

    // DM-LEDGER-OPERATION-DESCRIPTOR
    [Fact]
    public async Task GetAccount_preserves_the_published_not_found_error_and_exit()
    {
        var result = await client.GetAccountAsync("01J00000000000000000000000", "1.0", actor, CancellationToken.None);

        AssertError(result, 4, "LEDGER-ACCOUNT-NOT-FOUND", "not_found");
    }

    // NFR-INGEST-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public async Task GetAccount_rejects_an_unsupported_contract_version()
    {
        var result = await client.GetAccountAsync("01J00000000000000000000000", "2.0", actor, CancellationToken.None);

        AssertError(result, 7, "contract.incompatible", "compatibility");
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT
    [Fact]
    public async Task RecordTransaction_round_trips_the_exact_frozen_input_and_initial_evidence()
    {
        var account = await CreateAccountAsync();
        var request = FrozenRequest(account.AccountId, "ingest:candidate-1");

        var recorded = await client.RecordTransactionAsync(request, CancellationToken.None);
        var fetched = await client.GetTransactionAsync(recorded.Value!.TransactionId, "1.0", actor, CancellationToken.None);

        Assert.True(recorded.IsSuccess);
        Assert.True(fetched.IsSuccess);
        Assert.Equal(request.Input.AccountId, fetched.Value!.AccountId);
        Assert.Equal(request.Input.SignedAmount, fetched.Value.SignedAmount);
        Assert.Equal(request.Input.CurrencyCode, fetched.Value.CurrencyCode);
        Assert.Equal(request.Input.TransactionDate, fetched.Value.TransactionDate);
        Assert.Equal(request.Input.PostingDate, fetched.Value.PostingDate);
        Assert.Equal(request.Input.OriginalDescription, fetched.Value.OriginalDescription);
        var evidence = Assert.Single(fetched.Value.Evidence);
        Assert.Equal(request.Input.InitialEvidence.Kind, evidence.Kind);
        Assert.Equal(request.Input.InitialEvidence.LogicalIdentityDigest, evidence.LogicalIdentityDigest);
        Assert.Equal(request.Input.InitialEvidence.OpaqueExternalReference, evidence.OpaqueExternalReference);
        Assert.Equal(request.Input.InitialEvidence.ContentFingerprint, evidence.ContentFingerprint);
        Assert.Equal(request.Input.InitialEvidence.Observation, evidence.Observation);
    }

    // DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task RecordTransaction_replay_preserves_the_prior_result()
    {
        var account = await CreateAccountAsync();
        var request = FrozenRequest(account.AccountId, "ingest:replay");

        var first = await client.RecordTransactionAsync(request, CancellationToken.None);
        var replay = await client.RecordTransactionAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(
            JsonSerializer.Serialize(first.Value, LedgerJsonContext.Default.TransactionDetail),
            JsonSerializer.Serialize(replay.Value, LedgerJsonContext.Default.TransactionDetail));
    }

    // DM-LEDGER-OPERATION-DESCRIPTOR
    [Fact]
    public async Task RecordTransaction_preserves_idempotency_conflict_error_and_exit()
    {
        var account = await CreateAccountAsync();
        var request = FrozenRequest(account.AccountId, "ingest:conflict");
        Assert.True((await client.RecordTransactionAsync(request, CancellationToken.None)).IsSuccess);

        var result = await client.RecordTransactionAsync(request with
        {
            Input = request.Input with
            {
                SignedAmount = "-99.00",
                InitialEvidence = request.Input.InitialEvidence with
                {
                    Observation = request.Input.InitialEvidence.Observation! with { SignedAmountMinor = -9900 }
                }
            }
        }, CancellationToken.None);

        AssertError(result, 5, "LEDGER-IDEMPOTENCY-001", "conflict");
    }

    // DM-LEDGER-OPERATION-DESCRIPTOR
    [Fact]
    public async Task RecordTransaction_preserves_account_not_found_error_and_exit()
    {
        var result = await client.RecordTransactionAsync(FrozenRequest("01J00000000000000000000000", "ingest:missing"), CancellationToken.None);

        AssertError(result, 4, "LEDGER-ACCOUNT-NOT-FOUND", "not_found");
    }

    // NFR-INGEST-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public async Task Unsupported_record_version_fails_before_the_idempotent_write()
    {
        var account = await CreateAccountAsync();
        var unsupported = FrozenRequest(account.AccountId, "ingest:version") with
        {
            LedgerContractVersion = "2.0",
            Input = FrozenRequest(account.AccountId, "ignored").Input with { SignedAmount = "-99.00" }
        };

        var rejected = await client.RecordTransactionAsync(unsupported, CancellationToken.None);
        var accepted = await client.RecordTransactionAsync(FrozenRequest(account.AccountId, "ingest:version"), CancellationToken.None);

        AssertError(rejected, 7, "contract.incompatible", "compatibility");
        Assert.True(accepted.IsSuccess);
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT
    [Fact]
    public async Task RecordTransaction_uses_the_frozen_actor_without_substitution()
    {
        var account = await CreateAccountAsync();
        var request = FrozenRequest(account.AccountId, "ingest:actor") with { Actor = new SafeActor("automation", "unsafe label") };

        var result = await client.RecordTransactionAsync(request, CancellationToken.None);

        AssertError(result, 3, "validation.invalid_input", "validation");
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT
    [Fact]
    public async Task GetTransaction_requests_include_history_false_and_returns_the_public_detail()
    {
        var account = await CreateAccountAsync();
        var recorded = await client.RecordTransactionAsync(FrozenRequest(account.AccountId, "ingest:get"), CancellationToken.None);

        var fetched = await client.GetTransactionAsync(recorded.Value!.TransactionId, "1.0", actor, CancellationToken.None);

        Assert.True(fetched.IsSuccess);
        Assert.Null(fetched.Value!.History);
        Assert.Equal(recorded.Value.TransactionId, fetched.Value.TransactionId);
    }

    // DM-LEDGER-OPERATION-DESCRIPTOR
    [Fact]
    public async Task GetTransaction_preserves_the_published_not_found_error_and_exit()
    {
        var result = await client.GetTransactionAsync("01J00000000000000000000000", "1.0", actor, CancellationToken.None);

        AssertError(result, 4, "LEDGER-TRANSACTION-NOT-FOUND", "not_found");
    }

    // NFR-INGEST-PUBLIC-CONTRACT-COMPATIBILITY
    [Fact]
    public async Task GetTransaction_rejects_an_unsupported_contract_version()
    {
        var result = await client.GetTransactionAsync("01J00000000000000000000000", "2.0", actor, CancellationToken.None);

        AssertError(result, 7, "contract.incompatible", "compatibility");
    }

    // NFR-INGEST-AGENT-OPERABILITY
    [Fact]
    public async Task Cancellation_reaches_the_shared_executor()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAccountAsync("01J00000000000000000000000", "1.0", actor, source.Token));
    }

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        client = new LedgerContractClient(registry, process);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private static string Digest(char value) => new(value, 64);

    private FrozenLedgerRecordRequest FrozenRequest(string accountId, string idempotencyKey) => new(
        "1.0",
        "ledger.transaction.record",
        idempotencyKey,
        actor,
        new RecordTransactionInput(
            accountId,
            "-12.34",
            "ZAR",
            "2026-07-01",
            "2026-07-03",
            "Synthetic transaction",
            null,
            null,
            new RegisterEvidenceInput(
                EvidenceKind.StatementRow,
                Digest('a'),
                $"ingest:{Digest('a')}",
                Digest('b'),
                new EvidenceObservation(accountId, -1234, "ZAR", "2026-07-01", "2026-07-03", null, null, Digest('c')))));

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var input = JsonSerializer.SerializeToElement(
            new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1234", "ZAR"),
            LedgerJsonContext.Default.CreateAccountInput);
        var result = await RunAsync("ledger.account.create", input, $"account-{Guid.NewGuid():N}");
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!;
    }

    private async Task<AccountDetail> GetAccountDirectAsync(string accountId)
    {
        var input = JsonSerializer.SerializeToElement(new GetAccountInput(accountId), LedgerJsonContext.Default.GetAccountInput);
        var result = await RunAsync("ledger.account.get", input, null);
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.AccountDetail)!;
    }

    private async Task<ProcessResult> RunAsync(string operationId, JsonElement input, string? idempotencyKey)
    {
        var request = new RequestEnvelope("1.0", actor, input, idempotencyKey);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = registry.Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static void AssertError<T>(LedgerContractResult<T> result, int exitCode, string code, string category)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(category, result.Error.Category);
        Assert.Equal($"tally: {code}", result.StandardError);
    }
}
