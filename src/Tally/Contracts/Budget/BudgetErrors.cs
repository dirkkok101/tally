namespace Tally.Contracts.Budget;

/// <summary>Stable published BUDGET domain error codes (DM-BUDGET-OPERATION-CONTRACTS).</summary>
public static class BudgetErrors
{
    public const string InvalidInput = "BUDGET-INPUT-INVALID";
    public const string InvalidPeriod = "BUDGET-PERIOD-INVALID";
    public const string InvalidAmount = "BUDGET-AMOUNT-INVALID";
    public const string UnknownField = "BUDGET-UNKNOWN-FIELD";
    public const string UnsupportedVersion = "BUDGET-VERSION-UNSUPPORTED";
    public const string ActorRequired = "BUDGET-ACTOR-REQUIRED";
    public const string IdempotencyRequired = "BUDGET-IDEMPOTENCY-REQUIRED";
    public const string NotFound = "BUDGET-NOT-FOUND";
    public const string PlanNotFound = "BUDGET-PLAN-NOT-FOUND";
    public const string RevisionNotFound = "BUDGET-REVISION-NOT-FOUND";
    public const string NoActiveBudgetPlanRevision = "BUDGET-NO-ACTIVE-REVISION";
    public const string RevisionPeriodMismatch = "BUDGET-REVISION-PERIOD-MISMATCH";
    public const string CategoryInactive = "BUDGET-CATEGORY-INACTIVE";
    public const string CategoryUnknown = "BUDGET-CATEGORY-UNKNOWN";
    public const string Conflict = "BUDGET-CONFLICT";
    public const string IdempotencyConflict = "BUDGET-IDEMPOTENCY-CONFLICT";
    public const string SourceStateChanged = "BUDGET-SOURCE-STATE-CHANGED";
    public const string ResourceLimit = "BUDGET-RESOURCE-LIMIT";
    public const string LedgerUnavailable = "BUDGET-LEDGER-UNAVAILABLE";
    public const string LedgerIncompatible = "BUDGET-LEDGER-INCOMPATIBLE";
    public const string Integrity = "BUDGET-INTEGRITY";
    public const string Unexpected = "BUDGET-UNEXPECTED";
}
