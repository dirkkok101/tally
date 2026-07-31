using Microsoft.Data.Sqlite;

namespace Tally.Infrastructure.Classify.Storage;

public sealed record ClassifyStoreMetaRow(int SchemaVersion, string StoreId, string CreatedAt);

public sealed record ClassifyOperationIdempotencyRow(
    string IdempotencyKey,
    string OperationId,
    string ContractVersion,
    string RequestFingerprint,
    string TerminalResult,
    string CreatedAt);

public sealed record ClassifyActiveNormalizationRow(
    int SingletonId,
    string NormalizationVersion,
    long ActivationEpoch);

public sealed record ClassifyAbandonmentTombstoneRow(
    string TombstoneId,
    string SubjectType,
    string SubjectId,
    string Reason,
    string Actor,
    string AbandonedAt,
    int RemovedPayloadCount);

public sealed record ClassifyCleanupEventRow(
    string CleanupId,
    string PolicyVersion,
    int RecognizedRemovedCount,
    int ExpiredPreviewCount,
    int AbandonedPayloadCount,
    string Actor,
    string OccurredAt);

public sealed record ClassifyRuleRow(string RuleId, string CreatedAt, string CreatedBy);

public sealed record ClassifyRuleVersionRow(
    string RuleVersionId,
    string RuleId,
    string? PriorVersionId,
    string NormalizationVersion,
    string CategoryId,
    string ScopeHash,
    string RuleOrigin,
    string? SourceFeedbackId,
    string Reason,
    string LifecycleState,
    int BroadApplyAllowed,
    string? ValidationRunId,
    string CreatedAt,
    string CreatedBy);

public sealed record ClassifyEvaluationRunRow(
    string EvaluationId,
    string? OperationIdempotencyKey,
    string RuleSetVersionId,
    string NormalizationVersion,
    string LedgerContractVersion,
    string ProjectionVersion,
    string StoreGenerationFingerprint,
    string SnapshotId,
    string SnapshotExpiresAt,
    string CategoryLifecycleFingerprint,
    string OrderedItemsFingerprint,
    int InputCount,
    int SuggestionCount,
    int NoSuggestionCount,
    int ConflictCount,
    int StaleCount,
    string LifecycleState,
    string Actor,
    string CreatedAt);

public sealed record ClassifyOutcomeRow(
    string OutcomeId,
    string EvaluationId,
    int Ordinal,
    string TransactionId,
    string OutcomeType,
    string? CategoryId,
    string ItemLifecycleFingerprint,
    string SafeReason);

/// <summary>
/// Pure SqliteDataReader → typed row mapping for CLASSIFY storage (DM-CLASSIFY-*).
/// </summary>
public static class ClassifyRowMapper
{
    public static ClassifyStoreMetaRow MapStoreMeta(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2));

    public static ClassifyOperationIdempotencyRow MapIdempotency(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5));

    public static ClassifyActiveNormalizationRow MapActiveNormalization(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetInt64(2));

    public static ClassifyAbandonmentTombstoneRow MapAbandonment(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt32(6));

    public static ClassifyCleanupEventRow MapCleanupEvent(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetString(5),
        reader.GetString(6));

    public static ClassifyRuleRow MapRule(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2));

    public static ClassifyRuleVersionRow MapRuleVersion(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetString(12),
        reader.GetString(13));

    public static ClassifyEvaluationRunRow MapEvaluationRun(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetInt32(11),
        reader.GetInt32(12),
        reader.GetInt32(13),
        reader.GetInt32(14),
        reader.GetInt32(15),
        reader.GetString(16),
        reader.GetString(17),
        reader.GetString(18));

    public static ClassifyOutcomeRow MapOutcome(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7));
}
