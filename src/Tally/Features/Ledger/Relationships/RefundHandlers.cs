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

public sealed class ConfirmRefundHandler(
    LedgerMutationExecutor executor,
    AccountStore accountStore,
    TransactionStore transactionStore,
    RelationshipStore relationshipStore)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(ConfirmRefundInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key) || !RefundPolicy.TryCreateCommand(input, out var command)) return Failure(RefundErrors.Invalid);
        var actorIdentity = Actor(actor);
        var canonicalInput = new ConfirmRefundInput(command!.OriginalTransactionId, command.RefundTransactionId, command.Reason);
        var request = new IdempotencyRequest(
            "1.0", "ledger.refund.confirm", key, actorIdentity,
            JsonSerializer.SerializeToElement(canonicalInput, LedgerJsonContext.Default.ConfirmRefundInput),
            new LogicalEffectIdentity("refund:" + command.OriginalTransactionId + ":" + command.RefundTransactionId, "refund_confirmation"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var original = await transactionStore.GetAsync(connection, transaction, command.OriginalTransactionId, false, token);
            var refund = await transactionStore.GetAsync(connection, transaction, command.RefundTransactionId, false, token);
            if (original is null || refund is null) return Failure(TransactionErrors.NotFound);
            if (original.LifecycleStatus != TransactionLifecycleStatus.Active || refund.LifecycleStatus != TransactionLifecycleStatus.Active) return Failure(RefundErrors.TransactionInactive);
            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, original.AccountId, token) is { } originalAccountError) return Failure(originalAccountError);
            if (await accountStore.ActiveWriteErrorAsync(connection, transaction, refund.AccountId, token) is { } refundAccountError) return Failure(refundAccountError);
            if (!RefundPolicy.TryFullAmount(original, refund, out var amountMinor, out var error)) return Failure(error!);
            if (await relationshipStore.HasActiveRoleAsync(connection, transaction, original.TransactionId, refund.TransactionId, token)) return Failure(RefundErrors.ActiveRoleConflict);

            var relationshipId = LedgerId.New().ToString();
            var createdAt = Now();
            await relationshipStore.InsertAsync(connection, transaction, new(
                relationshipId, FinancialRelationshipType.Refund,
                original.TransactionId, FinancialRelationshipRole.RefundOriginal,
                refund.TransactionId, FinancialRelationshipRole.RefundCredit,
                amountMinor, actorIdentity, createdAt), token);
            var detail = await relationshipStore.GetAsync(connection, transaction, relationshipId, true, token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail!, LedgerJsonContext.Default.FinancialRelationshipDetail));
        }, cancellationToken);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static CommandResult<JsonElement> Failure(string error) => CommandResult<JsonElement>.Failure(error);
}
