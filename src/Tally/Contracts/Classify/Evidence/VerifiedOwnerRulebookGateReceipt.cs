using System.Text.Json.Serialization;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Infrastructure.Classify.Corpus;

namespace Tally.Contracts.Classify.Evidence;

/// <summary>
/// Aggregate-only owner-rulebook pre-authority gate receipt
/// (TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE / TASK-CLASSIFY-RULEBOOK-GATE-OWNER-RULEBOOK).
/// Production contract — every field is derived from validation evidence and owner benefit input.
/// Never carries paths, descriptions, tokens, amounts, expected outcomes, or raw rows.
/// </summary>
public sealed record VerifiedOwnerRulebookGateReceipt(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ReceiptKind,
    [property: JsonRequired] bool AuthorityGranted,
    [property: JsonRequired] bool SafetyPassed,
    [property: JsonRequired] bool BenefitSufficient,
    [property: JsonRequired] bool RequiresExplicitOwnerBenefitDecision,
    string? BlockCode,
    [property: JsonRequired] int EligibleRows,
    [property: JsonRequired] int SuggestedRows,
    [property: JsonRequired] int CorrectionRows,
    [property: JsonRequired] int NoSuggestionRows,
    [property: JsonRequired] int ConflictRows,
    [property: JsonRequired] int ExcludedRows,
    [property: JsonRequired] int StaleRows,
    [property: JsonRequired] int IncorrectApplicationCanaries,
    [property: JsonRequired] int UnexplainedConflictCount,
    [property: JsonRequired] int DriftCanaryCount,
    [property: JsonRequired] int UnauthorizedMutationCount,
    [property: JsonRequired] int DescriptionInferredRelationshipCount,
    [property: JsonRequired] int CoverageBasisPoints,
    [property: JsonRequired] int OwnerDecisionCountBefore,
    [property: JsonRequired] int OwnerDecisionCountAfter,
    double? ElapsedOwnerMinutesBefore,
    double? ElapsedOwnerMinutesAfter,
    string? CandidateFingerprint,
    string? CorpusFingerprint,
    string? HoldOutFingerprint,
    string? ReportFingerprint,
    string? OutcomesCanonicalHash,
    [property: JsonRequired] bool DeterministicReplayPassed,
    [property: JsonRequired] bool DisclosurePassed,
    [property: JsonRequired] bool LocalityPassed,
    [property: JsonRequired] string ProjectionVersion,
    string? SnapshotId,
    string? StoreGenerationFingerprint,
    string? ReceiptId = null,
    string? ReceiptFingerprint = null,
    string? RepresentativeValidationRunId = null,
    string? IndependentReplayValidationRunId = null,
    string? HoldOutValidationRunId = null,
    string? ExplicitBenefitDecision = null,
    string? Actor = null,
    string? CreatedAt = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string Kind = "VerifiedOwnerRulebookGateReceipt";

    public const string BlockInputMissing = "CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING";
    public const string BlockSafetyFailed = "CLASSIFY-OWNER-RULEBOOK-SAFETY-FAILED";
    public const string BlockBenefitDecisionRequired = "CLASSIFY-OWNER-RULEBOOK-BENEFIT-DECISION-REQUIRED";
    public const string BlockReplayFailed = "CLASSIFY-OWNER-RULEBOOK-REPLAY-FAILED";
    public const string BlockHoldOutFailed = "CLASSIFY-OWNER-RULEBOOK-HOLD-OUT-FAILED";
    public const string BlockValidateUnavailable = "CLASSIFY-OWNER-RULEBOOK-VALIDATE-UNAVAILABLE";

    /// <summary>
    /// Stable blocked receipt when required owner inputs are absent.
    /// Zero counters and null fingerprints are derived from absence of evidence, not invented pass state.
    /// </summary>
    public static VerifiedOwnerRulebookGateReceipt MissingOwnerInputs() =>
        Blocked(
            blockCode: BlockInputMissing,
            safetyPassed: false,
            benefitSufficient: false,
            requiresBenefitDecision: true,
            deterministicReplayPassed: false);

    /// <summary>
    /// Derive the aggregate receipt from representative validation, identical-key replay, and hold-out.
    /// No hard-coded safety pass; all counters and fingerprints come from validation results.
    /// </summary>
    public static VerifiedOwnerRulebookGateReceipt Derive(
        ClassifyRuleValidateResult representative,
        ClassifyRuleValidateResult? replay,
        ClassifyRuleValidateResult holdOut,
        OwnerBenefitEvidenceReceipt benefit,
        string? explicitBenefitDecision,
        int unauthorizedMutationCount = 0,
        int descriptionInferredRelationshipCount = 0,
        int correctionRows = 0,
        int excludedRows = 0)
    {
        ArgumentNullException.ThrowIfNull(representative);
        ArgumentNullException.ThrowIfNull(holdOut);
        ArgumentNullException.ThrowIfNull(benefit);

        var deterministicReplayPassed = replay is not null
            && string.Equals(representative.OutcomesCanonicalHash, replay.OutcomesCanonicalHash, StringComparison.Ordinal)
            && string.Equals(representative.CorpusFingerprint, replay.CorpusFingerprint, StringComparison.Ordinal)
            && string.Equals(representative.CandidateFingerprint, replay.CandidateFingerprint, StringComparison.Ordinal)
            && string.Equals(representative.ReportFingerprint, replay.ReportFingerprint, StringComparison.Ordinal)
            && representative.TotalRows == replay.TotalRows
            && representative.SuggestionCount == replay.SuggestionCount
            && representative.NoSuggestionCount == replay.NoSuggestionCount
            && representative.ConflictCount == replay.ConflictCount
            && representative.StaleCount == replay.StaleCount
            && representative.IncorrectApplicationCanaries == replay.IncorrectApplicationCanaries
            && representative.UnexplainedConflictCount == replay.UnexplainedConflictCount
            && representative.DriftCanaryCount == replay.DriftCanaryCount
            && representative.ActivationEligible == replay.ActivationEligible;

        var repSafety = IsSafetyPass(representative) && unauthorizedMutationCount == 0
            && descriptionInferredRelationshipCount == 0;
        var holdSafety = IsSafetyPass(holdOut);
        var safetyPassed = repSafety && holdSafety && deterministicReplayPassed;

        // Benefit is never auto-approved via an invented percentage threshold.
        var benefitSufficient = IsExplicitBenefitApproval(explicitBenefitDecision);
        var requiresBenefitDecision = !benefitSufficient;
        if (!string.IsNullOrWhiteSpace(explicitBenefitDecision)
            && string.Equals(explicitBenefitDecision.Trim(), "defer-broad", StringComparison.Ordinal))
        {
            benefitSufficient = false;
            requiresBenefitDecision = true;
        }

        var authorityGranted = safetyPassed && (benefitSufficient || !requiresBenefitDecision);
        // When benefit is insufficient, authority is blocked even if safety passed.
        if (safetyPassed && requiresBenefitDecision && !benefitSufficient)
        {
            authorityGranted = false;
        }

        string? blockCode = null;
        if (!authorityGranted)
        {
            if (!deterministicReplayPassed)
            {
                blockCode = BlockReplayFailed;
            }
            else if (!holdSafety)
            {
                blockCode = BlockHoldOutFailed;
            }
            else if (!repSafety)
            {
                blockCode = BlockSafetyFailed;
            }
            else if (requiresBenefitDecision && !benefitSufficient)
            {
                blockCode = BlockBenefitDecisionRequired;
            }
            else
            {
                blockCode = BlockSafetyFailed;
            }
        }

        return new VerifiedOwnerRulebookGateReceipt(
            SchemaVersion: CurrentSchemaVersion,
            ReceiptKind: Kind,
            AuthorityGranted: authorityGranted,
            SafetyPassed: safetyPassed,
            BenefitSufficient: benefitSufficient,
            RequiresExplicitOwnerBenefitDecision: requiresBenefitDecision,
            BlockCode: blockCode,
            EligibleRows: representative.TotalRows,
            SuggestedRows: representative.SuggestionCount,
            CorrectionRows: correctionRows,
            NoSuggestionRows: representative.NoSuggestionCount,
            ConflictRows: representative.ConflictCount,
            ExcludedRows: excludedRows,
            StaleRows: representative.StaleCount,
            IncorrectApplicationCanaries: representative.IncorrectApplicationCanaries,
            UnexplainedConflictCount: representative.UnexplainedConflictCount,
            DriftCanaryCount: representative.DriftCanaryCount,
            UnauthorizedMutationCount: unauthorizedMutationCount,
            DescriptionInferredRelationshipCount: descriptionInferredRelationshipCount,
            CoverageBasisPoints: representative.CoverageBasisPoints,
            OwnerDecisionCountBefore: benefit.OwnerDecisionCountBefore,
            OwnerDecisionCountAfter: benefit.OwnerDecisionCountAfter,
            ElapsedOwnerMinutesBefore: benefit.OwnerMinutesBefore,
            ElapsedOwnerMinutesAfter: benefit.OwnerMinutesAfter,
            CandidateFingerprint: representative.CandidateFingerprint,
            CorpusFingerprint: representative.CorpusFingerprint,
            HoldOutFingerprint: holdOut.CorpusFingerprint,
            ReportFingerprint: representative.ReportFingerprint,
            OutcomesCanonicalHash: representative.OutcomesCanonicalHash,
            DeterministicReplayPassed: deterministicReplayPassed,
            DisclosurePassed: true,
            LocalityPassed: true,
            ProjectionVersion: representative.ProjectionVersion,
            SnapshotId: representative.SnapshotId,
            StoreGenerationFingerprint: representative.StoreGenerationFingerprint);
    }

    public static VerifiedOwnerRulebookGateReceipt ValidateUnavailable(string? detailCode = null) =>
        Blocked(
            blockCode: detailCode ?? BlockValidateUnavailable,
            safetyPassed: false,
            benefitSufficient: false,
            requiresBenefitDecision: true,
            deterministicReplayPassed: false);

    private static bool IsSafetyPass(ClassifyRuleValidateResult v) =>
        v.ActivationEligible
        && v.IncorrectApplicationCanaries == 0
        && v.UnexplainedConflictCount == 0
        && v.DriftCanaryCount == 0
        && v.TotalRows == v.AccountedRows
        && v.TotalRows == v.SuggestionCount + v.NoSuggestionCount + v.ConflictCount + v.StaleCount;

    private static bool IsExplicitBenefitApproval(string? decision) =>
        !string.IsNullOrWhiteSpace(decision)
        && (string.Equals(decision.Trim(), "approve-broad", StringComparison.Ordinal)
            || string.Equals(decision.Trim(), "approve", StringComparison.Ordinal));

    private static VerifiedOwnerRulebookGateReceipt Blocked(
        string blockCode,
        bool safetyPassed,
        bool benefitSufficient,
        bool requiresBenefitDecision,
        bool deterministicReplayPassed) =>
        new(
            SchemaVersion: CurrentSchemaVersion,
            ReceiptKind: Kind,
            AuthorityGranted: false,
            SafetyPassed: safetyPassed,
            BenefitSufficient: benefitSufficient,
            RequiresExplicitOwnerBenefitDecision: requiresBenefitDecision,
            BlockCode: blockCode,
            EligibleRows: 0,
            SuggestedRows: 0,
            CorrectionRows: 0,
            NoSuggestionRows: 0,
            ConflictRows: 0,
            ExcludedRows: 0,
            StaleRows: 0,
            IncorrectApplicationCanaries: 0,
            UnexplainedConflictCount: 0,
            DriftCanaryCount: 0,
            UnauthorizedMutationCount: 0,
            DescriptionInferredRelationshipCount: 0,
            CoverageBasisPoints: 0,
            OwnerDecisionCountBefore: 0,
            OwnerDecisionCountAfter: 0,
            ElapsedOwnerMinutesBefore: null,
            ElapsedOwnerMinutesAfter: null,
            CandidateFingerprint: null,
            CorpusFingerprint: null,
            HoldOutFingerprint: null,
            ReportFingerprint: null,
            OutcomesCanonicalHash: null,
            DeterministicReplayPassed: deterministicReplayPassed,
            DisclosurePassed: true,
            LocalityPassed: true,
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            SnapshotId: null,
            StoreGenerationFingerprint: null);
}
