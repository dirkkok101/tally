using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Domain.Ledger.Evidence;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Reconciliation;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class ReconciliationApplyOperationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-reconciliation-apply-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private AccountStore accountStore = null!;
    private EvidenceStore evidenceStore = null!;
    private TransactionStore transactionStore = null!;
    private ReconciliationProjectionStore projectionStore = null!;
    private ReconciliationApplyHandler handler = null!;
    private ReconciliationApplyOperationModule module = null!;
    private int sequence;

    [Theory]
    [InlineData(ReconciliationApplyDisposition.MatchExisting)]
    [InlineData(ReconciliationApplyDisposition.CreateStatementOnly)]
    [InlineData(ReconciliationApplyDisposition.RecordAmbiguous)]
    [InlineData(ReconciliationApplyDisposition.RecordException)]
    public void FR_LEDGER_STATEMENT_RECONCILIATION_defines_a_closed_base_disposition(ReconciliationApplyDisposition disposition) =>
        Assert.True(ReconciliationDispositionPolicy.IsBaseDisposition(disposition));

    [Fact]
    public void FR_LEDGER_STATEMENT_RECONCILIATION_reserves_statement_correction_for_the_composite() =>
        Assert.False(ReconciliationDispositionPolicy.IsBaseDisposition(ReconciliationApplyDisposition.CorrectExistingFromStatement));

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_owner_match_appends_decision_authority_and_confirming_link()
    {
        var statement = await SeedStatement();
        var target = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var result = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target));

        Assert.Equal(target, result.ActiveTransactionId);
        Assert.False(result.CreatedStatementOnly);
        Assert.NotNull(result.ConfirmingLinkEventId);
        Assert.Null(result.ExceptionId);
        Assert.Equal((1L, 1L, 1L), await DecisionCounts(statement.EvidenceId));

        var detail = await transactionStore.GetAsync(target, false, CancellationToken.None);
        Assert.Equal(TransactionReconciliationState.OwnerConfirmedMatch, detail!.ReconciliationState);
        Assert.Contains(detail.Evidence, item => item.EvidenceId == statement.EvidenceId && item.Role == EvidenceLinkRole.Confirming);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_owner_can_explicitly_confirm_a_guard_candidate()
    {
        var statement = await SeedStatement();
        var guard = await SeedTransaction(statement.AccountId, -9999, statement.TransactionDate);

        var result = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [guard], guard));

        Assert.Equal(guard, result.ActiveTransactionId);
        Assert.Equal(TransactionReconciliationState.OwnerConfirmedMatch, (await transactionStore.GetAsync(guard, false, CancellationToken.None))!.ReconciliationState);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_statement_only_creates_one_transaction_with_explicit_unknown_defaults()
    {
        var statement = await SeedStatement();

        var result = Success(await Apply(
            statement,
            ReconciliationApplyDisposition.CreateStatementOnly,
            [],
            statementFact: statement.Fact));

        Assert.True(result.CreatedStatementOnly);
        Assert.NotNull(result.ActiveTransactionId);
        var detail = await transactionStore.GetAsync(result.ActiveTransactionId!, true, CancellationToken.None);
        Assert.Equal(TransactionReconciliationState.StatementOnly, detail!.ReconciliationState);
        Assert.Equal(TransactionKnowledgeState.Unknown, detail.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionKnowledgeState.Unknown, detail.PaymentAttribution.CardholderState);
        Assert.Equal(TransactionPoolState.Unassigned, detail.Pool.State);
        Assert.Null(detail.PaymentAttribution.InstrumentId);
        Assert.Null(detail.PaymentAttribution.CardholderId);
        Assert.Null(detail.Pool.PoolId);
        Assert.Contains(detail.Evidence, item => item.EvidenceId == statement.EvidenceId && item.Role == EvidenceLinkRole.Confirming);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM transaction_fact WHERE transaction_id = $id;", ("$id", result.ActiveTransactionId!)));
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_ambiguous_preserves_the_complete_candidate_set_without_financial_effect()
    {
        var statement = await SeedStatement();
        var first = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var second = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var before = await Scalar("SELECT COUNT(*) FROM transaction_fact;");

        var result = Success(await Apply(statement, ReconciliationApplyDisposition.RecordAmbiguous, [second, first]));

        Assert.Equal([first, second], result.ReviewedCandidateIds.Order(StringComparer.Ordinal).ToArray());
        Assert.Null(result.ActiveTransactionId);
        Assert.Null(result.ConfirmingLinkEventId);
        Assert.NotNull(result.ExceptionId);
        Assert.Equal(before, await Scalar("SELECT COUNT(*) FROM transaction_fact;"));
        Assert.Equal(0, await Scalar("SELECT COUNT(*) FROM evidence_link_event WHERE evidence_id = $id;", ("$id", statement.EvidenceId)));
        var basis = await Text("SELECT match_basis FROM reconciliation_decision WHERE decision_id = $id;", ("$id", result.DecisionId));
        Assert.Contains(first, basis, StringComparison.Ordinal);
        Assert.Contains(second, basis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_exception_preserves_stable_code_and_reason_without_financial_effect()
    {
        var statement = await SeedStatement();
        var before = await Scalar("SELECT COUNT(*) FROM transaction_fact;");

        var result = Success(await Apply(statement, ReconciliationApplyDisposition.RecordException, [], exceptionCode: "MISSING-MERCHANT"));

        Assert.Equal("MISSING-MERCHANT", result.ExceptionCode);
        Assert.Equal(before, await Scalar("SELECT COUNT(*) FROM transaction_fact;"));
        Assert.Equal("MISSING-MERCHANT: owner reviewed statement row", await Text(
            "SELECT reason FROM reconciliation_exception WHERE exception_id = $id;", ("$id", result.ExceptionId!)));
    }

    [Theory]
    [InlineData(ReconciliationAuthorityKind.DeterministicPolicy, ReconciliationApplyDisposition.MatchExisting, ReconciliationApplyErrors.UnsupportedAutomaticAuthority)]
    [InlineData(ReconciliationAuthorityKind.Owner, ReconciliationApplyDisposition.CorrectExistingFromStatement, ReconciliationApplyErrors.UnsupportedStatementCorrection)]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_reserved_authority_and_correction_without_mutation(
        ReconciliationAuthorityKind authority,
        ReconciliationApplyDisposition disposition,
        string expectedError)
    {
        var statement = await SeedStatement();
        var before = await MutationCounts();
        var target = disposition == ReconciliationApplyDisposition.MatchExisting
            ? await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate)
            : null;
        if (target is not null) before = await MutationCounts();

        var result = await Apply(statement, disposition, target is null ? [] : [target], target, authority: authority);

        AssertError(result, expectedError);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("fingerprint", ReconciliationApplyErrors.EvidenceFingerprintChanged)]
    [InlineData("token", ReconciliationApplyErrors.ProjectionChanged)]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_stale_review_material_without_mutation(string changed, string expectedError)
    {
        var statement = await SeedStatement();
        var target = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var before = await MutationCounts();

        var result = await Apply(
            statement,
            ReconciliationApplyDisposition.MatchExisting,
            [target],
            target,
            fingerprint: changed == "fingerprint" ? Digest() : null,
            token: changed == "token" ? Digest() : null);

        AssertError(result, expectedError);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_an_incomplete_reviewed_candidate_set()
    {
        var statement = await SeedStatement();
        var first = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);

        AssertError(
            await Apply(statement, ReconciliationApplyDisposition.RecordAmbiguous, [first]),
            ReconciliationApplyErrors.CandidateSetChanged);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_a_target_outside_the_reviewed_projection()
    {
        var statement = await SeedStatement();
        var candidate = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var otherAccount = await SeedAccount();
        var excluded = await SeedTransaction(otherAccount, statement.AmountMinor, statement.TransactionDate);

        AssertError(
            await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [candidate], excluded),
            ReconciliationApplyErrors.TargetNotCandidate);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_rejects_statement_only_when_a_candidate_exists()
    {
        var statement = await SeedStatement();
        var candidate = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);

        AssertError(
            await Apply(statement, ReconciliationApplyDisposition.CreateStatementOnly, [candidate], statementFact: statement.Fact),
            ReconciliationApplyErrors.DispositionIncompatible);
    }

    [Fact]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_statement_only_revalidates_the_account_inside_the_write_transaction()
    {
        var statement = await SeedStatement();
        await ArchiveAccount(statement.AccountId);
        var before = await MutationCounts();

        var result = await Apply(
            statement,
            ReconciliationApplyDisposition.CreateStatementOnly,
            [],
            statementFact: statement.Fact);

        AssertError(result, AccountStore.ArchivedError);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("date")]
    [InlineData("posting")]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_statement_only_fact_must_equal_the_normalized_observation(string changed)
    {
        var statement = await SeedStatement(postingDate: "2026-07-11");
        var otherAccount = changed == "account" ? await SeedAccount() : statement.AccountId;
        var fact = statement.Fact with
        {
            AccountId = otherAccount,
            SignedAmount = changed == "amount" ? "-99" : statement.Fact.SignedAmount,
            CurrencyCode = changed == "currency" ? "USD" : statement.Fact.CurrencyCode,
            TransactionDate = changed == "date" ? "2026-07-12" : statement.Fact.TransactionDate,
            PostingDate = changed == "posting" ? "2026-07-13" : statement.Fact.PostingDate
        };

        var result = await Apply(statement, ReconciliationApplyDisposition.CreateStatementOnly, [], statementFact: fact);

        AssertError(
            result,
            changed == "currency" ? ReconciliationApplyErrors.InvalidInput : ReconciliationApplyErrors.StatementFactMismatch);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_same_key_replay_returns_the_original_outcome()
    {
        var statement = await SeedStatement();
        var target = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);

        var first = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target, key: "same-key"));
        var replay = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target, key: "same-key", useCapturedProjection: first));

        Assert.Equal(
            JsonSerializer.Serialize(first, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult),
            JsonSerializer.Serialize(replay, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult));
        Assert.Equal((1L, 1L, 1L), await DecisionCounts(statement.EvidenceId));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_cross_key_exact_replay_returns_the_original_outcome()
    {
        var statement = await SeedStatement();
        var target = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);

        var first = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target, key: "first-key"));
        var replay = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target, key: "second-key", useCapturedProjection: first));

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal((1L, 1L, 1L), await DecisionCounts(statement.EvidenceId));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_statement_only_replay_creates_at_most_one_canonical_transaction()
    {
        var statement = await SeedStatement();
        var before = await Scalar("SELECT COUNT(*) FROM transaction_fact;");

        var first = Success(await Apply(
            statement,
            ReconciliationApplyDisposition.CreateStatementOnly,
            [],
            statementFact: statement.Fact,
            key: "first-key"));
        var replay = Success(await Apply(
            statement,
            ReconciliationApplyDisposition.CreateStatementOnly,
            [],
            statementFact: statement.Fact,
            key: "second-key",
            useCapturedProjection: first));

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(first.ActiveTransactionId, replay.ActiveTransactionId);
        Assert.Equal(before + 1, await Scalar("SELECT COUNT(*) FROM transaction_fact;"));
        Assert.Equal((1L, 1L, 1L), await DecisionCounts(statement.EvidenceId));
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_cross_key_replay_preserves_the_original_effect()
    {
        var statement = await SeedStatement();
        var target = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var first = Success(await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target, key: "first-key"));

        var changed = await Apply(statement, ReconciliationApplyDisposition.MatchExisting, [target], target, key: "second-key", reason: "changed reason", useCapturedProjection: first);

        AssertError(changed, LedgerMutationExecutor.ConflictCode);
        Assert.Equal((1L, 1L, 1L), await DecisionCounts(statement.EvidenceId));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("rawPayload")]
    [InlineData("recipient")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_apply_contract_rejects_transport_and_payload_fields(string field)
    {
        var statement = await SeedStatement();
        var projection = await Projection(statement);
        var input = Input(statement, ReconciliationApplyDisposition.RecordException, [], null, null, "REVIEW", projection.AdvisoryToken);
        var json = JsonSerializer.SerializeToElement(input, ReconciliationApplyJsonContext.Default.ReconciliationApplyInput).GetRawText();
        json = json.TrimEnd('}') + $",\"{field}\":\"forbidden\"}}";

        var result = await module.ApplyAsync(
            new(JsonDocument.Parse(json).RootElement.Clone(), new("owner", "dirk"), "privacy-key"),
            CancellationToken.None);

        AssertError(result, ReconciliationApplyErrors.InvalidInput);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FR_LEDGER_STATEMENT_RECONCILIATION_requires_actor_and_idempotency_key(bool hasActor, bool hasKey)
    {
        var statement = await SeedStatement();
        var projection = await Projection(statement);
        var input = Input(statement, ReconciliationApplyDisposition.RecordException, [], null, null, "REVIEW", projection.AdvisoryToken);

        var result = await handler.HandleAsync(
            input,
            hasActor ? new("owner", "dirk") : null,
            hasKey ? "key" : null,
            CancellationToken.None);

        AssertError(result, ReconciliationApplyErrors.InvalidInput);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        accountStore = new(database, factory);
        evidenceStore = new(database, factory);
        transactionStore = new(database, factory);
        projectionStore = new(database, factory, evidenceStore, transactionStore);
        var executor = new LedgerMutationExecutor(database, factory, new IdempotencyStore());
        var writeStore = new ReconciliationWriteStore(evidenceStore, transactionStore);
        handler = new(executor, accountStore, projectionStore, writeStore);
        module = new(handler);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<CommandResult<JsonElement>> Apply(
        StatementFixture statement,
        ReconciliationApplyDisposition disposition,
        IReadOnlyList<string> candidates,
        string? target = null,
        AuthoritativeStatementFact? statementFact = null,
        string? exceptionCode = null,
        ReconciliationAuthorityKind authority = ReconciliationAuthorityKind.Owner,
        string? fingerprint = null,
        string? token = null,
        string key = "apply-key",
        string reason = "owner reviewed statement row",
        ReconciliationApplyResult? useCapturedProjection = null)
    {
        var projection = useCapturedProjection is null ? await Projection(statement) : null;
        var input = new ReconciliationApplyInput(
            statement.EvidenceId,
            fingerprint ?? statement.Fingerprint,
            statement.ScopeId,
            token ?? useCapturedProjection?.ProjectionToken ?? projection!.AdvisoryToken,
            disposition,
            authority,
            candidates,
            target,
            statementFact,
            exceptionCode,
            reason);
        return await handler.HandleAsync(input, new("owner", "dirk", "run-1"), key, CancellationToken.None);
    }

    private static ReconciliationApplyInput Input(
        StatementFixture statement,
        ReconciliationApplyDisposition disposition,
        IReadOnlyList<string> candidates,
        string? target,
        AuthoritativeStatementFact? fact,
        string? exceptionCode,
        string token) => new(
            statement.EvidenceId,
            statement.Fingerprint,
            statement.ScopeId,
            token,
            disposition,
            ReconciliationAuthorityKind.Owner,
            candidates,
            target,
            fact,
            exceptionCode,
            "owner reviewed statement row");

    private async Task<ReconciliationProjectionResult> Projection(StatementFixture statement)
    {
        var read = await projectionStore.ReadAsync(statement.EvidenceId, statement.ScopeId, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        return ManualReviewProjectionV1.Project(read.Source!);
    }

    private async Task<string> SeedAccount()
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var accountId = LedgerId.New().ToString();
        var suffix = (1000 + Interlocked.Increment(ref sequence)).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Account " + suffix, AccountType.Cheque, "****" + suffix, "ZAR"), out var account, out _));
        await accountStore.InsertAsync(connection, transaction, accountId, LedgerId.New().ToString(), account!, "actor", At(0), CancellationToken.None);
        await transaction.CommitAsync();
        return accountId;
    }

    private async Task<string> SeedTransaction(string accountId, long amount, string date)
    {
        var input = new RecordTransactionInput(
            accountId,
            Money.FromMinorUnits(amount).ToString(),
            "ZAR",
            date,
            null,
            "agent-captured transaction",
            null,
            null,
            new(EvidenceKind.AgentCapture, Digest(), null, null, null));
        Assert.True(TransactionFact.TryCreate(input, out var fact, out _));
        var transactionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await transactionStore.InsertFactAndDefaultsAsync(
            connection,
            transaction,
            transactionId,
            LedgerId.New().ToString(),
            null,
            LedgerId.New().ToString(),
            fact!,
            At(1),
            "ubuntu",
            "actor",
            CancellationToken.None);
        await transaction.CommitAsync();
        return transactionId;
    }

    private async Task ArchiveAccount(string accountId)
    {
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var current = await accountStore.FindCurrentAsync(connection, transaction, accountId, CancellationToken.None);
        await accountStore.AppendLifecycleAsync(
            connection,
            transaction,
            LedgerId.New().ToString(),
            current!,
            AccountLifecycleAction.Archive,
            null,
            "test archive",
            "actor",
            At(3),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<StatementFixture> SeedStatement(string? postingDate = null)
    {
        var accountId = await SeedAccount();
        const long amount = -1234;
        const string transactionDate = "2026-07-10";
        var fingerprint = Digest();
        var observation = new EvidenceObservation(accountId, amount, "ZAR", transactionDate, postingDate, null, null, Digest());
        var input = new RegisterEvidenceInput(EvidenceKind.StatementRow, Digest(), "statement:row", fingerprint, observation);
        Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out _));
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, identity!, input, "actor", At(2), CancellationToken.None);
        var scopeId = LedgerId.New().ToString();
        await Execute(connection, transaction, """
            INSERT INTO statement_scope(scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at)
            VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', 'statement:manifest', 'open', 'actor', $at);
            INSERT INTO statement_scope_evidence(scope_id, evidence_id) VALUES ($scopeId, $evidenceId);
            """, ("$scopeId", scopeId), ("$accountId", accountId), ("$at", At(2)), ("$evidenceId", evidence.EvidenceId));
        await transaction.CommitAsync();
        return new(
            evidence.EvidenceId,
            scopeId,
            accountId,
            amount,
            transactionDate,
            fingerprint,
            new(accountId, Money.FromMinorUnits(amount).ToString(), "ZAR", transactionDate, postingDate, "statement transaction"));
    }

    private async Task<(long Decisions, long Authorities, long Links)> DecisionCounts(string evidenceId) => (
        await Scalar("SELECT COUNT(*) FROM reconciliation_decision WHERE evidence_id = $id;", ("$id", evidenceId)),
        await Scalar("SELECT COUNT(*) FROM reconciliation_decision_authority AS authority JOIN reconciliation_decision AS decision ON decision.decision_id = authority.decision_id WHERE decision.evidence_id = $id;", ("$id", evidenceId)),
        await Scalar("SELECT COUNT(*) FROM evidence_link_event WHERE evidence_id = $id AND role = 'confirming';", ("$id", evidenceId)));

    private async Task<IReadOnlyDictionary<string, long>> MutationCounts()
    {
        var tables = new[] { "transaction_fact", "reconciliation_decision", "reconciliation_decision_authority", "evidence_link_event", "reconciliation_exception", "idempotency_record", "logical_effect" };
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables) counts.Add(table, await Scalar($"SELECT COUNT(*) FROM {table};"));
        return counts;
    }

    private async Task<long> Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> Text(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<SqliteConnection> Open() => await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static ReconciliationApplyResult Success(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult)!;
    }

    private static void AssertError(CommandResult<JsonElement> result, string expected)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorCode);
    }

    private static string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
    private static string At(int second) => $"2026-07-22T00:00:{second:D2}Z";
    private sealed record StatementFixture(
        string EvidenceId,
        string ScopeId,
        string AccountId,
        long AmountMinor,
        string TransactionDate,
        string Fingerprint,
        AuthoritativeStatementFact Fact);
}
