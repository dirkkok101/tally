using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationApplyHandler(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    ReconciliationProjectionStore projectionStore,
    ReconciliationWriteStore writeStore)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        ReconciliationApplyInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key))
        {
            return CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.InvalidInput);
        }

        if (!ReconciliationDispositionPolicy.TryNormalize(input, out var normalized, out var validationError))
        {
            return CommandResult<JsonElement>.Failure(validationError!);
        }

        var advisoryRead = await projectionStore.ReadAsync(normalized!.EvidenceId, normalized.ScopeId, cancellationToken);
        if (!advisoryRead.IsSuccess) return CommandResult<JsonElement>.Failure(advisoryRead.ErrorCode!);

        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            ReconciliationApplyOperationModule.OperationId,
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(normalized.CanonicalInput(), ReconciliationApplyJsonContext.Default.ReconciliationApplyInput),
            new LogicalEffectIdentity("reconciliation:" + normalized.EvidenceId, "reconciliation_apply"));

        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var currentRead = await writeStore.ReadProjectionSourceAsync(
                connection,
                transaction,
                normalized.EvidenceId,
                normalized.ScopeId,
                token);
            if (!currentRead.IsSuccess) return CommandResult<JsonElement>.Failure(currentRead.ErrorCode!);
            var currentProjection = ManualReviewProjectionV1.Project(currentRead.Source!);
            if (ReconciliationDispositionPolicy.ValidateProjection(normalized, currentRead.Source!, currentProjection) is { } currentError)
                return CommandResult<JsonElement>.Failure(currentError);

            var evidence = await writeStore.GetEvidenceAsync(connection, transaction, normalized.EvidenceId, token);
            if (evidence is null) return CommandResult<JsonElement>.Failure(ReconciliationProjectionErrors.EvidenceNotFound);

            var decisionId = LedgerId.New().ToString();
            var transactionId = normalized.TargetTransactionId;
            string? linkEventId = null;
            string? exceptionId = null;
            var createdStatementOnly = false;
            var occurredAt = Now();

            if (normalized.Disposition == ReconciliationApplyDisposition.CreateStatementOnly)
            {
                if (!ReconciliationDispositionPolicy.TryCreateStatementTransactionFact(normalized.StatementFact!, evidence, out var fact))
                    return CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.StatementFactMismatch);
                if (await accountStore.ActiveWriteErrorAsync(connection, transaction, fact!.AccountId, token) is { } accountError)
                    return CommandResult<JsonElement>.Failure(accountError);
                transactionId = LedgerId.New().ToString();
                await writeStore.InsertStatementOnlyTransactionAsync(
                    connection,
                    transaction,
                    transactionId,
                    LedgerId.New().ToString(),
                    LedgerId.New().ToString(),
                    fact,
                    actorIdentity,
                    occurredAt,
                    token);
                createdStatementOnly = true;
            }

            var shape = Shape(normalized.Disposition, normalized.AuthorityKind);
            var automaticDecision = normalized.AuthorityKind == ReconciliationAuthorityKind.DeterministicPolicy
                ? ReconciliationPolicyV1.Evaluate(currentRead.Source!, currentProjection)
                : null;
            var policyId = automaticDecision?.PolicyId ?? currentProjection.PolicyId;
            var policyVersion = automaticDecision?.PolicyVersion ?? currentProjection.PolicyVersion;
            var matchBasis = automaticDecision?.MatchBasis ?? MatchBasis(normalized, currentProjection);
            var statementAuthorityBasis = $"scope:{normalized.ScopeId}|evidence:{normalized.EvidenceFingerprint}";
            await writeStore.InsertDecisionAsync(
                connection,
                transaction,
                new(
                    decisionId,
                    normalized.EvidenceId,
                    transactionId,
                    shape.BaseDisposition,
                    policyId,
                    policyVersion,
                    matchBasis,
                    normalized.Reason,
                    actorIdentity,
                    occurredAt,
                    normalized.AuthorityKind == ReconciliationAuthorityKind.DeterministicPolicy),
                token);
            await writeStore.InsertDecisionAuthorityAsync(
                connection,
                transaction,
                new(
                    decisionId,
                    shape.DetailDisposition,
                    transactionId,
                    statementAuthorityBasis,
                    occurredAt,
                    normalized.AuthorityKind),
                token);

            if (normalized.Disposition is ReconciliationApplyDisposition.MatchExisting or ReconciliationApplyDisposition.CreateStatementOnly)
            {
                linkEventId = LedgerId.New().ToString();
                await writeStore.InsertConfirmingLinkAsync(
                    connection,
                    transaction,
                    linkEventId,
                    normalized.EvidenceId,
                    transactionId!,
                    decisionId,
                    normalized.Reason,
                    actorIdentity,
                    occurredAt,
                    token);
            }
            else
            {
                exceptionId = LedgerId.New().ToString();
                var exceptionReason = normalized.ExceptionCode is null
                    ? normalized.Reason
                    : normalized.ExceptionCode + ": " + normalized.Reason;
                await writeStore.InsertExceptionAsync(
                    connection,
                    transaction,
                    exceptionId,
                    normalized.ScopeId,
                    normalized.EvidenceId,
                    shape.BaseDisposition,
                    exceptionReason,
                    decisionId,
                    actorIdentity,
                    occurredAt,
                    token);
            }

            var result = new ReconciliationApplyResult(
                decisionId,
                normalized.EvidenceId,
                normalized.ScopeId,
                normalized.Disposition,
                normalized.AuthorityKind,
                transactionId,
                createdStatementOnly,
                linkEventId,
                exceptionId,
                normalized.ExceptionCode,
                normalized.ReviewedCandidateIds,
                normalized.Reason,
                policyId,
                policyVersion,
                currentProjection.AdvisoryToken);
            return CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(result, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult));
        }, cancellationToken);
    }

    private static (string BaseDisposition, string DetailDisposition) Shape(
        ReconciliationApplyDisposition disposition,
        ReconciliationAuthorityKind authorityKind) => (disposition, authorityKind) switch
        {
            (ReconciliationApplyDisposition.MatchExisting, ReconciliationAuthorityKind.DeterministicPolicy) => ("deterministic_match", "confirmed_existing"),
            (ReconciliationApplyDisposition.MatchExisting, _) => ("owner_confirmed", "owner_confirmed_match"),
            (ReconciliationApplyDisposition.CreateStatementOnly, _) => ("statement_only", "statement_only"),
            (ReconciliationApplyDisposition.RecordAmbiguous, _) => ("ambiguous", "ambiguous"),
            (ReconciliationApplyDisposition.RecordException, _) => ("exception", "exception"),
            _ => throw new InvalidOperationException("Unsupported reconciliation disposition reached the write boundary.")
        };

    private static string MatchBasis(NormalizedReconciliationApply input, ReconciliationProjectionResult projection) =>
        $"policy={projection.PolicyId}:{projection.PolicyVersion};token={projection.AdvisoryToken};candidates={string.Join(',', input.ReviewedCandidateIds)};target={input.TargetTransactionId ?? "none"};exception={input.ExceptionCode ?? "none"}";

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
