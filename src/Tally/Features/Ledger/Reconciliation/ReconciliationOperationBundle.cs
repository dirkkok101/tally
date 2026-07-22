using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.System;
using Tally.Domain.Ledger.Transactions;
using Tally.Infrastructure.Storage.Accounts;
using Tally.Infrastructure.Storage.Reconciliation;
using Tally.Infrastructure.Storage.Transactions;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationOperationBundle(
    ReconciliationProjectionOperationModule projection,
    ReconciliationApplyOperationModule apply,
    ReconciliationDecisionOperationModule decisions,
    ReconciliationCoverageOperationModule coverage)
{
    private const string IdempotencyConflict = "LEDGER-IDEMPOTENCY-001";

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } = CreateDescriptors(
        projection ?? throw new ArgumentNullException(nameof(projection)),
        apply ?? throw new ArgumentNullException(nameof(apply)),
        decisions ?? throw new ArgumentNullException(nameof(decisions)),
        coverage ?? throw new ArgumentNullException(nameof(coverage)));

    private static IReadOnlyList<OperationDescriptor> CreateDescriptors(
        ReconciliationProjectionOperationModule projection,
        ReconciliationApplyOperationModule apply,
        ReconciliationDecisionOperationModule decisions,
        ReconciliationCoverageOperationModule coverage)
    {
        OperationDescriptor[] descriptors =
        [
            Descriptor(
                ReconciliationProjectionOperationModule.OperationId,
                "candidates",
                "query",
                false,
                ReconciliationProjectionJsonContext.Default.GetReconciliationCandidatesInput,
                ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult,
                "ReconciliationProjectionOperationModule.Candidates",
                (_, _) => new ReconciliationProjectionOperationHandler(projection),
                ProjectionErrors),
            Descriptor(
                ReconciliationApplyOperationModule.OperationId,
                "apply",
                "mutation",
                true,
                ReconciliationApplyJsonContext.Default.ReconciliationApplyInput,
                ReconciliationApplyJsonContext.Default.ReconciliationApplyResult,
                "ReconciliationApplyOperationModule.Apply",
                (_, _) => new ReconciliationApplyOperationHandler(apply),
                ApplyErrors,
                "tally ledger reconciliation apply --input - # correct_existing_from_statement"),
            DecisionDescriptor(
                "ledger.reconciliation.decision.get",
                "get",
                "query",
                false,
                ReconciliationDecisionJsonContext.Default.GetReconciliationDecisionInput,
                decisions,
                DecisionGetErrors),
            DecisionDescriptor(
                "ledger.reconciliation.decision.confirm",
                "confirm",
                "mutation",
                true,
                ReconciliationDecisionJsonContext.Default.ConfirmReconciliationDecisionInput,
                decisions,
                DecisionMutationErrors),
            DecisionDescriptor(
                "ledger.reconciliation.decision.reject",
                "reject",
                "mutation",
                true,
                ReconciliationDecisionJsonContext.Default.RejectReconciliationDecisionInput,
                decisions,
                DecisionMutationErrors),
            DecisionDescriptor(
                "ledger.reconciliation.decision.revoke",
                "revoke",
                "mutation",
                true,
                ReconciliationDecisionJsonContext.Default.RevokeReconciliationDecisionInput,
                decisions,
                DecisionMutationErrors),
            DecisionDescriptor(
                "ledger.reconciliation.decision.replace",
                "replace",
                "mutation",
                true,
                ReconciliationDecisionJsonContext.Default.ReplaceReconciliationDecisionInput,
                decisions,
                DecisionMutationErrors),
            Descriptor(
                ReconciliationCoverageOperationModule.CompleteOperationId,
                "coverage complete",
                "mutation",
                true,
                ReconciliationCoverageJsonContext.Default.CompleteStatementCoverageInput,
                ReconciliationCoverageJsonContext.Default.StatementCoverageSummary,
                "ReconciliationCoverageOperationModule.Complete",
                (_, _) => new ReconciliationCoverageOperationHandler(coverage, ReconciliationCoverageOperationModule.CompleteOperationId),
                CoverageCompleteErrors),
            Descriptor(
                ReconciliationCoverageOperationModule.GetOperationId,
                "coverage get",
                "query",
                false,
                ReconciliationCoverageJsonContext.Default.GetStatementCoverageInput,
                ReconciliationCoverageJsonContext.Default.StatementCoverageSummary,
                "ReconciliationCoverageOperationModule.Get",
                (_, _) => new ReconciliationCoverageOperationHandler(coverage, ReconciliationCoverageOperationModule.GetOperationId),
                CoverageGetErrors)
        ];

        return descriptors.OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal).ToArray();
    }

    private static OperationDescriptor DecisionDescriptor(
        string operationId,
        string path,
        string kind,
        bool requiresIdempotencyKey,
        JsonTypeInfo requestType,
        ReconciliationDecisionOperationModule decisions,
        IReadOnlyList<ErrorSchema> errors) =>
        Descriptor(
            operationId,
            "decision " + path,
            kind,
            requiresIdempotencyKey,
            requestType,
            operationId.EndsWith(".get", StringComparison.Ordinal)
                ? ReconciliationDecisionJsonContext.Default.ReconciliationDecisionDetail
                : ReconciliationDecisionJsonContext.Default.ReconciliationDecisionMutationResult,
            "ReconciliationDecisionOperationModule." + char.ToUpperInvariant(path[0]) + path[1..],
            (_, _) => new ReconciliationDecisionOperationHandler(decisions, operationId),
            errors);

    private static OperationDescriptor Descriptor(
        string operationId,
        string path,
        string kind,
        bool requiresIdempotencyKey,
        JsonTypeInfo requestType,
        JsonTypeInfo resultType,
        string handlerTarget,
        Func<LedgerServices, OperationRegistry, IOperationHandler> handlerFactory,
        IReadOnlyList<ErrorSchema> domainErrors,
        string? example = null) =>
        new(
            operationId,
            "tally ledger reconciliation " + path,
            kind,
            requiresIdempotencyKey,
            requestType,
            resultType,
            handlerTarget,
            handlerFactory,
            example ?? "tally ledger reconciliation " + path + " --input -",
            domainErrors);

    private static ErrorSchema Validation(string code) => new(code, "validation", 3);
    private static ErrorSchema NotFound(string code) => new(code, "not_found", 4);
    private static ErrorSchema Conflict(string code) => new(code, "conflict", 5);
    private static ErrorSchema Lifecycle(string code) => new(code, "lifecycle", 6);
    private static ErrorSchema Compatibility(string code) => new(code, "compatibility", 7);
    private static ErrorSchema Integrity(string code) => new(code, "integrity", 8);

    private static readonly IReadOnlyList<ErrorSchema> ProjectionErrors =
    [
        NotFound(ReconciliationProjectionErrors.EvidenceNotFound),
        Validation(ReconciliationProjectionErrors.StatementEvidenceRequired),
        Validation(ReconciliationProjectionErrors.IncompleteObservation),
        NotFound(ReconciliationProjectionErrors.ScopeNotFound),
        Conflict(ReconciliationProjectionErrors.ScopeConflict),
        Lifecycle(ReconciliationProjectionErrors.ScopeInactive),
        Compatibility(ReconciliationProjectionErrors.UnsupportedPolicy)
    ];

    private static readonly IReadOnlyList<ErrorSchema> ApplyErrors =
    [
        Conflict(IdempotencyConflict),
        Integrity(ReconciliationApplyErrors.UnsupportedAutomaticAuthority),
        Integrity(ReconciliationApplyErrors.UnsupportedStatementCorrection),
        Conflict(ReconciliationApplyErrors.EvidenceFingerprintChanged),
        Conflict(ReconciliationApplyErrors.ProjectionChanged),
        Conflict(ReconciliationApplyErrors.CandidateSetChanged),
        Integrity(ReconciliationApplyErrors.TargetNotCandidate),
        Conflict(ReconciliationApplyErrors.ProjectionConflict),
        Lifecycle(ReconciliationApplyErrors.DispositionIncompatible),
        Conflict(ReconciliationApplyErrors.StatementFactMismatch),
        Conflict(StatementCorrectionEffectErrors.Conflict),
        NotFound(AccountStore.NotFoundError),
        Lifecycle(AccountStore.ArchivedError),
        NotFound(TransactionLifecycle.NotFoundError),
        Lifecycle(TransactionLifecycle.InactiveError)
    ];

    private static readonly IReadOnlyList<ErrorSchema> DecisionGetErrors =
    [
        NotFound(ReconciliationDecisionErrors.NotFound)
    ];

    private static readonly IReadOnlyList<ErrorSchema> DecisionMutationErrors =
    [
        Conflict(IdempotencyConflict),
        NotFound(ReconciliationDecisionErrors.NotFound),
        Conflict(ReconciliationDecisionErrors.StalePredecessor),
        Lifecycle(ReconciliationDecisionErrors.TransitionIncompatible),
        NotFound(ReconciliationDecisionErrors.CandidateNotFound),
        Lifecycle(ReconciliationDecisionErrors.CandidateInactive),
        Integrity(ReconciliationDecisionErrors.CandidateIncompatible),
        Conflict(ReconciliationDecisionErrors.CandidateAlreadyReconciled),
        Conflict(ReconciliationDecisionErrors.LinkConflict),
        NotFound(ReconciliationProjectionErrors.EvidenceNotFound),
        Validation(ReconciliationProjectionErrors.StatementEvidenceRequired),
        Validation(ReconciliationProjectionErrors.IncompleteObservation),
        NotFound(ReconciliationProjectionErrors.ScopeNotFound),
        Conflict(ReconciliationProjectionErrors.ScopeConflict),
        Lifecycle(ReconciliationProjectionErrors.ScopeInactive)
    ];

    private static readonly IReadOnlyList<ErrorSchema> CoverageCompleteErrors =
    [
        Conflict(IdempotencyConflict),
        NotFound(ReconciliationCoverageErrors.ScopeNotFound),
        Integrity(ReconciliationCoverageErrors.ScopeIncomplete),
        Lifecycle(ReconciliationCoverageErrors.ScopeInactive),
        Conflict(ReconciliationCoverageErrors.ScopeConflict),
        Conflict(ReconciliationCoverageErrors.EvidenceSetChanged),
        Compatibility(ReconciliationCoverageErrors.PolicyUnsupported),
        Integrity(ReconciliationCoverageErrors.MissingOutcome),
        Conflict(ReconciliationCoverageErrors.DuplicateTransactionOutcome),
        Conflict(ReconciliationCoverageErrors.AlreadyCompleted)
    ];

    private static readonly IReadOnlyList<ErrorSchema> CoverageGetErrors =
    [
        NotFound(ReconciliationCoverageErrors.NotFound)
    ];
}
