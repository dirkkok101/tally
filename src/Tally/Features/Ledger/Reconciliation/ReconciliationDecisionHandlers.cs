using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class GetReconciliationDecisionHandler(ReconciliationDecisionStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        GetReconciliationDecisionInput input,
        CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.EvidenceId, out _, out _))
            return CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.InvalidInput);
        var detail = await store.GetAsync(input.EvidenceId, cancellationToken);
        return detail is null
            ? CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.NotFound)
            : CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(detail, ReconciliationDecisionJsonContext.Default.ReconciliationDecisionDetail));
    }
}

public sealed class ReconciliationDecisionMutationHandler(
    LedgerMutationExecutor executor,
    ReconciliationDecisionStore store)
{
    public Task<CommandResult<JsonElement>> ConfirmAsync(
        ConfirmReconciliationDecisionInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        ValidMutationInput(input.EvidenceId, input.ScopeId, input.ExpectedDecisionId, input.TargetTransactionId, input.AuthorityKind, input.Reason, actor, key)
            ? ExecuteAsync(
                ReconciliationDecisionAction.Confirm,
                input.EvidenceId,
                input.ScopeId,
                input.ExpectedDecisionId,
                input.TargetTransactionId,
                input.Reason.Trim(),
                JsonSerializer.SerializeToElement(input with { Reason = input.Reason.Trim() }, ReconciliationDecisionJsonContext.Default.ConfirmReconciliationDecisionInput),
                actor!,
                key!,
                cancellationToken)
            : Invalid();

    public Task<CommandResult<JsonElement>> RejectAsync(
        RejectReconciliationDecisionInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        ValidMutationInput(input.EvidenceId, input.ScopeId, input.ExpectedDecisionId, targetTransactionId: null, input.AuthorityKind, input.Reason, actor, key)
            ? ExecuteAsync(
                ReconciliationDecisionAction.Reject,
                input.EvidenceId,
                input.ScopeId,
                input.ExpectedDecisionId,
                null,
                input.Reason.Trim(),
                JsonSerializer.SerializeToElement(input with { Reason = input.Reason.Trim() }, ReconciliationDecisionJsonContext.Default.RejectReconciliationDecisionInput),
                actor!,
                key!,
                cancellationToken)
            : Invalid();

    public Task<CommandResult<JsonElement>> RevokeAsync(
        RevokeReconciliationDecisionInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        ValidMutationInput(input.EvidenceId, scopeId: null, input.ExpectedDecisionId, targetTransactionId: null, input.AuthorityKind, input.Reason, actor, key)
            ? ExecuteAsync(
                ReconciliationDecisionAction.Revoke,
                input.EvidenceId,
                null,
                input.ExpectedDecisionId,
                null,
                input.Reason.Trim(),
                JsonSerializer.SerializeToElement(input with { Reason = input.Reason.Trim() }, ReconciliationDecisionJsonContext.Default.RevokeReconciliationDecisionInput),
                actor!,
                key!,
                cancellationToken)
            : Invalid();

    public Task<CommandResult<JsonElement>> ReplaceAsync(
        ReplaceReconciliationDecisionInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        ValidMutationInput(input.EvidenceId, input.ScopeId, input.ExpectedDecisionId, input.TargetTransactionId, input.AuthorityKind, input.Reason, actor, key)
            ? ExecuteAsync(
                ReconciliationDecisionAction.Replace,
                input.EvidenceId,
                input.ScopeId,
                input.ExpectedDecisionId,
                input.TargetTransactionId,
                input.Reason.Trim(),
                JsonSerializer.SerializeToElement(input with { Reason = input.Reason.Trim() }, ReconciliationDecisionJsonContext.Default.ReplaceReconciliationDecisionInput),
                actor!,
                key!,
                cancellationToken)
            : Invalid();

    private async Task<CommandResult<JsonElement>> ExecuteAsync(
        ReconciliationDecisionAction action,
        string evidenceId,
        string? scopeId,
        string expectedDecisionId,
        string? targetTransactionId,
        string reason,
        JsonElement canonicalInput,
        SafeActor actor,
        string key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            OperationId(action),
            key,
            actorIdentity,
            canonicalInput,
            new LogicalEffectIdentity(
                $"reconciliation-decision:{evidenceId}:{ActionValue(action)}:{expectedDecisionId}",
                "reconciliation_decision_transition"));

        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var currentDetail = await store.GetAsync(connection, transaction, evidenceId, token);
            if (currentDetail is null) return CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.NotFound);
            if (!string.Equals(currentDetail.CurrentDecisionId, expectedDecisionId, StringComparison.Ordinal))
                return CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.StalePredecessor);

            var current = currentDetail.History.Single(item => item.DecisionId == currentDetail.CurrentDecisionId);
            var activeLink = currentDetail.History.SelectMany(item => item.Links).SingleOrDefault(link => link.IsActive);
            if (ReconciliationStateReducer.ValidateTransition(action, current, activeLink) is { } transitionError)
                return CommandResult<JsonElement>.Failure(transitionError);

            CandidateValidationResult? candidate = null;
            StatementScopeValidationResult? statement = null;
            if (action is ReconciliationDecisionAction.Confirm or ReconciliationDecisionAction.Replace)
            {
                if (action == ReconciliationDecisionAction.Confirm
                    && current.Disposition == ReconciliationDecisionDisposition.Ambiguous
                    && !ReconciliationStateReducer.ContainsReviewedCandidate(current, targetTransactionId!))
                    return CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.CandidateIncompatible);
                if (action == ReconciliationDecisionAction.Replace
                    && string.Equals(current.ActiveTransactionId, targetTransactionId, StringComparison.Ordinal))
                    return CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.CandidateIncompatible);
                candidate = await store.ValidateCandidateAsync(connection, transaction, evidenceId, scopeId!, targetTransactionId!, token);
                if (!candidate.IsSuccess) return CommandResult<JsonElement>.Failure(candidate.ErrorCode!);
            }
            else if (action == ReconciliationDecisionAction.Reject)
            {
                statement = await store.ValidateStatementScopeAsync(connection, transaction, evidenceId, scopeId!, token);
                if (!statement.IsSuccess) return CommandResult<JsonElement>.Failure(statement.ErrorCode!);
            }

            var decisionId = LedgerId.New().ToString();
            var linkId = action == ReconciliationDecisionAction.Reject ? null : LedgerId.New().ToString();
            var occurredAt = Now();
            var shape = Shape(action);
            var priorTransactionId = current.ActiveTransactionId;
            var activeTransactionId = action switch
            {
                ReconciliationDecisionAction.Confirm or ReconciliationDecisionAction.Replace => targetTransactionId,
                _ => null
            };
            var authorityBasis = candidate?.StatementAuthorityBasis
                ?? statement?.StatementAuthorityBasis
                ?? current.StatementAuthorityBasis;
            await store.InsertTransitionAsync(
                connection,
                transaction,
                new(
                    decisionId,
                    evidenceId,
                    current.DecisionId,
                    priorTransactionId,
                    activeTransactionId,
                    shape.BaseDisposition,
                    shape.DetailDisposition,
                    current.PolicyId,
                    current.PolicyVersion,
                    MatchBasis(action, current.DecisionId, priorTransactionId, activeTransactionId),
                    authorityBasis,
                    reason,
                    actorIdentity,
                    occurredAt),
                token);

            if (linkId is not null)
            {
                var linkTransactionId = action == ReconciliationDecisionAction.Revoke
                    ? current.ActiveTransactionId!
                    : targetTransactionId!;
                await store.InsertLinkTransitionAsync(
                    connection,
                    transaction,
                    new(
                        linkId,
                        evidenceId,
                        linkTransactionId,
                        decisionId,
                        LinkAction(action),
                        action == ReconciliationDecisionAction.Confirm ? null : activeLink!.LinkEventId,
                        reason,
                        actorIdentity,
                        occurredAt),
                    token);
            }

            var state = ReconciliationStateReducer.CurrentState(shape.Disposition, activeTransactionIsInactive: false);
            var result = new ReconciliationDecisionMutationResult(
                action,
                evidenceId,
                decisionId,
                current.DecisionId,
                priorTransactionId,
                activeTransactionId,
                linkId,
                state,
                reason);
            return CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(result, ReconciliationDecisionJsonContext.Default.ReconciliationDecisionMutationResult));
        }, cancellationToken);
    }

    private static bool ValidMutationInput(
        string evidenceId,
        string? scopeId,
        string expectedDecisionId,
        string? targetTransactionId,
        ReconciliationAuthorityKind authorityKind,
        string? reason,
        SafeActor? actor,
        string? key) =>
        LedgerId.TryParse(evidenceId, out _, out _)
        && (scopeId is null || LedgerId.TryParse(scopeId, out _, out _))
        && LedgerId.TryParse(expectedDecisionId, out _, out _)
        && (targetTransactionId is null || LedgerId.TryParse(targetTransactionId, out _, out _))
        && authorityKind == ReconciliationAuthorityKind.Owner
        && reason?.Trim() is { Length: > 0 and <= 512 } normalized
        && normalized.All(character => !char.IsControl(character))
        && actor is not null
        && !string.IsNullOrWhiteSpace(key);

    private static (string BaseDisposition, string DetailDisposition, ReconciliationDecisionDisposition Disposition) Shape(
        ReconciliationDecisionAction action) => action switch
        {
            ReconciliationDecisionAction.Confirm => ("owner_confirmed", "owner_confirmed_match", ReconciliationDecisionDisposition.OwnerConfirmedMatch),
            ReconciliationDecisionAction.Reject => ("rejected", "rejected", ReconciliationDecisionDisposition.Rejected),
            ReconciliationDecisionAction.Revoke => ("revoked", "revoked", ReconciliationDecisionDisposition.Revoked),
            ReconciliationDecisionAction.Replace => ("replaced", "replaced", ReconciliationDecisionDisposition.Replaced),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private static string LinkAction(ReconciliationDecisionAction action) => action switch
    {
        ReconciliationDecisionAction.Confirm => "link",
        ReconciliationDecisionAction.Revoke => "revoke",
        ReconciliationDecisionAction.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string MatchBasis(
        ReconciliationDecisionAction action,
        string previousDecisionId,
        string? priorTransactionId,
        string? activeTransactionId) =>
        $"action={ActionValue(action)};previous={previousDecisionId};prior={priorTransactionId ?? "none"};active={activeTransactionId ?? "none"}";

    private static string OperationId(ReconciliationDecisionAction action) =>
        "ledger.reconciliation.decision." + ActionValue(action);
    private static string ActionValue(ReconciliationDecisionAction action) => action.ToString().ToLowerInvariant();
    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static Task<CommandResult<JsonElement>> Invalid() =>
        Task.FromResult(CommandResult<JsonElement>.Failure(ReconciliationDecisionErrors.InvalidInput));
}
