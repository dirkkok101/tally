using System.Text.Json.Serialization;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Ledger.Actuals;

namespace Tally.Contracts.Classify.Operations;

/// <summary>Apply selection mode for preview (DM-CLASSIFY-OPERATION-CONTRACTS).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyApplySelectionMode>))]
public enum ClassifyApplySelectionMode
{
    [JsonStringEnumMemberName("selected_outcomes")]
    SelectedOutcomes,

    [JsonStringEnumMemberName("exact_rule")]
    ExactRule,

    [JsonStringEnumMemberName("explicit_corrections")]
    ExplicitCorrections
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassifyStatusSubjectType>))]
public enum ClassifyStatusSubjectType
{
    [JsonStringEnumMemberName("rule")]
    Rule,

    [JsonStringEnumMemberName("validation")]
    Validation,

    [JsonStringEnumMemberName("evaluation")]
    Evaluation,

    [JsonStringEnumMemberName("preview")]
    Preview,

    [JsonStringEnumMemberName("apply")]
    Apply,

    [JsonStringEnumMemberName("feedback")]
    Feedback,

    [JsonStringEnumMemberName("abandonment")]
    Abandonment,

    [JsonStringEnumMemberName("cleanup")]
    Cleanup
}

[JsonConverter(typeof(JsonStringEnumConverter<ClassifyFeedbackDecision>))]
public enum ClassifyFeedbackDecision
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [JsonStringEnumMemberName("rejected")]
    Rejected,

    [JsonStringEnumMemberName("corrected")]
    Corrected
}

/// <summary>Explicit correction item — never selected by broad exact-rule mode.</summary>
public sealed record ClassifyExplicitCorrectionItem(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string OutcomeId,
    [property: JsonRequired] string CurrentCategoryId,
    [property: JsonRequired] string TargetCategoryId,
    [property: JsonRequired] string Reason);

/// <summary>
/// Selection union for apply preview. Exactly one mode is active; mixed modes are rejected by validation.
/// </summary>
public sealed record ClassifyApplySelection(
    [property: JsonRequired] ClassifyApplySelectionMode Mode,
    IReadOnlyList<string>? OutcomeIds = null,
    string? RuleVersionId = null,
    IReadOnlyList<ClassifyExplicitCorrectionItem>? CorrectionItems = null);

public sealed record ClassifyEvaluateRequest(
    [property: JsonRequired] string ContractVersion);

public sealed record ClassifyOutcomeGetRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] string TransactionId);

public sealed record ClassifyApplyPreviewRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] ClassifyApplySelection Selection);

public sealed record ClassifyApplyRunRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] string ApplyId);

public sealed record ClassifyRuleSaveRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RuleId,
    string? PriorVersionId,
    [property: JsonRequired] string CategoryId,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] IReadOnlyList<ClassificationRuleConditionInput> Conditions,
    [property: JsonRequired] string Reason);

/// <summary>
/// Public classify.rule.validate input. Optional owner-gate finalization fields apply only on the
/// hold-out run: production reloads stored representative + independent-replay + this hold-out
/// validation, derives and persists a trusted aggregate receipt, and returns receipt identity.
/// Never accepts a caller-supplied authority boolean or receipt body.
/// </summary>
public sealed record ClassifyRuleValidateRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] IReadOnlyList<string> CandidateIds,
    [property: JsonRequired] string CorpusSource,
    string? RepresentativeValidationId = null,
    string? IndependentReplayValidationId = null,
    int? OwnerDecisionCountBefore = null,
    int? OwnerDecisionCountAfter = null,
    double? OwnerMinutesBefore = null,
    double? OwnerMinutesAfter = null,
    string? ExplicitBenefitDecision = null);

/// <summary>
/// Public classify.rule.activate input. Requires a trusted persisted owner-rulebook gate receipt ID.
/// Never accepts a caller-supplied receipt body or authority boolean.
/// </summary>
public sealed record ClassifyRuleActivateRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string ValidationId,
    [property: JsonRequired] string OwnerRulebookGateReceiptId,
    [property: JsonRequired] bool BroadApplyAllowed,
    [property: JsonRequired] string Reason);

public sealed record ClassifyRuleRetireRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string RuleVersionId,
    [property: JsonRequired] string Reason);

public sealed record ClassifyFeedbackRecordRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string OutcomeId,
    [property: JsonRequired] ClassifyFeedbackDecision Decision,
    IReadOnlyList<string>? LedgerAllocationRefs,
    [property: JsonRequired] string Reason);

public sealed record ClassifyStatusRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId);

public sealed record ClassifyAbandonRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] ClassifyStatusSubjectType SubjectType,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] string Reason);

public sealed record ClassifyCleanupRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string PolicyVersion);

// ── Operator ergonomics additive contracts (PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1) ──
// Contract-only shapes + pure boundary validation. Descriptors/handlers: later beads.

