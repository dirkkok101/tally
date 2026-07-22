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
public sealed class ReconciliationDecisionOperationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-reconciliation-decision-{Guid.NewGuid():N}");
    private LedgerDb database = null!;
    private LedgerConnectionFactory factory = null!;
    private AccountStore accountStore = null!;
    private EvidenceStore evidenceStore = null!;
    private TransactionStore transactionStore = null!;
    private ReconciliationProjectionStore projectionStore = null!;
    private LedgerMutationExecutor executor = null!;
    private ReconciliationDecisionStore decisionStore = null!;
    private ReconciliationApplyHandler applyHandler = null!;
    private GetReconciliationDecisionHandler getHandler = null!;
    private ReconciliationDecisionMutationHandler mutationHandler = null!;
    private ReconciliationDecisionOperationModule module = null!;
    private int sequence;

    [Theory]
    [InlineData(ReconciliationDecisionAction.Confirm, false)]
    [InlineData(ReconciliationDecisionAction.Reject, false)]
    [InlineData(ReconciliationDecisionAction.Revoke, true)]
    [InlineData(ReconciliationDecisionAction.Replace, true)]
    public void FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_defines_closed_active_link_predecessor_rules(
        ReconciliationDecisionAction action,
        bool requiresPredecessor) =>
        Assert.Equal(requiresPredecessor, ReconciliationStateReducer.RequiresActiveLinkPredecessor(action));

    [Fact]
    public void FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_correction_states_are_closed()
    {
        Assert.Equal(
            [
                ReconciliationDecisionCurrentState.ConfirmedExisting,
                ReconciliationDecisionCurrentState.CorrectedFromStatement,
                ReconciliationDecisionCurrentState.StatementOnly,
                ReconciliationDecisionCurrentState.Ambiguous,
                ReconciliationDecisionCurrentState.Exception,
                ReconciliationDecisionCurrentState.OwnerConfirmedMatch,
                ReconciliationDecisionCurrentState.Rejected,
                ReconciliationDecisionCurrentState.Revoked,
                ReconciliationDecisionCurrentState.Replaced
            ],
            Enum.GetValues<ReconciliationDecisionCurrentState>());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_get_returns_attributable_ordered_history()
    {
        var seeded = await SeedAmbiguous();

        var detail = SuccessDetail(await Get(seeded.Statement.EvidenceId));

        var item = Assert.Single(detail.History);
        Assert.Equal(seeded.Ambiguous.DecisionId, detail.CurrentDecisionId);
        Assert.Equal(ReconciliationDecisionCurrentState.Ambiguous, detail.CurrentState);
        Assert.True(detail.RequiresOwnerReview);
        Assert.Equal(ReconciliationDecisionDisposition.Ambiguous, item.Disposition);
        Assert.Equal(ReconciliationAuthorityKind.Owner, item.AuthorityKind);
        Assert.Equal("owner reviewed statement row", item.Reason);
        Assert.Equal("owner:dirk:run-1", item.Actor);
        Assert.Equal(ManualReviewProjectionV1.PolicyId, item.PolicyId);
        Assert.Equal(ManualReviewProjectionV1.PolicyVersion, item.PolicyVersion);
        Assert.StartsWith("scope:", item.StatementAuthorityBasis, StringComparison.Ordinal);
        Assert.Empty(item.Links);
        Assert.Null(item.CarryForward);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_get_preserves_deterministic_authority_detail()
    {
        var statement = await SeedStatement();
        var target = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var decisionId = LedgerId.New().ToString();
        await using (var connection = await Open())
        await using (var transaction = connection.BeginTransaction())
        {
            await Execute(connection, transaction, """
                INSERT INTO reconciliation_decision(
                    decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                    match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
                VALUES ($decisionId, $evidenceId, $transactionId, 'deterministic_match', 'exact-policy', '1.0',
                        'exact provider-neutral match', 1, 'exact match', 'system:policy', $at, NULL);
                INSERT INTO reconciliation_decision_authority(
                    decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                    authority_kind, statement_authority_basis, schema_origin, recorded_at)
                VALUES ($decisionId, 'confirmed_existing', NULL, $transactionId,
                        'deterministic_policy', 'statement:test', 'v2', $at);
                INSERT INTO evidence_link_event(
                    link_event_id, evidence_id, transaction_id, role, action, decision_id,
                    reason, recorded_by, recorded_at, previous_link_event_id)
                VALUES ($linkId, $evidenceId, $transactionId, 'confirming', 'link', $decisionId,
                        'exact match', 'system:policy', $at, NULL);
                """, ("$decisionId", decisionId), ("$evidenceId", statement.EvidenceId), ("$transactionId", target),
                ("$linkId", LedgerId.New().ToString()), ("$at", At(3)));
            await transaction.CommitAsync();
        }

        var detail = await GetDetail(statement.EvidenceId);
        var item = Assert.Single(detail.History);
        Assert.Equal(ReconciliationDecisionCurrentState.ConfirmedExisting, detail.CurrentState);
        Assert.Equal(ReconciliationDecisionDisposition.ConfirmedExisting, item.Disposition);
        Assert.Equal(ReconciliationAuthorityKind.DeterministicPolicy, item.AuthorityKind);
        Assert.Equal("exact-policy", item.PolicyId);
        Assert.NotNull(detail.ActiveConfirmingLinkEventId);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_confirm_selects_one_reviewed_candidate_and_changes_no_other_candidate()
    {
        var seeded = await SeedAmbiguous();
        var selected = seeded.Candidates[0];
        var other = seeded.Candidates[1];

        var result = SuccessMutation(await Confirm(seeded, selected));
        var detail = SuccessDetail(await Get(seeded.Statement.EvidenceId));

        Assert.Equal(ReconciliationDecisionCurrentState.OwnerConfirmedMatch, result.CurrentState);
        Assert.Equal(selected, result.ActiveTransactionId);
        Assert.Equal(2, detail.History.Count);
        Assert.Equal(result.DecisionId, detail.CurrentDecisionId);
        Assert.Equal(selected, detail.ActiveTransactionId);
        Assert.NotNull(detail.ActiveConfirmingLinkEventId);
        Assert.False(detail.RequiresOwnerReview);
        Assert.Equal(TransactionReconciliationState.OwnerConfirmedMatch, (await transactionStore.GetAsync(selected, false, CancellationToken.None))!.ReconciliationState);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, (await transactionStore.GetAsync(other, false, CancellationToken.None))!.ReconciliationState);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_confirm_accepts_an_explicitly_reviewed_guard()
    {
        var statement = await SeedStatement();
        var exact = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var guard = await SeedTransaction(statement.AccountId, -9999, statement.TransactionDate);
        var ambiguous = await Apply(statement, ReconciliationApplyDisposition.RecordAmbiguous, [exact, guard]);

        var result = SuccessMutation(await mutationHandler.ConfirmAsync(
            new(statement.EvidenceId, statement.ScopeId, ambiguous.DecisionId, guard, ReconciliationAuthorityKind.Owner, "owner selected guard"),
            Actor,
            "confirm-guard",
            CancellationToken.None));

        Assert.Equal(guard, result.ActiveTransactionId);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_confirm_resolves_a_previously_unmatched_exception()
    {
        var statement = await SeedStatement();
        var exception = await Apply(statement, ReconciliationApplyDisposition.RecordException, []);
        var laterCandidate = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);

        var result = SuccessMutation(await mutationHandler.ConfirmAsync(
            new(statement.EvidenceId, statement.ScopeId, exception.DecisionId, laterCandidate, ReconciliationAuthorityKind.Owner, "owner resolved exception"),
            Actor,
            "confirm-exception",
            CancellationToken.None));

        Assert.Equal(laterCandidate, result.ActiveTransactionId);
        Assert.Equal(ReconciliationDecisionCurrentState.OwnerConfirmedMatch, result.CurrentState);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_all_candidates_without_link_or_financial_effect()
    {
        var seeded = await SeedAmbiguous();
        var before = await Scalar("SELECT COUNT(*) FROM transaction_fact;");

        var result = SuccessMutation(await Reject(seeded));
        var detail = SuccessDetail(await Get(seeded.Statement.EvidenceId));

        Assert.Equal(ReconciliationDecisionCurrentState.Rejected, result.CurrentState);
        Assert.Null(result.ActiveTransactionId);
        Assert.Null(result.LinkEventId);
        Assert.Equal(before, await Scalar("SELECT COUNT(*) FROM transaction_fact;"));
        Assert.Null(detail.ActiveConfirmingLinkEventId);
        Assert.Equal(2, detail.History.Count);
        foreach (var candidate in seeded.Candidates)
        {
            Assert.Equal(
                TransactionReconciliationState.RecordedUnreconciled,
                (await transactionStore.GetAsync(candidate, false, CancellationToken.None))!.ReconciliationState);
        }
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_revoke_retires_the_active_link_and_preserves_history()
    {
        var confirmed = await SeedConfirmed();

        var result = SuccessMutation(await Revoke(confirmed));
        var detail = SuccessDetail(await Get(confirmed.Seeded.Statement.EvidenceId));

        Assert.Equal(confirmed.Confirmation.DecisionId, result.PreviousDecisionId);
        Assert.Equal(confirmed.Target, result.PriorTransactionId);
        Assert.Null(result.ActiveTransactionId);
        Assert.Equal(ReconciliationDecisionCurrentState.Revoked, detail.CurrentState);
        Assert.Null(detail.ActiveConfirmingLinkEventId);
        Assert.Equal(3, detail.History.Count);
        Assert.Contains(detail.History.SelectMany(item => item.Links), link => link.Action == EvidenceLinkAction.Revoke);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM evidence_link_event WHERE evidence_id = $id AND action = 'revoke';", ("$id", confirmed.Seeded.Statement.EvidenceId)));
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_replace_moves_only_the_active_link_to_a_compatible_candidate()
    {
        var confirmed = await SeedConfirmed();
        var replacement = confirmed.Seeded.Candidates.Single(id => id != confirmed.Target);

        var result = SuccessMutation(await Replace(confirmed, replacement));
        var detail = SuccessDetail(await Get(confirmed.Seeded.Statement.EvidenceId));

        Assert.Equal(confirmed.Target, result.PriorTransactionId);
        Assert.Equal(replacement, result.ActiveTransactionId);
        Assert.Equal(ReconciliationDecisionCurrentState.Replaced, detail.CurrentState);
        Assert.Equal(replacement, detail.ActiveTransactionId);
        Assert.Equal(result.LinkEventId, detail.ActiveConfirmingLinkEventId);
        Assert.Equal(3, detail.History.Count);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM evidence_active_confirming_target WHERE evidence_id = $id;", ("$id", confirmed.Seeded.Statement.EvidenceId)));
        Assert.Equal(replacement, await Text("SELECT transaction_id FROM evidence_active_confirming_target WHERE evidence_id = $id;", ("$id", confirmed.Seeded.Statement.EvidenceId)));
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_get_returns_stable_not_found()
    {
        AssertError(await Get(LedgerId.New().ToString()), ReconciliationDecisionErrors.NotFound);
    }

    [Theory]
    [InlineData(ReconciliationDecisionAction.Revoke)]
    [InlineData(ReconciliationDecisionAction.Replace)]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_stale_predecessors_without_mutation(ReconciliationDecisionAction action)
    {
        var confirmed = await SeedConfirmed();
        var before = await MutationCounts();
        var stale = LedgerId.New().ToString();
        var result = action == ReconciliationDecisionAction.Revoke
            ? await mutationHandler.RevokeAsync(new(confirmed.Seeded.Statement.EvidenceId, stale, ReconciliationAuthorityKind.Owner, "stale"), Actor, "stale", CancellationToken.None)
            : await mutationHandler.ReplaceAsync(new(confirmed.Seeded.Statement.EvidenceId, confirmed.Seeded.Statement.ScopeId, stale, confirmed.Seeded.Candidates[1], ReconciliationAuthorityKind.Owner, "stale"), Actor, "stale", CancellationToken.None);

        AssertError(result, ReconciliationDecisionErrors.StalePredecessor);
        Assert.Equal(before, await MutationCounts());
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_missing_candidate()
    {
        var statement = await SeedStatement();
        var exception = await Apply(statement, ReconciliationApplyDisposition.RecordException, []);

        AssertError(
            await mutationHandler.ConfirmAsync(
                new(statement.EvidenceId, statement.ScopeId, exception.DecisionId, LedgerId.New().ToString(), ReconciliationAuthorityKind.Owner, "missing"),
                Actor,
                "missing",
                CancellationToken.None),
            ReconciliationDecisionErrors.CandidateNotFound);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_inactive_candidate()
    {
        var seeded = await SeedAmbiguous();
        await Terminate(seeded.Candidates[0], "void");

        AssertError(await Confirm(seeded, seeded.Candidates[0]), ReconciliationDecisionErrors.CandidateInactive);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("period")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_incompatible_candidate(string incompatibility)
    {
        var seeded = await SeedAmbiguous();
        var account = incompatibility == "account" ? await SeedAccount() : seeded.Statement.AccountId;
        var date = incompatibility == "period" ? "2026-08-01" : seeded.Statement.TransactionDate;
        var candidate = await SeedTransaction(account, seeded.Statement.AmountMinor, date);

        AssertError(await Confirm(seeded, candidate), ReconciliationDecisionErrors.CandidateIncompatible);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_confirm_requires_selection_from_the_preserved_review_set()
    {
        var seeded = await SeedAmbiguous();
        var laterCandidate = await SeedTransaction(seeded.Statement.AccountId, seeded.Statement.AmountMinor, seeded.Statement.TransactionDate);

        AssertError(await Confirm(seeded, laterCandidate), ReconciliationDecisionErrors.CandidateIncompatible);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_candidate_already_confirmed_by_other_evidence()
    {
        var seeded = await SeedAmbiguous();
        var target = seeded.Candidates[0];
        var otherStatement = await SeedStatement(seeded.Statement.AccountId);
        var projection = await Projection(otherStatement);
        Assert.Contains(projection.ExactCandidates, item => item.TransactionId == target);
        await Apply(otherStatement, ReconciliationApplyDisposition.MatchExisting, projection.ExactCandidates.Concat(projection.GuardCandidates).Select(item => item.TransactionId).ToArray(), target);

        AssertError(await Confirm(seeded, target), ReconciliationDecisionErrors.CandidateAlreadyReconciled);
    }

    [Theory]
    [InlineData(ReconciliationDecisionAction.Confirm)]
    [InlineData(ReconciliationDecisionAction.Reject)]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_link_bearing_state_rejects_confirm_or_reject(ReconciliationDecisionAction action)
    {
        var confirmed = await SeedConfirmed();
        var result = action == ReconciliationDecisionAction.Confirm
            ? await mutationHandler.ConfirmAsync(new(confirmed.Seeded.Statement.EvidenceId, confirmed.Seeded.Statement.ScopeId, confirmed.Confirmation.DecisionId, confirmed.Seeded.Candidates[1], ReconciliationAuthorityKind.Owner, "invalid"), Actor, "invalid", CancellationToken.None)
            : await mutationHandler.RejectAsync(new(confirmed.Seeded.Statement.EvidenceId, confirmed.Seeded.Statement.ScopeId, confirmed.Confirmation.DecisionId, ReconciliationAuthorityKind.Owner, "invalid"), Actor, "invalid", CancellationToken.None);

        AssertError(result, ReconciliationDecisionErrors.TransitionIncompatible);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_ambiguous_state_cannot_be_revoked_without_an_active_link()
    {
        var seeded = await SeedAmbiguous();
        AssertError(
            await mutationHandler.RevokeAsync(new(seeded.Statement.EvidenceId, seeded.Ambiguous.DecisionId, ReconciliationAuthorityKind.Owner, "invalid"), Actor, "invalid", CancellationToken.None),
            ReconciliationDecisionErrors.TransitionIncompatible);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_replace_rejects_the_existing_active_target()
    {
        var confirmed = await SeedConfirmed();
        AssertError(await Replace(confirmed, confirmed.Target), ReconciliationDecisionErrors.CandidateIncompatible);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_same_key_replay_returns_the_original_transition()
    {
        var seeded = await SeedAmbiguous();
        var first = SuccessMutation(await Confirm(seeded, seeded.Candidates[0], "same-key"));
        var replay = SuccessMutation(await Confirm(seeded, seeded.Candidates[0], "same-key"));

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(first.LinkEventId, replay.LinkEventId);
        Assert.Equal(2, (await GetDetail(seeded.Statement.EvidenceId)).History.Count);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_cross_key_exact_replay_returns_the_original_transition()
    {
        var seeded = await SeedAmbiguous();
        var first = SuccessMutation(await Confirm(seeded, seeded.Candidates[0], "first-key"));
        var replay = SuccessMutation(await Confirm(seeded, seeded.Candidates[0], "second-key"));

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(2, (await GetDetail(seeded.Statement.EvidenceId)).History.Count);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_changed_cross_key_replay_conflicts_without_a_second_transition()
    {
        var seeded = await SeedAmbiguous();
        await Confirm(seeded, seeded.Candidates[0], "first-key");

        var changed = await mutationHandler.ConfirmAsync(
            new(seeded.Statement.EvidenceId, seeded.Statement.ScopeId, seeded.Ambiguous.DecisionId, seeded.Candidates[0], ReconciliationAuthorityKind.Owner, "changed reason"),
            Actor,
            "second-key",
            CancellationToken.None);

        AssertError(changed, LedgerMutationExecutor.ConflictCode);
        Assert.Equal(2, (await GetDetail(seeded.Statement.EvidenceId)).History.Count);
    }

    [Fact]
    public async Task DD_LEDGER_IDEMPOTENT_MUTATIONS_write_failure_rolls_back_the_decision_and_preserves_the_active_link()
    {
        var confirmed = await SeedConfirmed();
        var before = await MutationCounts();
        var current = (await GetDetail(confirmed.Seeded.Statement.EvidenceId)).History.Last();
        var request = new IdempotencyRequest(
            "1.0",
            "test.reconciliation.decision.failure",
            "failure-key",
            "owner:dirk:run-1",
            JsonDocument.Parse("{\"transition\":\"revoke\"}").RootElement.Clone(),
            new("decision-failure:" + current.DecisionId, "reconciliation_decision_failure_test"));

        await Assert.ThrowsAsync<InjectedWriteFailureException>(() => executor.ExecuteAsync(
            request,
            async (connection, transaction, token) =>
            {
                await decisionStore.InsertTransitionAsync(
                    connection,
                    transaction,
                    new(
                        LedgerId.New().ToString(),
                        confirmed.Seeded.Statement.EvidenceId,
                        current.DecisionId,
                        confirmed.Target,
                        null,
                        "revoked",
                        "revoked",
                        current.PolicyId,
                        current.PolicyVersion,
                        "injected write failure",
                        current.StatementAuthorityBasis,
                        "owner revoked",
                        "owner:dirk:run-1",
                        At(5)),
                    token);
                throw new InjectedWriteFailureException();
            },
            CancellationToken.None));

        Assert.Equal(before, await MutationCounts());
        var unchanged = await GetDetail(confirmed.Seeded.Statement.EvidenceId);
        Assert.Equal(confirmed.Confirmation.DecisionId, unchanged.CurrentDecisionId);
        Assert.Equal(confirmed.Confirmation.LinkEventId, unchanged.ActiveConfirmingLinkEventId);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("key")]
    [InlineData("reason")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_requires_explicit_owner_context(string missing)
    {
        var seeded = await SeedAmbiguous();
        var result = await mutationHandler.ConfirmAsync(
            new(seeded.Statement.EvidenceId, seeded.Statement.ScopeId, seeded.Ambiguous.DecisionId, seeded.Candidates[0], ReconciliationAuthorityKind.Owner, missing == "reason" ? " " : "owner reason"),
            missing == "actor" ? null : Actor,
            missing == "key" ? null : "key",
            CancellationToken.None);

        AssertError(result, ReconciliationDecisionErrors.InvalidInput);
    }

    [Fact]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_rejects_non_owner_authority_without_mutation()
    {
        var seeded = await SeedAmbiguous();
        var before = await MutationCounts();

        var result = await mutationHandler.ConfirmAsync(
            new(
                seeded.Statement.EvidenceId,
                seeded.Statement.ScopeId,
                seeded.Ambiguous.DecisionId,
                seeded.Candidates[0],
                ReconciliationAuthorityKind.DeterministicPolicy,
                "not owner authority"),
            Actor,
            "not-owner",
            CancellationToken.None);

        AssertError(result, ReconciliationDecisionErrors.InvalidInput);
        Assert.Equal(before, await MutationCounts());
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("rawPayload")]
    [InlineData("recipient")]
    public async Task NFR_LEDGER_LOCAL_PRIVACY_decision_contract_rejects_transport_and_payload_fields(string field)
    {
        var seeded = await SeedAmbiguous();
        var input = new ConfirmReconciliationDecisionInput(
            seeded.Statement.EvidenceId,
            seeded.Statement.ScopeId,
            seeded.Ambiguous.DecisionId,
            seeded.Candidates[0],
            ReconciliationAuthorityKind.Owner,
            "owner reason");
        var json = JsonSerializer.SerializeToElement(input, ReconciliationDecisionJsonContext.Default.ConfirmReconciliationDecisionInput).GetRawText();
        json = json.TrimEnd('}') + $",\"{field}\":\"forbidden\"}}";

        var result = await module.HandleAsync(
            "ledger.reconciliation.decision.confirm",
            new(JsonDocument.Parse(json).RootElement.Clone(), Actor, "privacy-key"),
            CancellationToken.None);

        AssertError(result, ReconciliationDecisionErrors.InvalidInput);
    }

    [Theory]
    [InlineData("void")]
    [InlineData("superseded")]
    public async Task FR_LEDGER_RECONCILIATION_DECISION_LIFECYCLE_inactive_target_derives_exception_without_moving_evidence(string action)
    {
        var confirmed = await SeedConfirmed();
        var activeLink = confirmed.Confirmation.LinkEventId;
        await Terminate(confirmed.Target, action);

        var detail = await GetDetail(confirmed.Seeded.Statement.EvidenceId);

        Assert.Equal(ReconciliationDecisionCurrentState.Exception, detail.CurrentState);
        Assert.True(detail.RequiresOwnerReview);
        Assert.Equal(activeLink, detail.ActiveConfirmingLinkEventId);
        Assert.Equal(confirmed.Target, detail.ActiveTransactionId);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM evidence_active_confirming_target WHERE evidence_id = $id;", ("$id", confirmed.Seeded.Statement.EvidenceId)));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        factory = new(new HostArtifactProtection());
        accountStore = new(database, factory);
        evidenceStore = new(database, factory);
        transactionStore = new(database, factory);
        projectionStore = new(database, factory, evidenceStore, transactionStore);
        executor = new(database, factory, new IdempotencyStore());
        var writeStore = new ReconciliationWriteStore(evidenceStore, transactionStore);
        applyHandler = new(executor, accountStore, projectionStore, writeStore);
        decisionStore = new(database, factory, evidenceStore, transactionStore);
        getHandler = new(decisionStore);
        mutationHandler = new(executor, decisionStore);
        module = new(getHandler, mutationHandler);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<SeededAmbiguous> SeedAmbiguous()
    {
        var statement = await SeedStatement();
        var first = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var second = await SeedTransaction(statement.AccountId, statement.AmountMinor, statement.TransactionDate);
        var ambiguous = await Apply(statement, ReconciliationApplyDisposition.RecordAmbiguous, [second, first]);
        return new(statement, [first, second], ambiguous);
    }

    private async Task<SeededConfirmed> SeedConfirmed()
    {
        var seeded = await SeedAmbiguous();
        var target = seeded.Candidates[0];
        var confirmation = SuccessMutation(await Confirm(seeded, target));
        return new(seeded, target, confirmation);
    }

    private async Task<ReconciliationApplyResult> Apply(
        StatementFixture statement,
        ReconciliationApplyDisposition disposition,
        IReadOnlyList<string> candidates,
        string? target = null)
    {
        var projection = await Projection(statement);
        var result = await applyHandler.HandleAsync(
            new(
                statement.EvidenceId,
                statement.Fingerprint,
                statement.ScopeId,
                projection.AdvisoryToken,
                disposition,
                ReconciliationAuthorityKind.Owner,
                candidates,
                target,
                null,
                disposition == ReconciliationApplyDisposition.RecordException ? "OWNER-REVIEW" : null,
                "owner reviewed statement row"),
            Actor,
            "apply-" + Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult)!;
    }

    private Task<CommandResult<JsonElement>> Confirm(SeededAmbiguous seeded, string target, string key = "confirm-key") =>
        mutationHandler.ConfirmAsync(
            new(seeded.Statement.EvidenceId, seeded.Statement.ScopeId, seeded.Ambiguous.DecisionId, target, ReconciliationAuthorityKind.Owner, "owner confirmed candidate"),
            Actor,
            key,
            CancellationToken.None);

    private Task<CommandResult<JsonElement>> Reject(SeededAmbiguous seeded) =>
        mutationHandler.RejectAsync(
            new(seeded.Statement.EvidenceId, seeded.Statement.ScopeId, seeded.Ambiguous.DecisionId, ReconciliationAuthorityKind.Owner, "owner rejected candidates"),
            Actor,
            "reject-key",
            CancellationToken.None);

    private Task<CommandResult<JsonElement>> Revoke(SeededConfirmed confirmed) =>
        mutationHandler.RevokeAsync(
            new(confirmed.Seeded.Statement.EvidenceId, confirmed.Confirmation.DecisionId, ReconciliationAuthorityKind.Owner, "owner revoked confirmation"),
            Actor,
            "revoke-key",
            CancellationToken.None);

    private Task<CommandResult<JsonElement>> Replace(SeededConfirmed confirmed, string target) =>
        mutationHandler.ReplaceAsync(
            new(confirmed.Seeded.Statement.EvidenceId, confirmed.Seeded.Statement.ScopeId, confirmed.Confirmation.DecisionId, target, ReconciliationAuthorityKind.Owner, "owner replaced confirmation"),
            Actor,
            "replace-key",
            CancellationToken.None);

    private Task<CommandResult<JsonElement>> Get(string evidenceId) =>
        getHandler.HandleAsync(new(evidenceId), CancellationToken.None);

    private async Task<ReconciliationDecisionDetail> GetDetail(string evidenceId) => SuccessDetail(await Get(evidenceId));

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
        Assert.True(AccountDefinition.TryCreate(new("Bank", "Decision " + suffix, AccountType.Cheque, "****" + suffix, "ZAR"), out var account, out _));
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

    private async Task<StatementFixture> SeedStatement(string? existingAccountId = null)
    {
        var accountId = existingAccountId ?? await SeedAccount();
        const long amount = -1234;
        const string transactionDate = "2026-07-10";
        var fingerprint = Digest();
        var observation = new EvidenceObservation(accountId, amount, "ZAR", transactionDate, null, null, null, Digest());
        var input = new RegisterEvidenceInput(EvidenceKind.StatementRow, Digest(), "statement:decision", fingerprint, observation);
        Assert.True(EvidenceIdentity.TryCreate(input, out var identity, out _));
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var evidence = await evidenceStore.RegisterInitialAsync(connection, transaction, identity!, input, "actor", At(2), CancellationToken.None);
        var scopeId = LedgerId.New().ToString();
        await Execute(connection, transaction, """
            INSERT INTO statement_scope(scope_id, account_id, period_start, period_end, manifest_opaque_reference, status, created_by, created_at)
            VALUES ($scopeId, $accountId, '2026-07-01', '2026-07-31', 'statement:decision', 'open', 'actor', $at);
            INSERT INTO statement_scope_evidence(scope_id, evidence_id) VALUES ($scopeId, $evidenceId);
            """, ("$scopeId", scopeId), ("$accountId", accountId), ("$at", At(2)), ("$evidenceId", evidence.EvidenceId));
        await transaction.CommitAsync();
        return new(evidence.EvidenceId, scopeId, accountId, amount, transactionDate, fingerprint);
    }

    private async Task Terminate(string transactionId, string action)
    {
        var detail = (await transactionStore.GetAsync(transactionId, false, CancellationToken.None))!;
        var replacement = action == "superseded"
            ? await SeedTransaction(detail.AccountId, -9999, "2026-08-01")
            : null;
        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Execute(connection, transaction, """
            INSERT INTO transaction_lifecycle_event(
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $action, $replacement, NULL, 'test lifecycle', 'actor', $at);
            """, ("$eventId", LedgerId.New().ToString()), ("$transactionId", transactionId), ("$action", action),
            ("$replacement", replacement ?? (object)DBNull.Value), ("$at", At(4)));
        await transaction.CommitAsync();
    }

    private async Task<IReadOnlyDictionary<string, long>> MutationCounts()
    {
        var tables = new[] { "reconciliation_decision", "reconciliation_decision_authority", "evidence_link_event", "idempotency_record", "logical_effect" };
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables) result.Add(table, await Scalar($"SELECT COUNT(*) FROM {table};"));
        return result;
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

    private static ReconciliationDecisionMutationResult SuccessMutation(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationDecisionJsonContext.Default.ReconciliationDecisionMutationResult)!;
    }

    private static ReconciliationDecisionDetail SuccessDetail(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value!, ReconciliationDecisionJsonContext.Default.ReconciliationDecisionDetail)!;
    }

    private static void AssertError(CommandResult<JsonElement> result, string expected)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorCode);
    }

    private static SafeActor Actor { get; } = new("owner", "dirk", "run-1");
    private static string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
    private static string At(int second) => $"2026-07-22T00:00:{second:D2}Z";
    private sealed record StatementFixture(string EvidenceId, string ScopeId, string AccountId, long AmountMinor, string TransactionDate, string Fingerprint);
    private sealed record SeededAmbiguous(StatementFixture Statement, IReadOnlyList<string> Candidates, ReconciliationApplyResult Ambiguous);
    private sealed record SeededConfirmed(SeededAmbiguous Seeded, string Target, ReconciliationDecisionMutationResult Confirmation);
    private sealed class InjectedWriteFailureException : Exception;
}
