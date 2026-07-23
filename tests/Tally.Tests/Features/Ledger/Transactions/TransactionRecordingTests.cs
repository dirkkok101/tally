using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Xunit;

namespace Tally.Tests.Features.Ledger.Transactions;

[SupportedOSPlatform("linux")]
public sealed class TransactionRecordingTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-transaction-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;

    [Fact]
    public void DM_LEDGER_TRANSACTION_CONTRACTS_registry_exposes_record_and_get()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(74, registry.Descriptors.Count);
        Assert.Equal(typeof(RecordTransactionInput), registry.Find("ledger.transaction.record")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(GetTransactionInput), registry.Find("ledger.transaction.get")!.RequestTypeInfo.Type);
        Assert.All(new[] { "ledger.transaction.record", "ledger.transaction.get" }, operation => Assert.Equal(typeof(TransactionDetail), registry.Find(operation)!.ResultTypeInfo.Type));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_records_exact_facts_and_initial_evidence()
    {
        var account = await CreateAccount();

        var detail = Transaction(await Record(Input(account.AccountId, amount: "-12.34"), "record"));

        Assert.True(LedgerId.TryParse(detail.TransactionId, out _, out _));
        Assert.Equal(account.AccountId, detail.AccountId);
        Assert.Equal("-12.34", detail.SignedAmount);
        Assert.Equal("ZAR", detail.CurrencyCode);
        Assert.Equal("2026-07-01", detail.TransactionDate);
        Assert.Equal("2026-07-01", detail.EffectiveDate);
        Assert.Equal("Owner-safe purchase", detail.OriginalDescription);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, detail.ReconciliationState);
        var evidence = Assert.Single(detail.Evidence);
        Assert.Equal(EvidenceKind.AgentCapture, evidence.Kind);
        Assert.Equal(EvidenceLinkRole.Supporting, evidence.Role);
        Assert.Equal(Digest('a'), evidence.LogicalIdentityDigest);
    }

    [Fact]
    public async Task DD_LEDGER_FINANCIAL_REPRESENTATION_distinct_posting_date_round_trips_without_changing_effective_date()
    {
        var account = await CreateAccount();

        var detail = Transaction(await Record(Input(account.AccountId, postingDate: "2026-07-03"), "record"));

        Assert.Equal("2026-07-01", detail.TransactionDate);
        Assert.Equal("2026-07-03", detail.PostingDate);
        Assert.Equal("2026-07-01", detail.EffectiveDate);
    }

    [Theory]
    [InlineData("12.34")]
    [InlineData("-12.34")]
    public async Task DD_LEDGER_FINANCIAL_REPRESENTATION_owner_economic_sign_round_trips_exactly(string amount)
    {
        var account = await CreateAccount();

        Assert.Equal(amount, Transaction(await Record(Input(account.AccountId, amount: amount), "record")).SignedAmount);
    }

    [Theory]
    [InlineData("0", "amount.zero")]
    [InlineData("0.00", "amount.invalid")]
    [InlineData("1.2", "amount.invalid")]
    [InlineData("1e2", "amount.invalid")]
    public async Task DD_LEDGER_FINANCIAL_REPRESENTATION_invalid_or_zero_money_is_rejected_atomically(string amount, string error)
    {
        var account = await CreateAccount();

        AssertError(await Record(Input(account.AccountId, amount: amount), "record"), 3, error);
        Assert.Equal(0, await Count("transaction_fact"));
        Assert.Equal(0, await Count("evidence_record"));
    }

    [Theory]
    [InlineData("USD", "2026-07-01", null, "currency.unsupported")]
    [InlineData("ZAR", "2026-02-30", null, "date.invalid")]
    [InlineData("ZAR", "2026-07-01", "03-07-2026", "date.invalid")]
    public async Task DD_LEDGER_FINANCIAL_REPRESENTATION_currency_and_dates_are_closed(string currency, string transactionDate, string? postingDate, string error)
    {
        var account = await CreateAccount();
        var input = Input(account.AccountId, transactionDate: transactionDate, postingDate: postingDate) with { CurrencyCode = currency };

        AssertError(await Record(input, "record"), 3, error);
        Assert.Equal(0, await Count("transaction_fact"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_missing_and_archived_accounts_are_rejected_without_evidence()
    {
        AssertError(await Record(Input(LedgerId.New().ToString()), "missing"), 4, AccountStore.NotFoundError);
        var account = await CreateAccount();
        await ArchiveAccount(account.AccountId);

        AssertError(await Record(Input(account.AccountId), "archived"), 6, AccountStore.ArchivedError);
        Assert.Equal(0, await Count("transaction_fact"));
        Assert.Equal(0, await Count("evidence_record"));
    }

    [Theory]
    [InlineData("providerPayload")]
    [InlineData("sourceReference")]
    [InlineData("credential")]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_provider_and_secret_fields_are_rejected_before_mutation(string field)
    {
        var account = await CreateAccount();
        var input = RawInput(account.AccountId, $", \"{field}\":\"forbidden\"");

        AssertError(await Run("ledger.transaction.record", input, "record"), 3, "validation.invalid_input");
        Assert.Equal(0, await Count("transaction_fact"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_unsupported_evidence_kind_is_rejected()
    {
        var account = await CreateAccount();
        var input = Json(RawInputText(account.AccountId).Replace("agent_capture", "bank_email", StringComparison.Ordinal));

        AssertError(await Run("ledger.transaction.record", input, "record"), 3, "validation.invalid_input");
        Assert.Equal(0, await Count("evidence_record"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_incompatible_evidence_observation_is_atomic()
    {
        var account = await CreateAccount();
        var observation = new EvidenceObservation(account.AccountId, -999, "ZAR", "2026-07-01", null, null, null, null);

        AssertError(await Record(Input(account.AccountId, observation: observation), "record"), 3, TransactionFact.EvidenceIncompatibleError);
        Assert.Equal(0, await Count("transaction_fact"));
        Assert.Equal(0, await Count("evidence_record"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_declared_payment_identities_are_independently_known()
    {
        var account = await CreateAccount();
        var instrument = await CreateInstrument(account.AccountId);
        var cardholder = await CreateCardholder();

        var detail = Transaction(await Record(Input(account.AccountId) with { InstrumentId = instrument.InstrumentId, CardholderId = cardholder.CardholderId }, "record"));

        Assert.Equal(TransactionKnowledgeState.Known, detail.PaymentAttribution.InstrumentState);
        Assert.Equal(instrument.InstrumentId, detail.PaymentAttribution.InstrumentId);
        Assert.Equal(TransactionKnowledgeState.Known, detail.PaymentAttribution.CardholderState);
        Assert.Equal(cardholder.CardholderId, detail.PaymentAttribution.CardholderId);
        Assert.Equal(2, detail.History!.PaymentAttribution.Count);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_incompatible_instrument_account_is_rejected()
    {
        var transactionAccount = await CreateAccount("Transaction", "****1111", "account-one");
        var otherAccount = await CreateAccount("Other", "****2222", "account-two");
        var instrument = await CreateInstrument(otherAccount.AccountId);

        AssertError(await Record(Input(transactionAccount.AccountId) with { InstrumentId = instrument.InstrumentId }, "record"), 6, TransactionErrors.AttributionIncompatible);
        Assert.Equal(0, await Count("transaction_fact"));
    }

    [Fact]
    public async Task DD_LEDGER_DIMENSIONAL_ATTRIBUTION_missing_dimensions_remain_explicit_and_uninferred()
    {
        var account = await CreateAccount();

        var detail = Transaction(await Record(Input(account.AccountId), "record"));

        Assert.Equal(TransactionCategoryState.Uncategorized, detail.Category.State);
        Assert.Null(detail.Category.CategoryId);
        Assert.Empty(detail.Category.CurrentAncestryIds);
        Assert.Equal(TransactionPoolState.Unassigned, detail.Pool.State);
        Assert.Null(detail.Pool.PoolId);
        Assert.Equal(TransactionKnowledgeState.Unknown, detail.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, detail.PaymentAttribution.CardholderState);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_get_history_is_explicit_and_missing_identity_is_stable()
    {
        var account = await CreateAccount();
        var recorded = Transaction(await Record(Input(account.AccountId), "record"));

        Assert.Null(Transaction(await Get(recorded.TransactionId, false)).History);
        var history = Transaction(await Get(recorded.TransactionId, true)).History!;
        Assert.Empty(history.Lifecycle);
        Assert.Single(history.PaymentAttribution);
        Assert.Single(history.PoolAssignments);
        Assert.Empty(history.CategoryAssignments);
        AssertError(await Get(LedgerId.New().ToString(), false), 4, TransactionErrors.NotFound);
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_request_replay_is_stable_and_changed_request_conflicts()
    {
        var account = await CreateAccount();
        var input = Input(account.AccountId);
        var first = Transaction(await Record(input, "same"));

        Assert.Equal(first.TransactionId, Transaction(await Record(input, "same")).TransactionId);
        AssertError(await Record(input with { SignedAmount = "-99" }, "same"), 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(1, await Count("transaction_fact"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_logical_evidence_replay_is_stable_and_changed_facts_conflict()
    {
        var account = await CreateAccount();
        var input = Input(account.AccountId);
        var first = Transaction(await Record(input, "first"));

        Assert.Equal(first.TransactionId, Transaction(await Record(input, "second")).TransactionId);
        AssertError(await Record(input with { SignedAmount = "-99" }, "third"), 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(1, await Count("evidence_record"));
    }

    [Fact]
    public async Task FR_LEDGER_TRANSACTION_RECORDING_preexisting_evidence_identity_is_a_stable_conflict()
    {
        var account = await CreateAccount();
        var evidence = Input(account.AccountId).InitialEvidence;
        await RegisterEvidence(evidence);

        AssertError(await Record(Input(account.AccountId), "record"), 5, TransactionErrors.EvidenceConflict);
        Assert.Equal(0, await Count("transaction_fact"));
        Assert.Equal(1, await Count("evidence_record"));
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

    private static RecordTransactionInput Input(
        string accountId,
        string amount = "-12.34",
        string transactionDate = "2026-07-01",
        string? postingDate = null,
        EvidenceObservation? observation = null) => new(
            accountId,
            amount,
            "ZAR",
            transactionDate,
            postingDate,
            "Owner-safe purchase",
            null,
            null,
            new(EvidenceKind.AgentCapture, Digest('a'), "capture:one", null, observation));

    private Task<ProcessResult> Record(RecordTransactionInput input, string key) => Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), key);
    private Task<ProcessResult> Get(string transactionId, bool history) => Run("ledger.transaction.get", JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId, history), LedgerJsonContext.Default.GetTransactionInput), null);

    private async Task<AccountDetail> CreateAccount(string name = "Primary", string masked = "****1234", string key = "account")
    {
        var input = JsonSerializer.SerializeToElement(new CreateAccountInput("Test Bank", name, AccountType.Cheque, masked, "ZAR"), LedgerJsonContext.Default.CreateAccountInput);
        return Success(await Run("ledger.account.create", input, key), LedgerJsonContext.Default.AccountDetail);
    }

    private Task<ProcessResult> ArchiveAccount(string accountId) => Run("ledger.account.archive", JsonSerializer.SerializeToElement(new ArchiveAccountInput(accountId, "closed"), LedgerJsonContext.Default.ArchiveAccountInput), "archive-account");

    private async Task<PaymentInstrumentDetail> CreateInstrument(string accountId)
    {
        var input = JsonSerializer.SerializeToElement(new CreatePaymentInstrumentInput("Primary card", accountId, "1234"), LedgerJsonContext.Default.CreatePaymentInstrumentInput);
        return Success(await Run("ledger.instrument.create", input, "instrument"), LedgerJsonContext.Default.PaymentInstrumentDetail);
    }

    private async Task<CardholderDetail> CreateCardholder()
    {
        var input = JsonSerializer.SerializeToElement(new CreateCardholderInput("Owner"), LedgerJsonContext.Default.CreateCardholderInput);
        return Success(await Run("ledger.cardholder.create", input, "cardholder"), LedgerJsonContext.Default.CardholderDetail);
    }

    private Task<ProcessResult> RegisterEvidence(RegisterEvidenceInput evidence) => Run("ledger.evidence.register", JsonSerializer.SerializeToElement(evidence, LedgerJsonContext.Default.RegisterEvidenceInput), "evidence");

    private async Task<long> Count(string table)
    {
        await using var connection = await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new SafeActor("human", "transaction-test"), input, key), LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static JsonElement RawInput(string accountId, string suffix) => Json(RawInputText(accountId).Replace("\n}", suffix + "\n}", StringComparison.Ordinal));
    private static string RawInputText(string accountId) => $$"""
        {
          "accountId":"{{accountId}}",
          "signedAmount":"-12.34",
          "currencyCode":"ZAR",
          "transactionDate":"2026-07-01",
          "postingDate":null,
          "originalDescription":"Owner-safe purchase",
          "instrumentId":null,
          "cardholderId":null,
          "initialEvidence":{
            "kind":"agent_capture",
            "logicalIdentityDigest":"{{Digest('a')}}",
            "opaqueExternalReference":"capture:one",
            "contentFingerprint":null,
            "observation":null
          }
        }
        """;

    private static string Digest(char value) => new(value, 64);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static TransactionDetail Transaction(ProcessResult result) => Success(result, LedgerJsonContext.Default.TransactionDetail);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code);
    }
}
