using System.Collections.Immutable;
using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Recovery;
using Tally.Features.Ingest.Review;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit INGEST composition root (no reflection / plugin scan).
/// GATE-INT-PUBLIC-CONTRACT: six operation modules + adapters + state + Ledger client.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class IngestOperationBundle(
    PreviewOperationModule preview,
    ReviewOperationModule review,
    CommitOperationModule commit,
    ResumeOperationModule resume,
    StatusOperationModule status,
    RecoveryCleanupOperationModule recovery)
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
        preview.Descriptors
            .Concat(review.Descriptors)
            .Concat(commit.Descriptors)
            .Concat(resume.Descriptors)
            .Concat(status.Descriptors)
            .Concat(recovery.Descriptors)
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Descriptor-only bundle for registry inventory (handlers not executed).
    /// </summary>
    public static IngestOperationBundle CreateDescriptorTemplates() => new(
        new PreviewOperationModule(null!),
        new ReviewOperationModule(null!, null!),
        new CommitOperationModule(null!),
        new ResumeOperationModule(null!),
        new StatusOperationModule(null!),
        new RecoveryCleanupOperationModule(null!, null!));

    public static IngestServices CreateServices(
        string dataRoot,
        LedgerContractClient ledgerClient,
        TimeProvider? timeProvider = null,
        IPreviewPdfExtractor? pdfExtractor = null,
        ICommitFaultHook? commitFaultHook = null)
    {
        var protection = new IngestArtifactProtection();
        var database = new IngestDatabase(dataRoot, protection);
        var errors = new BatchErrorEventStore();
        var previewStore = new PreviewStateStore(database, errors);
        var reviewStore = new ReviewStateStore(database);
        var commitStore = new CommitStateStore(database, errors);
        var recoveryStore = new RecoveryStateStore(database, errors);
        var batchLock = new BatchCommitLock(database, protection);
        var clock = timeProvider ?? TimeProvider.System;

        var adapters = StatementAdapterRegistry.CreateDefault();
        var previewHandler = new PreviewHandler(
            new CallerOwnedSourceReader(),
            new LedgerPreviewAccountDirectory(async (accountId, version, actor, ct) =>
            {
                var result = await ledgerClient.GetAccountAsync(accountId, version, actor, ct);
                return result.IsSuccess ? result.Value : null;
            }),
            pdfExtractor ?? new PreviewPdfExtractorAdapter(new PdfStatementTextExtractor()),
            adapters,
            previewStore,
            clock);
        var inspectHandler = new InspectHandler(reviewStore);
        var approveHandler = new ApproveHandler(reviewStore, clock);
        var saga = new CandidateCommitSaga(reviewStore, commitStore, batchLock, ledgerClient, clock, commitFaultHook);
        var resumeHandler = new ResumeHandler(commitStore, saga);
        var statusHandler = new StatusHandler(new StatusStateStore(database, errors), clock);
        var abandonHandler = new AbandonHandler(recoveryStore, batchLock, clock);
        var cleanupHandler = new CleanupHandler(recoveryStore, batchLock, clock);

        var bundle = new IngestOperationBundle(
            new PreviewOperationModule(previewHandler),
            new ReviewOperationModule(inspectHandler, approveHandler),
            new CommitOperationModule(saga),
            new ResumeOperationModule(resumeHandler),
            new StatusOperationModule(statusHandler),
            new RecoveryCleanupOperationModule(abandonHandler, cleanupHandler));

        return new IngestServices(bundle, adapters, database, protection, ledgerClient);
    }

    private sealed class PreviewPdfExtractorAdapter(PdfStatementTextExtractor inner) : IPreviewPdfExtractor
    {
        public ValueTask<PdfExtractionResult> ExtractAsync(
            ImmutableArray<byte> source,
            PdfExtractionLimits limits,
            CancellationToken cancellationToken) =>
            inner.ExtractAsync(source, limits, cancellationToken);
    }
}

[SupportedOSPlatform("linux")]
public sealed record IngestServices(
    IngestOperationBundle Operations,
    StatementAdapterRegistry Adapters,
    IngestDatabase Database,
    IngestArtifactProtection Protection,
    LedgerContractClient LedgerClient);
