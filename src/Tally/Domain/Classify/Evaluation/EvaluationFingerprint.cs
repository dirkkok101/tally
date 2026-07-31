using System.Globalization;
using System.Text;

namespace Tally.Domain.Classify.Evaluation;

/// <summary>
/// Exact evaluation fingerprint dimensions for replay and staleness
/// (DM-CLASSIFY-EVALUATION-OUTCOME / FR-CLASSIFY-OUTCOME-INVALIDATION).
/// Pure value — no clock, network, or host-session inputs.
/// </summary>
public sealed class EvaluationFingerprint : IEquatable<EvaluationFingerprint>
{
    public const string DimensionLedgerContractVersion = "ledger_contract_version";
    public const string DimensionProjectionVersion = "projection_version";
    public const string DimensionStoreGeneration = "store_generation_fingerprint";
    public const string DimensionSnapshotId = "snapshot_id";
    public const string DimensionSnapshotExpiresAt = "snapshot_expires_at";
    public const string DimensionCategoryLifecycle = "category_lifecycle_fingerprint";
    public const string DimensionNormalizationVersion = "normalization_version";
    public const string DimensionRuleSetVersion = "rule_set_version_id";
    public const string DimensionOrderedItems = "ordered_items_fingerprint";

    /// <summary>All dimensions in stable discovery order.</summary>
    public static IReadOnlyList<string> AllDimensions { get; } =
    [
        DimensionLedgerContractVersion,
        DimensionProjectionVersion,
        DimensionStoreGeneration,
        DimensionSnapshotId,
        DimensionSnapshotExpiresAt,
        DimensionCategoryLifecycle,
        DimensionNormalizationVersion,
        DimensionRuleSetVersion,
        DimensionOrderedItems
    ];

    private EvaluationFingerprint(
        string ledgerContractVersion,
        string projectionVersion,
        string storeGenerationFingerprint,
        string snapshotId,
        string snapshotExpiresAt,
        string categoryLifecycleFingerprint,
        string normalizationVersion,
        string ruleSetVersionId,
        string orderedItemsFingerprint,
        string canonicalHash)
    {
        LedgerContractVersion = ledgerContractVersion;
        ProjectionVersion = projectionVersion;
        StoreGenerationFingerprint = storeGenerationFingerprint;
        SnapshotId = snapshotId;
        SnapshotExpiresAt = snapshotExpiresAt;
        CategoryLifecycleFingerprint = categoryLifecycleFingerprint;
        NormalizationVersion = normalizationVersion;
        RuleSetVersionId = ruleSetVersionId;
        OrderedItemsFingerprint = orderedItemsFingerprint;
        CanonicalHash = canonicalHash;
    }

    public string LedgerContractVersion { get; }
    public string ProjectionVersion { get; }
    public string StoreGenerationFingerprint { get; }
    public string SnapshotId { get; }
    public string SnapshotExpiresAt { get; }
    public string CategoryLifecycleFingerprint { get; }
    public string NormalizationVersion { get; }
    public string RuleSetVersionId { get; }
    public string OrderedItemsFingerprint { get; }

    /// <summary>SHA-256 hex over the canonical dimension payload.</summary>
    public string CanonicalHash { get; }

    public static EvaluationFingerprint Create(
        string ledgerContractVersion,
        string projectionVersion,
        string storeGenerationFingerprint,
        string snapshotId,
        string snapshotExpiresAt,
        string categoryLifecycleFingerprint,
        string normalizationVersion,
        string ruleSetVersionId,
        string orderedItemsFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerContractVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeGenerationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotExpiresAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryLifecycleFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizationVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleSetVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderedItemsFingerprint);

        var canonicalHash = CanonicalClassificationHasher.HashParts(
            ledgerContractVersion.Trim(),
            projectionVersion.Trim(),
            storeGenerationFingerprint.Trim(),
            snapshotId.Trim(),
            snapshotExpiresAt.Trim(),
            categoryLifecycleFingerprint.Trim(),
            normalizationVersion.Trim(),
            ruleSetVersionId.Trim(),
            orderedItemsFingerprint.Trim());

