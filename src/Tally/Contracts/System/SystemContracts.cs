namespace Tally.Contracts.System;

public sealed record SchemaListResult(string ContractVersion, IReadOnlyList<OperationSchema> Operations);
public sealed record SchemaShowResult(OperationSchema Operation);
public sealed record SchemaShowRequest(string OperationId);
/// <summary>
/// <paramref name="Version"/> is the product/executable semver (module minor + patch).
/// <paramref name="ContractVersion"/> / <paramref name="Compatibility"/> are the public API contract line.
/// </summary>
public sealed record VersionResult(string Product, string Version, string ContractVersion, string Compatibility);
public sealed record OperationUnavailableResult(string OperationId, string Status);
public sealed record OperationSchema(string OperationId, string CliPath, string Kind, string RequestSchema, string ResultSchema, string RequestType, string ResultType, IReadOnlyList<ErrorSchema> Errors, int SuccessExit, bool RequiresIdempotencyKey, string MinimumContractVersion, string MaximumContractVersion, string HandlerTarget, string Example);
public sealed record ErrorSchema(string Code, string Category, int ExitCode);
