using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class EvidenceLinkOperationTests : IAsyncLifetime
{
    private const string Fingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-evidence-link-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;

    [Fact]
    public void DM_LEDGER_EVIDENCE_RECONCILIATION_CONTRACTS_registry_exposes_typed_supporting_link_operation()
    {
        var descriptor = OperationRegistry.Create().Find("ledger.evidence.link-supporting")!;

        Assert.Equal(typeof(LinkSupportingEvidenceInput), descriptor.RequestTypeInfo.Type);
        Assert.Equal(typeof(EvidenceLinkResult), descriptor.ResultTypeInfo.Type);
        Assert.Equal("mutation", descriptor.Kind);
        Assert.True(descriptor.RequiresIdempotencyKey);
        Assert.Equal("EvidenceLinkOperationModule.LinkSupporting", descriptor.HandlerTarget);
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_links_additional_support_without_changing_financial_state()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 1);
        var evidence = await Register(EvidenceKind.Receipt, 101, null);

        var result = LinkResult(await Link(transaction.TransactionId, evidence.EvidenceId, "receipt supplied", "link"));

        Assert.Equal(2, result.Transaction.Evidence.Count);
        Assert.Equal(evidence.EvidenceId, result.Evidence.EvidenceId);
        var link = Assert.Single(result.Evidence.LinkHistory);
        Assert.Equal(result.LinkEventId, link.LinkEventId);
        Assert.Equal(EvidenceLinkRole.Supporting, link.Role);
        Assert.Equal(EvidenceLinkAction.Link, link.Action);
        Assert.Null(link.DecisionId);
        Assert.Equal("automation:evidence-link-test:run-01", link.RecordedBy);
        Assert.Equal(transaction.SignedAmount, result.Transaction.SignedAmount);
        Assert.Equal(transaction.Category.State, result.Transaction.Category.State);
        Assert.Equal(transaction.Category.CategoryId, result.Transaction.Category.CategoryId);
        Assert.Equal(transaction.Category.CurrentAncestryIds, result.Transaction.Category.CurrentAncestryIds);
        Assert.Equal(transaction.Pool, result.Transaction.Pool);
        Assert.Equal(transaction.PaymentAttribution, result.Transaction.PaymentAttribution);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, result.Transaction.ReconciliationState);
        Assert.Equal(1, await Count("transaction_fact"));
        Assert.Equal(0, await Count("reconciliation_decision"));
        Assert.Equal(0, await Count("reconciliation_decision_authority"));
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_exact_normalized_observation_is_compatible()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var instrument = await CreateInstrument(account.AccountId, "1234", "instrument");
        var cardholder = await CreateCardholder("cardholder");
        var transaction = await Record(account.AccountId, 2, "2026-07-02", instrument.InstrumentId, cardholder.CardholderId);
        var observation = new EvidenceObservation(
            account.AccountId, -1234, "ZAR", "2026-07-01", "2026-07-02",
            instrument.InstrumentId, cardholder.CardholderId, Fingerprint);
        var evidence = await Register(EvidenceKind.ExternalDocument, 102, observation);

        var result = LinkResult(await Link(transaction.TransactionId, evidence.EvidenceId, "all observed fields agree", "link"));

        Assert.Equal(evidence.EvidenceId, result.Evidence.EvidenceId);
        Assert.Equal(observation, result.Evidence.Observation);
        Assert.Equal(instrument.InstrumentId, result.Transaction.PaymentAttribution.InstrumentId);
        Assert.Equal(cardholder.CardholderId, result.Transaction.PaymentAttribution.CardholderId);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("amount")]
    [InlineData("transaction-date")]
    [InlineData("posting-date")]
    [InlineData("instrument")]
    [InlineData("cardholder")]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_conflicting_observation_is_rejected_atomically(string field)
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var otherAccount = await CreateAccount("Other", "2222", "other-account");
        var instrument = await CreateInstrument(account.AccountId, "1234", "instrument");
        var otherInstrument = await CreateInstrument(account.AccountId, "5678", "other-instrument");
        var cardholder = await CreateCardholder("cardholder");
        var otherCardholder = await CreateCardholder("other-cardholder");
        var transaction = await Record(account.AccountId, 3, "2026-07-02", instrument.InstrumentId, cardholder.CardholderId);
        var observation = new EvidenceObservation(
            field == "account" ? otherAccount.AccountId : account.AccountId,
            field == "amount" ? -999 : -1234,
            "ZAR",
            field == "transaction-date" ? "2026-07-03" : "2026-07-01",
            field == "posting-date" ? "2026-07-03" : "2026-07-02",
            field == "instrument" ? otherInstrument.InstrumentId : instrument.InstrumentId,
            field == "cardholder" ? otherCardholder.CardholderId : cardholder.CardholderId,
            null);
        var evidence = await Register(EvidenceKind.Receipt, 103, observation);

        AssertError(await Link(transaction.TransactionId, evidence.EvidenceId, "conflict", "link"), 3, TransactionFact.EvidenceIncompatibleError);
        Assert.Empty((await GetEvidence(evidence.EvidenceId)).LinkHistory);
        Assert.Single((await GetTransaction(transaction.TransactionId)).Evidence);
        Assert.Equal(0, await Count("reconciliation_decision"));
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_missing_transaction_and_evidence_are_stable_not_found_errors()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 4);
        var evidence = await Register(EvidenceKind.Receipt, 104, null);

        AssertError(await Link(LedgerId.New().ToString(), evidence.EvidenceId, "missing transaction", "missing-transaction"), 4, TransactionErrors.NotFound);
        AssertError(await Link(transaction.TransactionId, LedgerId.New().ToString(), "missing evidence", "missing-evidence"), 4, EvidenceLinkErrors.EvidenceNotFound);
        Assert.Equal(1, await Count("evidence_link_event"));
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_inactive_transaction_rejects_link_without_mutation()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 5);
        var evidence = await Register(EvidenceKind.Receipt, 105, null);
        await Terminate(transaction.TransactionId, "void", null);

        AssertError(await Link(transaction.TransactionId, evidence.EvidenceId, "late", "link"), 6, EvidenceLinkErrors.TransactionInactive);
        Assert.Empty((await GetEvidence(evidence.EvidenceId)).LinkHistory);
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_same_request_and_logical_replay_return_original_link()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 6);
        var evidence = await Register(EvidenceKind.Receipt, 106, null);
        var input = new LinkSupportingEvidenceInput(transaction.TransactionId, evidence.EvidenceId, "receipt supplied");
        var first = LinkResult(await Link(input, "first"));

        Assert.Equal(first.LinkEventId, LinkResult(await Link(input, "first")).LinkEventId);
        Assert.Equal(first.LinkEventId, LinkResult(await Link(input, "second")).LinkEventId);
        Assert.Equal(2, await Count("evidence_link_event"));
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_existing_initial_link_is_returned_without_duplication()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 7);
        var initial = Assert.Single(transaction.Evidence);

        var result = LinkResult(await Link(transaction.TransactionId, initial.EvidenceId, "already attached", "link"));

        Assert.Equal(initial.LinkEventId, result.LinkEventId);
        Assert.Equal(1, await Count("evidence_link_event"));
        Assert.Single(result.Evidence.LinkHistory);
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_changed_replays_conflict_and_preserve_original()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 8);
        var evidence = await Register(EvidenceKind.Receipt, 108, null);
        var input = new LinkSupportingEvidenceInput(transaction.TransactionId, evidence.EvidenceId, "original");
        await Link(input, "same");

        AssertError(await Link(input with { Reason = "changed" }, "same"), 5, LedgerMutationExecutor.ConflictCode);
        AssertError(await Link(input with { Reason = "also changed" }, "other"), 5, LedgerMutationExecutor.ConflictCode);
        Assert.Single((await GetEvidence(evidence.EvidenceId)).LinkHistory);
    }

    [Fact]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_direct_link_to_second_transaction_is_a_stable_conflict()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var first = await Record(account.AccountId, 9);
        var second = await Record(account.AccountId, 10);
        var firstEvidence = Assert.Single(first.Evidence);

        AssertError(await Link(second.TransactionId, firstEvidence.EvidenceId, "move evidence", "link"), 5, EvidenceLinkErrors.Conflict);
        Assert.Equal(first.TransactionId, Assert.Single((await GetEvidence(firstEvidence.EvidenceId)).LinkHistory).TransactionId);
        Assert.Single((await GetTransaction(second.TransactionId)).Evidence);
    }

    [Fact]
    public async Task DD_LEDGER_RECONCILIATION_CONTRACT_statement_evidence_supporting_link_is_not_confirmation()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 11);
        var evidence = await Register(EvidenceKind.StatementRow, 111, null);

        var result = LinkResult(await Link(transaction.TransactionId, evidence.EvidenceId, "statement candidate only", "link"));

        Assert.Equal(EvidenceLinkRole.Supporting, Assert.Single(result.Evidence.LinkHistory).Role);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, result.Transaction.ReconciliationState);
        Assert.Equal(0, await Count("reconciliation_decision"));
        Assert.Equal(0, await Count("coverage_entry"));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("decisionId")]
    [InlineData("mailbox")]
    [InlineData("rawPayload")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_closed_schema_rejects_authority_and_provider_fields(string field)
    {
        var input = $$"""{"transactionId":"{{LedgerId.New()}}","evidenceId":"{{LedgerId.New()}}","reason":"support","{{field}}":"forbidden"}""";

        AssertError(await RunRaw("ledger.evidence.link-supporting", input, "link"), 3, "validation.invalid_input");
        Assert.Equal(0, await Count("evidence_link_event"));
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("evidence")]
    [InlineData("reason")]
    public async Task FR_LEDGER_EVIDENCE_REGISTRATION_invalid_link_input_is_atomic(string field)
    {
        var input = new LinkSupportingEvidenceInput(
            field == "transaction" ? "invalid" : LedgerId.New().ToString(),
            field == "evidence" ? "invalid" : LedgerId.New().ToString(),
            field == "reason" ? "" : "support");

        AssertError(await Link(input, "link"), 3, EvidenceLinkErrors.Invalid);
        Assert.Equal(0, await Count("evidence_link_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_supporting_link_is_not_moved_across_supersession()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var source = await Record(account.AccountId, 12);
        var replacement = await Record(account.AccountId, 13);
        var sourceEvidence = Assert.Single(source.Evidence);
        await Terminate(source.TransactionId, "superseded", replacement.TransactionId);

        AssertError(await Link(replacement.TransactionId, sourceEvidence.EvidenceId, "move", "link"), 5, EvidenceLinkErrors.Conflict);
        Assert.Equal(source.TransactionId, Assert.Single((await GetEvidence(sourceEvidence.EvidenceId)).LinkHistory).TransactionId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_evidence_link_events_reject_update_and_delete()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 14);
        var evidence = await Register(EvidenceKind.Receipt, 114, null);
        await Link(transaction.TransactionId, evidence.EvidenceId, "support", "link");

        await using var connection = await Open();
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE evidence_link_event SET reason = 'changed';"))).SqliteErrorCode > 0);
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM evidence_link_event;"))).SqliteErrorCode > 0);
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

    private async Task<AccountDetail> CreateAccount(string name, string suffix, string key)
    {
        var input = new CreateAccountInput("Test Bank", name, AccountType.Cheque, "****" + suffix, "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), key), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<PaymentInstrumentDetail> CreateInstrument(string accountId, string suffix, string key)
    {
        var input = new CreatePaymentInstrumentInput("Card " + suffix, accountId, suffix);
        return Success(await Run("ledger.instrument.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreatePaymentInstrumentInput), key), LedgerJsonContext.Default.PaymentInstrumentDetail);
    }

    private async Task<CardholderDetail> CreateCardholder(string key)
    {
        var input = new CreateCardholderInput("Holder " + key);
        return Success(await Run("ledger.cardholder.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCardholderInput), key), LedgerJsonContext.Default.CardholderDetail);
    }

    private async Task<TransactionDetail> Record(string accountId, int digestSeed, string? postingDate = null, string? instrumentId = null, string? cardholderId = null)
    {
        var input = new RecordTransactionInput(
            accountId, "-12.34", "ZAR", "2026-07-01", postingDate, "Owner-safe purchase", instrumentId, cardholderId,
            new(EvidenceKind.AgentCapture, Digest(digestSeed), "capture:" + digestSeed, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), "record-" + digestSeed), LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<EvidenceRecordDetail> Register(EvidenceKind kind, int digestSeed, EvidenceObservation? observation)
    {
        var input = new RegisterEvidenceInput(kind, Digest(digestSeed), "evidence:" + digestSeed, Fingerprint, observation);
        return Success(await Run("ledger.evidence.register", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RegisterEvidenceInput), "register-" + digestSeed), LedgerJsonContext.Default.EvidenceRecordDetail);
    }

    private Task<ProcessResult> Link(string transactionId, string evidenceId, string reason, string key) =>
        Link(new(transactionId, evidenceId, reason), key);

    private Task<ProcessResult> Link(LinkSupportingEvidenceInput input, string key) => Run(
        "ledger.evidence.link-supporting",
        JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.LinkSupportingEvidenceInput),
        key);

    private async Task<TransactionDetail> GetTransaction(string transactionId) => Success(
        await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId, true), LedgerJsonContext.Default.GetTransactionInput), null),
        LedgerJsonContext.Default.TransactionDetail);

    private async Task<EvidenceRecordDetail> GetEvidence(string evidenceId) => Success(
        await Run("ledger.evidence.get", JsonSerializer.SerializeToElement(new GetEvidenceInput(evidenceId), LedgerJsonContext.Default.GetEvidenceInput), null),
        LedgerJsonContext.Default.EvidenceRecordDetail);

    private async Task Terminate(string transactionId, string action, string? replacementId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, $action, $replacementId, NULL, 'test', 'system:test', $at);";
        command.Parameters.AddWithValue("$eventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$replacementId", replacementId is null ? DBNull.Value : replacementId);
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProcessResult> RunRaw(string operationId, string input, string? key)
    {
        using var document = JsonDocument.Parse(input);
        return await Run(operationId, document.RootElement.Clone(), key);
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var request = new RequestEnvelope("1.0", new("automation", "evidence-link-test", "run-01"), input, key);
        var body = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private async Task<SqliteConnection> Open() => await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Digest(int seed) => string.Concat(Enumerable.Repeat(seed.ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
    private static EvidenceLinkResult LinkResult(ProcessResult result) => Success(result, LedgerJsonContext.Default.EvidenceLinkResult);

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
