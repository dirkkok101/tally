namespace Tally.Features.Classify.Contract;

/// <summary>
/// Seventeen public CLASSIFY operations: twelve released 0.3.3 C12 operations plus five
/// additive operator-ergonomics operations (DD-CLASSIFY-OPERATOR-ERGONOMICS-CONTRACT).
/// No generic action discriminator — each transition is its own named operation.
/// </summary>
public static class ClassifyOperationIds
{
    public const string ContractVersion = "1.0";

    public const string Evaluate = "classify.evaluate";
    public const string OutcomeGet = "classify.outcome.get";
    public const string ApplyPreview = "classify.apply.preview";
    public const string ApplyRun = "classify.apply.run";
    public const string RuleSave = "classify.rule.save";
    public const string RuleValidate = "classify.rule.validate";
    public const string RuleActivate = "classify.rule.activate";
    public const string RuleRetire = "classify.rule.retire";
    public const string FeedbackRecord = "classify.feedback.record";
    public const string Status = "classify.status";
    public const string Abandon = "classify.abandon";
    public const string Cleanup = "classify.cleanup";

    // ── Additive operator ergonomics (DD-CLASSIFY-OPERATOR-ERGONOMICS-CONTRACT) ──
    public const string OutcomeList = "classify.outcome.list";
    public const string RuleList = "classify.rule.list";
    public const string RuleSetActiveGet = "classify.rule-set.active.get";
    public const string CorpusBuild = "classify.corpus.build";
    public const string UnresolvedReport = "classify.unresolved.report";

    /// <summary>Canonical inventory order for discovery and inventory proofs (C12 then five additive).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Evaluate,
        OutcomeGet,
        ApplyPreview,
        ApplyRun,
        RuleSave,
        RuleValidate,
        RuleActivate,
        RuleRetire,
        FeedbackRecord,
        Status,
        Abandon,
        Cleanup,
        OutcomeList,
        RuleList,
        RuleSetActiveGet,
        CorpusBuild,
        UnresolvedReport
    ];

    /// <summary>The twelve released 0.3.3 operation IDs (compatibility fingerprint surface).</summary>
    public static readonly IReadOnlyList<string> ReleasedC12 =
    [
        Evaluate,
        OutcomeGet,
        ApplyPreview,
        ApplyRun,
        RuleSave,
        RuleValidate,
        RuleActivate,
        RuleRetire,
        FeedbackRecord,
        Status,
        Abandon,
        Cleanup
    ];
}
