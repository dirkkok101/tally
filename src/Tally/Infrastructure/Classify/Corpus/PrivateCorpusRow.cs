using System.Text.Json.Serialization;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Rules;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// One private corpus JSONL row at the validation boundary
/// (DD-CLASSIFY-PRIVATE-VALIDATION / DM-CLASSIFY-VALIDATION-RUN).
/// Memory-only — never persisted to classify.db, logs, or tracked fixtures.
/// </summary>
public sealed record PrivateCorpusRow(
    [property: JsonRequired, JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonRequired, JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonRequired, JsonPropertyName("accountId")] string AccountId,
    [property: JsonRequired, JsonPropertyName("sourceDescription")] string SourceDescription,
    [property: JsonPropertyName("amountDirection")] string? AmountDirection,
    [property: JsonRequired, JsonPropertyName("amountAbsoluteMinor")] long AmountAbsoluteMinor,
    [property: JsonRequired, JsonPropertyName("itemLifecycleFingerprint")] string ItemLifecycleFingerprint,
    [property: JsonPropertyName("expectedCategoryId")] string? ExpectedCategoryId = null,
    [property: JsonPropertyName("expectedOutcomeKind")] string? ExpectedOutcomeKind = null)
{
    /// <summary>Map to the production evaluation engine item shape (no second evaluator).</summary>
    public ClassificationEvaluationItem ToEvaluationItem(
        IReadOnlyList<string>? itemStaleDimensions = null) =>
        new(
            Ordinal,
            TransactionId,
            AccountId,
            SourceDescription,
            AmountDirection,
            AmountAbsoluteMinor,
            ItemLifecycleFingerprint,
            itemStaleDimensions);
}

/// <summary>
/// Aggregate-only gate input identity (EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS).
/// Never includes paths, descriptions, amounts, or raw rows.
/// </summary>
public sealed record OwnerRulebookGateInputManifest(
    [property: JsonRequired] string CorpusFingerprint,
    [property: JsonRequired] long ByteLength,
    [property: JsonRequired] int RowCount,
    [property: JsonRequired] string NormalizationVersion);

/// <summary>
/// Aggregate owner benefit evidence (EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS).
/// Counts and optional minutes only — no private payload.
/// </summary>
public sealed record OwnerBenefitEvidenceReceipt(
    [property: JsonRequired] int OwnerDecisionCountBefore,
    [property: JsonRequired] int OwnerDecisionCountAfter,
    double? OwnerMinutesBefore = null,
    double? OwnerMinutesAfter = null);

/// <summary>Stable metadata-only private corpus error codes (no paths or payloads).</summary>
public static class PrivateCorpusErrors
{
    public const string PathRequired = "CLASSIFY-CORPUS-PATH-REQUIRED";
    public const string NotFound = "CLASSIFY-CORPUS-NOT-FOUND";
    public const string SymlinkRejected = "CLASSIFY-CORPUS-SYMLINK";
    public const string OwnerRejected = "CLASSIFY-CORPUS-OWNER";
    public const string PermissionsRejected = "CLASSIFY-CORPUS-PERMISSIONS";
    public const string NotRegularFile = "CLASSIFY-CORPUS-NOT-REGULAR";
    public const string Malformed = "CLASSIFY-CORPUS-MALFORMED";
    public const string DuplicateOrdinal = "CLASSIFY-CORPUS-DUPLICATE-ORDINAL";
    public const string LimitExceeded = "CLASSIFY-CORPUS-LIMIT";
    public const string Timeout = "CLASSIFY-CORPUS-TIMEOUT";
    public const string Cancelled = "CLASSIFY-CORPUS-CANCELLED";
    public const string ReadFailed = "CLASSIFY-CORPUS-READ-FAILED";
    public const string FieldInvalid = "CLASSIFY-CORPUS-FIELD-INVALID";
}

/// <summary>
/// Result of a private corpus read. On failure, only <see cref="ErrorCode"/> is meaningful —
/// messages never embed paths, descriptions, amounts, or raw rows.
/// </summary>
public sealed class PrivateCorpusReadResult
{
    private PrivateCorpusReadResult(
        bool isSuccess,
        string? errorCode,
        CorpusFingerprint? fingerprint,
        IReadOnlyList<PrivateCorpusRow>? rows)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        Fingerprint = fingerprint;
        Rows = rows;
    }

    public bool IsSuccess { get; }
    public string? ErrorCode { get; }
    public CorpusFingerprint? Fingerprint { get; }
    public IReadOnlyList<PrivateCorpusRow>? Rows { get; }

    public int RowCount => Rows?.Count ?? 0;

    public static PrivateCorpusReadResult Success(
        CorpusFingerprint fingerprint,
        IReadOnlyList<PrivateCorpusRow> rows) =>
        new(true, null, fingerprint, rows);

    public static PrivateCorpusReadResult Failure(string errorCode) =>
        new(false, errorCode, null, null);

    /// <summary>Aggregate-only gate manifest derived from a successful read.</summary>
    public OwnerRulebookGateInputManifest ToGateManifest(string normalizationVersion)
    {
        if (!IsSuccess || Fingerprint is null || Rows is null)
        {
            throw new InvalidOperationException("Gate manifest requires a successful corpus read.");
        }

        return new OwnerRulebookGateInputManifest(
            Fingerprint.Sha256Hex,
            Fingerprint.ByteLength,
            Rows.Count,
            normalizationVersion);
    }
}

internal sealed class PrivateCorpusLimitException(string errorCode) : Exception
{
    public string ErrorCode { get; } = errorCode;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PrivateCorpusRow))]
internal partial class PrivateCorpusJsonContext : JsonSerializerContext;
