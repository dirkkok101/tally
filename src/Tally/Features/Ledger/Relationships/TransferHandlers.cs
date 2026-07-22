using System.Globalization;
using System.Text.Json;
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

public sealed class ConfirmTransferHandler(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    TransactionStore transactionStore,
    RelationshipStore relationshipStore)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ConfirmTransferInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key) || !TransferPolicy.TryCreateCommand(input, out var command)) return Failure(TransferErrors.Invalid);
        var actorIdentity = Actor(actor);
        var canonicalInput = new ConfirmTransferInput(command!.OutflowTransactionId, command.InflowTransactionId, command.Reason);
        var request = new IdempotencyRequest(
            "1.0", "ledger.transfer.confirm", key, actorIdentity,
            JsonSerializer.SerializeToElement(canonicalInput, LedgerJsonContext.Default.ConfirmTransferInput),
            new LogicalEffectIdentity("transfer:" + command.OutflowTransactionId + ":" + command.InflowTransactionId, "transfer_confirmation"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var outflow = await transactionStore.GetAsync(connection, transaction, command.OutflowTransactionId, false, token);
            var inflow = await transactionStore.GetAsync(connection, transaction, command.InflowTransactionId, false, token);
            if (outflow is null || inflow is null) return Failure(TransactionErrors.NotFound);
            if (outflow.LifecycleStatus != TransactionLifecycleStatus.Active || inflow.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(TransferErrors.TransactionInactive);
            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, outflow.AccountId, token) is { } outflowAccountError) return Failure(outflowAccountError);
            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, inflow.AccountId, token) is { } inflowAccountError) return Failure(inflowAccountError);
            if (!TransferPolicy.TryPrincipal(outflow, inflow, out var principalMinor, out var error)) return Failure(error!);
            if (await relationshipStore.HasActiveRoleAsync(connection, transaction, outflow.TransactionId, inflow.TransactionId, token)) return Failure(TransferErrors.ActiveRoleConflict);

            var relationshipId = LedgerId.New().ToString();
            var createdAt = Now();
            await relationshipStore.InsertAsync(connection, transaction, new(
                relationshipId, FinancialRelationshipType.Transfer,
                outflow.TransactionId, FinancialRelationshipRole.TransferOutflow,
                inflow.TransactionId, FinancialRelationshipRole.TransferInflow,
                principalMinor, actorIdentity, createdAt), token);
            var detail = await relationshipStore.GetAsync(connection, transaction, relationshipId, true, token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail!, LedgerJsonContext.Default.FinancialRelationshipDetail));
        }, cancellationToken);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}

public sealed class GetRelationshipHandler(RelationshipStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetRelationshipInput input, CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.RelationshipId, out _, out _)) return CommandResult<JsonElement>.Failure(TransferErrors.Invalid);
        var detail = await store.GetAsync(input.RelationshipId, input.IncludeHistory, cancellationToken);
        return detail is null
            ? CommandResult<JsonElement>.Failure(TransferErrors.RelationshipNotFound)
            : CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail, LedgerJsonContext.Default.FinancialRelationshipDetail));
    }
}
