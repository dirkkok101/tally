using System.Runtime.Versioning;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Domain.Classify.Discovery;
using Tally.Domain.Classify.Rules;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;

// ClassifyRuleVersionRow / RuleLifecycleTimestamps are Linux-supported infrastructure types.
// This mapper is pure; callers are Linux-only discovery handlers.
#pragma warning disable CA1416

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure mapping for classify.rule.list and classify.rule-set.active.get
/// (DM-CLASSIFY-RULE-DISCOVERY / FR-CLASSIFY-RULEBOOK-DISCOVERY / bd-2vbg).
/// Closed predicate contract only; never maps owner reason prose or corpus metadata.
/// </summary>
public static partial class ClassifyContractMapper
{
    public static string FormatStoredLifecycle(ClassifyRuleLifecycleFilter lifecycle) => lifecycle switch
    {
        ClassifyRuleLifecycleFilter.Draft => RuleLifecyclePolicy.StateDraft,
        ClassifyRuleLifecycleFilter.Active => RuleLifecyclePolicy.StateActive,
        ClassifyRuleLifecycleFilter.Retired => RuleLifecyclePolicy.StateRetired,
        ClassifyRuleLifecycleFilter.Superseded => RuleLifecyclePolicy.StateSuperseded,
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown lifecycle filter.")
    };

    public static ClassifyRuleLifecycleFilter ToPublicLifecycle(string storedLifecycle) => storedLifecycle switch
    {
        RuleLifecyclePolicy.StateDraft => ClassifyRuleLifecycleFilter.Draft,
        RuleLifecyclePolicy.StateValidated => ClassifyRuleLifecycleFilter.Draft, // validated drafts still non-active catalogue
        RuleLifecyclePolicy.StateActive => ClassifyRuleLifecycleFilter.Active,
        RuleLifecyclePolicy.StateActiveBroadApply => ClassifyRuleLifecycleFilter.Active,
        RuleLifecyclePolicy.StateRetired => ClassifyRuleLifecycleFilter.Retired,
        RuleLifecyclePolicy.StateSuperseded => ClassifyRuleLifecycleFilter.Superseded,
        _ => throw new ArgumentOutOfRangeException(nameof(storedLifecycle), storedLifecycle, "Unknown stored lifecycle.")
    };

    public static ClassifyRuleProvenanceKind ToProvenance(string ruleOrigin) => ruleOrigin switch
    {
        "owner_authored" => ClassifyRuleProvenanceKind.OwnerAuthored,
        "feedback_derived" => ClassifyRuleProvenanceKind.FeedbackDerived,
        _ => throw new ArgumentOutOfRangeException(nameof(ruleOrigin), ruleOrigin, "Unknown rule origin.")
    };

    public static ClassifyCategoryLifecycleState ToCategoryLifecycle(string? lifecycleState) =>
        string.Equals(lifecycleState, "active", StringComparison.Ordinal)
            ? ClassifyCategoryLifecycleState.Active
            : ClassifyCategoryLifecycleState.Archived;

    /// <summary>
    /// Map one durable rule version + conditions + membership + category display into a list item.
    /// Never includes owner reason or corpus paths.
    /// </summary>
    public static bool TryMapRuleListItem(
        ClassifyRuleVersionRow version,
        IReadOnlyList<RuleCondition> conditions,
        bool activeMembership,
        string? categoryDisplayName,
        string categoryLifecycleState,
        RuleLifecycleTimestamps timestamps,
        out ClassifyRuleListItem item,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(timestamps);
        item = null!;
        errorCode = null;

        ClassifyRuleLifecycleFilter effective;
        ClassifyRuleProvenanceKind provenance;
        try
        {
            // Active membership is durable authority superseding the immutable draft row state
            // (activation does not rewrite rule_version.lifecycle_state in-place).
            effective = activeMembership
                ? (version.BroadApplyAllowed != 0
                    ? ClassifyRuleLifecycleFilter.Active
                    : ClassifyRuleLifecycleFilter.Active)
                : ToPublicLifecycle(version.LifecycleState);
            provenance = ToProvenance(version.RuleOrigin);
        }
        catch (ArgumentOutOfRangeException)
        {
            errorCode = ClassifyErrors.Integrity;
            return false;
        }

        IReadOnlyList<ClassificationRuleConditionInput> wireConditions;
        try
        {
            wireConditions = ToConditionInputs(conditions);
        }
        catch (ArgumentOutOfRangeException)
        {
            errorCode = ClassifyErrors.Integrity;
            return false;
        }

        item = new ClassifyRuleListItem(
            LogicalRuleId: version.RuleId,
            RuleVersionId: version.RuleVersionId,
            PriorRuleVersionId: version.PriorVersionId,
            CategoryId: version.CategoryId,
            CategoryDisplayName: categoryDisplayName,
            CategoryLifecycle: ToCategoryLifecycle(categoryLifecycleState),
            NormalizationVersion: version.NormalizationVersion,
            EffectiveLifecycle: effective,
            ActiveMembership: activeMembership,
            BroadApplyAllowed: version.BroadApplyAllowed != 0,
            Provenance: provenance,
            ScopeHash: version.ScopeHash,
            CreatedAt: version.CreatedAt,
            ValidatedAt: timestamps.ValidatedAt,
            ActivatedAt: timestamps.ActivatedAt,
            RetiredAt: timestamps.RetiredAt,
            Conditions: wireConditions);
        return true;
    }

    public static ClassifyRuleListResult ToRuleListResult(
        int overallCount,
        int filteredCount,
        IReadOnlyList<ClassifyRuleListItem> items,
        string? continuation) =>
        new(
            ContractVersion: ClassifyOperationIds.ContractVersion,
            OverallCount: overallCount,
            FilteredCount: filteredCount,
            ReturnedCount: items.Count,
            Items: items,
            Continuation: continuation);

    public static string RuleListFilterFingerprint(
        string? logicalRuleId,
        ClassifyRuleLifecycleFilter? lifecycle,
        string? categoryId,
        bool? activeMembership) =>
        ClassifyDiscoveryFilterFingerprint.ForRuleList(
            logicalRuleId,
            lifecycle,
            categoryId,
            activeMembership);

    public static ClassifyRuleSetActiveGetResult ToActiveRuleSetResult(
        string ruleSetVersionId,
        bool broadApplyAllowed,
        string activationId,
        string validationId,
        string? trustedGateReceiptId,
        string? trustedGateReceiptFingerprint,
        string normalizationVersion,
        string activationEpoch,
        string activatedAt,
        string? retiredAt,
        IReadOnlyList<string> ruleVersionIds,
        IReadOnlyList<ClassifyActiveRuleSetCategory> categories) =>
        new(
            ContractVersion: ClassifyOperationIds.ContractVersion,
            RuleSetVersionId: ruleSetVersionId,
            BroadApplyAllowed: broadApplyAllowed,
            ActivationId: activationId,
            ValidationId: validationId,
            TrustedGateReceiptId: trustedGateReceiptId,
            TrustedGateReceiptFingerprint: trustedGateReceiptFingerprint,
            NormalizationVersion: normalizationVersion,
            ActivationEpoch: activationEpoch,
            LifecycleStatus: ClassifyActiveRuleSetLifecycleStatus.Active,
            ActivatedAt: activatedAt,
            RetiredAt: retiredAt,
            RuleVersionIds: ruleVersionIds,
            Categories: categories);
}

#pragma warning restore CA1416
