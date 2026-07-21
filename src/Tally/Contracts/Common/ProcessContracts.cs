using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tally.Contracts.Common;

public sealed record ResultEnvelope(string ContractVersion, string OperationId, string Outcome, JsonElement? Result, ProcessError? Error);
public sealed record ProcessError(string Code, string Category, string Message, IReadOnlyList<string>? Fields = null);
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
public sealed record SafeActor(string Kind, string Label, string? RunId = null);
public sealed record RequestEnvelope(string ContractVersion, SafeActor Actor, JsonElement Input, string? IdempotencyKey = null);
public sealed record EmptyInput;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResultEnvelope))]
[JsonSerializable(typeof(ProcessError))]
[JsonSerializable(typeof(SafeActor))]
[JsonSerializable(typeof(RequestEnvelope))]
[JsonSerializable(typeof(EmptyInput))]
[JsonSerializable(typeof(Tally.Contracts.System.VersionResult))]
[JsonSerializable(typeof(Tally.Contracts.System.SchemaListResult))]
[JsonSerializable(typeof(Tally.Contracts.System.SchemaShowResult))]
[JsonSerializable(typeof(Tally.Contracts.System.SchemaShowRequest))]
[JsonSerializable(typeof(Tally.Contracts.System.OperationUnavailableResult))]
[JsonSerializable(typeof(Tally.Contracts.System.OperationSchema))]
[JsonSerializable(typeof(Tally.Contracts.System.OperationSchema[]))]
public partial class LedgerJsonContext : JsonSerializerContext;