/// <summary>Closed stale filter for classify.outcome.list (DM-CLASSIFY-OUTCOME-PAGE).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyOutcomeStaleFilter>))]
public enum ClassifyOutcomeStaleFilter
{
    [JsonStringEnumMemberName("any")]
    Any,

    [JsonStringEnumMemberName("fresh")]
    Fresh,

    [JsonStringEnumMemberName("stale")]
    Stale
}

/// <summary>Closed rule lifecycle filter/item state for classify.rule.list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyRuleLifecycleFilter>))]
public enum ClassifyRuleLifecycleFilter
{
    [JsonStringEnumMemberName("draft")]
    Draft,

    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("retired")]
    Retired,

    [JsonStringEnumMemberName("superseded")]
    Superseded
}

/// <summary>Closed provenance enum for rule discovery items (DM-CLASSIFY-RULE-DISCOVERY).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyRuleProvenanceKind>))]
public enum ClassifyRuleProvenanceKind
{
    [JsonStringEnumMemberName("owner_authored")]
    OwnerAuthored,

    [JsonStringEnumMemberName("feedback_derived")]
    FeedbackDerived
}

/// <summary>Closed category lifecycle for discovery surfaces (aligns with public category states).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyCategoryLifecycleState>))]
public enum ClassifyCategoryLifecycleState
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("archived")]
    Archived
}

/// <summary>Closed lifecycle status for the active rule-set authority summary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyActiveRuleSetLifecycleStatus>))]
public enum ClassifyActiveRuleSetLifecycleStatus
{
    [JsonStringEnumMemberName("active")]
    Active
}

/// <summary>Closed terminal publication state for corpus.build aggregate receipts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifyCorpusBuildTerminalState>))]
public enum ClassifyCorpusBuildTerminalState
{
    [JsonStringEnumMemberName("completed")]
    Completed
}

/// <summary>
/// classify.outcome.list request (DM-CLASSIFY-OUTCOME-PAGE / FR-CLASSIFY-OUTCOME-DISCOVERY).
/// pageSize is 1..500; continuation is opaque and snapshot-bound.
/// </summary>
public sealed record ClassifyOutcomeListRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] int PageSize,
    ClassifyOutcomeKind? OutcomeKind = null,
    string? SuggestedCategoryId = null,
    string? ContributingRuleVersionId = null,
    ClassifyOutcomeStaleFilter? StaleState = null,
    string? TransactionId = null,
    string? Continuation = null);

/// <summary>
/// classify.rule.list request (DM-CLASSIFY-RULE-DISCOVERY / FR-CLASSIFY-RULEBOOK-DISCOVERY).
/// pageSize is 1..500; continuation is opaque and high-water bound.
/// </summary>
public sealed record ClassifyRuleListRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] int PageSize,
    string? LogicalRuleId = null,
    ClassifyRuleLifecycleFilter? Lifecycle = null,
    string? CategoryId = null,
    bool? ActiveMembership = null,
    string? Continuation = null);

/// <summary>
/// classify.rule-set.active.get request — no caller-supplied authority flag.
/// </summary>
public sealed record ClassifyRuleSetActiveGetRequest(
    [property: JsonRequired] string ContractVersion);

/// <summary>
/// One explicit owner label for classify.corpus.build
/// (DM-CLASSIFY-PRIVATE-CORPUS-BUILD / FR-CLASSIFY-PRIVATE-CORPUS-BUILDER).
/// expectedCategoryId is required when the outcome kind requires a category.
/// </summary>
public sealed record ClassifyCorpusBuildLabel(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] ClassifyOutcomeKind ExpectedOutcome,
    string? ExpectedCategoryId = null);

/// <summary>
/// Complete fresh classification_v1 projection envelope for corpus.build.
/// Items are released Ledger <see cref="ClassificationProjectionItem"/> rows — no invented dialect.
/// Never returned on the public aggregate receipt.
/// </summary>
public sealed record ClassifyCorpusBuildProjectionEnvelope(
    [property: JsonRequired] string LedgerContractVersion,
    [property: JsonRequired] string ProjectionVersion,
    [property: JsonRequired] string StoreGenerationFingerprint,
    [property: JsonRequired] string SnapshotId,
    [property: JsonRequired] string SnapshotExpiresAt,
    [property: JsonRequired] string CatalogueFingerprint,
    [property: JsonRequired] string NormalizationVersion,
    [property: JsonRequired] IReadOnlyList<ClassificationProjectionItem> Items);

/// <summary>
/// classify.corpus.build request. Idempotent; labels 1..10000; absolute outputPath required.
/// Aggregate receipt never echoes path or row data.
/// </summary>
public sealed record ClassifyCorpusBuildRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string OutputPath,
    [property: JsonRequired] ClassifyCorpusBuildProjectionEnvelope Projection,
    [property: JsonRequired] IReadOnlyList<ClassifyCorpusBuildLabel> Labels);

