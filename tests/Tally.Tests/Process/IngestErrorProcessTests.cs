using System.Reflection;
using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Recovery;
using Tally.Features.Ingest.Review;
using Xunit;

namespace Tally.Tests.Process;

/// <summary>
/// Every published INGEST ErrorSchema code must map through TallyProcess.ErrorForHandler
/// to its contracted exit/category instead of collapsing to host.unexpected.
/// </summary>
public sealed class IngestErrorProcessTests
{
    [Theory]
    [MemberData(nameof(DeclaredIngestErrors))]
    public void Declared_ingest_errors_map_to_their_public_process_contract(string code, int exitCode, string category)
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

    public static TheoryData<string, int, string> DeclaredIngestErrors => new()
    {
        // ingest.preview
        { PreviewErrors.InvalidInput, 3, "validation" },
        { PreviewErrors.AccountNotFound, 4, "not_found" },
        { PreviewErrors.AccountInactive, 3, "validation" },
        { PreviewErrors.AccountCurrency, 3, "validation" },
        // Literal codes: CallerOwnedSourceReader is Linux-only for IO; the code contract is platform-agnostic.
        { "INGEST-PREVIEW-SOURCE-PATH-INVALID", 3, "validation" },
        { "INGEST-PREVIEW-SOURCE-UNREADABLE", 5, "unsafe_source" },
        { "INGEST-PREVIEW-SOURCE-CHANGED", 5, "unsafe_source" },
        { "INGEST-PREVIEW-SOURCE-TOO-LARGE", 6, "resource" },
        { PreviewErrors.Unsupported, 5, "unsupported" },
        { PreviewErrors.AmbiguousAdapter, 5, "unsupported" },
        { PreviewErrors.OverlapBlocked, 5, "overlap" },
        { PreviewErrors.ReconciliationBlocked, 5, "reconciliation" },
        { PreviewErrors.Unexpected, 10, "unexpected" },
        // ingest.inspect
        { InspectErrors.InvalidInput, 3, "validation" },
        { InspectErrors.NotFound, 4, "not_found" },
        // ingest.approve
        { ApproveErrors.InvalidInput, 3, "validation" },
        { ApproveErrors.NotFound, 4, "not_found" },
        { ApproveErrors.DigestMismatch, 5, "conflict" },
        { ApproveErrors.NotCommittable, 3, "validation" },
        { ApproveErrors.Blocked, 3, "validation" },
        // ingest.commit
        { CommitErrors.InvalidInput, 3, "validation" },
        { CommitErrors.NotFound, 4, "not_found" },
        { CommitErrors.DigestMismatch, 5, "conflict" },
        { CommitErrors.NotApproved, 3, "validation" },
        { CommitErrors.NotCommittable, 3, "validation" },
        { CommitErrors.AccountInactive, 3, "validation" },
        { CommitErrors.VersionIncompatible, 7, "compatibility" },
        { CommitErrors.LockHeld, 5, "conflict" },
        { CommitErrors.LedgerConflict, 5, "conflict" },
        { CommitErrors.LedgerRejected, 3, "validation" },
        { CommitErrors.VerificationFailed, 6, "ledger" },
        { CommitErrors.Interrupted, 6, "interrupted" },
        // ingest.resume (includes shared commit codes already listed)
        { ResumeErrors.InvalidInput, 3, "validation" },
        { ResumeErrors.NotFound, 4, "not_found" },
        { ResumeErrors.NotResumable, 3, "validation" },
        // ingest.status
        { StatusErrors.InvalidInput, 3, "validation" },
        { StatusErrors.BatchNotFound, 4, "not_found" },
        { StatusErrors.SnapshotBusy, 5, "conflict" },
        { StatusErrors.SnapshotExpired, 6, "lifecycle" },
        { StatusErrors.CursorInvalid, 7, "compatibility" },
        { StatusErrors.ContractMismatch, 7, "compatibility" },
        { StatusErrors.GenerationMismatch, 7, "compatibility" },
        { StatusErrors.SnapshotNotFound, 4, "not_found" },
        // ingest.abandon
        { AbandonErrors.InvalidInput, 3, "validation" },
        { AbandonErrors.NotFound, 4, "not_found" },
        { AbandonErrors.NotAbandonable, 3, "validation" },
        { AbandonErrors.LockHeld, 5, "conflict" },
        // ingest.cleanup
        { CleanupErrors.InvalidInput, 3, "validation" },
        { CleanupErrors.NotFound, 4, "not_found" },
        { CleanupErrors.RetainedForRecovery, 3, "validation" },
        { CleanupErrors.LockHeld, 5, "conflict" }
    };
}
