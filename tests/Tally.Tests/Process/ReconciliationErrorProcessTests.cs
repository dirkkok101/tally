using System.Reflection;
using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Reconciliation;
using Xunit;

namespace Tally.Tests.Process;

public sealed class ReconciliationErrorProcessTests
{
    [Theory]
    [MemberData(nameof(DeclaredReconciliationErrors))]
    public void Declared_reconciliation_errors_map_to_their_public_process_contract(string code, int exitCode, string category)
    {
        var mapper = typeof(TallyProcess).GetMethod("ErrorForHandler", BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<ProcessResult>(mapper!.Invoke(null, [code]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
    }

    public static TheoryData<string, int, string> DeclaredReconciliationErrors => new()
    {
        { ReconciliationProjectionErrors.EvidenceNotFound, 4, "not_found" },
        { ReconciliationProjectionErrors.StatementEvidenceRequired, 3, "validation" },
        { ReconciliationProjectionErrors.IncompleteObservation, 3, "validation" },
        { ReconciliationProjectionErrors.ScopeNotFound, 4, "not_found" },
        { ReconciliationProjectionErrors.ScopeConflict, 5, "conflict" },
        { ReconciliationProjectionErrors.ScopeInactive, 6, "lifecycle" },
        { ReconciliationProjectionErrors.UnsupportedPolicy, 7, "compatibility" },
        { ReconciliationApplyErrors.UnsupportedAutomaticAuthority, 8, "integrity" },
        { ReconciliationApplyErrors.UnsupportedStatementCorrection, 8, "integrity" },
        { ReconciliationApplyErrors.EvidenceFingerprintChanged, 5, "conflict" },
        { ReconciliationApplyErrors.ProjectionChanged, 5, "conflict" },
        { ReconciliationApplyErrors.CandidateSetChanged, 5, "conflict" },
        { ReconciliationApplyErrors.TargetNotCandidate, 8, "integrity" },
        { ReconciliationApplyErrors.ProjectionConflict, 5, "conflict" },
        { ReconciliationApplyErrors.DispositionIncompatible, 6, "lifecycle" },
        { ReconciliationApplyErrors.StatementFactMismatch, 5, "conflict" },
        { "LEDGER-RECONCILIATION-CORRECTION-CONFLICT", 5, "conflict" }
    };
}
