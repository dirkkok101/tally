using Microsoft.Data.Sqlite;
using Tally.Contracts.Budget.Plans;

namespace Tally.Infrastructure.Budget.Storage;

public sealed record BudgetPlanRow(
    string PlanId,
    string PeriodStart,
    string PeriodEndExclusive,
    string CurrencyCode,
    string? ActiveRevisionId,
    string CreatedAtUtc);

public sealed record BudgetPlanRevisionRow(
    string RevisionId,
    string PlanId,
    int RevisionNumber,
    BudgetRevisionStatus Status,
    string ActorKind,
    string ActorLabel,
    string? ActorRunId,
    string Reason,
    string CreatedAtUtc,
    string CategoryContractVersion,
    string PayloadHash,
    string? ActivatedAtUtc,
    string? SupersededAtUtc,
    string? SupersededByRevisionId);

public sealed record BudgetPlanEntryRow(
    string RevisionId,
    string CategoryId,
    long PlannedMinorUnits);

public sealed record BudgetLifecycleEventRow(
    string EventId,
    string PlanId,
    string RevisionId,
    string EventType,
    string ActorKind,
    string ActorLabel,
    string? ActorRunId,
    string Reason,
    string OccurredAtUtc,
    string? PriorStatus,
    string? ResultingStatus,
    string? ReplacementRevisionId,
    int EventSequence);

public sealed record BudgetIdempotencyRow(
    string KeyDigest,
    string ContractVersion,
    string OperationId,
    string RequestHash,
    string State,
    string? PlanId,
    string? ResultRevisionId,
    string? PriorActiveRevisionId,
    string LifecycleEventIds,
    string ResultHash,
    string CreatedAtUtc,
    string CompletedAtUtc);

/// <summary>
/// Maps SqliteDataReader columns to typed BUDGET storage rows (DM-BUDGET-*).
/// </summary>
public static class BudgetRowMapper
{
    public static BudgetPlanRow MapPlan(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5));

    public static BudgetPlanRevisionRow MapRevision(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        ParseStatus(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    public static BudgetPlanEntryRow MapEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2));

    public static BudgetLifecycleEventRow MapLifecycleEvent(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt32(12));

    public static BudgetIdempotencyRow MapIdempotency(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetString(11));

    public static string FormatStatus(BudgetRevisionStatus status) => status switch
    {
        BudgetRevisionStatus.Draft => "Draft",
        BudgetRevisionStatus.Active => "Active",
        BudgetRevisionStatus.Superseded => "Superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown budget revision status.")
    };

    public static BudgetRevisionStatus ParseStatus(string value) => value switch
    {
        "Draft" => BudgetRevisionStatus.Draft,
        "Active" => BudgetRevisionStatus.Active,
        "Superseded" => BudgetRevisionStatus.Superseded,
        _ => throw new InvalidOperationException($"Unknown budget revision status '{value}'.")
    };

    /// <summary>
    /// Canonical ordered lifecycle event id list (stable references only — no financial payloads).
    /// </summary>
    public static string FormatLifecycleEventIds(IReadOnlyList<string> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        return string.Join(',', eventIds);
    }

    public static IReadOnlyList<string> ParseLifecycleEventIds(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return [];
        }

        return encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