        return new EvaluationFingerprint(
            ledgerContractVersion.Trim(),
            projectionVersion.Trim(),
            storeGenerationFingerprint.Trim(),
            snapshotId.Trim(),
            snapshotExpiresAt.Trim(),
            categoryLifecycleFingerprint.Trim(),
            normalizationVersion.Trim(),
            ruleSetVersionId.Trim(),
            orderedItemsFingerprint.Trim(),
            canonicalHash);
    }

    /// <summary>
    /// Fingerprint of ordered projection membership: ordinal + transaction id + item lifecycle revision tuple.
    /// </summary>
    public static string ComputeOrderedItemsFingerprint(
        IEnumerable<(int Ordinal, string TransactionId, string ItemLifecycleFingerprint)> items) =>
        CanonicalClassificationHasher.HashOrderedLines(
            items
                .OrderBy(i => i.Ordinal)
                .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
                .Select(i => string.Concat(
                    i.Ordinal.ToString(CultureInfo.InvariantCulture),
                    '\t',
                    i.TransactionId,
                    '\t',
                    i.ItemLifecycleFingerprint)));

    /// <summary>
    /// Fingerprint of the active category catalogue (id + lifecycle state), ordered by category id.
    /// </summary>
    public static string ComputeCategoryLifecycleFingerprint(
        IEnumerable<(string CategoryId, string LifecycleState)> categories) =>
        CanonicalClassificationHasher.HashOrderedLines(
            categories
                .OrderBy(c => c.CategoryId, StringComparer.Ordinal)
                .Select(c => string.Concat(c.CategoryId, '\t', c.LifecycleState)));

    /// <summary>
    /// Returns dimension names that differ from <paramref name="other"/> (stable order).
    /// Empty when fingerprints are identical.
    /// </summary>
    public IReadOnlyList<string> DiffDimensions(EvaluationFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (string.Equals(CanonicalHash, other.CanonicalHash, StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        var diffs = new List<string>(AllDimensions.Count);
        void Check(string dimension, string left, string right)
        {
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                diffs.Add(dimension);
            }
        }

        Check(DimensionLedgerContractVersion, LedgerContractVersion, other.LedgerContractVersion);
        Check(DimensionProjectionVersion, ProjectionVersion, other.ProjectionVersion);
        Check(DimensionStoreGeneration, StoreGenerationFingerprint, other.StoreGenerationFingerprint);
        Check(DimensionSnapshotId, SnapshotId, other.SnapshotId);
        Check(DimensionSnapshotExpiresAt, SnapshotExpiresAt, other.SnapshotExpiresAt);
        Check(DimensionCategoryLifecycle, CategoryLifecycleFingerprint, other.CategoryLifecycleFingerprint);
        Check(DimensionNormalizationVersion, NormalizationVersion, other.NormalizationVersion);
        Check(DimensionRuleSetVersion, RuleSetVersionId, other.RuleSetVersionId);
        Check(DimensionOrderedItems, OrderedItemsFingerprint, other.OrderedItemsFingerprint);
        return diffs;
    }

    public string ToCanonicalJson()
    {
        var sb = new StringBuilder(512);
        sb.Append('{');
        Append(sb, "canonicalHash", CanonicalHash);
        sb.Append(',');
        Append(sb, DimensionCategoryLifecycle, CategoryLifecycleFingerprint);
        sb.Append(',');
        Append(sb, DimensionLedgerContractVersion, LedgerContractVersion);
        sb.Append(',');
        Append(sb, DimensionNormalizationVersion, NormalizationVersion);
        sb.Append(',');
        Append(sb, DimensionOrderedItems, OrderedItemsFingerprint);
        sb.Append(',');
        Append(sb, DimensionProjectionVersion, ProjectionVersion);
        sb.Append(',');
        Append(sb, DimensionRuleSetVersion, RuleSetVersionId);
        sb.Append(',');
        Append(sb, DimensionSnapshotExpiresAt, SnapshotExpiresAt);
        sb.Append(',');
        Append(sb, DimensionSnapshotId, SnapshotId);
        sb.Append(',');
        Append(sb, DimensionStoreGeneration, StoreGenerationFingerprint);
        sb.Append('}');
        return sb.ToString();
    }

    public bool Equals(EvaluationFingerprint? other) =>
        other is not null && string.Equals(CanonicalHash, other.CanonicalHash, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is EvaluationFingerprint other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalHash);

    private static void Append(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":\"");
        foreach (var ch in value)
        {
            if (ch is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        sb.Append('"');
    }
}
