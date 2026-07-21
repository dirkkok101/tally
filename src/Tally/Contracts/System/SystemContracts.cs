namespace Tally.Contracts.System;

public sealed record SchemaListResult(string ContractVersion, IReadOnlyList<OperationSchema> Operations);
public sealed record SchemaShowResult(OperationSchema Operation);
public sealed record SchemaShowRequest(string OperationId);
public sealed record VersionResult(string Product, string ContractVersion, string Compatibility);
public sealed record OperationUnavailableResult(string OperationId, string Status);
public sealed record OperationSchema(string OperationId, string CliPath, string Kind, string RequestSchema, string ResultSchema, string RequestType, string ResultType, IReadOnlyList<ErrorSchema> Errors, int SuccessExit, bool RequiresIdempotencyKey, string MinimumContractVersion, string MaximumContractVersion, string HandlerTarget, string Example);
public sealed record ErrorSchema(string Code, string Category, int ExitCode);
