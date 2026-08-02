namespace Tally.Contracts.Classify.Operations;

/// <summary>Stable published CLASSIFY domain error codes (DM-CLASSIFY-OPERATION-CONTRACTS).</summary>
public static class ClassifyErrors
{
    public const string InvalidInput = "CLASSIFY-INPUT-INVALID";
    public const string UnknownField = "CLASSIFY-UNKNOWN-FIELD";
    public const string UnsupportedVersion = "CLASSIFY-VERSION-UNSUPPORTED";
    public const string ActorRequired = "CLASSIFY-ACTOR-REQUIRED";
    public const string IdempotencyRequired = "CLASSIFY-IDEMPOTENCY-REQUIRED";
    public const string NotFound = "CLASSIFY-NOT-FOUND";
    public const string EvaluationNotFound = "CLASSIFY-EVALUATION-NOT-FOUND";
    public const string OutcomeNotFound = "CLASSIFY-OUTCOME-NOT-FOUND";
    public const string PreviewNotFound = "CLASSIFY-PREVIEW-NOT-FOUND";
    public const string RuleNotFound = "CLASSIFY-RULE-NOT-FOUND";
    public const string RuleVersionNotFound = "CLASSIFY-RULE-VERSION-NOT-FOUND";
    public const string ValidationNotFound = "CLASSIFY-VALIDATION-NOT-FOUND";
    public const string Conflict = "CLASSIFY-CONFLICT";
    public const string IdempotencyConflict = "CLASSIFY-IDEMPOTENCY-CONFLICT";
    public const string Stale = "CLASSIFY-STALE";
    public const string SelectionInvalid = "CLASSIFY-SELECTION-INVALID";
    public const string ResourceLimit = "CLASSIFY-RESOURCE-LIMIT";
    public const string Lifecycle = "CLASSIFY-LIFECYCLE";
    public const string LedgerUnavailable = "CLASSIFY-LEDGER-UNAVAILABLE";
    public const string LedgerIncompatible = "CLASSIFY-LEDGER-INCOMPATIBLE";
    public const string Integrity = "CLASSIFY-INTEGRITY";
    public const string Unexpected = "CLASSIFY-UNEXPECTED";

    // ── Operator ergonomics additive typed errors (map through existing exit classes) ──
    // Cursor/lifecycle/privacy/resource failures for discovery and private tools.
    // Handlers (later beads) map these to the same exit codes as C12 peers.

    /// <summary>Malformed, expired, cross-scope, or fingerprint-mismatched opaque continuation.</summary>
    public const string CursorInvalid = "CLASSIFY-CURSOR-INVALID";

    /// <summary>Continuation snapshot fingerprints no longer match current public state.</summary>
    public const string CursorStale = "CLASSIFY-CURSOR-STALE";

    /// <summary>Active rule set is absent; never fabricate empty authority.</summary>
    public const string ActiveRuleSetNotFound = "CLASSIFY-ACTIVE-RULE-SET-NOT-FOUND";

    /// <summary>Owner-private path, ownership, mode, or symlink boundary rejected.</summary>
    public const string PrivacyRejected = "CLASSIFY-PRIVACY-REJECTED";

    /// <summary>Corpus destination already exists; never overwrite.</summary>
    public const string DestinationExists = "CLASSIFY-DESTINATION-EXISTS";

    /// <summary>Corpus label set is incomplete, duplicate, or inconsistent with projection.</summary>
    public const string LabelInvalid = "CLASSIFY-LABEL-INVALID";
}
