using System.Text.Json.Serialization;
using Tally.Contracts.Common;

namespace Tally.Contracts.System;

public sealed record SchemaListResult(string ContractVersion, IReadOnlyList<OperationSchema> Operations);
public sealed record SchemaShowResult(OperationSchema Operation);
public sealed record SchemaShowRequest(string OperationId);
public sealed record VersionResult(string Product, string Version, string ContractVersion, string Compatibility);
public sealed record OperationUnavailableResult(string OperationId, string Status);

/// <summary>
/// Public operation schema. <see cref="Limits"/> is required/non-null for CLASSIFY descriptors;
/// omitted (null, not written) for legacy Ledger/INGEST/BUDGET operations to preserve bytes.
/// </summary>
public sealed record OperationSchema(
    string OperationId,
    string CliPath,
    string Kind,
    string RequestSchema,
    string ResultSchema,
    string RequestType,
    string ResultType,
    IReadOnlyList<ErrorSchema> Errors,
    int SuccessExit,
    bool RequiresIdempotencyKey,
    string MinimumContractVersion,
    string MaximumContractVersion,
    string HandlerTarget,
    string Example,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OperationLimits? Limits = null);

public sealed record ErrorSchema(string Code, string Category, int ExitCode);