/// <summary>
/// classify.unresolved.report request (DM-CLASSIFY-UNRESOLVED-REPORT / FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT).
/// topN 1..500; minimumCount 2..500 (FR-required); optional account/direction filters use
/// released Ledger classification amount-direction vocabulary.
/// </summary>
public sealed record ClassifyUnresolvedReportRequest(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string EvaluationId,
    [property: JsonRequired] int TopN,
    [property: JsonRequired] int MinimumCount,
    string? AccountId = null,
    ClassificationAmountDirection? AmountDirection = null);

/// <summary>
/// Pure boundary validation for operator-ergonomics request shapes (bd-1gly).
/// Mirrors the established TryValidate pattern used by feature validators, without
/// publishing descriptors or handlers. Bounds match DM/FR contracts exactly.
/// </summary>
public static class ClassifyOperatorErgonomicsContracts
{
    /// <summary>Contract version for additive ergonomics operations (same family as C12: 1.0).</summary>
    public const string ContractVersion = "1.0";

    public const int MinPageSize = 1;
    public const int MaxPageSize = 500;
    public const int MinTopN = 1;
    public const int MaxTopN = 500;
    public const int MinMinimumCount = 2;
    public const int MaxMinimumCount = 500;
    public const int MinLabelCount = 1;
    public const int MaxLabelCount = 10_000;

    /// <summary>Released apply selected_outcomes upper bound — unchanged by this bead.</summary>
    public const int SelectedOutcomesMax = 200;

    public static bool IsSupportedContractVersion(string? version) =>
        string.Equals(version, ContractVersion, StringComparison.Ordinal);

    public static bool IsValidPageSize(int pageSize) =>
        pageSize is >= MinPageSize and <= MaxPageSize;

    public static bool IsValidTopN(int topN) =>
        topN is >= MinTopN and <= MaxTopN;

    public static bool IsValidMinimumCount(int minimumCount) =>
        minimumCount is >= MinMinimumCount and <= MaxMinimumCount;

    public static bool IsValidLabelCount(int labelCount) =>
        labelCount is >= MinLabelCount and <= MaxLabelCount;

    public static bool TryValidate(ClassifyOutcomeListRequest? request, out string? errorCode)
    {
        errorCode = null;
        if (request is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsSupportedContractVersion(request.ContractVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.EvaluationId))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsValidPageSize(request.PageSize))
        {
            errorCode = ClassifyErrors.ResourceLimit;
            return false;
        }

        return true;
    }

    public static bool TryValidate(ClassifyRuleListRequest? request, out string? errorCode)
    {
        errorCode = null;
        if (request is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsSupportedContractVersion(request.ContractVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (!IsValidPageSize(request.PageSize))
        {
            errorCode = ClassifyErrors.ResourceLimit;
            return false;
        }

        return true;
    }

    public static bool TryValidate(ClassifyRuleSetActiveGetRequest? request, out string? errorCode)
    {
        errorCode = null;
        if (request is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsSupportedContractVersion(request.ContractVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        return true;
    }

    public static bool TryValidate(ClassifyCorpusBuildRequest? request, out string? errorCode)
    {
        errorCode = null;
        if (request is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsSupportedContractVersion(request.ContractVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.OutputPath)
            || request.Projection is null
            || request.Labels is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!string.Equals(
                request.Projection.ProjectionVersion,
                ClassificationProjectionVersions.ClassificationV1,
                StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.LedgerIncompatible;
            return false;
        }

        if (request.Projection.Items is null
            || string.IsNullOrWhiteSpace(request.Projection.LedgerContractVersion)
            || string.IsNullOrWhiteSpace(request.Projection.StoreGenerationFingerprint)
            || string.IsNullOrWhiteSpace(request.Projection.SnapshotId)
            || string.IsNullOrWhiteSpace(request.Projection.CatalogueFingerprint)
            || string.IsNullOrWhiteSpace(request.Projection.NormalizationVersion))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsValidLabelCount(request.Labels.Count))
        {
            errorCode = ClassifyErrors.ResourceLimit;
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in request.Labels)
        {
            if (string.IsNullOrWhiteSpace(label.TransactionId) || !seen.Add(label.TransactionId))
            {
                errorCode = ClassifyErrors.LabelInvalid;
                return false;
            }

            if (label.ExpectedOutcome is ClassifyOutcomeKind.Suggestion
                && string.IsNullOrWhiteSpace(label.ExpectedCategoryId))
            {
                errorCode = ClassifyErrors.LabelInvalid;
                return false;
            }
        }

        return true;
    }

    public static bool TryValidate(ClassifyUnresolvedReportRequest? request, out string? errorCode)
    {
        errorCode = null;
        if (request is null)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsSupportedContractVersion(request.ContractVersion))
        {
            errorCode = ClassifyErrors.UnsupportedVersion;
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.EvaluationId))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (!IsValidTopN(request.TopN) || !IsValidMinimumCount(request.MinimumCount))
        {
            errorCode = ClassifyErrors.ResourceLimit;
            return false;
        }

        return true;
    }
}
