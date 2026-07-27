using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Contracts.Budget.Plans;

namespace Tally.Domain.Budget.Plans;

/// <summary>
/// Immutable category amount row for one revision (DM-BUDGET-REVISION-ENTRY).
/// Explicit zero is preserved; omission has no row; display names are never authority.
/// </summary>
public sealed record BudgetPlanEntry(
    string CategoryId,
    long PlannedMinorUnits);

/// <summary>
/// Immutable Budget Plan Revision payload with separately mutable lifecycle status
/// (DM-BUDGET-REVISION-ENTRY / DD-BUDGET-PLAN-REVISION-LIFECYCLE).
/// </summary>
public sealed record BudgetPlanRevision(
    string RevisionId,
    string PlanId,
    int RevisionNumber,
    BudgetRevisionStatus Status,
    string ActorKind,
    string ActorLabel,
    string? ActorRunId,
    string Reason,
    DateTimeOffset CreatedAtUtc,
    string CategoryContractVersion,
    string PayloadHash,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? SupersededAtUtc,
    string? SupersededByRevisionId,
    IReadOnlyList<BudgetPlanEntry> Entries)
{
    public const string PayloadHashSchemaVersion = "budget-revision-payload-v1";

    /// <summary>Exact checked base-10 sum of planned minor units (not a separately stored authority).</summary>
    public long PlannedTotalMinorUnits()
    {
        long total = 0;
        foreach (var entry in Entries)
        {
            total = checked(total + entry.PlannedMinorUnits);
        }

        return total;
    }

    /// <summary>
    /// Canonical SHA-256 hex digest of immutable revision content: category contract version
    /// plus category-ID-sorted entries. Names and lifecycle status never participate.
    /// </summary>
    public static string ComputePayloadHash(
        string categoryContractVersion,
        IReadOnlyList<BudgetPlanEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryContractVersion);
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries
            .OrderBy(entry => entry.CategoryId, StringComparer.Ordinal)
            .ToArray();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", PayloadHashSchemaVersion);
            writer.WriteString("categoryContractVersion", categoryContractVersion);
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (var entry in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("categoryId", entry.CategoryId);
                writer.WriteNumber("plannedMinorUnits", entry.PlannedMinorUnits);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    /// <summary>Exact checked total for validated entry inputs before mutation.</summary>
    public static bool TrySumPlannedMinorUnits(
        IReadOnlyList<BudgetPlanEntry> entries,
        out long total)
    {
        ArgumentNullException.ThrowIfNull(entries);
        total = 0;
        try
        {
            foreach (var entry in entries)
            {
                if (entry.PlannedMinorUnits < 0)
                {
                    return false;
                }

                total = checked(total + entry.PlannedMinorUnits);
            }

            return true;
        }
        catch (OverflowException)
        {
            total = 0;
            return false;
        }
    }

    public static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
