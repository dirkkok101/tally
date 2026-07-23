using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class RegisterReconciliationScopeHandler(LedgerMutationExecutor executor, ReconciliationScopeStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(RegisterReconciliationScopeInput input, SafeActor? actor, string? key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key))
            return CommandResult<JsonElement>.Failure(ReconciliationScopeErrors.InvalidInput);
        if (!StatementScopeRegistrationPolicy.TryNormalize(input, out var normalized, out var error))
            return CommandResult<JsonElement>.Failure(error!);
        var actorIdentity = actor.RunId is null ? actor.Kind + ":" + actor.Label : actor.Kind + ":" + actor.Label + ":" + actor.RunId;
        var request = new IdempotencyRequest("1.0", ReconciliationScopeOperationModule.RegisterOperationId, key, actorIdentity,
            JsonSerializer.SerializeToElement(normalized!.CanonicalInput(), ReconciliationScopeJsonContext.Default.RegisterReconciliationScopeInput),
            new LogicalEffectIdentity("statement-scope:" + normalized.AccountId + ":" + normalized.PeriodStart + ":" + normalized.PeriodEnd, "statement_scope_registration"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var validation = await store.ValidateAsync(connection, transaction, normalized, token);
            if (validation is not null) return CommandResult<JsonElement>.Failure(validation);
            var scope = await store.InsertAsync(connection, transaction, LedgerId.New().ToString(), normalized, actorIdentity,
                DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture), token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(scope, ReconciliationScopeJsonContext.Default.ReconciliationScopeDetail));
        }, cancellationToken);
    }
}
