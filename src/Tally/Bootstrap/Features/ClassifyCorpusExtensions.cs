using System.Runtime.Versioning;
using Tally.Domain.Classify.Normalization;
using Tally.Infrastructure.Classify.Corpus;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY private-corpus composition (no reflection / plugin scan).
/// Registers the one production <see cref="PrivateCorpusReader"/> implementation.
/// </summary>
[SupportedOSPlatform("linux")]
public static class ClassifyCorpusExtensions
{
    /// <summary>Create the production private corpus reader.</summary>
    public static PrivateCorpusReader CreateReader() => new();

    /// <summary>
    /// Build an aggregate-only gate input manifest from a successful corpus read.
    /// Never embeds paths, descriptions, amounts, or raw rows.
    /// </summary>
    public static OwnerRulebookGateInputManifest CreateGateManifest(
        PrivateCorpusReadResult readResult,
        string? normalizationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        return readResult.ToGateManifest(normalizationVersion ?? NormalizationDescriptor.V1.Version);
    }

    /// <summary>
    /// Build an aggregate owner benefit evidence receipt (counts / optional minutes only).
    /// </summary>
    public static OwnerBenefitEvidenceReceipt CreateBenefitReceipt(
        int ownerDecisionCountBefore,
        int ownerDecisionCountAfter,
        double? ownerMinutesBefore = null,
        double? ownerMinutesAfter = null) =>
        new(ownerDecisionCountBefore, ownerDecisionCountAfter, ownerMinutesBefore, ownerMinutesAfter);
}
