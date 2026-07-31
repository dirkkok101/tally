namespace Tally.Features.Classify.Contract;

/// <summary>
/// Twelve public CLASSIFY operations (DD-CLASSIFY-CLI-OPERATION-CONTRACT / C12).
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

    /// <summary>Canonical C12 order for discovery and inventory proofs.</summary>
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
        Cleanup
    ];
}
