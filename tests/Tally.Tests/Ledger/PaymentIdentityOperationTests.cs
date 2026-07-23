using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Dimensions;
using Tally.Features.Ledger.Dimensions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Dimensions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class PaymentIdentityOperationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-payment-identity-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;

    [Fact]
    public void DM_LEDGER_ATTRIBUTION_POOL_CONTRACTS_registry_exposes_twelve_typed_operations()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(74, registry.Descriptors.Count);
        Assert.Equal(12, registry.Descriptors.Count(descriptor => descriptor.OperationId.StartsWith("ledger.instrument.", StringComparison.Ordinal) || descriptor.OperationId.StartsWith("ledger.cardholder.", StringComparison.Ordinal)));
        Assert.Equal(typeof(CreatePaymentInstrumentInput), registry.Find("ledger.instrument.create")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(PaymentInstrumentDetail), registry.Find("ledger.instrument.create")!.ResultTypeInfo.Type);
        Assert.Equal(typeof(CardholderLifecycleResult), registry.Find("ledger.cardholder.reactivate")!.ResultTypeInfo.Type);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_create_returns_a_local_masked_instrument_identity()
    {
        var result = Instrument(await CreateInstrument("Daily card", null, "1234", "create-instrument"));

        Assert.True(LedgerId.TryParse(result.InstrumentId, out _, out _));
        Assert.Equal("Daily card", result.Label);
        Assert.Equal("1234", result.MaskedSuffix);
        Assert.Equal(PaymentIdentityStatus.Active, result.Status);
        Assert.Single(result.LifecycleHistory);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_optional_instrument_facts_may_be_unknown()
    {
        var result = Instrument(await CreateInstrument("Cash wallet", null, null, "unknown-facts"));

        Assert.Null(result.AccountId);
        Assert.Null(result.MaskedSuffix);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_active_account_association_is_preserved()
    {
        var account = await CreateAccount("Primary", "0001", "account");

        var result = Instrument(await CreateInstrument("Primary card", account.AccountId, "4321", "instrument"));

        Assert.Equal(account.AccountId, result.AccountId);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_inactive_account_association_is_rejected()
    {
        var account = await CreateAccount("Closed", "0002", "account");
        await ArchiveAccount(account.AccountId, "closed", "archive-account");

        var result = await CreateInstrument("Closed card", account.AccountId, "2222", "instrument");

        AssertError(result, 6, PaymentIdentityErrors.InstrumentAccountNotActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12x4")]
    public async Task DD_LEDGER_DIMENSIONAL_ATTRIBUTION_full_or_invalid_identifiers_are_rejected(string maskedSuffix)
    {
        var result = await CreateInstrument("Unsafe", null, maskedSuffix, "invalid-suffix");

        AssertError(result, 3, PaymentIdentity.InvalidError);
    }

    [Theory]
    [InlineData("providerId")]
    [InlineData("cardNumber")]
    [InlineData("providerPayload")]
    public async Task DD_LEDGER_DIMENSIONAL_ATTRIBUTION_provider_fields_are_rejected_before_mutation(string field)
    {
        var input = Json($$"""{"label":"Unsafe","maskedSuffix":"1234","{{field}}":"secret"}""");

        var result = await Run("ledger.instrument.create", input, "provider-field");

        AssertError(result, 3, "validation.invalid_input");
        Assert.Empty(Instruments(await ListInstruments()).Items);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_duplicate_active_label_and_masked_identity_are_stable_conflicts()
    {
        await CreateInstrument("Daily", null, "1111", "first");

        AssertError(await CreateInstrument(" daily ", null, "2222", "duplicate-label"), 5, PaymentIdentityErrors.InstrumentDuplicate);
        AssertError(await CreateInstrument("Other", null, "1111", "duplicate-identity"), 5, PaymentIdentityErrors.InstrumentDuplicate);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_instrument_replay_is_stable_and_changed_replay_conflicts()
    {
        var first = Instrument(await CreateInstrument("Daily", null, "1111", "same"));
        var replay = Instrument(await CreateInstrument("Daily", null, "1111", "same"));

        Assert.Equal(first.InstrumentId, replay.InstrumentId);
        AssertError(await CreateInstrument("Changed", null, "2222", "same"), 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_instrument_lifecycle_appends_history_and_preserves_identity()
    {
        var created = Instrument(await CreateInstrument("Daily", null, "1111", "create"));
        var renamed = InstrumentLifecycle(await RenameInstrument(created.InstrumentId, "Household", "clearer", "rename"));
        var archived = InstrumentLifecycle(await ArchiveInstrument(created.InstrumentId, "unused", "archive"));
        var active = InstrumentLifecycle(await ReactivateInstrument(created.InstrumentId, "needed", "reactivate"));

        Assert.Equal(created.InstrumentId, active.Instrument.InstrumentId);
        Assert.Equal("Household", active.Instrument.Label);
        Assert.Equal(PaymentIdentityStatus.Active, active.Instrument.Status);
        Assert.Equal(4, active.Instrument.LifecycleHistory.Count);
        Assert.Equal(renamed.LifecycleEventId, active.Instrument.LifecycleHistory[2].PreviousLifecycleEventId);
        Assert.Equal(archived.LifecycleEventId, active.Instrument.LifecycleHistory[3].PreviousLifecycleEventId);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_archived_instrument_rejects_new_lifecycle_writes_until_reactivated()
    {
        var instrument = Instrument(await CreateInstrument("Daily", null, "1111", "create"));
        await ArchiveInstrument(instrument.InstrumentId, "unused", "archive");

        AssertError(await RenameInstrument(instrument.InstrumentId, "Changed", "why", "rename"), 6, PaymentIdentityErrors.InstrumentArchived);
        AssertError(await ArchiveInstrument(instrument.InstrumentId, "again", "archive-again"), 6, PaymentIdentityErrors.InstrumentAlreadyArchived);
        await ReactivateInstrument(instrument.InstrumentId, "needed", "reactivate");
        AssertError(await ReactivateInstrument(instrument.InstrumentId, "again", "reactivate-again"), 6, PaymentIdentityErrors.InstrumentAlreadyActive);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_get_history_is_explicit_and_unknown_instrument_is_stable()
    {
        var instrument = Instrument(await CreateInstrument("Daily", null, "1111", "create"));
        await RenameInstrument(instrument.InstrumentId, "Household", "clearer", "rename");

        Assert.Empty(Instrument(await GetInstrument(instrument.InstrumentId, false)).LifecycleHistory);
        Assert.Equal(2, Instrument(await GetInstrument(instrument.InstrumentId, true)).LifecycleHistory.Count);
        AssertError(await GetInstrument(LedgerId.New().ToString(), false), 4, PaymentIdentityErrors.InstrumentNotFound);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_instrument_list_filters_are_deterministic()
    {
        var firstAccount = await CreateAccount("First", "0101", "account-a");
        var secondAccount = await CreateAccount("Second", "0202", "account-b");
        var archived = Instrument(await CreateInstrument("Archived", firstAccount.AccountId, "1000", "instrument-a"));
        await CreateInstrument("Zulu", firstAccount.AccountId, "1001", "instrument-b");
        await CreateInstrument("Alpha", secondAccount.AccountId, "2000", "instrument-c");
        await ArchiveInstrument(archived.InstrumentId, "unused", "archive");

        var active = Instruments(await ListInstruments(new(PaymentIdentityStatus.Active)));
        var firstAccountItems = Instruments(await ListInstruments(new(null, firstAccount.AccountId)));

        Assert.Equal(["Alpha", "Zulu"], active.Items.Select(item => item.Label));
        Assert.Equal(["Archived", "Zulu"], firstAccountItems.Items.Select(item => item.Label));
        Assert.Equal(firstAccountItems.Items.Select(item => item.InstrumentId), Instruments(await ListInstruments(new(null, firstAccount.AccountId))).Items.Select(item => item.InstrumentId));
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_archived_instrument_is_rejected_by_the_attribution_guard()
    {
        var instrument = Instrument(await CreateInstrument("Daily", null, "1111", "create"));
        await ArchiveInstrument(instrument.InstrumentId, "unused", "archive");
        var store = Store();
        var factory = Factory();
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction();

        var error = await store.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Instrument, instrument.InstrumentId, CancellationToken.None);

        Assert.Equal(PaymentIdentityErrors.InstrumentArchived, error);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_account_archival_blocks_instrument_reactivation()
    {
        var account = await CreateAccount("Primary", "0303", "account");
        var instrument = Instrument(await CreateInstrument("Primary", account.AccountId, "3030", "instrument"));
        await ArchiveInstrument(instrument.InstrumentId, "unused", "archive-instrument");
        await ArchiveAccount(account.AccountId, "closed", "archive-account");

        AssertError(await ReactivateInstrument(instrument.InstrumentId, "needed", "reactivate"), 6, PaymentIdentityErrors.InstrumentAccountNotActive);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_cardholder_create_returns_a_local_identity()
    {
        var cardholder = Cardholder(await CreateCardholder("Owner", "create"));

        Assert.True(LedgerId.TryParse(cardholder.CardholderId, out _, out _));
        Assert.Equal(PaymentIdentityStatus.Active, cardholder.Status);
        Assert.Single(cardholder.LifecycleHistory);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_duplicate_cardholder_label_is_a_stable_conflict()
    {
        await CreateCardholder("Owner", "first");

        AssertError(await CreateCardholder(" owner ", "second"), 5, PaymentIdentityErrors.CardholderDuplicate);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_cardholder_replay_is_stable_and_changed_replay_conflicts()
    {
        var first = Cardholder(await CreateCardholder("Owner", "same"));
        var replay = Cardholder(await CreateCardholder("Owner", "same"));

        Assert.Equal(first.CardholderId, replay.CardholderId);
        AssertError(await CreateCardholder("Changed", "same"), 5, "LEDGER-IDEMPOTENCY-001");
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_cardholder_lifecycle_appends_history_and_replays_original_events()
    {
        var created = Cardholder(await CreateCardholder("Owner", "create"));
        var renamed = CardholderLifecycle(await RenameCardholder(created.CardholderId, "Partner", "clearer", "rename"));
        Assert.Equal(renamed.LifecycleEventId, CardholderLifecycle(await RenameCardholder(created.CardholderId, "Partner", "clearer", "rename")).LifecycleEventId);
        var archived = CardholderLifecycle(await ArchiveCardholder(created.CardholderId, "unused", "archive"));
        Assert.Equal(archived.LifecycleEventId, CardholderLifecycle(await ArchiveCardholder(created.CardholderId, "unused", "archive")).LifecycleEventId);
        var active = CardholderLifecycle(await ReactivateCardholder(created.CardholderId, "needed", "reactivate"));

        Assert.Equal(created.CardholderId, active.Cardholder.CardholderId);
        Assert.Equal(4, active.Cardholder.LifecycleHistory.Count);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_archived_cardholder_rejects_new_attribution_until_reactivated()
    {
        var cardholder = Cardholder(await CreateCardholder("Owner", "create"));
        await ArchiveCardholder(cardholder.CardholderId, "unused", "archive");
        var store = Store();
        var factory = Factory();
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction();

        Assert.Equal(PaymentIdentityErrors.CardholderArchived, await store.ActiveAttributionErrorAsync(connection, transaction, PaymentIdentityKind.Cardholder, cardholder.CardholderId, CancellationToken.None));
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_cardholder_get_list_and_unknown_results_are_stable()
    {
        var zulu = Cardholder(await CreateCardholder("Zulu", "zulu"));
        var alpha = Cardholder(await CreateCardholder("Alpha", "alpha"));
        await RenameCardholder(zulu.CardholderId, "Beta", "order", "rename");

        Assert.Equal(["Alpha", "Beta"], Cardholders(await ListCardholders()).Items.Select(item => item.Label));
        Assert.Equal(2, Cardholder(await GetCardholder(zulu.CardholderId, true)).LifecycleHistory.Count);
        Assert.Equal(alpha.CardholderId, Cardholders(await ListCardholders()).Items[0].CardholderId);
        AssertError(await GetCardholder(LedgerId.New().ToString(), false), 4, PaymentIdentityErrors.CardholderNotFound);
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

    private Task<ProcessResult> CreateInstrument(string label, string? accountId, string? maskedSuffix, string key) => Run("ledger.instrument.create", JsonSerializer.SerializeToElement(new CreatePaymentInstrumentInput(label, accountId, maskedSuffix), LedgerJsonContext.Default.CreatePaymentInstrumentInput), key);
    private Task<ProcessResult> GetInstrument(string id, bool history) => Run("ledger.instrument.get", JsonSerializer.SerializeToElement(new GetPaymentInstrumentInput(id, history), LedgerJsonContext.Default.GetPaymentInstrumentInput), null);
    private Task<ProcessResult> ListInstruments(ListPaymentInstrumentsInput? input = null) => Run("ledger.instrument.list", JsonSerializer.SerializeToElement(input ?? new(), LedgerJsonContext.Default.ListPaymentInstrumentsInput), null);
    private Task<ProcessResult> RenameInstrument(string id, string label, string reason, string key) => Run("ledger.instrument.rename", JsonSerializer.SerializeToElement(new RenamePaymentInstrumentInput(id, label, reason), LedgerJsonContext.Default.RenamePaymentInstrumentInput), key);
    private Task<ProcessResult> ArchiveInstrument(string id, string reason, string key) => Run("ledger.instrument.archive", JsonSerializer.SerializeToElement(new ArchivePaymentInstrumentInput(id, reason), LedgerJsonContext.Default.ArchivePaymentInstrumentInput), key);
    private Task<ProcessResult> ReactivateInstrument(string id, string reason, string key) => Run("ledger.instrument.reactivate", JsonSerializer.SerializeToElement(new ReactivatePaymentInstrumentInput(id, reason), LedgerJsonContext.Default.ReactivatePaymentInstrumentInput), key);
    private Task<ProcessResult> CreateCardholder(string label, string key) => Run("ledger.cardholder.create", JsonSerializer.SerializeToElement(new CreateCardholderInput(label), LedgerJsonContext.Default.CreateCardholderInput), key);
    private Task<ProcessResult> GetCardholder(string id, bool history) => Run("ledger.cardholder.get", JsonSerializer.SerializeToElement(new GetCardholderInput(id, history), LedgerJsonContext.Default.GetCardholderInput), null);
    private Task<ProcessResult> ListCardholders(ListCardholdersInput? input = null) => Run("ledger.cardholder.list", JsonSerializer.SerializeToElement(input ?? new(), LedgerJsonContext.Default.ListCardholdersInput), null);
    private Task<ProcessResult> RenameCardholder(string id, string label, string reason, string key) => Run("ledger.cardholder.rename", JsonSerializer.SerializeToElement(new RenameCardholderInput(id, label, reason), LedgerJsonContext.Default.RenameCardholderInput), key);
    private Task<ProcessResult> ArchiveCardholder(string id, string reason, string key) => Run("ledger.cardholder.archive", JsonSerializer.SerializeToElement(new ArchiveCardholderInput(id, reason), LedgerJsonContext.Default.ArchiveCardholderInput), key);
    private Task<ProcessResult> ReactivateCardholder(string id, string reason, string key) => Run("ledger.cardholder.reactivate", JsonSerializer.SerializeToElement(new ReactivateCardholderInput(id, reason), LedgerJsonContext.Default.ReactivateCardholderInput), key);

    private async Task<AccountDetail> CreateAccount(string name, string maskedIdentifier, string key)
    {
        var input = JsonSerializer.SerializeToElement(new CreateAccountInput("Test Bank", name, AccountType.Cheque, "****" + maskedIdentifier, "ZAR"), LedgerJsonContext.Default.CreateAccountInput);
        return Success(await Run("ledger.account.create", input, key), LedgerJsonContext.Default.AccountDetail);
    }

    private Task<ProcessResult> ArchiveAccount(string accountId, string reason, string key) => Run("ledger.account.archive", JsonSerializer.SerializeToElement(new ArchiveAccountInput(accountId, reason), LedgerJsonContext.Default.ArchiveAccountInput), key);

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new SafeActor("human", "payment-test"), input, key), LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private LedgerConnectionFactory Factory() => new(new HostArtifactProtection());
    private PaymentIdentityStore Store() => new(database, Factory());
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static PaymentInstrumentDetail Instrument(ProcessResult result) => Success(result, LedgerJsonContext.Default.PaymentInstrumentDetail);
    private static PaymentInstrumentLifecycleResult InstrumentLifecycle(ProcessResult result) => Success(result, LedgerJsonContext.Default.PaymentInstrumentLifecycleResult);
    private static PaymentInstrumentListResult Instruments(ProcessResult result) => Success(result, LedgerJsonContext.Default.PaymentInstrumentListResult);
    private static CardholderDetail Cardholder(ProcessResult result) => Success(result, LedgerJsonContext.Default.CardholderDetail);
    private static CardholderLifecycleResult CardholderLifecycle(ProcessResult result) => Success(result, LedgerJsonContext.Default.CardholderLifecycleResult);
    private static CardholderListResult Cardholders(ProcessResult result) => Success(result, LedgerJsonContext.Default.CardholderListResult);

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
