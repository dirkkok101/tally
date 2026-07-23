using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Dimensions;
using Tally.Features.Ledger.Dimensions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Dimensions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-PAYMENT-ATTRIBUTION-CONTRACT.
public sealed class PaymentAttributionOperationTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-payment-attribution-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;
    private PaymentAttributionStore store = null!;
    private PaymentIdentityStore identityStore = null!;

    [Fact]
    public void DM_LEDGER_ATTRIBUTION_POOL_CONTRACTS_registry_exposes_assign_and_correct()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(typeof(AssignPaymentAttributionInput), registry.Find("ledger.transaction.attribution.assign")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(CorrectPaymentAttributionInput), registry.Find("ledger.transaction.attribution.correct")!.RequestTypeInfo.Type);
        Assert.All(
            new[] { "ledger.transaction.attribution.assign", "ledger.transaction.attribution.correct" },
            operation => Assert.Equal(typeof(PaymentAttributionResult), registry.Find(operation)!.ResultTypeInfo.Type));
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_transaction_initializes_independent_unknown_states()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 1);

        Assert.True(LedgerId.TryParse(transaction.PaymentAttribution.AttributionEventId, out _, out _));
        Assert.Equal(TransactionKnowledgeState.Unknown, transaction.PaymentAttribution.InstrumentState);
        Assert.Null(transaction.PaymentAttribution.InstrumentId);
        Assert.Equal(TransactionKnowledgeState.Unknown, transaction.PaymentAttribution.CardholderState);
        Assert.Null(transaction.PaymentAttribution.CardholderId);
        var history = Assert.Single(transaction.History!.PaymentAttribution);
        Assert.Equal(TransactionAssignmentAction.Initialize, history.Action);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_assigns_instrument_without_changing_cardholder_or_other_dimensions()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 2);
        var instrument = await CreateInstrument("Card", account.AccountId, "1234", "instrument");

        var result = Attribution(await Assign(
            transaction, new(TransactionKnowledgeState.Known, instrument.InstrumentId), null, "owner identified card", "assign"));

        Assert.Equal(instrument.InstrumentId, result.Transaction.PaymentAttribution.InstrumentId);
        Assert.Equal(TransactionKnowledgeState.Known, result.Transaction.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, result.Transaction.PaymentAttribution.CardholderState);
        Assert.Equal(TransactionCategoryState.Uncategorized, result.Transaction.Category.State);
        Assert.Equal(TransactionPoolState.Unassigned, result.Transaction.Pool.State);
        Assert.Equal(transaction.Evidence.Single().EvidenceId, result.Transaction.Evidence.Single().EvidenceId);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, result.Transaction.ReconciliationState);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_assigns_cardholder_without_changing_instrument()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 3);
        var cardholder = await CreateCardholder("Owner", "cardholder");

        var result = Attribution(await Assign(
            transaction, null, new(TransactionKnowledgeState.Known, cardholder.CardholderId), "owner identified holder", "assign"));

        Assert.Equal(TransactionKnowledgeState.Unknown, result.Transaction.PaymentAttribution.InstrumentState);
        Assert.Equal(cardholder.CardholderId, result.Transaction.PaymentAttribution.CardholderId);
        Assert.Equal(TransactionKnowledgeState.Known, result.Transaction.PaymentAttribution.CardholderState);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_correction_replaces_one_dimension_and_retains_history()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 4);
        var first = await CreateInstrument("First", account.AccountId, "1111", "first");
        var second = await CreateInstrument("Second", account.AccountId, "2222", "second");
        var assigned = Attribution(await Assign(transaction, new(TransactionKnowledgeState.Known, first.InstrumentId), null, "initial", "assign"));

        var corrected = Attribution(await Correct(
            assigned.Transaction, new(TransactionKnowledgeState.Known, second.InstrumentId), null, "owner correction", "correct"));

        Assert.Equal(second.InstrumentId, corrected.Transaction.PaymentAttribution.InstrumentId);
        Assert.Equal(TransactionKnowledgeState.Unknown, corrected.Transaction.PaymentAttribution.CardholderState);
        Assert.Collection(
            corrected.Transaction.History!.PaymentAttribution,
            item => Assert.Equal(TransactionAssignmentAction.Initialize, item.Action),
            item => Assert.Equal(TransactionAssignmentAction.Assign, item.Action),
            item =>
            {
                Assert.Equal(TransactionAssignmentAction.Correct, item.Action);
                Assert.Equal(assigned.AttributionEventId, item.PreviousEventId);
                Assert.Equal("owner correction", item.Reason);
                Assert.Equal("human:payment-attribution-test", item.Actor);
            });
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_correction_can_make_a_dimension_explicitly_unknown()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 5);
        var cardholder = await CreateCardholder("Owner", "cardholder");
        var assigned = Attribution(await Assign(transaction, null, new(TransactionKnowledgeState.Known, cardholder.CardholderId), "initial", "assign"));

        var corrected = Attribution(await Correct(
            assigned.Transaction, null, new(TransactionKnowledgeState.Unknown), "identity withdrawn", "correct"));

        Assert.Equal(TransactionKnowledgeState.Unknown, corrected.Transaction.PaymentAttribution.CardholderState);
        Assert.Null(corrected.Transaction.PaymentAttribution.CardholderId);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("known-without-id")]
    [InlineData("unknown-with-id")]
    [InlineData("blank-reason")]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_invalid_selection_is_atomic(string scenario)
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 6);
        var instrument = scenario switch
        {
            "known-without-id" => new InstrumentAttributionInput(TransactionKnowledgeState.Known),
            "unknown-with-id" => new InstrumentAttributionInput(TransactionKnowledgeState.Unknown, LedgerId.New().ToString()),
            _ => null
        };
        var input = new AssignPaymentAttributionInput(
            transaction.TransactionId, transaction.PaymentAttribution.AttributionEventId,
            instrument, null, scenario == "blank-reason" ? "" : "reason");

        AssertError(await Assign(input, "invalid-" + scenario), 3, PaymentAttributionPolicy.InvalidError);
        Assert.Single((await Get(transaction.TransactionId)).History!.PaymentAttribution);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_selection_state_is_required_by_the_closed_schema()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 17);
        var input = JsonDocument.Parse($$"""
            {
              "transactionId":"{{transaction.TransactionId}}",
              "expectedAttributionEventId":"{{transaction.PaymentAttribution.AttributionEventId}}",
              "instrument":{"instrumentId":null},
              "cardholder":null,
              "reason":"explicit state required"
            }
            """).RootElement.Clone();

        AssertError(await Run("ledger.transaction.attribution.assign", input, "missing-state"), 3, "validation.invalid_input");
        Assert.Single((await Get(transaction.TransactionId)).History!.PaymentAttribution);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_missing_archived_and_incompatible_instruments_are_rejected()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var otherAccount = await CreateAccount("Other", "2222", "other-account");
        var transaction = await Record(account.AccountId, 7);
        var archived = await CreateInstrument("Archived", account.AccountId, "1111", "archived");
        var incompatible = await CreateInstrument("Other card", otherAccount.AccountId, "2222", "incompatible-instrument");
        await ArchiveInstrument(archived.InstrumentId);

        AssertError(await Assign(transaction, new(TransactionKnowledgeState.Known, LedgerId.New().ToString()), null, "missing", "missing"), 4, PaymentIdentityErrors.InstrumentNotFound);
        AssertError(await Assign(transaction, new(TransactionKnowledgeState.Known, archived.InstrumentId), null, "archived", "archived-assign"), 6, PaymentIdentityErrors.InstrumentArchived);
        AssertError(await Assign(transaction, new(TransactionKnowledgeState.Known, incompatible.InstrumentId), null, "wrong account", "incompatible-assign"), 6, PaymentAttributionErrors.AccountIncompatible);
        Assert.Single((await Get(transaction.TransactionId)).History!.PaymentAttribution);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_missing_and_archived_cardholders_are_rejected()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 8);
        var archived = await CreateCardholder("Archived", "archived");
        await ArchiveCardholder(archived.CardholderId);

        AssertError(await Assign(transaction, null, new(TransactionKnowledgeState.Known, LedgerId.New().ToString()), "missing", "missing"), 4, PaymentIdentityErrors.CardholderNotFound);
        AssertError(await Assign(transaction, null, new(TransactionKnowledgeState.Known, archived.CardholderId), "archived", "archived-assign"), 6, PaymentIdentityErrors.CardholderArchived);
        Assert.Single((await Get(transaction.TransactionId)).History!.PaymentAttribution);
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_preserved_archived_identity_returns_stable_lifecycle_error()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 16);
        var instrument = await CreateInstrument("Card", account.AccountId, "1111", "instrument");
        var holder = await CreateCardholder("Owner", "holder");
        var assigned = Attribution(await Assign(transaction, new(TransactionKnowledgeState.Known, instrument.InstrumentId), null, "instrument", "assign"));
        await ArchiveInstrument(instrument.InstrumentId);

        AssertError(
            await Correct(assigned.Transaction, null, new(TransactionKnowledgeState.Known, holder.CardholderId), "holder", "correct-holder"),
            6,
            PaymentIdentityErrors.InstrumentArchived);
        Assert.Equal(2, await Count("transaction_attribution_event"));
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_inactive_transaction_changes_nothing()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 9);
        var holder = await CreateCardholder("Owner", "holder");
        await Terminate(transaction.TransactionId, "void", null, null);

        AssertError(await Assign(transaction, null, new(TransactionKnowledgeState.Known, holder.CardholderId), "late", "late"), 6, PaymentAttributionErrors.TransactionInactive);
        Assert.Equal(1, await Count("transaction_attribution_event"));
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_stale_and_same_state_requests_are_rejected()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 10);
        var holder = await CreateCardholder("Owner", "holder");
        var assigned = Attribution(await Assign(transaction, null, new(TransactionKnowledgeState.Known, holder.CardholderId), "assign", "assign"));

        AssertError(await Correct(transaction, null, new(TransactionKnowledgeState.Unknown), "stale", "stale"), 5, PaymentAttributionErrors.Stale);
        AssertError(await Correct(assigned.Transaction, null, new(TransactionKnowledgeState.Known, holder.CardholderId), "same", "same"), 5, PaymentAttributionErrors.Unchanged);
        AssertError(await Assign(assigned.Transaction, null, new(TransactionKnowledgeState.Unknown), "assign again", "again"), 5, PaymentAttributionErrors.AlreadyAssigned);
        Assert.Equal(2, await Count("transaction_attribution_event"));
    }

    [Fact]
    public async Task FR_LEDGER_PAYMENT_ATTRIBUTION_replay_returns_original_and_changed_input_conflicts()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var transaction = await Record(account.AccountId, 11);
        var holder = await CreateCardholder("Owner", "holder");
        var input = new AssignPaymentAttributionInput(
            transaction.TransactionId, transaction.PaymentAttribution.AttributionEventId,
            null, new(TransactionKnowledgeState.Known, holder.CardholderId), "owner choice");
        var original = Attribution(await Assign(input, "same"));

        Assert.Equal(original.AttributionEventId, Attribution(await Assign(input, "same")).AttributionEventId);
        AssertError(await Assign(input with { Reason = "changed" }, "same"), 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(2, await Count("transaction_attribution_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_compatible_statement_correction_carries_attribution()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var source = await Record(account.AccountId, 12);
        var instrument = await CreateInstrument("Card", account.AccountId, "1234", "instrument");
        var holder = await CreateCardholder("Owner", "holder");
        source = Attribution(await Assign(
            source, new(TransactionKnowledgeState.Known, instrument.InstrumentId), new(TransactionKnowledgeState.Known, holder.CardholderId), "known", "assign")).Transaction;
        var replacementId = await CreateReplacementFact(account.AccountId);
        var decisionId = await AuthorizeStatementCorrection(source.TransactionId, replacementId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var result = await store.CarryForwardOrUnknownAsync(
            connection, transaction, identityStore, source.TransactionId, replacementId, decisionId,
            "statement correction", "system:reconciliation", At, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(PaymentAttributionCarryForwardResolution.CarryForward, result.Resolution);
        Assert.False(result.ReviewRequired);
        var replacement = await Get(replacementId);
        Assert.Equal(instrument.InstrumentId, replacement.PaymentAttribution.InstrumentId);
        Assert.Equal(holder.CardholderId, replacement.PaymentAttribution.CardholderId);
        var history = Assert.Single(replacement.History!.PaymentAttribution);
        Assert.Equal(source.TransactionId, history.SourceTransactionId);
        Assert.Equal(decisionId, history.ReconciliationDecisionId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_incompatible_statement_correction_initializes_unknown_with_review_metadata()
    {
        var sourceAccount = await CreateAccount("Source", "1111", "source-account");
        var replacementAccount = await CreateAccount("Replacement", "2222", "replacement-account");
        var source = await Record(sourceAccount.AccountId, 13);
        var instrument = await CreateInstrument("Source card", sourceAccount.AccountId, "1234", "instrument");
        source = Attribution(await Assign(source, new(TransactionKnowledgeState.Known, instrument.InstrumentId), null, "known", "assign")).Transaction;
        var replacementId = await CreateReplacementFact(replacementAccount.AccountId);
        var decisionId = await AuthorizeStatementCorrection(source.TransactionId, replacementId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var result = await store.CarryForwardOrUnknownAsync(
            connection, transaction, identityStore, source.TransactionId, replacementId, decisionId,
            "incompatible statement correction", "system:reconciliation", At, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(PaymentAttributionCarryForwardResolution.UnknownInitialization, result.Resolution);
        Assert.True(result.ReviewRequired);
        var replacement = await Get(replacementId);
        Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.InstrumentState);
        Assert.Equal(1, await Count("statement_unknown_attribution_authority"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_ordinary_supersession_does_not_inherit_attribution()
    {
        var account = await CreateAccount("Primary", "1111", "account");
        var source = await Record(account.AccountId, 14);
        var replacement = await Record(account.AccountId, 15);
        var holder = await CreateCardholder("Owner", "holder");
        source = Attribution(await Assign(source, null, new(TransactionKnowledgeState.Known, holder.CardholderId), "known", "assign")).Transaction;

        await Terminate(source.TransactionId, "superseded", replacement.TransactionId, null);

        replacement = await Get(replacement.TransactionId);
        Assert.Equal(TransactionKnowledgeState.Unknown, replacement.PaymentAttribution.CardholderState);
        Assert.Single(replacement.History!.PaymentAttribution);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        var factory = new LedgerConnectionFactory(new HostArtifactProtection());
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
        store = new PaymentAttributionStore();
        identityStore = new PaymentIdentityStore(database, factory);
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

    private async Task<PaymentInstrumentDetail> CreateInstrument(string label, string? accountId, string suffix, string key)
    {
        var input = new CreatePaymentInstrumentInput(label, accountId, suffix);
        return Success(await Run("ledger.instrument.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreatePaymentInstrumentInput), key), LedgerJsonContext.Default.PaymentInstrumentDetail);
    }

    private async Task<CardholderDetail> CreateCardholder(string label, string key)
    {
        var input = new CreateCardholderInput(label);
        return Success(await Run("ledger.cardholder.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCardholderInput), key), LedgerJsonContext.Default.CardholderDetail);
    }

    private Task<ProcessResult> ArchiveInstrument(string id) => Run("ledger.instrument.archive", JsonSerializer.SerializeToElement(new ArchivePaymentInstrumentInput(id, "archive"), LedgerJsonContext.Default.ArchivePaymentInstrumentInput), "archive-instrument");
    private Task<ProcessResult> ArchiveCardholder(string id) => Run("ledger.cardholder.archive", JsonSerializer.SerializeToElement(new ArchiveCardholderInput(id, "archive"), LedgerJsonContext.Default.ArchiveCardholderInput), "archive-cardholder");

    private async Task<TransactionDetail> Record(string accountId, int digestSeed)
    {
        var digest = digestSeed.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
        var input = new RecordTransactionInput(
            accountId, "-12.34", "ZAR", "2026-07-01", null, "Owner-safe purchase", null, null,
            new(EvidenceKind.AgentCapture, string.Concat(Enumerable.Repeat(digest, 32)), "capture:" + digestSeed, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), "record-" + digestSeed), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Assign(TransactionDetail transaction, InstrumentAttributionInput? instrument, CardholderAttributionInput? cardholder, string reason, string key) =>
        Assign(new(transaction.TransactionId, transaction.PaymentAttribution.AttributionEventId, instrument, cardholder, reason), key);
    private Task<ProcessResult> Assign(AssignPaymentAttributionInput input, string key) => Run("ledger.transaction.attribution.assign", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.AssignPaymentAttributionInput), key);
    private Task<ProcessResult> Correct(TransactionDetail transaction, InstrumentAttributionInput? instrument, CardholderAttributionInput? cardholder, string reason, string key) =>
        Run("ledger.transaction.attribution.correct", JsonSerializer.SerializeToElement(new CorrectPaymentAttributionInput(transaction.TransactionId, transaction.PaymentAttribution.AttributionEventId, instrument, cardholder, reason), LedgerJsonContext.Default.CorrectPaymentAttributionInput), key);

    private async Task<TransactionDetail> Get(string transactionId) => Success(
        await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId, true), LedgerJsonContext.Default.GetTransactionInput), null),
        LedgerJsonContext.Default.TransactionDetail);

    private async Task<string> CreateReplacementFact(string accountId)
    {
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            VALUES ($transactionId, $accountId, -1234, 'ZAR', '2026-07-01', NULL, 'Statement replacement', $at, 'test');
            INSERT INTO pool_assignment_event (
                pool_assignment_event_id, transaction_id, assignment_state, pool_id, action,
                previous_event_id, source_transaction_id, reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($poolEventId, $transactionId, 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initialize', 'system:test', $at);
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$poolEventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
        return transactionId;
    }

    private async Task<string> AuthorizeStatementCorrection(string sourceId, string replacementId)
    {
        var evidenceId = LedgerId.New().ToString();
        var decisionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence_record VALUES ($evidenceId, 'statement_row', $digest, NULL, NULL, 'system:test', $at);
            INSERT INTO reconciliation_decision (
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', NULL, NULL,
                    'statement authority', 0, 'corrected from statement', 'system:reconciliation', $at, NULL);
            INSERT INTO reconciliation_decision_authority (
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'corrected_from_statement', $sourceId, $replacementId,
                    'owner', 'statement row authority', 'v2', $at);
            INSERT INTO transaction_lifecycle_event (
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($lifecycleId, $sourceId, 'statement_authoritative_replacement', $replacementId,
                    $decisionId, 'statement correction', 'system:reconciliation', $at);
            """;
        command.Parameters.AddWithValue("$evidenceId", evidenceId);
        command.Parameters.AddWithValue("$digest", new string('f', 64));
        command.Parameters.AddWithValue("$decisionId", decisionId);
        command.Parameters.AddWithValue("$replacementId", replacementId);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$lifecycleId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
        return decisionId;
    }

    private async Task Terminate(string transactionId, string action, string? replacementId, string? decisionId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO transaction_lifecycle_event VALUES ($eventId, $transactionId, $action, $replacementId, $decisionId, 'test', 'system:test', $at);";
        command.Parameters.AddWithValue("$eventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$replacementId", replacementId is null ? DBNull.Value : replacementId);
        command.Parameters.AddWithValue("$decisionId", decisionId is null ? DBNull.Value : decisionId);
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> Open() => await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
    private async Task<long> Count(string table) { await using var connection = await Open(); await using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {table};"; return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new SafeActor("human", "payment-attribution-test"), input, key), LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static PaymentAttributionResult Attribution(ProcessResult result) => Success(result, LedgerJsonContext.Default.PaymentAttributionResult);
    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) { Assert.Equal(0, result.ExitCode); var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!; return JsonSerializer.Deserialize(envelope.Result!.Value, type)!; }
    private static void AssertError(ProcessResult result, int exitCode, string code) { Assert.Equal(exitCode, result.ExitCode); Assert.Equal(code, JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code); }
}
