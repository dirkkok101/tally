using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ledger;
using Tally.Features.Ledger.Accounts;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Features.Ledger.Accounts;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-ACCOUNT-MAINTENANCE-CONTRACT.
public sealed class AccountOperationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-account-operations-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private TallyProcess process = null!;

    // DM-LEDGER-ACCOUNT-CATEGORY-CONTRACTS
    [Fact]
    public void Registry_exposes_five_typed_account_operations_and_stable_errors()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(typeof(CreateAccountInput), registry.Find("ledger.account.create")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(GetAccountInput), registry.Find("ledger.account.get")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(ListAccountsInput), registry.Find("ledger.account.list")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(RenameAccountInput), registry.Find("ledger.account.rename")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(ArchiveAccountInput), registry.Find("ledger.account.archive")!.RequestTypeInfo.Type);
        Assert.Contains(registry.Find("ledger.account.create")!.ToSchema().Errors, error => error.Code == CreateAccountHandler.DuplicateError && error.ExitCode == 5);
        Assert.Contains(registry.Find("ledger.account.archive")!.ToSchema().Errors, error => error.Code == ArchiveAccountHandler.AlreadyArchivedError && error.ExitCode == 6);
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Theory]
    [InlineData(AccountType.Cheque, AccountClass.Asset)]
    [InlineData(AccountType.Savings, AccountClass.Asset)]
    [InlineData(AccountType.OtherAsset, AccountClass.Asset)]
    [InlineData(AccountType.CreditCard, AccountClass.Liability)]
    [InlineData(AccountType.OtherLiability, AccountClass.Liability)]
    public async Task Supported_account_type_has_deterministic_economic_class(AccountType accountType, AccountClass accountClass)
    {
        var detail = Success<AccountDetail>(await CreateAsync(accountType: accountType), LedgerJsonContext.Default.AccountDetail);

        Assert.Equal(accountClass, detail.AccountClass);
        Assert.Equal(accountType, detail.AccountType);
        Assert.Equal(AccountStatus.Active, detail.Status);
        Assert.True(LedgerId.TryParse(detail.AccountId, out _, out _));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Create_trims_safe_display_values_and_returns_attributable_history()
    {
        var detail = Success<AccountDetail>(await CreateAsync(institution: "  Example Bank  ", displayName: "  Daily Cheque  "), LedgerJsonContext.Default.AccountDetail);

        Assert.Equal("Example Bank", detail.InstitutionName);
        Assert.Equal("Daily Cheque", detail.DisplayName);
        Assert.Equal("****1234", detail.MaskedIdentifier);
        Assert.Equal("ZAR", detail.CurrencyCode);
        Assert.Equal("automation:account-test:run-01", detail.CreatedActor);
        var created = Assert.Single(detail.LifecycleHistory);
        Assert.Equal(AccountLifecycleAction.Create, created.Action);
        Assert.Null(created.Reason);
    }

    // DM-LEDGER-ACCOUNT
    [Fact]
    public async Task Unicode_display_names_are_retained_without_cross_runtime_normalization_drift()
    {
        var detail = Success<AccountDetail>(await CreateAsync(displayName: "Épargne"), LedgerJsonContext.Default.AccountDetail);

        Assert.Equal("Épargne", detail.DisplayName);
        Assert.Equal("Épargne", Success<AccountDetail>(await GetAsync(detail.AccountId, false), LedgerJsonContext.Default.AccountDetail).DisplayName);
    }

    // NFR-LEDGER-LOCAL-PRIVACY
    [Theory]
    [InlineData("123456789")]
    [InlineData("account:123456789")]
    [InlineData("credential:1234")]
    public async Task Full_or_credential_bearing_identifiers_are_rejected_before_storage(string maskedIdentifier)
    {
        var result = await CreateAsync(maskedIdentifier: maskedIdentifier);

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("account"));
    }

    // NFR-LEDGER-LOCAL-PRIVACY
    [Theory]
    [InlineData("onlineBankingPassword")]
    [InlineData("credential")]
    [InlineData("fullAccountNumber")]
    public async Task Credential_and_full_identifier_fields_are_rejected_by_the_closed_schema(string field)
    {
        var input = $"{{\"institutionName\":\"Bank\",\"displayName\":\"Daily\",\"accountType\":\"cheque\",\"maskedIdentifier\":\"****1234\",\"currencyCode\":\"ZAR\",\"{field}\":\"private\"}}";
        var result = await RunRawAsync("ledger.account.create", input, "key");

        AssertError(result, 3, "validation.invalid_input");
        Assert.Equal(0L, await CountAsync("account"));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Duplicate_masked_identity_is_case_insensitive_within_institution()
    {
        await CreateAsync(key: "first", institution: "Example Bank", displayName: "Daily", maskedIdentifier: "XXXX1234");
        var duplicate = await CreateAsync(key: "second", institution: "example bank", displayName: "Reserve", maskedIdentifier: "xxxx1234");

        AssertError(duplicate, 5, CreateAccountHandler.DuplicateError);
        Assert.Equal(1L, await CountAsync("account"));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Identical_create_replay_returns_the_original_account()
    {
        var first = Success<AccountDetail>(await CreateAsync(key: "same"), LedgerJsonContext.Default.AccountDetail);
        var replay = Success<AccountDetail>(await CreateAsync(key: "same"), LedgerJsonContext.Default.AccountDetail);

        Assert.Equal(first.AccountId, replay.AccountId);
        Assert.Equal(first.CreatedAt, replay.CreatedAt);
        Assert.Equal(1L, await CountAsync("account"));
        Assert.Equal(1L, await CountAsync("idempotency_record"));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Changed_create_replay_conflicts_without_a_second_account()
    {
        await CreateAsync(key: "same", displayName: "Daily");
        var conflict = await CreateAsync(key: "same", displayName: "Changed");

        AssertError(conflict, 5, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(1L, await CountAsync("account"));
    }

    // DD-LEDGER-IMMUTABLE-HISTORY
    [Fact]
    public async Task Rename_preserves_identity_and_appends_attributable_history()
    {
        var created = Success<AccountDetail>(await CreateAsync(), LedgerJsonContext.Default.AccountDetail);
        var renamed = Success<AccountLifecycleResult>(await RenameAsync(created.AccountId, "Household", "Owner clarified label"), LedgerJsonContext.Default.AccountLifecycleResult);
        var fetched = Success<AccountDetail>(await GetAsync(created.AccountId, true), LedgerJsonContext.Default.AccountDetail);

        Assert.Equal(created.AccountId, renamed.Account.AccountId);
        Assert.Equal("Household", renamed.Account.DisplayName);
        Assert.Equal(renamed.LifecycleEventId, fetched.LifecycleHistory[^1].LifecycleEventId);
        Assert.Equal("Owner clarified label", fetched.LifecycleHistory[^1].Reason);
        Assert.Equal("automation:account-test:run-01", fetched.LifecycleHistory[^1].Actor);
        Assert.Equal([AccountLifecycleAction.Create, AccountLifecycleAction.Rename], fetched.LifecycleHistory.Select(item => item.Action));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Rename_rejects_an_active_name_conflict_without_history_change()
    {
        var first = Success<AccountDetail>(await CreateAsync(key: "first", displayName: "Daily"), LedgerJsonContext.Default.AccountDetail);
        await CreateAsync(key: "second", displayName: "Reserve", maskedIdentifier: "****4321");
        var conflict = await RenameAsync(first.AccountId, "reserve", "Would collide");

        AssertError(conflict, 5, RenameAccountHandler.NameConflictError);
        Assert.Equal(1L, await HistoryCountAsync(first.AccountId));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Archive_retains_queryable_history_and_rejects_later_mutation()
    {
        var created = Success<AccountDetail>(await CreateAsync(), LedgerJsonContext.Default.AccountDetail);
        var archived = Success<AccountLifecycleResult>(await ArchiveAsync(created.AccountId, "Closed at bank"), LedgerJsonContext.Default.AccountLifecycleResult);
        var rename = await RenameAsync(created.AccountId, "No longer valid", "Archived");
        var fetched = Success<AccountDetail>(await GetAsync(created.AccountId, true), LedgerJsonContext.Default.AccountDetail);

        Assert.Equal(AccountStatus.Archived, archived.Account.Status);
        Assert.NotNull(archived.Account.ArchivedAt);
        AssertError(rename, 6, "LEDGER-ACCOUNT-ARCHIVED");
        Assert.Equal(2, fetched.LifecycleHistory.Count);
        Assert.Equal(1L, await CountAsync("account"));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Archive_replay_is_stable_but_another_request_is_already_archived()
    {
        var account = Success<AccountDetail>(await CreateAsync(), LedgerJsonContext.Default.AccountDetail);
        var first = Success<AccountLifecycleResult>(await ArchiveAsync(account.AccountId, "Closed", "archive-key"), LedgerJsonContext.Default.AccountLifecycleResult);
        var replay = Success<AccountLifecycleResult>(await ArchiveAsync(account.AccountId, "Closed", "archive-key"), LedgerJsonContext.Default.AccountLifecycleResult);
        var another = await ArchiveAsync(account.AccountId, "Closed again", "another-key");

        Assert.Equal(first.LifecycleEventId, replay.LifecycleEventId);
        AssertError(another, 6, ArchiveAccountHandler.AlreadyArchivedError);
        Assert.Equal(2L, await HistoryCountAsync(account.AccountId));
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Get_applies_the_explicit_history_option()
    {
        var account = Success<AccountDetail>(await CreateAsync(), LedgerJsonContext.Default.AccountDetail);
        await RenameAsync(account.AccountId, "Renamed", "Reason");

        Assert.Empty(Success<AccountDetail>(await GetAsync(account.AccountId, false), LedgerJsonContext.Default.AccountDetail).LifecycleHistory);
        Assert.Equal(2, Success<AccountDetail>(await GetAsync(account.AccountId, true), LedgerJsonContext.Default.AccountDetail).LifecycleHistory.Count);
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task List_filters_status_and_institution_with_deterministic_one_row_ordering()
    {
        var second = Success<AccountDetail>(await CreateAsync(key: "second", institution: "Beta Bank", displayName: "Zulu", maskedIdentifier: "****2222"), LedgerJsonContext.Default.AccountDetail);
        var first = Success<AccountDetail>(await CreateAsync(key: "first", institution: "Alpha Bank", displayName: "Alpha", maskedIdentifier: "****1111"), LedgerJsonContext.Default.AccountDetail);
        await CreateAsync(key: "third", institution: "Alpha Bank", displayName: "Beta", maskedIdentifier: "****3333");
        await ArchiveAsync(second.AccountId, "Closed", "archive");

        var active = Success<AccountListResult>(await ListAsync(new(AccountStatus.Active)), LedgerJsonContext.Default.AccountListResult);
        var alpha = Success<AccountListResult>(await ListAsync(new(null, " alpha bank ")), LedgerJsonContext.Default.AccountListResult);

        Assert.Equal([first.AccountId, alpha.Items[1].AccountId], active.Items.Select(item => item.AccountId));
        Assert.Equal(["Alpha", "Beta"], alpha.Items.Select(item => item.DisplayName));
        Assert.Equal(2, alpha.Items.Select(item => item.AccountId).Distinct().Count());
    }

    // FR-LEDGER-ACCOUNT-MAINTENANCE
    [Fact]
    public async Task Unknown_and_invalid_account_ids_have_stable_no_data_results()
    {
        AssertError(await GetAsync("invalid", false), 3, "validation.invalid_input");
        AssertError(await GetAsync(LedgerId.New().ToString(), false), 4, "LEDGER-ACCOUNT-NOT-FOUND");
    }

    // DM-LEDGER-ACCOUNT-CATEGORY-CONTRACTS
    [Theory]
    [InlineData("USD", 0, "LEDGER-CURRENCY-UNSUPPORTED")]
    [InlineData("ZAR", 99, "LEDGER-ACCOUNT-TYPE-UNSUPPORTED")]
    public async Task Unsupported_currency_or_account_type_has_a_stable_validation_error(string currency, int accountType, string errorCode)
    {
        var input = $"{{\"institutionName\":\"Bank\",\"displayName\":\"Daily\",\"accountType\":{accountType},\"maskedIdentifier\":\"****1234\",\"currencyCode\":\"{currency}\"}}";
        var result = await RunRawAsync("ledger.account.create", input, "key");

        AssertError(result, 3, errorCode);
        Assert.Equal(0L, await CountAsync("account"));
    }

    // DD-LEDGER-DIMENSIONAL-ATTRIBUTION
    [Fact]
    public async Task Account_creation_does_not_create_other_financial_dimensions()
    {
        await CreateAsync();

        Assert.Equal(0L, await CountAsync("spend_category"));
        Assert.Equal(0L, await CountAsync("payment_instrument"));
        Assert.Equal(0L, await CountAsync("cardholder"));
        Assert.Equal(0L, await CountAsync("spend_pool"));
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

    private Task<ProcessResult> CreateAsync(string key = "create-key", string institution = "Example Bank", string displayName = "Daily", string maskedIdentifier = "****1234", AccountType accountType = AccountType.Cheque) =>
        RunAsync("ledger.account.create", JsonSerializer.SerializeToElement(new CreateAccountInput(institution, displayName, accountType, maskedIdentifier, "ZAR"), LedgerJsonContext.Default.CreateAccountInput), key);

    private Task<ProcessResult> GetAsync(string accountId, bool includeHistory) =>
        RunAsync("ledger.account.get", JsonSerializer.SerializeToElement(new GetAccountInput(accountId, includeHistory), LedgerJsonContext.Default.GetAccountInput), null);

    private Task<ProcessResult> ListAsync(ListAccountsInput input) =>
        RunAsync("ledger.account.list", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.ListAccountsInput), null);

    private Task<ProcessResult> RenameAsync(string accountId, string newName, string reason, string key = "rename-key") =>
        RunAsync("ledger.account.rename", JsonSerializer.SerializeToElement(new RenameAccountInput(accountId, newName, reason), LedgerJsonContext.Default.RenameAccountInput), key);

    private Task<ProcessResult> ArchiveAsync(string accountId, string reason, string key = "archive-key") =>
        RunAsync("ledger.account.archive", JsonSerializer.SerializeToElement(new ArchiveAccountInput(accountId, reason), LedgerJsonContext.Default.ArchiveAccountInput), key);

    private async Task<ProcessResult> RunRawAsync(string operationId, string input, string? idempotencyKey)
    {
        using var document = JsonDocument.Parse(input);
        return await RunAsync(operationId, document.RootElement.Clone(), idempotencyKey);
    }

    private async Task<ProcessResult> RunAsync(string operationId, JsonElement input, string? idempotencyKey)
    {
        var request = new RequestEnvelope("1.0", new("automation", "account-test", "run-01"), input, idempotencyKey);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Equal("success", envelope.Outcome);
        return JsonSerializer.Deserialize(envelope.Result!.Value, typeInfo)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        Assert.Equal(errorCode, envelope.Error!.Code);
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> HistoryCountAsync(string accountId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM catalogue_lifecycle_event WHERE catalogue_kind = 'account' AND entity_id = $id;";
        command.Parameters.AddWithValue("$id", accountId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync() =>
        await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
}
