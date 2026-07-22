using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Features.Ledger.Recovery;
using Tally.Features.System.Guidance;

namespace Tally.Composition.Ledger;

[SupportedOSPlatform("linux")]
public sealed class RecoveryGuidanceOperationBundle(
    BackupOperationModule backup,
    RestoreOperationModule restore,
    StorageEvolutionOperationModule storageEvolution,
    GuidanceOperationModule guidance,
    IReadOnlyList<OperationDescriptor>? sourceDescriptors = null)
{
    private static readonly string[] ExpectedOperationIds =
    [
        "ledger.backup.create",
        "ledger.backup.verify",
        "ledger.restore.activate",
        "ledger.restore.prepare",
        "ledger.storage.evolution.activate",
        "ledger.storage.evolution.prepare",
        "ledger.storage.status",
        "system.guidance.check",
        "system.guidance.install",
        "system.guidance.list",
        "system.schema.list",
        "system.schema.show",
        "system.version"
    ];

    private static readonly HashSet<string> ExpectedOperationSet = new(ExpectedOperationIds, StringComparer.Ordinal);
    private static readonly string[] ForbiddenContractTerms =
    [
        "agentmail", "mailbox", "mime", "whatsapp", "recipient", "sender", "delivery",
        "acknowledgement", "schedule", "rawpayload", "providercursor", "provider"
    ];

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } = Compose(
        backup ?? throw new ArgumentNullException(nameof(backup)),
        restore ?? throw new ArgumentNullException(nameof(restore)),
        storageEvolution ?? throw new ArgumentNullException(nameof(storageEvolution)),
        guidance ?? throw new ArgumentNullException(nameof(guidance)),
        sourceDescriptors ?? DefaultSource(backup, restore, storageEvolution));

    private static IReadOnlyList<OperationDescriptor> Compose(
        BackupOperationModule backup,
        RestoreOperationModule restore,
        StorageEvolutionOperationModule storageEvolution,
        GuidanceOperationModule guidance,
        IReadOnlyList<OperationDescriptor> sourceDescriptors)
    {
        ArgumentNullException.ThrowIfNull(sourceDescriptors);
        if (sourceDescriptors.Any(descriptor =>
                descriptor.OperationId.StartsWith("system.guidance.", StringComparison.Ordinal)
                && !ExpectedOperationSet.Contains(descriptor.OperationId)))
        {
            throw new InvalidOperationException("Recovery guidance bundle contains an unpublished guidance-only capability.");
        }

        var descriptors = sourceDescriptors
            .Where(descriptor => ExpectedOperationSet.Contains(descriptor.OperationId))
            .ToArray();
        Validate(descriptors, backup, restore, storageEvolution);
        return descriptors
            .Select(descriptor => Bind(descriptor, backup, restore, storageEvolution, guidance))
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Validate(
        IReadOnlyList<OperationDescriptor> descriptors,
        BackupOperationModule backup,
        RestoreOperationModule restore,
        StorageEvolutionOperationModule storageEvolution)
    {
        if (descriptors.GroupBy(descriptor => descriptor.OperationId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Recovery guidance bundle contains a duplicate operation ID.");
        if (descriptors.GroupBy(descriptor => descriptor.CliPath, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Recovery guidance bundle contains a duplicate CLI path.");

        var actualIds = descriptors.Select(descriptor => descriptor.OperationId).Order(StringComparer.Ordinal).ToArray();
        if (!ExpectedOperationIds.SequenceEqual(actualIds, StringComparer.Ordinal))
            throw new InvalidOperationException("Recovery guidance bundle does not contain the exact 13-operation contract.");
        if (descriptors.Any(descriptor => descriptor.MinimumContractVersion != "1.0" || descriptor.MaximumContractVersion != "1.0"))
            throw new InvalidOperationException("Recovery guidance bundle contains an incompatible contract version.");
        if (descriptors.Any(ContainsForbiddenContractTerm))
            throw new InvalidOperationException("Recovery guidance bundle contains provider or transport vocabulary.");

        var suppliedSchemas = descriptors
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .Select(descriptor => descriptor.ToSchema())
            .ToArray();
        var canonicalSchemas = DefaultSource(backup, restore, storageEvolution)
            .Select(descriptor => descriptor.ToSchema())
            .ToArray();
        if (!string.Equals(
                JsonSerializer.Serialize(suppliedSchemas, LedgerJsonContext.Default.OperationSchemaArray),
                JsonSerializer.Serialize(canonicalSchemas, LedgerJsonContext.Default.OperationSchemaArray),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery guidance bundle contains a rewritten descriptor contract.");
        }
    }

    private static OperationDescriptor Bind(
        OperationDescriptor descriptor,
        BackupOperationModule backup,
        RestoreOperationModule restore,
        StorageEvolutionOperationModule storageEvolution,
        GuidanceOperationModule guidance) => descriptor with
        {
            HandlerFactory = descriptor.OperationId switch
            {
                BackupOperationModule.CreateOperationId or BackupOperationModule.VerifyOperationId =>
                    (_, _) => new RecoveryGuidanceBundledOperationHandler(backup.HandleAsync, descriptor.OperationId),
                RestoreOperationModule.PrepareOperationId or RestoreOperationModule.ActivateOperationId =>
                    (_, _) => new RecoveryGuidanceBundledOperationHandler(restore.HandleAsync, descriptor.OperationId),
                StorageEvolutionOperationModule.StatusOperationId
                    or StorageEvolutionOperationModule.PrepareOperationId
                    or StorageEvolutionOperationModule.ActivateOperationId =>
                    (_, _) => new RecoveryGuidanceBundledOperationHandler(storageEvolution.HandleAsync, descriptor.OperationId),
                "system.guidance.list" or "system.guidance.check" or "system.guidance.install" =>
                    (_, registry) => new GuidanceOperationHandler(guidance, registry, descriptor.OperationId),
                "system.schema.list" or "system.schema.show" or "system.version" => descriptor.HandlerFactory,
                _ => throw new InvalidOperationException("Recovery guidance operation is not explicitly composed.")
            }
        };

    private static OperationDescriptor[] DefaultSource(
        BackupOperationModule backup,
        RestoreOperationModule restore,
        StorageEvolutionOperationModule storageEvolution) => backup.Descriptors
        .Concat(restore.Descriptors)
        .Concat(storageEvolution.Descriptors)
        .Concat(OperationRegistry.Create().Descriptors.Where(descriptor => ExpectedOperationSet.Contains(descriptor.OperationId)))
        .DistinctBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
        .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
        .ToArray();

    private static bool ContainsForbiddenContractTerm(OperationDescriptor descriptor) =>
        ContractTokens(descriptor).Any(token => ForbiddenContractTerms.Any(forbidden =>
            NormalizeToken(token).Contains(forbidden, StringComparison.OrdinalIgnoreCase)));

    private static string NormalizeToken(string token) => new(token.Where(char.IsLetterOrDigit).ToArray());

    private static IEnumerable<string> ContractTokens(OperationDescriptor descriptor)
    {
        yield return descriptor.OperationId;
        yield return descriptor.CliPath;
        yield return descriptor.HandlerTarget;
        yield return descriptor.Example;
        foreach (var token in TypeTokens(descriptor.RequestTypeInfo)) yield return token;
        foreach (var token in TypeTokens(descriptor.ResultTypeInfo)) yield return token;
    }

    private static IEnumerable<string> TypeTokens(JsonTypeInfo typeInfo)
    {
        yield return typeInfo.Type.FullName ?? typeInfo.Type.Name;
        foreach (var property in typeInfo.Properties)
        {
            yield return property.Name;
            yield return property.PropertyType.FullName ?? property.PropertyType.Name;
        }
    }
}

internal sealed class RecoveryGuidanceBundledOperationHandler(
    Func<string, OperationRequest, CancellationToken, Task<CommandResult<JsonElement>>> dispatch,
    string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        dispatch(operationId, request, cancellationToken);
}
