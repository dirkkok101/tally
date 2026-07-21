using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.System;

namespace Tally.Features.System.Contract;

public sealed class SystemOperationModule
{
    public Task<CommandResult<JsonElement>> VersionAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new VersionResult("tally", "1.0", "1.0"), LedgerJsonContext.Default.VersionResult)));
    }

    public Task<CommandResult<JsonElement>> ListAsync(IReadOnlyList<OperationSchema> operations, JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new SchemaListResult("1.0", operations), LedgerJsonContext.Default.SchemaListResult)));
    }

    public Task<CommandResult<JsonElement>> ShowAsync(OperationSchema? operation, JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(operation is null
            ? CommandResult<JsonElement>.Failure("operation.not_found")
            : CommandResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new SchemaShowResult(operation), LedgerJsonContext.Default.SchemaShowResult)));
    }
}
