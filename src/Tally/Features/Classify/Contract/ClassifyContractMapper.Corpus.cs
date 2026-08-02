using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Corpus;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure aggregate-only mapping for classify.corpus.build
/// (DM-CLASSIFY-PRIVATE-CORPUS-BUILD / FR-CLASSIFY-PRIVATE-CORPUS-BUILDER / bd-1cik).
/// Never serializes raw outputPath, labels, rows, descriptions, amounts, or raw projection items
/// into durable terminal receipts. Destination is bound only via a one-way SHA-256 digest.
/// </summary>
public static partial class ClassifyContractMapper
{
    /// <summary>Published operation identity for corpus.build (registry wiring is bd-rly1).</summary>
    public const string CorpusBuildOperationId = "classify.corpus.build";

    /// <summary>
    /// Canonical request fingerprint element for corpus.build.
    /// Binds the absolute destination via a one-way SHA-256 digest (never the raw path).
    /// Excludes the idempotency key (store identity). Includes contract version, ordered labels,
    /// and projection identity.
    /// </summary>
    public static JsonElement ToCorpusBuildFingerprintElement(ClassifyCorpusBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Projection);
        ArgumentNullException.ThrowIfNull(request.Labels);

        var destinationBinding = ComputeDestinationBinding(request.OutputPath);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            // Sorted keys: contractVersion, destinationBinding, labels, projection
            writer.WriteString("contractVersion", request.ContractVersion);
            writer.WriteString("destinationBinding", destinationBinding);
            writer.WritePropertyName("labels");
            writer.WriteStartArray();
            foreach (var label in request.Labels
                         .OrderBy(l => l.TransactionId, StringComparer.Ordinal)
                         .ThenBy(l => l.ExpectedOutcome.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("expectedCategoryId", label.ExpectedCategoryId);
                writer.WriteString("expectedOutcome", FormatOutcomeKind(label.ExpectedOutcome));
                writer.WriteString("transactionId", label.TransactionId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("projection");
            WriteProjectionIdentity(writer, request.Projection);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// One-way binding of a canonical absolute destination path into a 64-char hex digest.
    /// Never embeds the raw path in durable state or public receipts.
    /// </summary>
    public static string ComputeDestinationBinding(string? absoluteOutputPath)
    {
        if (string.IsNullOrWhiteSpace(absoluteOutputPath))
        {
            return CanonicalClassificationHasher.HashUtf8(string.Empty);
        }

        // Canonical form: trimmed absolute path (no trailing separator except root).
        var path = absoluteOutputPath.Trim();
        if (path.Length > 1
            && (path.EndsWith(Path.DirectorySeparatorChar)
                || path.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }

    /// <summary>
    /// Projection identity fingerprint over contract versions, generation, snapshot, catalogue,
    /// normalization, and ordered item lifecycle tuples — never descriptions or amounts alone.
    /// </summary>
    public static string ComputeCorpusProjectionFingerprint(ClassifyCorpusBuildProjectionEnvelope projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var itemLines = (projection.Items ?? Array.Empty<ClassificationProjectionItem>())
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .Select(i => string.Join(
                '\u001f',
                i.Ordinal.ToString(CultureInfo.InvariantCulture),
                i.TransactionId,
                i.AccountId,
                ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(i),
                i.CategoryMutationState.ToString()));
        return CanonicalClassificationHasher.HashParts(
            projection.LedgerContractVersion,
            projection.ProjectionVersion,
            projection.StoreGenerationFingerprint,
            projection.SnapshotId,
            projection.CatalogueFingerprint,
            projection.NormalizationVersion,
            CanonicalClassificationHasher.HashOrderedLines(itemLines));
    }

    public static ClassifyCorpusBuildResult ToCorpusBuildResult(
        string buildId,
        string idempotencyFingerprint,
        string projectionFingerprint,
        string storeGenerationFingerprint,
        string catalogueFingerprint,
        string normalizationVersion,
        int labelCount,
        int writtenRowCount,
        long writtenByteCount,
        string corpusFingerprint,
        bool replayed) =>
        new(
            ClassifyOperationIds.ContractVersion,
            buildId,
            idempotencyFingerprint,
            projectionFingerprint,
            storeGenerationFingerprint,
            catalogueFingerprint,
            normalizationVersion,
            labelCount,
            writtenRowCount,
            writtenByteCount,
            corpusFingerprint,
            ClassifyCorpusBuildTerminalState.Completed,
            replayed);

    public static string SerializeCorpusBuildResult(ClassifyCorpusBuildResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);

    public static ClassifyCorpusBuildResult? TryDeserializeCorpusBuildResult(string terminalResult)
    {
        try
        {
            return JsonSerializer.Deserialize(
                terminalResult,
                ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Map private-corpus / writer boundary codes onto published CLASSIFY codes for the process envelope.
    /// </summary>
    public static string MapCorpusPublishError(string? errorCode) => errorCode switch
    {
        null => ClassifyErrors.Unexpected,
        ClassifyErrors.DestinationExists => ClassifyErrors.DestinationExists,
        ClassifyErrors.PrivacyRejected => ClassifyErrors.PrivacyRejected,
        ClassifyErrors.LabelInvalid => ClassifyErrors.LabelInvalid,
        ClassifyErrors.Stale => ClassifyErrors.Stale,
        ClassifyErrors.ResourceLimit => ClassifyErrors.ResourceLimit,
        ClassifyErrors.LedgerIncompatible => ClassifyErrors.LedgerIncompatible,
        ClassifyErrors.Integrity => ClassifyErrors.Integrity,
        PrivateCorpusErrors.PathRequired => ClassifyErrors.PrivacyRejected,
        PrivateCorpusErrors.NotFound => ClassifyErrors.PrivacyRejected,
        PrivateCorpusErrors.SymlinkRejected => ClassifyErrors.PrivacyRejected,
        PrivateCorpusErrors.OwnerRejected => ClassifyErrors.PrivacyRejected,
        PrivateCorpusErrors.PermissionsRejected => ClassifyErrors.PrivacyRejected,
        PrivateCorpusErrors.NotRegularFile => ClassifyErrors.PrivacyRejected,
        PrivateCorpusErrors.LimitExceeded => ClassifyErrors.ResourceLimit,
        PrivateCorpusErrors.Timeout => ClassifyErrors.ResourceLimit,
        PrivateCorpusErrors.Cancelled => ClassifyErrors.Unexpected,
        PrivateCorpusErrors.Malformed => ClassifyErrors.Integrity,
        PrivateCorpusErrors.DuplicateOrdinal => ClassifyErrors.Integrity,
        PrivateCorpusErrors.FieldInvalid => ClassifyErrors.LabelInvalid,
        PrivateCorpusErrors.ReadFailed => ClassifyErrors.Unexpected,
        _ => errorCode.StartsWith("CLASSIFY-", StringComparison.Ordinal)
            ? errorCode
            : ClassifyErrors.Unexpected
    };

    public static ClassificationProjectionCorpusMapper.ExactLabel ToExactLabel(ClassifyCorpusBuildLabel label) =>
        new(
            label.TransactionId,
            label.ExpectedOutcome,
            label.ExpectedCategoryId);

    private static void WriteProjectionIdentity(Utf8JsonWriter writer, ClassifyCorpusBuildProjectionEnvelope projection)
    {
        writer.WriteStartObject();
        writer.WriteString("catalogueFingerprint", projection.CatalogueFingerprint);
        writer.WriteString("ledgerContractVersion", projection.LedgerContractVersion);
        writer.WriteString("normalizationVersion", projection.NormalizationVersion);
        writer.WritePropertyName("orderedItems");
        writer.WriteStartArray();
        foreach (var item in (projection.Items ?? Array.Empty<ClassificationProjectionItem>())
                     .OrderBy(i => i.Ordinal)
                     .ThenBy(i => i.TransactionId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("accountId", item.AccountId);
            writer.WriteString(
                "lifecycle",
                ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(item));
            writer.WriteNumber("ordinal", item.Ordinal);
            writer.WriteString("transactionId", item.TransactionId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("projectionVersion", projection.ProjectionVersion);
        writer.WriteString("snapshotId", projection.SnapshotId);
        writer.WriteString("storeGenerationFingerprint", projection.StoreGenerationFingerprint);
        writer.WriteEndObject();
    }

    private static string FormatOutcomeKind(ClassifyOutcomeKind kind) => kind switch
    {
        ClassifyOutcomeKind.Suggestion => "suggestion",
        ClassifyOutcomeKind.NoSuggestion => "no_suggestion",
        ClassifyOutcomeKind.Conflict => "conflict",
        ClassifyOutcomeKind.Stale => "stale",
        _ => kind.ToString()
    };
}
