using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;

namespace Tally.Contracts.Budget.Insights;

[JsonConverter(typeof(JsonStringEnumConverter<BudgetInsightPlanState>))]
public enum BudgetInsightPlanState
{
    [JsonStringEnumMemberName("bound_revision")]
    BoundRevision,
    [JsonStringEnumMemberName("no_budget_plan")]
    NoBudgetPlan,
    [JsonStringEnumMemberName("no_active_budget_plan_revision")]
    NoActiveBudgetPlanRevision
}

public sealed record GetBudgetInsightEvidenceInput(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] BudgetPeriodInput BudgetPeriod,
    string? RevisionId,
    int? MemberLimit);

public sealed record BudgetActualMember(
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string EffectiveDate,
    string? CategoryId,
    [property: JsonRequired] long BudgetActualMinorUnits,
    /// <summary>Frozen Spend Category ancestry root-first with the assigned category last; empty when unset.</summary>
    [property: JsonRequired] IReadOnlyList<string> AncestryIds,
    /// <summary>Effective Spend Category that governed the actual, or null for unbudgeted/uncategorized.</summary>
    string? EffectiveCategoryId);

/// <summary>
/// Coherent plan-state + dated-member evidence from one public LEDGER snapshot
/// (DM-BUDGET-INSIGHTS-READ-CONTRACT / DD-INSIGHTS-COHERENT-PUBLIC-EVIDENCE).
/// BoundRevision includes immutable plan detail and canonical BudgetPosition from the same
/// materialized members; plan-absence states omit plan, position, and calculation schema.
/// No pace, forecast, trend, anomaly, recommendation, narrative, alert, or report fields.
/// </summary>
public sealed record BudgetInsightEvidence(
    [property: JsonRequired] BudgetInsightPlanState PlanState,
    BudgetPlanRevisionDetail? Revision,
    BudgetPosition? Position,
    [property: JsonRequired] IReadOnlyList<BudgetActualMember> ActualMembers,
    [property: JsonRequired] long BudgetActualTotalMinorUnits,
    [property: JsonRequired] LedgerSnapshotEvidence Ledger,
    string? CalculationSchemaVersion,
    /// <summary>Released LEDGER category contract version cited by supplemental evidence (DM-BUDGET-INSIGHTS-READ-CONTRACT).</summary>
    string? CategoryContractVersion,
    [property: JsonRequired] string BindingFingerprint);

public sealed record GetBudgetInsightEvidenceResult(
    [property: JsonRequired] BudgetInsightEvidence Evidence);

/// <summary>
/// Pure evidence-binding helpers — no I/O, no calculator invocation.
/// </summary>
public static class BudgetInsightEvidenceBinding
{
    /// <summary>Canonical binding digest schema (DM-BUDGET-INSIGHTS-READ-CONTRACT).</summary>
    public const string BindingSchemaVersion = "budget-insight-evidence-binding-v1";

    /// <summary>
    /// Deterministic SHA-256 hex fingerprint over plan state, optional revision/calc schema,
    /// shared LEDGER snapshot provenance, exact total, and complete ordered membership.
    /// Proves one coherent binding; consumers recompute to detect drift.
    /// </summary>
    public static string ComputeBindingFingerprint(
        BudgetInsightPlanState planState,
        string? revisionId,
        string? calculationSchemaVersion,
        LedgerSnapshotEvidence ledger,
        long budgetActualTotalMinorUnits,
        IReadOnlyList<BudgetActualMember> actualMembers,
        string? categoryContractVersion = null,
        IReadOnlyList<CategoryLifecycleEvidence>? categoryEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(actualMembers);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", BindingSchemaVersion);
            writer.WriteString("planState", PlanStateWireName(planState));
            if (revisionId is not null)
            {
                writer.WriteString("revisionId", revisionId);
            }
            else
            {
                writer.WriteNull("revisionId");
            }

            if (calculationSchemaVersion is not null)
            {
                writer.WriteString("calculationSchemaVersion", calculationSchemaVersion);
            }
            else
            {
                writer.WriteNull("calculationSchemaVersion");
            }

            if (categoryContractVersion is not null)
            {
                writer.WriteString("categoryContractVersion", categoryContractVersion);
            }
            else
            {
                writer.WriteNull("categoryContractVersion");
            }

            writer.WritePropertyName("ledger");
            writer.WriteStartObject();
            writer.WriteString("contractVersion", ledger.ContractVersion);
            writer.WriteString("snapshotId", ledger.SnapshotId);
            writer.WriteString("storeGenerationFingerprint", ledger.StoreGenerationFingerprint);
            writer.WriteEndObject();

            writer.WriteNumber("budgetActualTotalMinorUnits", budgetActualTotalMinorUnits);
            writer.WriteNumber("memberCount", actualMembers.Count);
            writer.WritePropertyName("members");
            writer.WriteStartArray();
            foreach (var member in actualMembers)
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", member.Ordinal);
                writer.WriteString("transactionId", member.TransactionId);
                writer.WriteString("effectiveDate", member.EffectiveDate);
                if (member.CategoryId is not null)
                {
                    writer.WriteString("categoryId", member.CategoryId);
                }
                else
                {
                    writer.WriteNull("categoryId");
                }

                writer.WriteNumber("budgetActualMinorUnits", member.BudgetActualMinorUnits);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WritePropertyName("categoryEvidence");
            writer.WriteStartArray();
            if (categoryEvidence is not null)
            {
                foreach (var item in categoryEvidence.OrderBy(e => e.CategoryId, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("categoryId", item.CategoryId);
                    writer.WriteString("lifecycle", item.Lifecycle switch
                    {
                        CategoryLifecycleStatus.Active => "active",
                        CategoryLifecycleStatus.Archived => "archived",
                        _ => "unknown"
                    });
                    writer.WriteString("categoryContractVersion", item.CategoryContractVersion);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    /// <summary>
    /// Checked exact sum of dated members; throws <see cref="OverflowException"/> on overflow.
    /// </summary>
    public static long CheckedMemberSum(IReadOnlyList<BudgetActualMember> actualMembers)
    {
        ArgumentNullException.ThrowIfNull(actualMembers);
        long total = 0;
        foreach (var member in actualMembers)
        {
            total = checked(total + member.BudgetActualMinorUnits);
        }

        return total;
    }

    private static string PlanStateWireName(BudgetInsightPlanState planState) => planState switch
    {
        BudgetInsightPlanState.BoundRevision => "bound_revision",
        BudgetInsightPlanState.NoBudgetPlan => "no_budget_plan",
        BudgetInsightPlanState.NoActiveBudgetPlanRevision => "no_active_budget_plan_revision",
        _ => planState.ToString()
    };
}
