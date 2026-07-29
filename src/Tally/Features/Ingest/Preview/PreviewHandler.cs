using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Accounts;
using Tally.Domain.Ingest.Identity;
using Tally.Domain.Ingest.Overlap;
using Tally.Infrastructure.Ingest.Pdf;
using Tally.Infrastructure.Ingest.Storage;

namespace Tally.Features.Ingest.Preview;

public interface IPreviewAccountDirectory
{
    Task<AccountDetail?> GetActiveZarAccountAsync(
        string accountId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken);
}

public interface IPreviewPdfExtractor
{
    ValueTask<PdfExtractionResult> ExtractAsync(
        ImmutableArray<byte> source,
        PdfExtractionLimits limits,
        CancellationToken cancellationToken);
}

public static class PreviewErrors
{
    public const string InvalidInput = "INGEST-PREVIEW-INPUT-INVALID";
    public const string AccountNotFound = "INGEST-PREVIEW-ACCOUNT-NOT-FOUND";
    public const string AccountInactive = "INGEST-PREVIEW-ACCOUNT-INACTIVE";
    public const string AccountCurrency = "INGEST-PREVIEW-ACCOUNT-CURRENCY";
    public const string Unsupported = "INGEST-PREVIEW-UNSUPPORTED";
    public const string AmbiguousAdapter = "INGEST-PREVIEW-ADAPTER-AMBIGUOUS";
    public const string OverlapBlocked = "INGEST-PREVIEW-OVERLAP-BLOCKED";
    public const string ReconciliationBlocked = "INGEST-PREVIEW-RECONCILIATION-BLOCKED";
    public const string Unexpected = "INGEST-PREVIEW-UNEXPECTED";
}

