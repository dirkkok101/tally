namespace Tally.Features.Budget.Contract;

/// <summary>
/// Six public BUDGET operations (DD-BUDGET-CLI-OPERATION-CONTRACT).
/// No generic action discriminator — each transition is its own named operation.
/// </summary>
public static class BudgetOperationIds
{
    public const string ContractVersion = "1.0";

    public const string DraftCreate = "budget.plan.draft.create";
    public const string RevisionGet = "budget.plan.revision.get";
    public const string RevisionList = "budget.plan.revision.list";
    public const string RevisionActivate = "budget.plan.revision.activate";
    public const string PositionGet = "budget.position.get";
    public const string InsightsEvidenceGet = "budget.insights.evidence.get";

    public static readonly IReadOnlyList<string> All =
    [
        DraftCreate,
        RevisionGet,
        RevisionList,
        RevisionActivate,
        PositionGet,
        InsightsEvidenceGet
    ];
}
