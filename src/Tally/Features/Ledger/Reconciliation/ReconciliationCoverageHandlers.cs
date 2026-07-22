using System.Globalization;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class CompleteStatementCoverageHandler(
    LedgerMutationExecutor executor,
    ReconciliationCoverageStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        CompleteStatementCoverageInput input,
        SafeActor? actor,
        string? key,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (actor is null || string.IsNullOrWhiteSpace(key))
            return CommandResult<JsonElement>.Failure(ReconciliationCoverageErrors.InvalidInput);
        if (!StatementCoveragePolicy.TryNormalize(input, out var normalized, out var validationError))
            return CommandResult<JsonElement>.Failure(validationError!);

        var actorIdentity = Actor(actor);
        var request = new IdempotencyRequest(
            "1.0",
            ReconciliationCoverageOperationModule.CompleteOperationId,
            key,
            actorIdentity,
            JsonSerializer.SerializeToElement(
                normalized!.CanonicalInput(),
                ReconciliationCoverageJsonContext.Default.CompleteStatementCoverageInput),
            new LogicalEffectIdentity("statement-coverage:" + normalized.ScopeId, "statement_coverage_completion"));
        return await executor.ExecuteAsync(request, async (connection, transaction, token) =>
        {
            var preparation = await store.PrepareAsync(connection, transaction, normalized, token);
            if (!preparation.IsSuccess) return CommandResult<JsonElement>.Failure(preparation.ErrorCode!);
            await store.InsertCompletionAsync(connection, transaction, preparation, actorIdentity, Now(), token);
            var summary = await store.GetAsync(connection, transaction, normalized.ScopeId, token)
                ?? throw new InvalidOperationException("Completed coverage could not be read inside its write transaction.");
            return CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(summary, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary));
        }, cancellationToken);
    }

    private static string Actor(SafeActor actor) => actor.RunId is null
        ? actor.Kind + ":" + actor.Label
        : actor.Kind + ":" + actor.Label + ":" + actor.RunId;

    private static string Now() => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

public sealed class GetStatementCoverageHandler(ReconciliationCoverageStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        GetStatementCoverageInput input,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!StatementCoveragePolicy.TryNormalizeGet(input, out var scopeId))
            return CommandResult<JsonElement>.Failure(ReconciliationCoverageErrors.InvalidInput);
        var summary = await store.GetAsync(scopeId!, cancellationToken);
        return summary is null
            ? CommandResult<JsonElement>.Failure(ReconciliationCoverageErrors.NotFound)
            : CommandResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(summary, ReconciliationCoverageJsonContext.Default.StatementCoverageSummary));
    }
}
