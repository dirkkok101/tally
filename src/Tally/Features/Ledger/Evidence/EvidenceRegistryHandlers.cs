using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Evidence;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Evidence;
using Tally.Infrastructure.Storage.Evidence;

namespace Tally.Features.Ledger.Evidence;

public sealed class RegisterEvidenceHandler(LedgerMutationExecutor executor, EvidenceStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(RegisterEvidenceInput input, SafeActor? actor, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<JsonElement>.Failure(EvidenceIdentity.InvalidEvidenceError);
        }

        if (!EvidenceIdentity.TryCreate(input, out var identity, out var error))
        {
            return CommandResult<JsonElement>.Failure(error!);
        }

        var actorIdentity = ActorIdentity(actor);
        var request = new IdempotencyRequest(
            "1.0",
            "ledger.evidence.register",
            idempotencyKey,
            actorIdentity,
            JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RegisterEvidenceInput),
            new LogicalEffectIdentity("evidence:" + identity.LogicalIdentityDigest, "evidence"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            if (!await store.ObservationReferencesExistAsync(connection, transaction, input.Observation, token))
            {
                return CommandResult<JsonElement>.Failure("operation.not_found");
            }

            var detail = await store.RegisterInitialAsync(connection, transaction, identity, input, actorIdentity, TrustedNow(), token);
            return CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail, LedgerJsonContext.Default.EvidenceRecordDetail));
        }, cancellationToken);
    }

    private static string ActorIdentity(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static string TrustedNow() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

public sealed class GetEvidenceHandler(EvidenceStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(GetEvidenceInput input, CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.EvidenceId, out _, out _)) return CommandResult<JsonElement>.Failure("validation.invalid_input");
        var detail = await store.GetAsync(input.EvidenceId, cancellationToken);
        return detail is null
            ? CommandResult<JsonElement>.Failure("operation.not_found")
            : CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(detail, LedgerJsonContext.Default.EvidenceRecordDetail));
    }
}
