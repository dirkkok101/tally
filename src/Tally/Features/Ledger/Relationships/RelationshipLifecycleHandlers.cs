using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Relationships;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Relationships;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Relationships;

public sealed class RelationshipLifecycleHandler(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    TransactionStore transactionStore,
    RelationshipStore relationshipStore)
{
    public Task<CommandResult<JsonElement>> RevokeTransferAsync(
        RevokeRelationshipInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        RevokeAsync(input, FinancialRelationshipType.Transfer, "ledger.transfer.revoke", actor, key, cancellationToken);

    public Task<CommandResult<JsonElement>> RevokeRefundAsync(
        RevokeRelationshipInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        RevokeAsync(input, FinancialRelationshipType.Refund, "ledger.refund.revoke", actor, key, cancellationToken);

    public Task<CommandResult<JsonElement>> ReplaceTransferAsync(
        ReplaceTransferInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        RelationshipLifecycle.TryReplace(input, out var proposal)
            ? ReplaceAsync(
                input with { Reason = proposal!.Reason },
                LedgerJsonContext.Default.ReplaceTransferInput,
                proposal,
                "ledger.transfer.replace",
                actor,
                key,
                cancellationToken)
            : Task.FromResult(Failure(RelationshipLifecycleErrors.Invalid));

    public Task<CommandResult<JsonElement>> ReplaceRefundAsync(
        ReplaceRefundInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken) =>
        RelationshipLifecycle.TryReplace(input, out var proposal)
            ? ReplaceAsync(
                input with { Reason = proposal!.Reason },
                LedgerJsonContext.Default.ReplaceRefundInput,
                proposal,
                "ledger.refund.replace",
                actor,
                key,
                cancellationToken)
            : Task.FromResult(Failure(RelationshipLifecycleErrors.Invalid));

    private Task<CommandResult<JsonElement>> RevokeAsync(
        RevokeRelationshipInput input,
        FinancialRelationshipType expectedType,
        string operationId,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key) || !RelationshipLifecycle.TryRevoke(input, out var reason))
        {
            return Task.FromResult(Failure(RelationshipLifecycleErrors.Invalid));
        }

        var actorIdentity = Actor(actor);
        var canonicalInput = input with { Reason = reason };
        var request = new IdempotencyRequest(
            "1.0",
            operationId,
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(canonicalInput, LedgerJsonContext.Default.RevokeRelationshipInput),
            new("relationship-revoke:" + input.RelationshipId, "relationship_revoke"));
        return executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await relationshipStore.GetAsync(connection, transaction, input.RelationshipId, true, token);
            if (current is null) return Failure(RelationshipLifecycleErrors.NotFound);
            if (current.State == FinancialRelationshipState.Retired) return Failure(RelationshipLifecycleErrors.AlreadyRetired);
            if (current.Type != expectedType) return Failure(RelationshipLifecycleErrors.TypeMismatch);

            var lifecycleEventId = LedgerId.New().ToString();
            await relationshipStore.RevokeAsync(
                connection,
                transaction,
                input.RelationshipId,
                lifecycleEventId,
                reason,
                actorIdentity,
                Now(),
                token);
            var retired = await relationshipStore.GetAsync(connection, transaction, input.RelationshipId, true, token);
            return Success(new(retired!, lifecycleEventId, null));
        }, cancellationToken);
    }

    private Task<CommandResult<JsonElement>> ReplaceAsync<T>(
        T input,
        JsonTypeInfo<T> inputType,
        RelationshipReplacementProposal proposal,
        string operationId,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key))
        {
            return Task.FromResult(Failure(RelationshipLifecycleErrors.Invalid));
        }

        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            operationId,
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(input, inputType),
            new("relationship-replace:" + proposal.RelationshipId, "relationship_replacement"));
        return executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var current = await relationshipStore.GetAsync(connection, transaction, proposal.RelationshipId, false, token);
            if (current is null) return Failure(RelationshipLifecycleErrors.NotFound);
            if (current.State == FinancialRelationshipState.Retired) return Failure(RelationshipLifecycleErrors.AlreadyRetired);
            if (current.Type != proposal.Type) return Failure(RelationshipLifecycleErrors.TypeMismatch);

            var source = await transactionStore.GetAsync(connection, transaction, proposal.SourceTransactionId, false, token);
            var target = await transactionStore.GetAsync(connection, transaction, proposal.TargetTransactionId, false, token);
            if (source is null || target is null) return Failure(TransactionErrors.NotFound);
            if (source.LifecycleStatus != TransactionLifecycleStatus.Active || target.LifecycleStatus != TransactionLifecycleStatus.Active)
            {
                return Failure(TransactionInactiveError(proposal.Type));
            }

            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, source.AccountId, token) is { } sourceAccountError)
            {
                return Failure(sourceAccountError);
            }
            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, target.AccountId, token) is { } targetAccountError)
            {
                return Failure(targetAccountError);
            }

            if (!TryPrincipal(proposal.Type, source, target, out var principalMinor, out var error)) return Failure(error!);
            if (await relationshipStore.HasActiveRoleExceptAsync(
                    connection,
                    transaction,
                    proposal.SourceTransactionId,
                    proposal.TargetTransactionId,
                    proposal.RelationshipId,
                    token))
            {
                return Failure(TransferErrors.ActiveRoleConflict);
            }

            var occurredAt = Now();
            var lifecycleEventId = LedgerId.New().ToString();
            var replacement = new FinancialRelationship(
                LedgerId.New().ToString(),
                proposal.Type,
                proposal.SourceTransactionId,
                SourceRole(proposal.Type),
                proposal.TargetTransactionId,
                TargetRole(proposal.Type),
                principalMinor,
                actorIdentity,
                occurredAt);
            await relationshipStore.ReplaceAsync(
                connection,
                transaction,
                proposal.RelationshipId,
                replacement,
                lifecycleEventId,
                proposal.Reason,
                actorIdentity,
                occurredAt,
                null,
                token);

            var retired = await relationshipStore.GetAsync(connection, transaction, proposal.RelationshipId, true, token);
            var activeReplacement = await relationshipStore.GetAsync(connection, transaction, replacement.RelationshipId, true, token);
            return Success(new(retired!, lifecycleEventId, activeReplacement));
        }, cancellationToken);
    }

    private static bool TryPrincipal(
        FinancialRelationshipType type,
        TransactionDetail source,
        TransactionDetail target,
        out long principalMinor,
        out string? error) =>
        type == FinancialRelationshipType.Transfer
            ? TransferPolicy.TryPrincipal(source, target, out principalMinor, out error)
            : RefundPolicy.TryFullAmount(source, target, out principalMinor, out error);

    private static FinancialRelationshipRole SourceRole(FinancialRelationshipType type) =>
        type == FinancialRelationshipType.Transfer
            ? FinancialRelationshipRole.TransferOutflow
            : FinancialRelationshipRole.RefundOriginal;

    private static FinancialRelationshipRole TargetRole(FinancialRelationshipType type) =>
        type == FinancialRelationshipType.Transfer
            ? FinancialRelationshipRole.TransferInflow
            : FinancialRelationshipRole.RefundCredit;

    private static string TransactionInactiveError(FinancialRelationshipType type) =>
        type == FinancialRelationshipType.Transfer ? TransferErrors.TransactionInactive : RefundErrors.TransactionInactive;

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static string Now() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static CommandResult<JsonElement> Success(RelationshipLifecycleResult result) =>
        CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(result, LedgerJsonContext.Default.RelationshipLifecycleResult));

    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
