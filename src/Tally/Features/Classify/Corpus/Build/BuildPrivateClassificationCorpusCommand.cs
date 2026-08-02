using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Features.Classify.Corpus.Build;

/// <summary>
/// classify.corpus.build vertical slice
/// (FR-CLASSIFY-PRIVATE-CORPUS-BUILDER / DD-CLASSIFY-PRIVATE-CORPUS-PUBLICATION / bd-1cik).
/// Binds explicit labels through <see cref="ClassificationProjectionCorpusMapper"/>,
/// publishes via <see cref="PrivateCorpusWriter"/>, and records only a path-free aggregate
/// terminal result in the existing operation_idempotency store after durable rename.
/// Never mutates Ledger or CLASSIFY financial state; never returns outputPath.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BuildPrivateClassificationCorpusCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly PrivateCorpusWriter writer;
    private readonly PrivateCorpusReader reader;
    private readonly TimeProvider timeProvider;

    public BuildPrivateClassificationCorpusCommand(
        ClassifyStateStore stateStore,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        PrivateCorpusWriter? writer = null,
        PrivateCorpusReader? reader = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        this.stateStore = stateStore;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.reader = reader ?? new PrivateCorpusReader();
        this.writer = writer ?? new PrivateCorpusWriter(this.reader);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <param name="activeCategories">
    /// Active Spend Category catalogue from the same fresh classification_v1 projection
    /// that produced <see cref="ClassifyCorpusBuildRequest.Projection"/> (required for suggestion labels).
    /// </param>
    public async Task<CommandResult<ClassifyCorpusBuildResult>> HandleAsync(
        ClassifyCorpusBuildRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken,
        IReadOnlyList<ClassificationCategoryIdentity>? activeCategories = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyOperatorErgonomicsContracts.TryValidate(input, out var validationError)
            || validationError is not null)
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(
                validationError ?? ClassifyErrors.InvalidInput);
        }

        // Absolute destination required. Raw path never enters terminal receipt; request fingerprint
        // binds a one-way destination digest so another path under the same key conflicts.
        var destination = input.OutputPath.Trim();
        if (!Path.IsPathFullyQualified(destination)
            || destination.Contains('\0', StringComparison.Ordinal))
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.PrivacyRejected);
        }

        // Canonical absolute form (no trailing separator) so binding is stable.
        if (destination.Length > 1
            && (destination.EndsWith(Path.DirectorySeparatorChar)
                || destination.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            destination = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var idempotencyKey = input.IdempotencyKey.Trim();

        // Fingerprint uses the request's OutputPath binding (canonical absolute digest).
        var fingerprintRequest = input with { OutputPath = destination };
        var fingerprintElement = ClassifyContractMapper.ToCorpusBuildFingerprintElement(fingerprintRequest);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyContractMapper.CorpusBuildOperationId,
            ClassifyOperationIds.ContractVersion,
            actorKind,
            actorLabel,
            actorRunId,
            fingerprintElement);

        var probed = await LookupReplayAsync(idempotencyKey, requestFingerprint, cancellationToken);
        if (probed is not null)
        {
            return probed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(PrivateCorpusLimits.MaxProcessingTimeMs));
        var ct = timeout.Token;

        try
        {
            // Exact-label mapping — no invention; fails closed on stale/ineligible/invalid.
            var exactLabels = input.Labels
                .Select(ClassifyContractMapper.ToExactLabel)
                .ToArray();
            if (!ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
                    exactLabels,
                    input.Projection.Items,
                    activeCategories,
                    out var rows,
                    out var mapError))
            {
                return CommandResult<ClassifyCorpusBuildResult>.Failure(
                    mapError ?? ClassifyErrors.LabelInvalid);
            }

            var projectionFingerprint = ClassifyContractMapper.ComputeCorpusProjectionFingerprint(
                input.Projection);
            var now = timeProvider.GetUtcNow();
            var buildId = ClassifyContractMapper.NewRuleVersionId(now);
            var createdAtUtc = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

            // Recovery: post-rename / pre-idempotency crash window.
            // Requires BOTH the destination bound into this request fingerprint AND an exact
            // corpus fingerprint match for the rows that would be written. Never replace different content.
            var expectedBytes = EncodeRowsForFingerprint(rows);
            var expectedFingerprint = CorpusFingerprint.FromExactBytes(expectedBytes);

            if (PathExists(destination))
            {
                var recovered = await TryRecoverExistingDestinationAsync(
                    destination,
                    expectedFingerprint,
                    ct);
                if (recovered is null)
                {
                    return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.DestinationExists);
                }

                var recoveredResult = ClassifyContractMapper.ToCorpusBuildResult(
                    buildId,
                    requestFingerprint,
                    projectionFingerprint,
                    input.Projection.StoreGenerationFingerprint,
                    input.Projection.CatalogueFingerprint,
                    input.Projection.NormalizationVersion,
                    input.Labels.Count,
                    recovered.RowCount,
                    recovered.Fingerprint!.ByteLength,
                    recovered.Fingerprint.Sha256Hex,
                    replayed: false);

                return await CommitTerminalAsync(
                    idempotencyKey,
                    requestFingerprint,
                    recoveredResult,
                    createdAtUtc,
                    ct);
            }

            ct.ThrowIfCancellationRequested();
            var published = await writer.PublishAsync(destination, rows, ct);
            if (!published.IsSuccess || published.Fingerprint is null)
            {
                return CommandResult<ClassifyCorpusBuildResult>.Failure(
                    ClassifyContractMapper.MapCorpusPublishError(published.ErrorCode));
            }

            // Durable publication complete — only now record terminal success.
            var publicResult = ClassifyContractMapper.ToCorpusBuildResult(
                buildId,
                requestFingerprint,
                projectionFingerprint,
                input.Projection.StoreGenerationFingerprint,
                input.Projection.CatalogueFingerprint,
                input.Projection.NormalizationVersion,
                input.Labels.Count,
                published.WrittenRowCount,
                published.WrittenByteCount,
                published.Fingerprint.Sha256Hex,
                replayed: false);

            return await CommitTerminalAsync(
                idempotencyKey,
                requestFingerprint,
                publicResult,
                createdAtUtc,
                ct);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException)
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.Unexpected);
        }
    }

    private async Task<CommandResult<ClassifyCorpusBuildResult>> CommitTerminalAsync(
        string idempotencyKey,
        string requestFingerprint,
        ClassifyCorpusBuildResult publicResult,
        string createdAtUtc,
        CancellationToken cancellationToken)
    {
        return await stateStore.ExecuteWriteAsync(
            async (connection, transaction, writeCt) =>
            {
                var existing = await idempotencyStore.FindAsync(
                    connection, transaction, idempotencyKey, writeCt);
                var lookup = idempotencyStore.Resolve(
                    existing,
                    ClassifyContractMapper.CorpusBuildOperationId,
                    ClassifyOperationIds.ContractVersion,
                    requestFingerprint);
                switch (lookup.Disposition)
                {
                    case ClassifyIdempotencyDisposition.Replay:
                        return ReplayOrIntegrity(lookup.Record!);
                    case ClassifyIdempotencyDisposition.Conflict:
                        return CommandResult<ClassifyCorpusBuildResult>.Failure(
                            ClassifyErrors.IdempotencyConflict);
                    case ClassifyIdempotencyDisposition.Miss:
                        break;
                    default:
                        return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.Unexpected);
                }

                await idempotencyStore.CommitAsync(
                    connection,
                    transaction,
                    new ClassifyOperationIdempotencyRow(
                        idempotencyKey,
                        ClassifyContractMapper.CorpusBuildOperationId,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint,
                        ClassifyContractMapper.SerializeCorpusBuildResult(publicResult),
                        createdAtUtc),
                    writeCt);

                return CommandResult<ClassifyCorpusBuildResult>.Success(publicResult);
            },
            cancellationToken);
    }

    private async Task<CommandResult<ClassifyCorpusBuildResult>?> LookupReplayAsync(
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);
        // Read-only probe — no write transaction required for the idempotency lookup.
        var existing = await idempotencyStore.FindAsync(
            connection,
            transaction: null!,
            idempotencyKey,
            cancellationToken);
        var lookup = idempotencyStore.Resolve(
            existing,
            ClassifyContractMapper.CorpusBuildOperationId,
            ClassifyOperationIds.ContractVersion,
            requestFingerprint);
        return lookup.Disposition switch
        {
            ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
            ClassifyIdempotencyDisposition.Conflict =>
                CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.IdempotencyConflict),
            _ => null
        };
    }

    private static CommandResult<ClassifyCorpusBuildResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        var prior = ClassifyContractMapper.TryDeserializeCorpusBuildResult(record.TerminalResult);
        if (prior is null)
        {
            return CommandResult<ClassifyCorpusBuildResult>.Failure(ClassifyErrors.Integrity);
        }

        var replayed = prior with { Replayed = true };
        return CommandResult<ClassifyCorpusBuildResult>.Success(replayed);
    }

    private async Task<PrivateCorpusReadResult?> TryRecoverExistingDestinationAsync(
        string destination,
        CorpusFingerprint expectedFingerprint,
        CancellationToken cancellationToken)
    {
        var read = await reader.ReadAsync(destination, cancellationToken);
        if (!read.IsSuccess || read.Fingerprint is null)
        {
            return null;
        }

        if (!read.Fingerprint.Equals(expectedFingerprint))
        {
            return null;
        }

        return read;
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static byte[] EncodeRowsForFingerprint(IReadOnlyList<PrivateCorpusRow> rows)
    {
        using var buffer = new MemoryStream();
        foreach (var row in rows.OrderBy(r => r.Ordinal).ThenBy(r => r.TransactionId, StringComparer.Ordinal))
        {
            var line = JsonSerializer.SerializeToUtf8Bytes(
                row,
                PrivateCorpusJsonContext.Default.PrivateCorpusRow);
            buffer.Write(line, 0, line.Length);
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }
}
