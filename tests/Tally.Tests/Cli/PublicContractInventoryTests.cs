using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Transactions;
using Tally.Contracts.System;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Cli;

[SupportedOSPlatform("linux")]
public sealed class PublicContractInventoryTests(PublicContractFixture fixture) : IClassFixture<PublicContractFixture>
{
    public static TheoryData<string> OperationIds => new(
        OperationRegistry.Create().Descriptors.Select(descriptor => descriptor.OperationId));

    [Theory]
    [MemberData(nameof(OperationIds))]
    public void Every_public_operation_has_one_concrete_versioned_source_generated_contract(string operationId)
    {
        var descriptor = Assert.Single(OperationRegistry.Create().Descriptors, candidate => candidate.OperationId == operationId);
        var schema = descriptor.ToSchema();

        Assert.NotEqual(typeof(JsonElement), descriptor.RequestTypeInfo.Type);
        Assert.NotEqual(typeof(JsonElement), descriptor.ResultTypeInfo.Type);
        Assert.NotEqual(typeof(object), descriptor.RequestTypeInfo.Type);
        Assert.NotEqual(typeof(object), descriptor.ResultTypeInfo.Type);
        Assert.NotEqual(typeof(OperationUnavailableResult), descriptor.ResultTypeInfo.Type);
        Assert.NotEqual("FoundationOperationHandler", descriptor.HandlerTarget);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Example));
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
        Assert.Equal(descriptor.Kind == "mutation", descriptor.RequiresIdempotencyKey);
        Assert.Equal(schema.Errors.Count, schema.Errors.Select(error => error.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(schema.Errors, error => error.Code == "contract.incompatible" && error.ExitCode == 7);
        if (descriptor.RequiresIdempotencyKey)
        {
            Assert.Contains(schema.Errors, error => error.Code == "LEDGER-IDEMPOTENCY-001" && error.ExitCode == 5);
        }
    }

    [Fact]
    public void TC_LEDGER_AGENT_CONTRACT_CONFORMANCE_has_the_exact_73_id_and_path_inventory()
    {
        var descriptors = OperationRegistry.Create().Descriptors;

        Assert.Equal(73, descriptors.Count);
        Assert.Equal(73, descriptors.Select(descriptor => descriptor.OperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(73, descriptors.Select(descriptor => descriptor.CliPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(descriptors.Select(descriptor => descriptor.OperationId).Order(StringComparer.Ordinal), descriptors.Select(descriptor => descriptor.OperationId));
        Assert.Equal("ledger.account.archive", descriptors[0].OperationId);
        Assert.Equal("system.version", descriptors[^1].OperationId);
    }

    [Fact]
    public void TASK_LEDGER_GATE_INT_PUBLIC_CONTRACT_retains_the_four_exact_bundle_subtotals()
    {
        Assert.Equal(43, fixture.Services.CatalogueTransactions!.Descriptors.Count);
        Assert.Equal(9, fixture.Services.Reconciliation!.Descriptors.Count);
        Assert.Equal(8, fixture.Services.RelationshipActuals!.Descriptors.Count);
        Assert.Equal(13, fixture.Services.RecoveryGuidance!.Descriptors.Count);

        var bundled = fixture.Services.CatalogueTransactions.Descriptors
            .Concat(fixture.Services.Reconciliation.Descriptors)
            .Concat(fixture.Services.RelationshipActuals.Descriptors)
            .Concat(fixture.Services.RecoveryGuidance.Descriptors)
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(73, bundled.Length);
        Assert.Equal(
            JsonSerializer.Serialize(OperationRegistry.Create().Descriptors.Select(descriptor => descriptor.ToSchema()).ToArray(), LedgerJsonContext.Default.OperationSchemaArray),
            JsonSerializer.Serialize(bundled.Select(descriptor => descriptor.ToSchema()).ToArray(), LedgerJsonContext.Default.OperationSchemaArray));
    }

    [Fact]
    public void DM_LEDGER_OPERATION_DESCRIPTOR_binds_every_operation_to_an_explicit_runtime_handler()
    {
        var registry = OperationRegistry.Create();

        foreach (var descriptor in registry.Descriptors)
        {
            var handler = descriptor.HandlerFactory(fixture.Services, registry);

            Assert.EndsWith("OperationHandler", handler.GetType().Name, StringComparison.Ordinal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public void NFR_LEDGER_AGENT_CONTRACT_STABILITY_is_provider_neutral_and_contains_no_private_surface()
    {
        var schema = OperationRegistry.Create().SchemaListJson();

        foreach (var forbidden in new[]
                 {
                     "agentmail", "mailbox", "mime", "messaging", "sender", "recipient", "whatsapp",
                     "delivery", "retry", "providerCursor", "rawPayload", "statementDocument", "connectionString", "sqlite"
                 })
        {
            Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DM_LEDGER_OPERATION_DESCRIPTOR_has_no_alias_generic_crud_or_sql_operation()
    {
        foreach (var descriptor in OperationRegistry.Create().Descriptors)
        {
            Assert.DoesNotContain("invoke", descriptor.OperationId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("crud", descriptor.OperationId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sql", descriptor.OperationId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alias", descriptor.HandlerTarget, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FR_LEDGER_CONTRACT_DISCOVERY_exposes_hierarchy_and_statement_authority_contracts()
    {
        var registry = OperationRegistry.Create();
        var reparent = Assert.IsType<OperationDescriptor>(registry.Find("ledger.category.reparent"));
        var correction = Assert.IsType<OperationDescriptor>(registry.Find("ledger.transaction.supersede"));
        var reconciliation = Assert.IsType<OperationDescriptor>(registry.Find("ledger.reconciliation.apply"));

        Assert.Equal(typeof(ReparentCategoryInput), reparent.RequestTypeInfo.Type);
        Assert.Contains(reparent.RequestTypeInfo.Properties, property => property.Name == "parentCategoryId");
        Assert.Equal(typeof(SupersedeTransactionInput), correction.RequestTypeInfo.Type);
        Assert.Equal(typeof(TransactionCorrectionResult), correction.ResultTypeInfo.Type);
        Assert.Equal(typeof(ReconciliationApplyInput), reconciliation.RequestTypeInfo.Type);
        Assert.Contains("correct_existing_from_statement", reconciliation.Example, StringComparison.Ordinal);
    }

    [Fact]
    public void TC_LEDGER_AGENT_CONTRACT_CONFORMANCE_inventory_is_byte_stable_across_repeated_builds()
    {
        Assert.Equal(OperationRegistry.Create().SchemaListJson(), OperationRegistry.Create().SchemaListJson());
    }

    [Fact]
    public void TC_LEDGER_AGENT_CONTRACT_CONFORMANCE_matches_the_committed_schema_snapshot()
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot(), "tests", "Tally.Tests", "Cli", "Snapshots", "ledger-operations-v1.json")));
        Assert.Equal(1, snapshot.RootElement.GetProperty("schemaVersion").GetInt32());
        var expectedIds = snapshot.RootElement.GetProperty("operationIds").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        var registry = OperationRegistry.Create();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(registry.SchemaListJson())));

        Assert.Equal(registry.Descriptors.Select(descriptor => descriptor.OperationId), expectedIds);
        Assert.Equal(fingerprint, snapshot.RootElement.GetProperty("schemaFingerprint").GetString());
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Tally repository root.");
    }
}

[SupportedOSPlatform("linux")]
public sealed class PublicContractFixture : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-public-contract-" + Guid.NewGuid().ToString("N"));

    public LedgerServices Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        Services = LedgerServices.Create(database);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
