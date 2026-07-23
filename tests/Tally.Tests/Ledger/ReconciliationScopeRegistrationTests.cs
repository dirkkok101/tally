using System.Text.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Accounts;
using Tally.Domain.Ledger.Evidence;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Recovery;
using Tally.Features.Ledger.Reconciliation;
using Tally.Infrastructure.Recovery;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Evidence;
using Tally.Infrastructure.Storage.Reconciliation;
using Xunit;

namespace Tally.Tests.Ledger;

[SupportedOSPlatform("linux")]
public sealed class ReconciliationScopeRegistrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-scope-{Guid.NewGuid():N}");
    private LedgerConnectionFactory factory = null!;
    private LedgerDb database = null!;
    private AccountStore accounts = null!;
    private EvidenceStore evidence = null!;
    private RegisterReconciliationScopeHandler handler = null!;
    private int accountSequence;

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-RECONCILIATION-CONTRACT
    [Fact]
    public void TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_normalizes_a_complete_ordered_scope()
    {
        var first = LedgerId.New().ToString();
        var second = LedgerId.New().ToString();
        var input = new RegisterReconciliationScopeInput(
            LedgerId.New().ToString(), "2026-07-01", "2026-07-31", " statement:jul-2026 ", [second, first]);

        var accepted = StatementScopeRegistrationPolicy.TryNormalize(input, out var normalized, out var error);

        Assert.True(accepted, error);
        Assert.NotNull(normalized);
        Assert.Equal("statement:jul-2026", normalized.ManifestOpaqueReference);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), normalized.EvidenceIds);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public void TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_rejects_empty_or_duplicate_membership()
    {
        var accountId = LedgerId.New().ToString();
        var evidenceId = LedgerId.New().ToString();

        Assert.False(StatementScopeRegistrationPolicy.TryNormalize(
            new(accountId, "2026-07-01", "2026-07-31", "statement:jul-2026", []), out _, out _));
        Assert.False(StatementScopeRegistrationPolicy.TryNormalize(
            new(accountId, "2026-07-01", "2026-07-31", "statement:jul-2026", [evidenceId, evidenceId]), out _, out _));
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_commits_membership_atomically_and_replays()
    {
        var accountId = await SeedAccount();
        var second = await SeedStatementRow(accountId, "2026-07-11");
        var first = await SeedStatementRow(accountId, "2026-07-10");
        var input = new RegisterReconciliationScopeInput(accountId, "2026-07-01", "2026-07-31", "statement:jul", [second, first]);

        var firstResult = await handler.HandleAsync(input, new SafeActor("owner", "dirk"), "scope-key", CancellationToken.None);
        var replay = await handler.HandleAsync(input, new SafeActor("owner", "dirk"), "scope-key", CancellationToken.None);

        Assert.True(firstResult.IsSuccess, firstResult.ErrorCode);
        Assert.Equal(firstResult.Value.GetRawText(), replay.Value.GetRawText());
        var detail = firstResult.Value.Deserialize(ReconciliationScopeJsonContext.Default.ReconciliationScopeDetail)!;
        Assert.Equal("completed", detail.Status);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), detail.EvidenceIds);
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM statement_scope_evidence WHERE scope_id = $id;";
        command.Parameters.AddWithValue("$id", detail.ScopeId);
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_requires_actor_and_idempotency_key(bool omitActor, bool omitKey)
    {
        var accountId = await SeedAccount();
        var evidenceId = await SeedStatementRow(accountId, "2026-07-10");

        var result = await handler.HandleAsync(
            Scope(accountId, [evidenceId]),
            omitActor ? null : new SafeActor("owner", "dirk"),
            omitKey ? null : "scope-key",
            CancellationToken.None);

        await AssertFailureWithoutScope(result, ReconciliationScopeErrors.InvalidInput);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_cross_key_replay_returns_the_original()
    {
        var accountId = await SeedAccount();
        var evidenceId = await SeedStatementRow(accountId, "2026-07-10");
        var input = Scope(accountId, [evidenceId]);

        var first = await Register(input, "first-key");
        var replay = await Register(input, "second-key");

        Assert.True(first.IsSuccess, first.ErrorCode);
        Assert.Equal(first.Value.GetRawText(), replay.Value.GetRawText());
        Assert.Equal(1L, await Count("statement_scope"));
        Assert.Equal(1L, await Count("logical_effect"));
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION, DD-LEDGER-IDEMPOTENT-MUTATIONS
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_changed_replay_conflicts_without_new_rows(bool useAnotherKey)
    {
        var accountId = await SeedAccount();
        var evidenceId = await SeedStatementRow(accountId, "2026-07-10");
        await Register(Scope(accountId, [evidenceId]), "first-key");

        var conflict = await Register(
            Scope(accountId, [evidenceId]) with { ManifestOpaqueReference = "statement:changed" },
            useAnotherKey ? "second-key" : "first-key");

        Assert.False(conflict.IsSuccess);
        Assert.Equal(LedgerMutationExecutor.ConflictCode, conflict.ErrorCode);
        Assert.Equal(1L, await Count("statement_scope"));
        Assert.Equal(1L, await Count("statement_scope_evidence"));
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_rejects_missing_account_without_writes()
    {
        var result = await Register(Scope(LedgerId.New().ToString(), [LedgerId.New().ToString()]));

        await AssertFailureWithoutScope(result, ReconciliationScopeErrors.AccountNotFound);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_rejects_inactive_account_without_writes()
    {
        var accountId = await SeedAccount(archived: true);
        var result = await Register(Scope(accountId, [LedgerId.New().ToString()]));

        await AssertFailureWithoutScope(result, ReconciliationScopeErrors.AccountInactive);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-kind")]
    [InlineData("incomplete")]
    [InlineData("wrong-account")]
    [InlineData("before-period")]
    [InlineData("after-period")]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_rejects_ineligible_evidence_without_writes(string scenario)
    {
        var accountId = await SeedAccount();
        var evidenceId = scenario switch
        {
            "missing" => LedgerId.New().ToString(),
            "wrong-kind" => await SeedEvidence(EvidenceKind.Receipt, accountId, "2026-07-10", complete: true),
            "incomplete" => await SeedEvidence(EvidenceKind.StatementRow, accountId, "2026-07-10", complete: false),
            "wrong-account" => await SeedStatementRow(await SeedAccount(), "2026-07-10"),
            "before-period" => await SeedStatementRow(accountId, "2026-06-30"),
            "after-period" => await SeedStatementRow(accountId, "2026-08-01"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var expected = scenario switch
        {
            "missing" => ReconciliationScopeErrors.EvidenceNotFound,
            "wrong-kind" => ReconciliationScopeErrors.StatementEvidenceRequired,
            "incomplete" => ReconciliationScopeErrors.IncompleteObservation,
            _ => ReconciliationScopeErrors.AccountDateConflict
        };

        var result = await Register(Scope(accountId, [evidenceId]));

        await AssertFailureWithoutScope(result, expected);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_rejects_evidence_already_in_another_scope()
    {
        var accountId = await SeedAccount();
        var evidenceId = await SeedStatementRow(accountId, "2026-07-10");
        Assert.True((await Register(Scope(accountId, [evidenceId]), "first-key")).IsSuccess);

        var result = await Register(
            Scope(accountId, [evidenceId]) with { PeriodStart = "2026-07-10", PeriodEnd = "2026-07-10" },
            "second-key");

        Assert.False(result.IsSuccess);
        Assert.Equal(ReconciliationScopeErrors.EvidenceAlreadyScoped, result.ErrorCode);
        Assert.Equal(1L, await Count("statement_scope"));
        Assert.Equal(1L, await Count("statement_scope_evidence"));
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_rolls_back_the_whole_batch_when_one_member_is_invalid()
    {
        var accountId = await SeedAccount();
        var validEvidenceId = await SeedStatementRow(accountId, "2026-07-10");

        var result = await Register(Scope(accountId, [validEvidenceId, LedgerId.New().ToString()]));

        await AssertFailureWithoutScope(result, ReconciliationScopeErrors.EvidenceNotFound);
        Assert.Equal(0L, await Count("logical_effect"));
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_verifier_rejects_duplicate_account_periods()
    {
        var candidate = await Candidate("duplicate-period");
        var accountId = await SeedCandidateAccount(candidate);
        await Execute(candidate, """
            INSERT INTO statement_scope VALUES ($first, $account, '2026-07-01', '2026-07-31', 'statement:first', 'completed', 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            INSERT INTO statement_scope VALUES ($second, $account, '2026-07-01', '2026-07-31', 'statement:second', 'completed', 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            """, ("$first", LedgerId.New().ToString()), ("$second", LedgerId.New().ToString()), ("$account", accountId));

        var result = await new DurableLedgerVerifier(new HostArtifactProtection()).VerifyAsync(candidate, CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("statement_scope", result.SafeType);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_verifier_rejects_inconsistent_membership()
    {
        var candidate = await Candidate("inconsistent-membership");
        var accountId = await SeedCandidateAccount(candidate);
        var evidenceId = LedgerId.New().ToString();
        var scopeId = LedgerId.New().ToString();
        await Execute(candidate, """
            INSERT INTO evidence_record VALUES ($evidence, 'statement_row', $digest, 'statement:row', $fingerprint, 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            INSERT INTO evidence_observation VALUES ($evidence, $account, -100, 'ZAR', '2026-07-10', NULL, NULL, NULL, $fingerprint);
            INSERT INTO statement_scope VALUES ($scope, $account, '2026-07-01', '2026-07-31', 'statement:jul', 'open', 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            INSERT INTO statement_scope_evidence VALUES ($scope, $evidence);
            """, ("$evidence", evidenceId), ("$scope", scopeId), ("$account", accountId), ("$digest", Digest()), ("$fingerprint", Digest()));

        var result = await new DurableLedgerVerifier(new HostArtifactProtection()).VerifyAsync(candidate, CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("statement_scope", result.SafeType);
    }

    // TC-LEDGER-STATEMENT-SCOPE-REGISTRATION
    [Fact]
    public async Task TC_LEDGER_STATEMENT_SCOPE_REGISTRATION_verifier_rejects_evidence_in_multiple_scopes()
    {
        var candidate = await Candidate("duplicate-membership");
        var accountId = await SeedCandidateAccount(candidate);
        var evidenceId = LedgerId.New().ToString();
        await Execute(candidate, """
            INSERT INTO evidence_record VALUES ($evidence, 'statement_row', $digest, 'statement:row', $fingerprint, 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            INSERT INTO evidence_observation VALUES ($evidence, $account, -100, 'ZAR', '2026-07-10', NULL, NULL, NULL, $fingerprint);
            INSERT INTO statement_scope VALUES ($first, $account, '2026-07-01', '2026-07-31', 'statement:first', 'completed', 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            INSERT INTO statement_scope VALUES ($second, $account, '2026-07-10', '2026-07-20', 'statement:second', 'completed', 'owner:dirk', '2026-08-01T00:00:00.0000000Z');
            INSERT INTO statement_scope_evidence VALUES ($first, $evidence);
            INSERT INTO statement_scope_evidence VALUES ($second, $evidence);
            """, ("$evidence", evidenceId), ("$first", LedgerId.New().ToString()), ("$second", LedgerId.New().ToString()),
            ("$account", accountId), ("$digest", Digest()), ("$fingerprint", Digest()));

        var result = await new DurableLedgerVerifier(new HostArtifactProtection()).VerifyAsync(candidate, CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(DurableLedgerErrors.InvariantViolation, result.ErrorCode);
        Assert.Equal("statement_scope", result.SafeType);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        accounts = new(database, factory);
        evidence = new(database, factory);
        handler = new(new LedgerMutationExecutor(database, factory, new IdempotencyStore()), new ReconciliationScopeStore());
    }

    public Task DisposeAsync() { if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }

    private async Task<string> SeedAccount(bool archived = false)
    {
        var id = LedgerId.New().ToString();
        var accountSuffix = (++accountSequence).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Scope " + accountSuffix, AccountType.Cheque, "****" + accountSuffix, "ZAR"), out var definition, out _));
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        await accounts.InsertAsync(connection, transaction, id, LedgerId.New().ToString(), definition!, "owner:dirk", "2026-07-01T00:00:00.0000000Z", CancellationToken.None);
        if (archived)
        {
            var current = await accounts.FindCurrentAsync(connection, transaction, id, CancellationToken.None);
            await accounts.AppendLifecycleAsync(connection, transaction, LedgerId.New().ToString(), current!, AccountLifecycleAction.Archive, null, "closed", "owner:dirk", "2026-07-02T00:00:00.0000000Z", CancellationToken.None);
        }
        await transaction.CommitAsync(); return id;
    }

    private async Task<string> SeedStatementRow(string accountId, string date)
        => await SeedEvidence(EvidenceKind.StatementRow, accountId, date, complete: true);

    private async Task<string> SeedEvidence(EvidenceKind kind, string accountId, string date, bool complete)
    {
        var observation = complete ? new EvidenceObservation(accountId, -1234, "ZAR", date, null, null, null, Digest()) : null;
        var input = new RegisterEvidenceInput(kind, Digest(), "statement:row", Digest(), observation);
        Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out _));
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var transaction = connection.BeginTransaction();
        var detail = await evidence.RegisterInitialAsync(connection, transaction, identity!, input, "owner:dirk", "2026-07-01T00:00:00.0000000Z", CancellationToken.None);
        await transaction.CommitAsync(); return detail.EvidenceId;
    }

    private Task<CommandResult<JsonElement>> Register(RegisterReconciliationScopeInput input, string key = "scope-key") =>
        handler.HandleAsync(input, new SafeActor("owner", "dirk"), key, CancellationToken.None);

    private static RegisterReconciliationScopeInput Scope(string accountId, IReadOnlyList<string> evidenceIds) =>
        new(accountId, "2026-07-01", "2026-07-31", "statement:jul", evidenceIds);

    private async Task AssertFailureWithoutScope(CommandResult<JsonElement> result, string error)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.ErrorCode);
        Assert.Equal(0L, await Count("statement_scope"));
        Assert.Equal(0L, await Count("statement_scope_evidence"));
        Assert.Equal(0L, await Count("idempotency_record"));
    }

    private async Task<long> Count(string table)
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<LedgerDb> Candidate(string name)
    {
        var candidate = new LedgerDb(Path.Combine(root, name), Guid.NewGuid().ToString("N"));
        await using var connection = await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(candidate, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await CompleteLedgerSchema.CreateCurrent().ApplyAsync(connection, CancellationToken.None);
        return candidate;
    }

    private async Task<string> SeedCandidateAccount(LedgerDb candidate)
    {
        var accountId = LedgerId.New().ToString();
        await Execute(candidate, """
            INSERT INTO account VALUES ($account, 'Bank', 'cheque', 'asset', '****1234', 'ZAR', '2026-07-01T00:00:00.0000000Z');
            INSERT INTO catalogue_lifecycle_event VALUES ($event, 'account', $account, 'create', NULL, 'Scope', 'scope', NULL, 'owner:dirk', '2026-07-01T00:00:00.0000000Z', NULL);
            """, ("$account", accountId), ("$event", LedgerId.New().ToString()));
        return accountId;
    }

    private static async Task Execute(LedgerDb candidate, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(candidate, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string Digest() => Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();
}