[SupportedOSPlatform("linux")]
public sealed class PreviewHandler(
    CallerOwnedSourceReader sourceReader,
    IPreviewAccountDirectory accounts,
    IPreviewPdfExtractor extractor,
    StatementAdapterRegistry adapters,
    PreviewStateStore store,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult<PreviewImportResult>> HandleAsync(
        PreviewCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ContractVersion) ||
            string.IsNullOrWhiteSpace(command.SourcePath) ||
            string.IsNullOrWhiteSpace(command.AccountId) ||
            command.Actor is null ||
            string.IsNullOrWhiteSpace(command.Actor.Kind) ||
            string.IsNullOrWhiteSpace(command.Actor.Label))
        {
            return Failure(PreviewErrors.InvalidInput);
        }

        var createdAt = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        AccountDetail? account = null;
        string? fingerprint = null;
        string? adapterVersion = null;

        try
        {
            account = await accounts.GetActiveZarAccountAsync(
                command.AccountId,
                command.ContractVersion,
                command.Actor,
                cancellationToken);
            if (account is null)
            {
                return Failure(PreviewErrors.AccountNotFound);
            }

            if (account.Status != AccountStatus.Active)
            {
                return Failure(PreviewErrors.AccountInactive);
            }

            if (!string.Equals(account.CurrencyCode, "ZAR", StringComparison.Ordinal))
            {
                return Failure(PreviewErrors.AccountCurrency);
            }

            // Exact-replay short-circuit requires adapter version candidates from the registry.
            // Source is still validated first for path/bounds integrity.
            var limits = PdfExtractionLimits.PrivateFixture;
            var source = sourceReader.Read(command.SourcePath, limits.MaxBytes);
            if (source.Snapshot is null)
            {
                return Failure(source.ErrorCode ?? PreviewErrors.Unexpected);
            }

            fingerprint = source.Snapshot.SourceFingerprint;

            foreach (var adapter in adapters.Adapters)
            {
                var key = new ExactReplayKey(
                    fingerprint,
                    account.AccountId,
                    adapter.Descriptor.AdapterVersion,
                    command.ContractVersion);
                var prior = await store.FindExactReplayAsync(key, cancellationToken);
                if (prior is not null)
                {
                    return Success(new PreviewImportResult(
                        prior.BatchId,
                        prior.ManifestRevisionId,
                        prior.Status,
                        prior.AdapterVariantId,
                        prior.Counts,
                        prior.Reconciliation,
                        prior.ExactReplayOf,
                        null));
                }
            }

            var extraction = await extractor.ExtractAsync(source.Snapshot.Bytes, limits, cancellationToken);
            if (extraction.Evidence is null)
            {
                return Failure(extraction.Error?.Code ?? PreviewErrors.Unsupported);
            }

            var selection = adapters.Select(extraction.Evidence);
            if (selection.Status == AdapterSelectionStatus.NoMatch)
            {
                return Failure(PreviewErrors.Unsupported);
            }

            if (selection.Status == AdapterSelectionStatus.Ambiguous || selection.Adapter is null)
            {
                return Failure(PreviewErrors.AmbiguousAdapter);
            }

            adapterVersion = selection.Adapter.Descriptor.AdapterVersion;
            ExtractedStatement statement;
            try
            {
                statement = selection.Adapter.Extract(extraction.Evidence, account);
            }
            catch (InvalidOperationException)
            {
                return Failure(PreviewErrors.Unsupported);
            }

            if (!DateOnly.TryParse(statement.StatementPeriod.StartDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateOnly.TryParse(statement.StatementPeriod.EndDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                return Failure(PreviewErrors.Unsupported);
            }

            var windows = await store.ListWindowsForAccountAsync(account.AccountId, cancellationToken);
            var overlapKey = new ExactReplayKey(fingerprint, account.AccountId, adapterVersion, command.ContractVersion);
            var overlap = OverlapPolicy.Evaluate(overlapKey, start, end, windows);
            if (overlap.Decision == OverlapDecision.BlockedOverlap)
            {
                return Failure(PreviewErrors.OverlapBlocked);
            }

            if (overlap.Decision == OverlapDecision.ExactReplay && overlap.PriorManifestRevisionId is not null)
            {
                var prior = await store.FindExactReplayAsync(overlapKey, cancellationToken);
                if (prior is not null)
                {
                    return Success(new PreviewImportResult(
                        prior.BatchId,
                        prior.ManifestRevisionId,
                        prior.Status,
                        prior.AdapterVariantId,
                        prior.Counts,
                        prior.Reconciliation,
                        prior.ExactReplayOf,
                        null));
                }
            }

            // Pure boundary-touch with prior same-account windows: load economic keys for the shared
            // endpoint day(s) so shared boundary rows become exact duplicates, not second candidates.
            var boundaryDates = OverlapPolicy.SharedBoundaryDates(start, end, windows, account.AccountId);
            var priorBoundaryKeys = boundaryDates.Count == 0
                ? null
                : await store.ListAcceptedEconomicKeysAsync(account.AccountId, boundaryDates, cancellationToken);

            var mapped = PreviewManifestMapper.Map(
                fingerprint,
                account,
                statement,
                command.ContractVersion,
                command.Actor,
                priorBoundaryKeys);

            if (!mapped.Committable && mapped.Counts.Blocked > 0)
            {
                // Still persist a non-committable preview for inspect/abandon paths when records exist.
            }

            var stored = await store.PersistPreviewAsync(
                fingerprint,
                account.AccountId,
                command.ContractVersion,
                mapped,
                statement.StatementPeriod,
                createdAt,
                cancellationToken);

            return Success(new PreviewImportResult(
                stored.BatchId,
                stored.ManifestRevisionId,
                stored.Status,
                stored.AdapterVariantId,
                stored.Counts,
                stored.Reconciliation,
                null,
                mapped.Committable ? null : IngestRetryAction.CorrectSource));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(PreviewErrors.Unexpected);
        }
    }

    private static CommandResult<PreviewImportResult> Success(PreviewImportResult result) =>
        CommandResult<PreviewImportResult>.Success(result);

    private static CommandResult<PreviewImportResult> Failure(string errorCode) =>
        CommandResult<PreviewImportResult>.Failure(errorCode);
}

public sealed class LedgerPreviewAccountDirectory(Func<string, string, SafeActor, CancellationToken, Task<AccountDetail?>> lookup)
    : IPreviewAccountDirectory
{
    public Task<AccountDetail?> GetActiveZarAccountAsync(
        string accountId,
        string contractVersion,
        SafeActor actor,
        CancellationToken cancellationToken) =>
        lookup(accountId, contractVersion, actor, cancellationToken);
}

public sealed class DefaultPreviewPdfExtractor(PdfStatementTextExtractor extractor) : IPreviewPdfExtractor
{
    public ValueTask<PdfExtractionResult> ExtractAsync(
        ImmutableArray<byte> source,
        PdfExtractionLimits limits,
        CancellationToken cancellationToken) =>
        extractor.ExtractAsync(source, limits, cancellationToken);
}
