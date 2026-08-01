using System.Text.Json;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Feedback;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure feedback mapping helpers (DM-CLASSIFY-FEEDBACK-PROPOSAL / TASK-CLASSIFY-RULEBOOK-FEEDBACK-PROPOSALS).
/// No I/O; never maps private descriptions or normalized tokens into results.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const string FeedbackDecisionAccept = "accept";
    public const string FeedbackDecisionReject = "reject";
    public const string FeedbackDecisionCorrect = "correct";

    public static string FormatFeedbackDecision(ClassifyFeedbackDecision decision) => decision switch
    {
        ClassifyFeedbackDecision.Accepted => FeedbackDecisionAccept,
        ClassifyFeedbackDecision.Rejected => FeedbackDecisionReject,
        ClassifyFeedbackDecision.Corrected => FeedbackDecisionCorrect,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown feedback decision.")
    };

    public static JsonElement ToFeedbackFingerprintElement(
        string contractVersion,
        string outcomeId,
        ClassifyFeedbackDecision decision,
        string reason,
        IReadOnlyList<string>? ledgerAllocationRefs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("decision", FormatFeedbackDecision(decision));
            if (ledgerAllocationRefs is { Count: > 0 })
            {
                writer.WritePropertyName("ledgerAllocationRefs");
                writer.WriteStartArray();
                // Allocation refs are an ordered (prior, resulting) pair. Do not sort or
                // de-duplicate them: reversing the pair changes correction semantics and
                // must therefore change the idempotency fingerprint.
                foreach (var id in ledgerAllocationRefs
                             .Where(r => !string.IsNullOrWhiteSpace(r))
                             .Select(r => r.Trim()))
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNull("ledgerAllocationRefs");
            }

            writer.WriteString("outcomeId", outcomeId.Trim());
            writer.WriteString("reason", reason.Trim());
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Resolve prior/resulting allocation identities for correction feedback.
    /// Bind any explicit ordered pair to the durable outcome-scoped apply result.
    /// Caller-supplied identities are not correction authority by themselves.
    /// </summary>
    public static bool TryResolveCorrectionAllocations(
        IReadOnlyList<string>? ledgerAllocationRefs,
        string? appliedPriorAllocationId,
        string? appliedResultingAllocationId,
        out string? priorAllocationId,
        out string? resultingAllocationId,
        out string? errorCode)
    {
        priorAllocationId = null;
        resultingAllocationId = null;
        errorCode = null;

        var hasAuthoritativePair = !string.IsNullOrWhiteSpace(appliedPriorAllocationId)
            && !string.IsNullOrWhiteSpace(appliedResultingAllocationId);
        if (hasAuthoritativePair)
        {
            var authoritativePrior = appliedPriorAllocationId!.Trim();
            var authoritativeResulting = appliedResultingAllocationId!.Trim();
            if (ledgerAllocationRefs is { Count: > 0 })
            {
                var refs = ledgerAllocationRefs
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r.Trim())
                    .ToArray();
                if (refs.Length != 2
                    || string.Equals(refs[0], refs[1], StringComparison.Ordinal)
                    || !string.Equals(refs[0], authoritativePrior, StringComparison.Ordinal)
                    || !string.Equals(refs[1], authoritativeResulting, StringComparison.Ordinal))
                {
                    errorCode = ClassifyErrors.InvalidInput;
                    return false;
                }
            }

            priorAllocationId = authoritativePrior;
            resultingAllocationId = authoritativeResulting;
            return true;
        }

        // A direct owner correction may supply its Ledger result pair for attributable
        // history, but the caller pair is not sufficient authority to derive a reusable
        // proposal. The command keeps CorrectionAllocationsComplete false in this branch.
        if (ledgerAllocationRefs is { Count: > 0 })
        {
            var refs = ledgerAllocationRefs
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .ToArray();
            if (refs.Length == 2
                && !string.Equals(refs[0], refs[1], StringComparison.Ordinal))
            {
                priorAllocationId = refs[0];
                resultingAllocationId = refs[1];
                return true;
            }
        }

        errorCode = ClassifyErrors.InvalidInput;
        return false;
    }

    public static ClassifyFeedbackRow ToFeedbackRow(
        string feedbackId,
        string outcomeId,
        string transactionId,
        string evaluationId,
        string normalizationVersion,
        string ruleSetVersionId,
        ClassifyFeedbackDecision decision,
        string? priorLedgerAllocationId,
        string? resultingLedgerAllocationId,
        string reason,
        string actor,
        string occurredAtUtc) =>
        new(
            feedbackId,
            outcomeId,
            transactionId,
            evaluationId,
            normalizationVersion,
            ruleSetVersionId,
            FormatFeedbackDecision(decision),
            priorLedgerAllocationId,
            resultingLedgerAllocationId,
            reason,
            actor,
            occurredAtUtc);

    public static ClassifyRuleProposalRow? ToProposalRow(
        string proposalId,
        string feedbackId,
        FeedbackProposalBuilder.Result proposal,
        string createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!FeedbackProposalBuilder.IsActiveProposal(proposal.Kind))
        {
            return null;
        }

        if (proposal.ProposedScopeFingerprint.Length != 64)
        {
            throw new InvalidOperationException("Proposal scope fingerprint must be 64 hex chars.");
        }

        return new ClassifyRuleProposalRow(
            proposalId,
            feedbackId,
            FeedbackProposalBuilder.RuleOriginFeedbackDerived,
            proposal.ProposalTypeWire,
            proposal.SourceRuleVersionId,
            proposal.ProposedScopeFingerprint,
            proposal.ProposedCategoryId,
            FeedbackProposalBuilder.LifecycleDraft,
            createdAtUtc);
    }

    public static ClassifyFeedbackRecordResult ToFeedbackResult(
        string feedbackId,
        string outcomeId,
        string? proposalId) =>
        new(ClassifyOperationIds.ContractVersion, feedbackId, outcomeId, proposalId);

    public static ClassificationOutcomeKind ParseOutcomeTypeOrUnknown(string outcomeType)
    {
        try
        {
            return ParseStoredOutcomeType(outcomeType);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ClassificationOutcomeKind.Stale;
        }
    }
}

/// <summary>Durable classification_feedback row (no descriptions/tokens).</summary>
public sealed record ClassifyFeedbackRow(
    string FeedbackId,
    string OutcomeId,
    string TransactionId,
    string EvaluationId,
    string NormalizationVersion,
    string RuleSetVersionId,
    string DecisionType,
    string? PriorLedgerAllocationId,
    string? ResultingLedgerAllocationId,
    string Reason,
    string Actor,
    string OccurredAt);

/// <summary>Durable non-active rule_proposal row (draft only).</summary>
public sealed record ClassifyRuleProposalRow(
    string ProposalId,
    string FeedbackId,
    string RuleOrigin,
    string ProposalType,
    string? SourceRuleVersionId,
    string ProposedScopeFingerprint,
    string? ProposedCategoryId,
    string LifecycleState,
    string CreatedAt);
