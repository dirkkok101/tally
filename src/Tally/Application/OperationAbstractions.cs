using System.Text.Json;

namespace Tally.Application;

public interface ICommandHandler<in TCommand, TResult>
{
    Task<CommandResult<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

public sealed record CommandResult<TResult>(TResult? Value, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
    public static CommandResult<TResult> Success(TResult value) => new(value, null);
    public static CommandResult<TResult> Failure(string errorCode) => new(default, errorCode);
}

public interface IOperationHandler
{
    Task<CommandResult<JsonElement>> HandleAsync(JsonElement input, CancellationToken cancellationToken);
}
