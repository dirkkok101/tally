using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Composition.Ledger;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Tally.Features.Ledger.Recovery;
using Tally.Features.System.Guidance;
using Xunit;

namespace Tally.Tests.Composition.Ledger;

[SupportedOSPlatform("linux")]
public sealed class RecoveryGuidanceOperationBundleTests
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

    [Fact]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_contains_the_exact_thirteen_operation_inventory()
    {
        var descriptors = Bundle().Descriptors;

        Assert.Equal(ExpectedOperationIds, descriptors.Select(descriptor => descriptor.OperationId));
        Assert.Equal(13, descriptors.Select(descriptor => descriptor.OperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(13, descriptors.Select(descriptor => descriptor.CliPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, descriptors.Count(descriptor => descriptor.OperationId.StartsWith("ledger.", StringComparison.Ordinal)));
        Assert.Equal(3, descriptors.Count(descriptor => descriptor.OperationId is "system.schema.list" or "system.schema.show" or "system.version"));
        Assert.Equal(3, descriptors.Count(descriptor => descriptor.OperationId.StartsWith("system.guidance.", StringComparison.Ordinal)));
    }

    [Fact]
    public void DM_LEDGER_OPERATION_DESCRIPTOR_retains_every_source_schema_byte_for_byte()
    {
        var source = Source();
        var expected = JsonSerializer.Serialize(
            source.Select(descriptor => descriptor.ToSchema()).ToArray(),
            LedgerJsonContext.Default.OperationSchemaArray);
        var actual = JsonSerializer.Serialize(
            Bundle(source).Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray(),
            LedgerJsonContext.Default.OperationSchemaArray);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_binds_every_operation_without_a_foundation_fallback()
    {
        var registry = OperationRegistry.Create();
        var services = LedgerServices.Create();

        foreach (var descriptor in Bundle().Descriptors)
        {
            var handler = descriptor.HandlerFactory(services, registry);

            Assert.EndsWith("OperationHandler", handler.GetType().Name, StringComparison.Ordinal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public async Task FR_LEDGER_SKILL_COMPATIBILITY_schema_discovery_remains_complete_without_installed_guidance()
    {
        var registry = OperationRegistry.Create();
        var descriptor = Assert.Single(Bundle().Descriptors, candidate => candidate.OperationId == "system.schema.list");
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(new EmptyInput(), LedgerJsonContext.Default.EmptyInput),
            null,
            null);

        var result = await descriptor.HandlerFactory(LedgerServices.Create(), registry)
            .HandleAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var schemas = result.Value!.Deserialize(LedgerJsonContext.Default.SchemaListResult)!.Operations;
        Assert.All(ExpectedOperationIds, operationId => Assert.Contains(schemas, schema => schema.OperationId == operationId));
    }

    [Fact]
    public async Task FR_LEDGER_SKILL_COMPATIBILITY_guidance_is_optional_and_reports_missing_without_installing_it()
    {
        var scope = Path.Combine(Path.GetTempPath(), "tally-recovery-guidance-" + Guid.NewGuid().ToString("N"));
        var descriptor = Assert.Single(Bundle().Descriptors, candidate => candidate.OperationId == "system.guidance.list");
        var registry = OperationRegistry.Create();
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(new ListGuidanceInput(scope), LedgerJsonContext.Default.ListGuidanceInput),
            null,
            null);

        var result = await descriptor.HandlerFactory(LedgerServices.Create(), registry)
            .HandleAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var bundles = result.Value!.Deserialize(LedgerJsonContext.Default.GuidanceListResult)!.Bundles;
        Assert.Equal(2, bundles.Count);
        Assert.All(bundles, bundle => Assert.Equal("missing", bundle.Status));
        Assert.False(Directory.Exists(scope));
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    [InlineData("storageEvolution")]
    [InlineData("guidance")]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_rejects_a_missing_module(string missing)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => Bundle(missing: missing));

        Assert.Equal(missing, exception.ParamName);
    }

    [Fact]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_rejects_a_missing_descriptor()
    {
        var source = Source().Where(descriptor => descriptor.OperationId != "ledger.restore.prepare").ToArray();

        Assert.Contains("exact 13-operation", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_rejects_a_duplicate_operation_id()
    {
        var source = Source().ToList();
        source.Add(source[0]);

        Assert.Contains("duplicate operation ID", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_rejects_a_duplicate_cli_path()
    {
        var source = Source();
        source[1] = source[1] with { CliPath = source[0].CliPath };

        Assert.Contains("duplicate CLI path", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("maximum")]
    public void TASK_LEDGER_GATE_INT_RECOVERY_SKILL_BUNDLE_rejects_an_incompatible_contract_range(string bound)
    {
        var source = Source();
        source[0] = bound == "minimum"
            ? source[0] with { MinimumContractVersion = "2.0" }
            : source[0] with { MaximumContractVersion = "2.0" };

        Assert.Contains("incompatible contract version", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agentmail")]
    [InlineData("whatsapp")]
    [InlineData("providerCursor")]
    public void NFR_LEDGER_LOCAL_PRIVACY_rejects_provider_or_transport_vocabulary(string forbidden)
    {
        var source = Source();
        source[0] = source[0] with { Example = source[0].Example + " " + forbidden };

        Assert.Contains("provider or transport", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR_LEDGER_SKILL_COMPATIBILITY_rejects_an_unpublished_guidance_only_capability()
    {
        var source = Source().ToList();
        var guidance = source.Single(descriptor => descriptor.OperationId == "system.guidance.check");
        source.Add(guidance with
        {
            OperationId = "system.guidance.execute",
            CliPath = "tally system guidance execute",
            HandlerTarget = "GuidanceOperationModule.Execute",
            Example = "tally system guidance execute --input -"
        });

        Assert.Contains("guidance-only capability", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DM_LEDGER_OPERATION_DESCRIPTOR_rejects_a_rewritten_descriptor_contract()
    {
        var source = Source();
        var index = Array.FindIndex(source, descriptor => descriptor.OperationId == "ledger.backup.create");
        source[index] = source[index] with { RequestTypeInfo = LedgerJsonContext.Default.EmptyInput };

        Assert.Contains("rewritten descriptor contract", Assert.Throws<InvalidOperationException>(() => Bundle(source)).Message, StringComparison.Ordinal);
    }

    private static RecoveryGuidanceOperationBundle Bundle(
        IReadOnlyList<OperationDescriptor>? source = null,
        string? missing = null) => new(
        missing == "backup" ? null! : new BackupOperationModule(null!),
        missing == "restore" ? null! : new RestoreOperationModule(null!),
        missing == "storageEvolution" ? null! : new StorageEvolutionOperationModule(null!),
        missing == "guidance" ? null! : new GuidanceOperationModule(new GuidanceService()),
        source);

    private static OperationDescriptor[] Source()
    {
        var backup = new BackupOperationModule(null!);
        var restore = new RestoreOperationModule(null!);
        var evolution = new StorageEvolutionOperationModule(null!);
        return backup.Descriptors
            .Concat(restore.Descriptors)
            .Concat(evolution.Descriptors)
            .Concat(OperationRegistry.Create().Descriptors.Where(descriptor => ExpectedOperationIds.Contains(descriptor.OperationId, StringComparer.Ordinal)))
            .DistinctBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
    }
}
