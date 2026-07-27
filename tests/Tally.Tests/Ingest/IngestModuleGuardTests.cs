using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Features.Ingest.Contract;
using Xunit;

namespace Tally.Tests.Ingest;

/// <summary>
/// Final module-gate architecture and privacy guards for PLAN-INGEST-V1.
/// </summary>
// TC-INGEST-PUBLISHED-CONTRACT-MATRIX / NFR-INGEST-PUBLIC-CONTRACT-COMPATIBILITY
// TC-INGEST-LEDGER-PUBLIC-CONFORMANCE
[SupportedOSPlatform("linux")]
public sealed class IngestModuleGuardTests
{
    [Fact]
    public void Exactly_eight_ingest_operations_are_published()
    {
        var ingest = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("ingest.", StringComparison.Ordinal))
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(IngestOperationIds.All.Order(StringComparer.Ordinal), ingest);
    }

    [Fact]
    public void Ledger_operation_count_is_preserved()
    {
        var ledger = OperationRegistry.Create().Descriptors
            .Count(d => d.OperationId.StartsWith("ledger.", StringComparison.Ordinal));
        Assert.Equal(68, ledger);
    }

    [Fact]
    public void No_private_statement_paths_are_committed_under_src_or_tests()
    {
        var root = RepositoryRoot();
        foreach (var relative in new[] { "src", "tests" })
        {
            var path = Path.Combine(root, relative);
            if (!Directory.Exists(path))
            {
                continue;
            }

            var hits = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    || file.Contains("docs/statements", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(hits);
        }
    }

    [Fact]
    public void All_uc_and_security_gate_test_classes_are_discoverable()
    {
        var assembly = typeof(IngestModuleGuardTests).Assembly;
        string[] required =
        [
            "PreviewQualificationWorkflowTests",
            "ReviewApprovalWorkflowTests",
            "CommitResumeWorkflowTests",
            "ReplayOverlapWorkflowTests",
            "AgentContractWorkflowTests",
            "FailureCleanupWorkflowTests",
            "IngestSecurityBoundaryTests",
            "IngestResourceCanaryTests",
            "PublishedIngestSecurityTests",
            "IngestPublicContractInventoryTests"
        ];
        var names = assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(required, name => Assert.Contains(name, names));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
