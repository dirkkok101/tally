using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class StatementAuthoritativeCorrectionCoordinator(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    ReconciliationProjectionStore projectionStore,
    ReconciliationWriteStore writeStore,
    TransactionStore transactionStore,
    StatementCorrectionEffectWriter effectWriter)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        ReconciliationApplyInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key))
            return CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.InvalidInput);
        if (!StatementAuthorityPolicy.TryNormalize(input, out var normalized, out var validationError))
            return CommandResult<JsonElement>.Failure(validationError!);

        var advisoryRead = await projectionStore.ReadAsync(normalized!.EvidenceId, normalized.ScopeId, cancellationToken);
        if (!advisoryRead.IsSuccess) return CommandResult<JsonElement>.Failure(advisoryRead.ErrorCode!);

        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            ReconciliationApplyOperationModule.OperationId,
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(
                normalized.CanonicalInput(),
                ReconciliationApplyJsonContext.Default.ReconciliationApplyInput),
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
            if (StatementAuthorityPolicy.ValidateProjection(normalized, currentProjection) is { } projectionError)
                return CommandResult<JsonElement>.Failure(projectionError);

            var evidence = await writeStore.GetEvidenceAsync(connection, transaction, normalized.EvidenceId, token);
            if (evidence is null) return CommandResult<JsonElement>.Failure(ReconciliationProjectionErrors.EvidenceNotFound);
            if (!StatementAuthorityPolicy.TryCreateStatementTransactionFact(normalized.StatementFact, evidence, out var statementFact))
                return CommandResult<JsonElement>.Failure(ReconciliationApplyErrors.StatementFactMismatch);
            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, statementFact!.AccountId, token) is { } accountError)
                return CommandResult<JsonElement>.Failure(accountError);

            var prior = await transactionStore.GetAsync(
                connection,
                transaction,
                normalized.TargetTransactionId,
                includeHistory: false,
                token);
            if (TransactionLifecycle.ValidateActive(prior) is { } lifecycleError)
                return CommandResult<JsonElement>.Failure(lifecycleError);

            var relationshipIds = await ActiveRelationshipIdsAsync(
                connection,
                transaction,
                normalized.TargetTransactionId,
                token);
            var decisionId = LedgerId.New().ToString();
            var replacementId = LedgerId.New().ToString();
            var correctionId = LedgerId.New().ToString();
            var lifecycleEventId = LedgerId.New().ToString();
            var confirmingLinkEventId = LedgerId.New().ToString();
            var occurredAt = Now();
            var matchBasis = StatementAuthorityPolicy.MatchBasis(normalized, currentProjection);
            var statementAuthorityBasis = $"scope:{normalized.ScopeId}|evidence:{normalized.EvidenceFingerprint}";
            var effect = await effectWriter.AppendAsync(
                connection,
                transaction,
                new(
                    correctionId,
                    decisionId,
                    confirmingLinkEventId,
                    lifecycleEventId,
                    normalized.EvidenceId,
                    normalized.TargetTransactionId,
                    replacementId,
                    statementFact,
                    null,
                    null,
                    currentProjection.PolicyId,
                    currentProjection.PolicyVersion,
                    matchBasis,
                    statementAuthorityBasis,
                    normalized.Reason,
                    actorIdentity,
                    occurredAt,
                    relationshipIds),
                token);
            if (!effect.IsSuccess)
            {
                return CommandResult<JsonElement>.Failure(
                    effect.ReviewRequired
                        ? ReconciliationApplyErrors.ReviewRequired
                        : effect.ErrorCode ?? StatementCorrectionEffectErrors.Conflict);
            }

            var correction = new StatementCorrectionApplyResult(
                effect.CorrectionId!,
                normalized.TargetTransactionId,
                effect.ReplacementTransactionId!,
                effect.SupersessionLifecycleEventId!,
                effect.CategoryAllocationEventId,
                effect.PoolAssignmentEventId!,
                effect.AttributionEventId!,
                effect.PaymentResolution!.Value,
                effect.RelationshipLifecycleEventIds);
            var result = new ReconciliationApplyResult(
                effect.DecisionId!,
                normalized.EvidenceId,
                normalized.ScopeId,
                ReconciliationApplyDisposition.CorrectExistingFromStatement,
                normalized.AuthorityKind,
                effect.ReplacementTransactionId,
                false,
                effect.ConfirmingLinkEventId,
                null,
                null,
                normalized.ReviewedCandidateIds,
                normalized.Reason,
                currentProjection.PolicyId,
                currentProjection.PolicyVersion,
                currentProjection.AdvisoryToken,
                correction);
            return CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(result, ReconciliationApplyJsonContext.Default.ReconciliationApplyResult));
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ActiveRelationshipIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string transactionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT relationship_id
            FROM financial_relationship_current
            WHERE state = 'active'
              AND (source_transaction_id = $transactionId OR target_transaction_id = $transactionId)
            ORDER BY relationship_id;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static string Now() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
