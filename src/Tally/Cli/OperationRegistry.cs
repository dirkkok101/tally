using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Tally.Features.System.Contract;

namespace Tally.Cli;

public sealed record OperationDescriptor(string OperationId, string CliPath, string Kind, bool RequiresIdempotencyKey, JsonTypeInfo RequestTypeInfo, JsonTypeInfo ResultTypeInfo, string HandlerTarget, Func<LedgerServices, OperationRegistry, IOperationHandler> HandlerFactory, string Example)
{
    public OperationSchema ToSchema() => new(OperationId, CliPath, Kind, "{\"type\":\"object\",\"additionalProperties\":false}", "{\"type\":\"object\"}", RequestTypeInfo.Type.FullName!, ResultTypeInfo.Type.FullName!, Errors, 0, RequiresIdempotencyKey, "1.0", "1.0", HandlerTarget, Example);
    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new("usage.invalid_input_path", "usage", 2), new("validation.invalid_input", "validation", 3), new("operation.not_found", "not_found", 4),
        new("operation.conflict", "conflict", 5), new("operation.lifecycle", "lifecycle", 6), new("contract.incompatible", "compatibility", 7),
        new("operation.review_required", "integrity", 8), new("host.unavailable", "host", 9), new("host.unexpected", "host", 10)
    ];
}

public sealed class OperationRegistry
{
    private readonly IReadOnlyList<OperationDescriptor> descriptors;
    private OperationRegistry(IReadOnlyList<OperationDescriptor> descriptors) => this.descriptors = descriptors;
    public IReadOnlyList<OperationDescriptor> Descriptors => descriptors;
    public static OperationRegistry Create() => new(Inventory.Select(CreateDescriptor).OrderBy(x => x.OperationId, StringComparer.Ordinal).ToArray());
    public OperationDescriptor? Find(string operationId) => descriptors.SingleOrDefault(x => x.OperationId == operationId);
    public string SchemaListJson() => JsonSerializer.Serialize(descriptors.Select(x => x.ToSchema()).ToArray(), LedgerJsonContext.Default.OperationSchemaArray);
    public string SchemaShowJson(string operationId) => Find(operationId) is { } descriptor ? JsonSerializer.Serialize(descriptor.ToSchema(), LedgerJsonContext.Default.OperationSchema) : "null";
    private static OperationDescriptor CreateDescriptor(string operationId)
    {
        var isQuery = operationId is "system.schema.list" or "system.schema.show" or "system.version" or "system.guidance.list" or "system.guidance.check"
            || operationId.EndsWith(".get", StringComparison.Ordinal) || operationId.EndsWith(".list", StringComparison.Ordinal)
            || operationId.EndsWith(".query", StringComparison.Ordinal) || operationId.EndsWith(".candidates", StringComparison.Ordinal)
            || operationId.EndsWith(".status", StringComparison.Ordinal) || operationId.EndsWith(".verify", StringComparison.Ordinal);
        return operationId switch
        {
            "system.version" => new(operationId, "tally version", "query", false, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.VersionResult, "SystemOperationModule.Version", static (services, _) => new SystemOperationHandler(services.SystemOperations, null, "system.version"), "tally version"),
            "system.schema.list" => new(operationId, "tally schema list", "query", false, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.SchemaListResult, "SystemOperationModule.List", static (services, registry) => new SystemOperationHandler(services.SystemOperations, registry, "system.schema.list"), "tally schema list"),
            "system.schema.show" => new(operationId, "tally schema show <operation-id>", "query", false, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.SchemaShowResult, "SystemOperationModule.Show", static (services, registry) => new SystemOperationHandler(services.SystemOperations, registry, "system.schema.show"), "tally schema show system.version"),
            _ => new(operationId, "tally " + operationId.Replace('.', ' '), isQuery ? "query" : "mutation", !isQuery, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.OperationUnavailableResult, "FoundationOperationHandler", static (_, _) => new FoundationOperationHandler(), "tally " + operationId.Replace('.', ' '))
        };
    }
    private static readonly string[] Inventory =
    [
        "ledger.account.create","ledger.account.get","ledger.account.list","ledger.account.rename","ledger.account.archive",
        "ledger.category.create","ledger.category.get","ledger.category.list","ledger.category.rename","ledger.category.archive","ledger.category.reactivate",
        "ledger.instrument.create","ledger.instrument.get","ledger.instrument.list","ledger.instrument.rename","ledger.instrument.archive","ledger.instrument.reactivate",
        "ledger.cardholder.create","ledger.cardholder.get","ledger.cardholder.list","ledger.cardholder.rename","ledger.cardholder.archive","ledger.cardholder.reactivate",
        "ledger.pool.create","ledger.pool.get","ledger.pool.list","ledger.pool.rename","ledger.pool.archive","ledger.pool.reactivate",
        "ledger.transaction.record","ledger.transaction.get","ledger.transaction.void","ledger.transaction.supersede","ledger.transaction.category.assign","ledger.transaction.category.correct","ledger.transaction.attribution.assign","ledger.transaction.attribution.correct","ledger.transaction.pool.assign","ledger.transaction.pool.correct",
        "ledger.evidence.register","ledger.evidence.get","ledger.evidence.link-supporting","ledger.reconciliation.candidates","ledger.reconciliation.apply","ledger.reconciliation.decision.get","ledger.reconciliation.decision.confirm","ledger.reconciliation.decision.reject","ledger.reconciliation.decision.revoke","ledger.reconciliation.decision.replace","ledger.reconciliation.coverage.complete","ledger.reconciliation.coverage.get",
        "ledger.transfer.confirm","ledger.transfer.revoke","ledger.transfer.replace","ledger.refund.confirm","ledger.refund.revoke","ledger.refund.replace","ledger.relationship.get","ledger.actuals.query","ledger.backup.create","ledger.backup.verify","ledger.restore.prepare","ledger.restore.activate","ledger.storage.status","ledger.storage.evolution.prepare","ledger.storage.evolution.activate",
        "system.schema.list","system.schema.show","system.version","system.guidance.list","system.guidance.check","system.guidance.install"
    ];
}

internal sealed class SystemOperationHandler(SystemOperationModule module, OperationRegistry? registry, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(JsonElement input, CancellationToken cancellationToken) => operationId switch
    {
        "system.version" => module.VersionAsync(input, cancellationToken),
        "system.schema.list" => module.ListAsync(registry!.Descriptors.Select(x => x.ToSchema()).ToArray(), input, cancellationToken),
        "system.schema.show" => module.ShowAsync(registry!.Find(input.GetProperty("operationId").GetString()!)?.ToSchema(), input, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class FoundationOperationHandler : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CommandResult<JsonElement>.Failure("host.unavailable"));
    }
}
