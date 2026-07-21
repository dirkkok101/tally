using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Common;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;

namespace Tally.Application;

[SupportedOSPlatform("linux")]
public sealed class LedgerMutationExecutor(LedgerDb database, LedgerConnectionFactory connectionFactory, IdempotencyStore store)
{
    public const string ConflictCode = "LEDGER-IDEMPOTENCY-001";
    public const string ValidationCode = "validation.invalid_input";
    public const string BusyCode = "operation.conflict";

    public async Task<CommandResult<JsonElement>> ExecuteAsync(
        IdempotencyRequest request,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<CommandResult<JsonElement>>> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!IsValid(request))
        {
            return CommandResult<JsonElement>.Failure(ValidationCode);
        }

        var requestIdentity = request.CallerKey;
        var canonicalHash = new CanonicalRequestHasher().Hash(request.ContractVersion, request.OperationId, request.Actor, request.Input);
        try
        {
            await using var connection = await connectionFactory.OpenAsync(database, 1, cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var existing = await store.FindRequestAsync(connection, transaction, requestIdentity, cancellationToken);
                if (existing is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ExistingResult(existing, request, canonicalHash);
                }

                if (request.LogicalEffect is not null)
                {
                    var logical = await store.FindLogicalEffectAsync(connection, transaction, request.LogicalEffect.Value, cancellationToken);
                    if (logical.Record is not null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return logical.EffectType == request.LogicalEffect.EffectType && SameRequest(logical.Record, request, canonicalHash)
                            ? store.Deserialize(logical.Record.StableResult)
                            : CommandResult<JsonElement>.Failure(ConflictCode);
                    }
                }

                var outcome = await mutation(connection, transaction, cancellationToken);
                if (!outcome.IsSuccess)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return outcome;
                }

                await store.CommitAsync(connection, transaction, requestIdentity, request, canonicalHash, outcome, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return outcome;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return CommandResult<JsonElement>.Failure(BusyCode);
        }
    }

    private CommandResult<JsonElement> ExistingResult(StoredIdempotencyRecord existing, IdempotencyRequest request, string canonicalHash) =>
        SameRequest(existing, request, canonicalHash) ? store.Deserialize(existing.StableResult) : CommandResult<JsonElement>.Failure(ConflictCode);

    private static bool SameRequest(StoredIdempotencyRecord existing, IdempotencyRequest request, string canonicalHash) =>
        existing.OperationId == request.ContractVersion + "\n" + request.OperationId && existing.Actor == request.Actor && existing.CanonicalRequestHash == canonicalHash;

    private static bool IsValid(IdempotencyRequest? request) =>
        request is not null
        && !string.IsNullOrWhiteSpace(request.ContractVersion)
        && !string.IsNullOrWhiteSpace(request.OperationId)
        && !string.IsNullOrWhiteSpace(request.CallerKey)
        && !string.IsNullOrWhiteSpace(request.Actor)
        && request.Input.ValueKind is not JsonValueKind.Undefined
        && (request.LogicalEffect is null
            || (!string.IsNullOrWhiteSpace(request.LogicalEffect.Value)
                && !string.IsNullOrWhiteSpace(request.LogicalEffect.EffectType)));
}
