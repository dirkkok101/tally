using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Contracts.Common;
using Tally.Contracts.System;

namespace Tally.Cli;

public sealed class TallyProcess(OperationRegistry registry, LedgerServices? configuredServices = null)
{
    private readonly LedgerServices services = configuredServices ?? LedgerServices.Create();

    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = ExtractInput(arguments);
            if (selection.ErrorCode is not null) return Error(2, selection.ErrorCode, "usage", "The input path must be '-' or '@file'.");
            var invocation = Resolve(selection.Arguments);
            if (invocation.ErrorCode is not null) return Error(invocation.ExitCode, invocation.ErrorCode, invocation.Category!, invocation.Message!);
            if (invocation.UseRequestInput && !selection.HasInput) return Error(3, "validation.invalid_input", "validation", "Input does not match the published schema.");
            var input = await ReadInputAsync(selection, standardInput, cancellationToken);
            var requestEnvelope = selection.HasInput ? ReadRequest(input) : null;
            if (selection.HasInput && !ValidRequest(requestEnvelope, invocation.Descriptor!)) return Error(3, "validation.invalid_input", "validation", "Input does not match the published schema.");
            var handler = invocation.Descriptor!.HandlerFactory(services, registry);
            var request = new OperationRequest(invocation.UseRequestInput ? requestEnvelope!.Input : invocation.HandlerInput, requestEnvelope?.Actor, requestEnvelope?.IdempotencyKey);
            var result = await handler.HandleAsync(request, cancellationToken);
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
        _ when registry.FindByArguments(arguments) is { } descriptor => Invocation.For(descriptor, useRequestInput: true),
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

    private static RequestEnvelope? ReadRequest(string? input)
    {
        if (input is null) return null;
        try { return JsonSerializer.Deserialize(input!, LedgerJsonContext.Default.RequestEnvelope); }
        catch (JsonException) { return null; }
    }

