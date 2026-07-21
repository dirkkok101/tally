using System.Text.Json;
using Tally.Bootstrap;
using Tally.Contracts.Common;
using Tally.Contracts.System;

namespace Tally.Cli;

public sealed class TallyProcess(OperationRegistry registry)
{
    private readonly LedgerServices services = LedgerServices.Create();

    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = ExtractInput(arguments);
            if (selection.ErrorCode is not null) return Error(2, selection.ErrorCode, "usage", "The input path must be '-' or '@file'.");
            var invocation = Resolve(selection.Arguments);
            if (invocation.ErrorCode is not null) return Error(invocation.ExitCode, invocation.ErrorCode, invocation.Category!, invocation.Message!);
            var input = await ReadInputAsync(selection, standardInput, cancellationToken);
            if (selection.HasInput && !ValidRequest(input, invocation.Descriptor!)) return Error(3, "validation.invalid_input", "validation", "Input does not match the published schema.");
            var handler = invocation.Descriptor!.HandlerFactory(services, registry);
            var result = await handler.HandleAsync(invocation.HandlerInput, cancellationToken);
            return result.IsSuccess ? Success(invocation.Descriptor.OperationId, result.Value!) : ErrorForHandler(result.ErrorCode!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return UnexpectedFailure(); }
    }

    private Invocation Resolve(IReadOnlyList<string> arguments) => arguments switch
    {
        ["version"] => Invocation.For(registry.Find("system.version")!),
        ["help"] or ["schema", "list"] => Invocation.For(registry.Find("system.schema.list")!),
        ["schema", "show", var operationId] when registry.Find(operationId) is not null => Invocation.For(registry.Find("system.schema.show")!, JsonSerializer.SerializeToElement(new SchemaShowRequest(operationId), LedgerJsonContext.Default.SchemaShowRequest)),
        ["schema", "show", _] => Invocation.Error(4, "operation.not_found", "not_found", "The requested operation is not part of the public contract."),
        _ => Invocation.Error(2, "operation.unknown", "usage", "The requested operation is not part of the public contract.")
    };

    private static InputSelection ExtractInput(IReadOnlyList<string> arguments)
    {
        var index = Enumerable.Range(0, arguments.Count).FirstOrDefault(i => arguments[i] == "--input", -1);
        if (index < 0) return new(arguments, null, false, null);
        if (index + 1 != arguments.Count - 1) return new(arguments, null, true, "usage.invalid_input_path");
        var inputPath = arguments[index + 1];
        if (inputPath != "-" && (!inputPath.StartsWith('@') || inputPath.Length == 1)) return new(arguments, null, true, "usage.invalid_input_path");
        return new(arguments.Take(index).ToArray(), inputPath, true, null);
    }

    private static async Task<string?> ReadInputAsync(InputSelection selection, string? standardInput, CancellationToken cancellationToken) => selection.InputPath switch
    {
        null => standardInput,
        "-" => standardInput,
        var path => await File.ReadAllTextAsync(path![1..], cancellationToken)
    };

    private static bool ValidRequest(string? input, OperationDescriptor descriptor)
    {
        try
        {
            var request = JsonSerializer.Deserialize(input!, LedgerJsonContext.Default.RequestEnvelope);
            return request is not null && request.ContractVersion == "1.0"
                && request.Actor is { Kind: "automation" or "human" or "system" }
                && IsSafeLabel(request.Actor.Label)
                && (request.Actor.RunId is null || IsSafeLabel(request.Actor.RunId))
                && request.Input.ValueKind == JsonValueKind.Object
                && JsonSerializer.Deserialize(request.Input, descriptor.RequestTypeInfo) is not null
                && (descriptor.RequiresIdempotencyKey ? !string.IsNullOrWhiteSpace(request.IdempotencyKey) : request.IdempotencyKey is null);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsSafeLabel(string value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private static ProcessResult Success(string operationId, JsonElement result) => new(0, JsonSerializer.Serialize(new ResultEnvelope("1.0", operationId, "success", result, null), LedgerJsonContext.Default.ResultEnvelope), string.Empty);
    private static ProcessResult Error(int exitCode, string code, string category, string message) => new(exitCode, JsonSerializer.Serialize(new ResultEnvelope("1.0", "system.process", "error", null, new ProcessError(code, category, message)), LedgerJsonContext.Default.ResultEnvelope), "tally: " + code);
    public static ProcessResult UnexpectedFailure() => Error(10, "host.unexpected", "host", "The operation could not be completed.");
    private static ProcessResult ErrorForHandler(string code) => code switch
    {
        "operation.not_found" => Error(4, code, "not_found", "The requested operation is not part of the public contract."),
        "host.unavailable" => Error(9, code, "host", "The requested operation is not available in this foundation."),
        _ => UnexpectedFailure()
    };

    private sealed record InputSelection(IReadOnlyList<string> Arguments, string? InputPath, bool HasInput, string? ErrorCode);
    private sealed record Invocation(OperationDescriptor? Descriptor, JsonElement HandlerInput, int ExitCode, string? ErrorCode, string? Category, string? Message)
    {
        public static Invocation For(OperationDescriptor descriptor, JsonElement? input = null) => new(descriptor, input ?? JsonSerializer.SerializeToElement(new EmptyInput(), LedgerJsonContext.Default.EmptyInput), 0, null, null, null);
        public static Invocation Error(int exitCode, string code, string category, string message) => new(null, default, exitCode, code, category, message);
    }
}
