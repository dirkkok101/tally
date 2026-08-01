using System.Text.Json;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure apply-preview mapping helpers (DM-CLASSIFY-APPLY-RUN / TASK-CLASSIFY-RULEBOOK-APPLY-PREVIEW).
/// No I/O, no Ledger access, no TimeProvider. Never maps descriptions or amounts into preview rows.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const string SelectionModeSelectedOutcomes = "selected_outcomes";
    public const string SelectionModeExactRule = "exact_rule";
    public const string SelectionModeExplicitCorrections = "explicit_corrections";

    public const string PreviewItemModeAssign = ApplyAuthorizationPolicy.ModeAssign;
    public const string PreviewItemModeCorrect = ApplyAuthorizationPolicy.ModeCorrect;

    /// <summary>Canonical request fingerprint element for classify.apply.preview.</summary>
    public static JsonElement ToApplyPreviewFingerprintElement(
        string contractVersion,
        string evaluationId,
        ClassifyApplySelection selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        ArgumentNullException.ThrowIfNull(selection);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("evaluationId", evaluationId.Trim());
            writer.WritePropertyName("selection");
            WriteSelection(writer, selection);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static string FormatSelectionMode(ClassifyApplySelectionMode mode) => mode switch
    {
        ClassifyApplySelectionMode.SelectedOutcomes => SelectionModeSelectedOutcomes,
        ClassifyApplySelectionMode.ExactRule => SelectionModeExactRule,
        ClassifyApplySelectionMode.ExplicitCorrections => SelectionModeExplicitCorrections,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown apply selection mode.")
    };

    /// <summary>
    /// Stable 64-hex selection hash over mode and ordered selection identity only
    /// (no descriptions, amounts, or private paths).
    /// </summary>
    public static string ComputeSelectionHash(ClassifyApplySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var parts = new List<string> { FormatSelectionMode(selection.Mode) };

        switch (selection.Mode)
        {
            case ClassifyApplySelectionMode.SelectedOutcomes:
                foreach (var id in (selection.OutcomeIds ?? Array.Empty<string>())
                             .Where(id => !string.IsNullOrWhiteSpace(id))
                             .Select(id => id.Trim())
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(id => id, StringComparer.Ordinal))
                {
                    parts.Add("outcome:" + id);
                }

                break;
            case ClassifyApplySelectionMode.ExactRule:
                parts.Add("rule:" + (selection.RuleVersionId ?? string.Empty).Trim());
                break;
            case ClassifyApplySelectionMode.ExplicitCorrections:
                foreach (var item in (selection.CorrectionItems ?? Array.Empty<ClassifyExplicitCorrectionItem>())
                             .OrderBy(i => i.TransactionId, StringComparer.Ordinal)
                             .ThenBy(i => i.OutcomeId, StringComparer.Ordinal))
                {
                    parts.Add(string.Join(
                        '\t',
                        "correction",
                        item.TransactionId.Trim(),
                        item.OutcomeId.Trim(),
                        item.CurrentCategoryId.Trim(),
                        item.TargetCategoryId.Trim(),
                        item.Reason.Trim()));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(selection), selection.Mode, "Unknown mode.");
        }

        return CanonicalClassificationHasher.HashOrderedLines(parts);
    }

    /// <summary>Fingerprint over ordered target category identities on the authorized selection.</summary>
    public static string ComputeTargetCategoryFingerprint(
        IReadOnlyList<ApplyAuthorizationPolicy.AuthorizedCandidate> candidates) =>
        CanonicalClassificationHasher.HashOrderedLines(
            candidates
                .OrderBy(c => c.TransactionId, StringComparer.Ordinal)
                .Select(c => c.TargetCategoryId));

    /// <summary>
    /// Fingerprint over rule authority evidence for the selection
    /// (exact rule version when broad-authorized; ordered contributing rules for selected outcomes;
    /// "corrections" token for explicit correction mode).
    /// </summary>
    public static string ComputeRuleAuthorityFingerprint(
        ApplyAuthorizationPolicy.AuthorizationResult authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Mode == ClassifyApplySelectionMode.ExplicitCorrections)
        {
            return CanonicalClassificationHasher.HashUtf8("explicit_corrections");
        }

        if (authorization.Mode == ClassifyApplySelectionMode.ExactRule
            && authorization.BroadAuthorityGranted
            && !string.IsNullOrWhiteSpace(authorization.ExactRuleVersionId))
        {
            return CanonicalClassificationHasher.HashParts(
                "exact_rule",
                authorization.ExactRuleVersionId,
                "broad_apply");
        }

        var ruleIds = authorization.Candidates
            .Select(c => c.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return CanonicalClassificationHasher.HashOrderedLines(
            ruleIds.Length == 0 ? ["selected_outcomes:none"] : ruleIds.Select(id => "rule:" + id));
    }

    /// <summary>
    /// Build the complete bounded public apply.preview result from the retained
    /// preview row and ordered preview items (DM-CLASSIFY-APPLY-RUN disclosure).
    /// Transaction IDs and target categories follow preview ordinal order;
    /// contributing rule versions are distinct and ordered lexicographically.
    /// </summary>
    public static ClassifyApplyPreviewResult ToApplyPreviewResult(
        ClassifyApplyPreviewRow preview,
        IReadOnlyList<ClassifyApplyPreviewItemRow> orderedItems,
        int assignableCount,
        int correctableCount)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(orderedItems);

        var ordered = orderedItems
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .ToArray();

        var selectedTransactionIds = ordered
            .Select(i => i.TransactionId)
            .ToArray();
        var targetCategoryIds = ordered
            .Select(i => i.CategoryId)
            .ToArray();
        var contributingRuleVersionIds = ordered
            .Select(i => i.RuleVersionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (selectedTransactionIds.Length != preview.SelectedCount)
        {
            throw new InvalidOperationException(
                "Preview selected transaction count must match retained selected_count.");
        }

        return new ClassifyApplyPreviewResult(
            ContractVersion: ClassifyOperationIds.ContractVersion,
            PreviewId: preview.PreviewId,
            EvaluationId: preview.EvaluationId,
            EvaluationFingerprint: preview.EvaluationFingerprint,
            SelectionMode: preview.SelectionMode,
            SelectionHash: preview.SelectionHash,
            TargetCategoryFingerprint: preview.TargetCategoryFingerprint,
            RuleAuthorityFingerprint: preview.RuleAuthorityFingerprint,
            ContributingRuleVersionIds: contributingRuleVersionIds,
            SelectedTransactionIds: selectedTransactionIds,
            TargetCategoryIds: targetCategoryIds,
            SelectedCount: preview.SelectedCount,
            AssignableCount: assignableCount,
            CorrectableCount: correctableCount,
            ExclusionCount: preview.ExclusionCount,
            NoSuggestionCount: preview.NoSuggestionCount,
            ConflictCount: preview.ConflictCount,
            LedgerContractVersion: preview.LedgerContractVersion,
            ProjectionVersion: preview.ProjectionVersion,
            StoreGenerationFingerprint: preview.StoreGenerationFingerprint,
            PreflightSnapshotId: preview.PreflightSnapshotId,
            PreflightExpiresAt: preview.PreflightExpiresAt,
            CategoryLifecycleFingerprint: preview.CategoryLifecycleFingerprint,
            ExpiresAt: preview.ExpiresAt);
    }

    public static ClassifyApplyPreviewRow ToApplyPreviewRow(
        string previewId,
        string? operationIdempotencyKey,
        string evaluationId,
        string evaluationFingerprint,
        ClassifyApplySelectionMode selectionMode,
        string selectionHash,
        string ledgerContractVersion,
        string projectionVersion,
        string storeGenerationFingerprint,
        string preflightSnapshotId,
        string preflightExpiresAt,
        string categoryLifecycleFingerprint,
        string targetCategoryFingerprint,
        string ruleAuthorityFingerprint,
        string expiresAt,
        int selectedCount,
        int exclusionCount,
        int noSuggestionCount,
        int conflictCount,
        string actor,
        string createdAtUtc) =>
        new(
            previewId,
            operationIdempotencyKey,
            evaluationId,
            evaluationFingerprint,
            FormatSelectionMode(selectionMode),
            selectionHash,
            ledgerContractVersion,
            projectionVersion,
            storeGenerationFingerprint,
            preflightSnapshotId,
            preflightExpiresAt,
            categoryLifecycleFingerprint,
            targetCategoryFingerprint,
            ruleAuthorityFingerprint,
            expiresAt,
            selectedCount,
            exclusionCount,
            noSuggestionCount,
            conflictCount,
            actor,
            createdAtUtc);

    public static ClassifyApplyPreviewItemRow ToApplyPreviewItemRow(
        string previewId,
        int ordinal,
        ApplyAuthorizationPolicy.AuthorizedCandidate candidate,
        ClassificationProjectionItem preflightItem)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(preflightItem);

        return new ClassifyApplyPreviewItemRow(
            previewId,
            ordinal,
            candidate.OutcomeId,
            candidate.TransactionId,
            candidate.Mode,
            candidate.TargetCategoryId,
            candidate.RuleVersionId,
            candidate.Mode == PreviewItemModeCorrect ? candidate.ExpectedCurrentCategoryId : null,
            candidate.Mode == PreviewItemModeCorrect ? preflightItem.CurrentAllocationId : null,
            preflightItem.TransactionRevision,
            preflightItem.RelationshipRevision,
            preflightItem.AllocationRevision,
            candidate.Mode == PreviewItemModeCorrect ? candidate.CorrectionReason : null);
    }

    /// <summary>
    /// Validate that preflight returns every selected ID with a mutation state matching the reviewed mode
    /// and that retained item lifecycle still matches. Never accepts descriptions into the decision.
    /// </summary>
    public static bool TryMatchPreflightItem(
        ApplyAuthorizationPolicy.AuthorizedCandidate candidate,
        ClassificationProjectionItem? item,
        bool isMissing,
        IReadOnlySet<string> activeCategoryIds,
        out string? errorCode)
    {
        errorCode = null;
        if (isMissing || item is null)
        {
            errorCode = ClassifyErrors.SelectionInvalid;
            return false;
        }

        if (!string.Equals(item.TransactionId, candidate.TransactionId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.Integrity;
            return false;
        }

        if (!activeCategoryIds.Contains(candidate.TargetCategoryId))
        {
            errorCode = ClassifyErrors.Stale;
            return false;
        }

        if (candidate.Mode == PreviewItemModeAssign)
        {
            // Assignment previews require the retained evaluation item lifecycle to still match
            // (void / supersede / allocation / relationship drift fails closed as stale).
            var currentLifecycle = ComputeItemLifecycleFingerprint(item);
            if (!string.Equals(
                    currentLifecycle,
                    candidate.RetainedItemLifecycleFingerprint,
                    StringComparison.Ordinal))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            if (item.CategoryMutationState != CategoryMutationState.Assignable)
            {
                errorCode = item.CategoryMutationState == CategoryMutationState.Ineligible
                    ? ClassifyErrors.SelectionInvalid
                    : ClassifyErrors.Stale;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(item.CurrentCategoryId)
                || !string.IsNullOrWhiteSpace(item.CurrentAllocationId))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            return true;
        }

        if (candidate.Mode == PreviewItemModeCorrect)
        {
            // Correction previews freeze the current preflight allocation/revisions; retained
            // evaluation item lifecycle is not required to match because the transaction is
            // already categorized (allocation revision advanced after evaluation membership).
            if (item.CategoryMutationState != CategoryMutationState.Correctable)
            {
                errorCode = item.CategoryMutationState == CategoryMutationState.Ineligible
                    ? ClassifyErrors.SelectionInvalid
                    : ClassifyErrors.Stale;
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.CurrentCategoryId)
                || string.IsNullOrWhiteSpace(item.CurrentAllocationId)
                || !string.Equals(
                    item.CurrentCategoryId,
                    candidate.ExpectedCurrentCategoryId,
                    StringComparison.Ordinal))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            return true;
        }

        errorCode = ClassifyErrors.SelectionInvalid;
        return false;
    }

    private static void WriteSelection(Utf8JsonWriter writer, ClassifyApplySelection selection)
    {
        writer.WriteStartObject();
        writer.WriteString("mode", FormatSelectionMode(selection.Mode));
        if (selection.OutcomeIds is { Count: > 0 })
        {
            writer.WritePropertyName("outcomeIds");
            writer.WriteStartArray();
            foreach (var id in selection.OutcomeIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id.Trim())
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull("outcomeIds");
        }

        if (!string.IsNullOrWhiteSpace(selection.RuleVersionId))
        {
            writer.WriteString("ruleVersionId", selection.RuleVersionId.Trim());
        }
        else
        {
            writer.WriteNull("ruleVersionId");
        }

        if (selection.CorrectionItems is { Count: > 0 })
        {
            writer.WritePropertyName("correctionItems");
            writer.WriteStartArray();
            foreach (var item in selection.CorrectionItems
                         .OrderBy(i => i.TransactionId, StringComparer.Ordinal)
                         .ThenBy(i => i.OutcomeId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("currentCategoryId", item.CurrentCategoryId.Trim());
                writer.WriteString("outcomeId", item.OutcomeId.Trim());
                writer.WriteString("reason", item.Reason.Trim());
                writer.WriteString("targetCategoryId", item.TargetCategoryId.Trim());
                writer.WriteString("transactionId", item.TransactionId.Trim());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull("correctionItems");
        }

        writer.WriteEndObject();
    }
}

/// <summary>Durable apply_preview header row (no descriptions/amounts).</summary>
public sealed record ClassifyApplyPreviewRow(
    string PreviewId,
    string? OperationIdempotencyKey,
    string EvaluationId,
    string EvaluationFingerprint,
    string SelectionMode,
    string SelectionHash,
    string LedgerContractVersion,
    string ProjectionVersion,
    string StoreGenerationFingerprint,
    string PreflightSnapshotId,
    string PreflightExpiresAt,
    string CategoryLifecycleFingerprint,
    string TargetCategoryFingerprint,
    string RuleAuthorityFingerprint,
    string ExpiresAt,
    int SelectedCount,
    int ExclusionCount,
    int NoSuggestionCount,
    int ConflictCount,
    string Actor,
    string CreatedAt);

/// <summary>Durable apply_preview_item row — revisions and identities only.</summary>
public sealed record ClassifyApplyPreviewItemRow(
    string PreviewId,
    int Ordinal,
    string OutcomeId,
    string TransactionId,
    string Mode,
    string CategoryId,
    string? RuleVersionId,
    string? ExpectedCurrentCategoryId,
    string? ExpectedActiveAllocationId,
    string ExpectedTransactionRevision,
    string ExpectedRelationshipRevision,
    string ExpectedAllocationRevision,
    string? CorrectionReason);