    private static bool ValidRequest(RequestEnvelope? request, OperationDescriptor descriptor)
    {
        try
        {
            return request is not null && request.ContractVersion == "1.0"
                && request.Actor is { Kind: "automation" or "human" or "system" }
                && IsSafeLabel(request.Actor.Label)
                && (request.Actor.RunId is null || IsSafeLabel(request.Actor.RunId))
                && request.Input.ValueKind == JsonValueKind.Object
                && JsonSerializer.Deserialize(request.Input, descriptor.RequestTypeInfo) is not null
                && (descriptor.RequiresIdempotencyKey ? !string.IsNullOrWhiteSpace(request.IdempotencyKey) : request.IdempotencyKey is null);
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsSafeLabel(string value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private static ProcessResult Success(string operationId, JsonElement result) => new(0, JsonSerializer.Serialize(new ResultEnvelope("1.0", operationId, "success", result, null), LedgerJsonContext.Default.ResultEnvelope), string.Empty);
    private static ProcessResult Error(int exitCode, string code, string category, string message) => new(exitCode, JsonSerializer.Serialize(new ResultEnvelope("1.0", "system.process", "error", null, new ProcessError(code, category, message)), LedgerJsonContext.Default.ResultEnvelope), "tally: " + code);
    public static ProcessResult UnexpectedFailure() => Error(10, "host.unexpected", "host", "The operation could not be completed.");
    private static ProcessResult ErrorForHandler(string code) => code switch
    {
        "operation.not_found" => Error(4, code, "not_found", "The requested operation is not part of the public contract."),
        "validation.invalid_input" => Error(3, code, "validation", "Input does not match the published schema."),
        "LEDGER-ACCOUNT-TYPE-UNSUPPORTED" or "LEDGER-CURRENCY-UNSUPPORTED" => Error(3, code, "validation", "The account input is not supported."),
        "LEDGER-ACCOUNT-NOT-FOUND" => Error(4, code, "not_found", "The account was not found."),
        "LEDGER-ACCOUNT-DUPLICATE" or "LEDGER-ACCOUNT-NAME-CONFLICT" => Error(5, code, "conflict", "The account conflicts with existing state."),
        "LEDGER-ACCOUNT-ARCHIVED" or "LEDGER-ACCOUNT-ALREADY-ARCHIVED" => Error(6, code, "lifecycle", "The account lifecycle does not allow the operation."),
        "LEDGER-CATEGORY-INVALID" or "LEDGER-CATEGORY-SELF-PARENT" or "LEDGER-CATEGORY-SCOPE-INVALID" => Error(3, code, "validation", "The category input is invalid."),
        "LEDGER-CATEGORY-NOT-FOUND" or "LEDGER-CATEGORY-PARENT-NOT-FOUND" => Error(4, code, "not_found", "The category was not found."),
        "LEDGER-CATEGORY-DUPLICATE-SIBLING" => Error(5, code, "conflict", "The category conflicts with an active sibling."),
        "LEDGER-CATEGORY-PARENT-ARCHIVED" or "LEDGER-CATEGORY-ARCHIVED" or "LEDGER-CATEGORY-CYCLE" or "LEDGER-CATEGORY-ACTIVE-CHILDREN" or "LEDGER-CATEGORY-ALREADY-ARCHIVED" or "LEDGER-CATEGORY-ALREADY-ACTIVE" or "LEDGER-CATEGORY-ANCESTOR-ARCHIVED" => Error(6, code, "lifecycle", "The category lifecycle does not allow the operation."),
        "LEDGER-PAYMENT-IDENTITY-INVALID" => Error(3, code, "validation", "The payment identity input is invalid."),
        "LEDGER-PAYMENT-INSTRUMENT-NOT-FOUND" or "LEDGER-CARDHOLDER-NOT-FOUND" => Error(4, code, "not_found", "The payment identity was not found."),
        "LEDGER-PAYMENT-INSTRUMENT-DUPLICATE" or "LEDGER-CARDHOLDER-DUPLICATE" => Error(5, code, "conflict", "The payment identity conflicts with active catalogue state."),
        "LEDGER-PAYMENT-INSTRUMENT-ACCOUNT-NOT-ACTIVE" or "LEDGER-PAYMENT-INSTRUMENT-ARCHIVED" or "LEDGER-CARDHOLDER-ARCHIVED" or "LEDGER-PAYMENT-INSTRUMENT-ALREADY-ARCHIVED" or "LEDGER-CARDHOLDER-ALREADY-ARCHIVED" or "LEDGER-PAYMENT-INSTRUMENT-ALREADY-ACTIVE" or "LEDGER-CARDHOLDER-ALREADY-ACTIVE" => Error(6, code, "lifecycle", "The payment identity lifecycle does not allow the operation."),
        "LEDGER-SPEND-POOL-INVALID" => Error(3, code, "validation", "The Spend Pool input is invalid."),
        "LEDGER-SPEND-POOL-NOT-FOUND" => Error(4, code, "not_found", "The Spend Pool was not found."),
        "LEDGER-SPEND-POOL-DUPLICATE" => Error(5, code, "conflict", "The Spend Pool conflicts with active catalogue state."),
        "LEDGER-SPEND-POOL-ARCHIVED" or "LEDGER-SPEND-POOL-ALREADY-ARCHIVED" or "LEDGER-SPEND-POOL-ALREADY-ACTIVE" => Error(6, code, "lifecycle", "The Spend Pool lifecycle does not allow the operation."),
        "LEDGER-TRANSACTION-INVALID" or "LEDGER-TRANSACTION-EVIDENCE-INCOMPATIBLE" or "amount.invalid" or "amount.zero" or "currency.unsupported" or "date.invalid" => Error(3, code, "validation", "The transaction input is invalid."),
        "LEDGER-TRANSACTION-NOT-FOUND" => Error(4, code, "not_found", "The transaction was not found."),
        "LEDGER-TRANSACTION-EVIDENCE-CONFLICT" => Error(5, code, "conflict", "The transaction evidence conflicts with existing state."),
        "LEDGER-TRANSACTION-ATTRIBUTION-INCOMPATIBLE" => Error(6, code, "lifecycle", "The transaction payment attribution is incompatible."),
        "LEDGER-IDEMPOTENCY-001" or "operation.conflict" => Error(5, code, "conflict", "The operation conflicts with existing state."),
        "host.unavailable" => Error(9, code, "host", "The requested operation is not available in this foundation."),
        _ => UnexpectedFailure()
    };

    private sealed record InputSelection(IReadOnlyList<string> Arguments, string? InputPath, bool HasInput, string? ErrorCode);
    private sealed record Invocation(OperationDescriptor? Descriptor, JsonElement HandlerInput, bool UseRequestInput, int ExitCode, string? ErrorCode, string? Category, string? Message)
    {
        public static Invocation For(OperationDescriptor descriptor, JsonElement? input = null, bool useRequestInput = false) => new(descriptor, input ?? JsonSerializer.SerializeToElement(new EmptyInput(), LedgerJsonContext.Default.EmptyInput), useRequestInput, 0, null, null, null);
        public static Invocation Error(int exitCode, string code, string category, string message) => new(null, default, false, exitCode, code, category, message);
    }
}
