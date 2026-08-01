using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Evaluation;
using Tally.Infrastructure.Classify.Storage;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure apply-run mapping helpers (DM-CLASSIFY-APPLY-RUN / TASK-CLASSIFY-RULEBOOK-APPLY-RUN-SAGA).
/// No I/O, no Ledger access, no TimeProvider. Never regenerates frozen item intent fields on resume.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const string ApplyAssignReason = "classify.apply.run";

    /// <summary>Canonical request fingerprint element for classify.apply.run (preview + apply identity).</summary>
    public static JsonElement ToApplyRunFingerprintElement(
        string contractVersion,
        string previewId,
        string applyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("applyId", applyId.Trim());
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("previewId", previewId.Trim());
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Semantic apply-run request fingerprint bound to the immutable preview evidence.
    /// Different preview, selection, category, mode, revisions, or reason → different fingerprint.
    /// </summary>
    public static string ComputeApplyRunRequestFingerprint(
        string applyId,
        ClassifyApplyPreviewRow preview,
        IReadOnlyList<ClassifyApplyPreviewItemRow> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(items);

        var parts = new List<string>
        {
            "apply:" + applyId.Trim(),
            "preview:" + preview.PreviewId,
            "eval:" + preview.EvaluationId,
            "evalFp:" + preview.EvaluationFingerprint,
            "selection:" + preview.SelectionHash,
            "selectionMode:" + preview.SelectionMode,
            "targetCat:" + preview.TargetCategoryFingerprint,
            "ruleAuth:" + preview.RuleAuthorityFingerprint,
            "storeGen:" + preview.StoreGenerationFingerprint
        };

        foreach (var item in items
                     .OrderBy(i => i.Ordinal)
                     .ThenBy(i => i.TransactionId, StringComparer.Ordinal))
        {
            parts.Add(string.Join(
                '\t',
                "item",
                item.Ordinal.ToString(CultureInfo.InvariantCulture),
                item.TransactionId,
                item.Mode,
                item.CategoryId,
                item.RuleVersionId ?? "",
                item.ExpectedCurrentCategoryId ?? "",
                item.ExpectedActiveAllocationId ?? "",
                item.ExpectedTransactionRevision,
                item.ExpectedRelationshipRevision,
                item.ExpectedAllocationRevision,
                item.CorrectionReason ?? ""));
        }

        return CanonicalClassificationHasher.HashOrderedLines(parts);
    }

    /// <summary>
    /// Per-item Ledger idempotency key derived only from apply identity + transaction id
    /// (stable across resume; never regenerated from live Ledger state).
    /// </summary>
    public static string DeriveItemIdempotencyKey(string applyId, string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        var payload = string.Concat("classify.apply.item\n", applyId.Trim(), "\n", transactionId.Trim());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Canonical fingerprint of the exact frozen Ledger request for one item
    /// (operation, target, preconditions, reason, key) — never includes descriptions/amounts.
    /// </summary>
    public static string ComputeItemLedgerRequestFingerprint(
        string ledgerOperationId,
        string transactionId,
        string categoryId,
        string? expectedActiveAllocationId,
        string expectedTransactionRevision,
        string expectedRelationshipRevision,
        string expectedAllocationRevision,
        string? correctionReason,
        string ledgerIdempotencyKey)
    {
        return CanonicalClassificationHasher.HashParts(
            ledgerOperationId,
            transactionId,
            categoryId,
            expectedActiveAllocationId,
            expectedTransactionRevision,
            expectedRelationshipRevision,
            expectedAllocationRevision,
            correctionReason,
            ledgerIdempotencyKey,
            CategoryAllocationMutationVersions.ClassificationV1);
    }

    public static ClassifyApplyRunRow ToApplyRunRow(
        string applyId,
        string previewId,
        string requestFingerprint,
        string lifecycleState,
        int unresolvedFrontier,
        string actor,
        string startedAtUtc,
        string? completedAtUtc = null) =>
        new(
            applyId,
            previewId,
            requestFingerprint,
            lifecycleState,
            unresolvedFrontier,
            actor,
            startedAtUtc,
            completedAtUtc);

    public static ClassifyApplyItemRow ToPlannedApplyItemRow(
        string applyId,
        ClassifyApplyPreviewItemRow previewItem)
    {
        ArgumentNullException.ThrowIfNull(previewItem);
        var operationId = ApplyReplayPolicy.ResolveLedgerOperationId(previewItem.Mode)
            ?? throw new InvalidOperationException("Unknown preview item mode.");
        var idempotencyKey = DeriveItemIdempotencyKey(applyId, previewItem.TransactionId);
        var reason = previewItem.Mode == PreviewItemModeCorrect
            ? previewItem.CorrectionReason
            : ApplyAssignReason;
        var requestFingerprint = ComputeItemLedgerRequestFingerprint(
            operationId,
            previewItem.TransactionId,
            previewItem.CategoryId,
            previewItem.ExpectedActiveAllocationId,
            previewItem.ExpectedTransactionRevision,
            previewItem.ExpectedRelationshipRevision,
            previewItem.ExpectedAllocationRevision,
            reason,
            idempotencyKey);

        return new ClassifyApplyItemRow(
            applyId,
            previewItem.Ordinal,
            previewItem.TransactionId,
            operationId,
            previewItem.CategoryId,
            previewItem.ExpectedActiveAllocationId,
            previewItem.ExpectedTransactionRevision,
            previewItem.ExpectedRelationshipRevision,
            previewItem.ExpectedAllocationRevision,
            reason,
            requestFingerprint,
            idempotencyKey,
            ApplyReplayPolicy.ItemStatePlanned,
            LedgerResultFingerprint: null,
            LedgerAllocationId: null,
            PriorLedgerAllocationId: null,
            SafeErrorCode: null);
    }

    public static AssignCategoryInput ToAssignInput(ClassifyApplyItemRow item) =>
        new(
            item.TransactionId,
            item.CategoryId,
            item.CorrectionReason ?? ApplyAssignReason,
            item.ExpectedTransactionRevision,
            item.ExpectedRelationshipRevision,
            item.ExpectedAllocationRevision,
            ExpectedActiveAllocationId: null,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1);

    public static CorrectCategoryInput ToCorrectInput(ClassifyApplyItemRow item) =>
        new(
            item.TransactionId,
            item.CategoryId,
            item.CorrectionReason ?? ApplyAssignReason,
            item.ExpectedActiveAllocationId,
            item.ExpectedTransactionRevision,
            item.ExpectedRelationshipRevision,
            item.ExpectedAllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1);

    /// <summary>
    /// Revalidate one frozen preview item against a live apply_preflight row.
    /// Any mismatch rejects the whole run before mutation.
    /// </summary>
    public static bool TryMatchFrozenPreflight(
        ClassifyApplyPreviewItemRow frozen,
        ClassificationProjectionItem? live,
        bool isMissing,
        IReadOnlySet<string> activeCategoryIds,
        out string? errorCode)
    {
        errorCode = null;
        if (isMissing || live is null)
        {
            errorCode = ClassifyErrors.Stale;
            return false;
        }

        if (!string.Equals(live.TransactionId, frozen.TransactionId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.Integrity;
            return false;
        }

        if (!activeCategoryIds.Contains(frozen.CategoryId))
        {
            errorCode = ClassifyErrors.Stale;
            return false;
        }

        if (!string.Equals(live.TransactionRevision, frozen.ExpectedTransactionRevision, StringComparison.Ordinal)
            || !string.Equals(live.RelationshipRevision, frozen.ExpectedRelationshipRevision, StringComparison.Ordinal)
            || !string.Equals(live.AllocationRevision, frozen.ExpectedAllocationRevision, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.Stale;
            return false;
        }

        if (string.Equals(frozen.Mode, PreviewItemModeAssign, StringComparison.Ordinal))
        {
            if (live.CategoryMutationState != CategoryMutationState.Assignable
                || !string.IsNullOrWhiteSpace(live.CurrentCategoryId)
                || !string.IsNullOrWhiteSpace(live.CurrentAllocationId)
                || !string.IsNullOrWhiteSpace(frozen.ExpectedActiveAllocationId))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            return true;
        }

        if (string.Equals(frozen.Mode, PreviewItemModeCorrect, StringComparison.Ordinal))
        {
            if (live.CategoryMutationState != CategoryMutationState.Correctable
                || string.IsNullOrWhiteSpace(live.CurrentCategoryId)
                || string.IsNullOrWhiteSpace(live.CurrentAllocationId)
                || !string.Equals(live.CurrentCategoryId, frozen.ExpectedCurrentCategoryId, StringComparison.Ordinal)
                || !string.Equals(live.CurrentAllocationId, frozen.ExpectedActiveAllocationId, StringComparison.Ordinal))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            return true;
        }

        errorCode = ClassifyErrors.SelectionInvalid;
        return false;
    }

    public static ClassifyApplyRunResult ToApplyRunResult(
        string applyId,
        string previewId,
        IReadOnlyList<ClassifyApplyItemRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var publicItems = items
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .Select(i => new ClassifyApplyItemResult(
                i.TransactionId,
                ApplyReplayPolicy.ToPublicKind(i.ItemState),
                i.SafeErrorCode,
                i.LedgerAllocationId))
            .ToArray();

        return new ClassifyApplyRunResult(
            ClassifyOperationIds.ContractVersion,
            applyId,
            previewId,
            publicItems,
            publicItems.Count(i => i.Kind == ClassifyApplyItemResultKind.Applied),
            publicItems.Count(i => i.Kind == ClassifyApplyItemResultKind.AlreadyApplied),
            publicItems.Count(i => i.Kind == ClassifyApplyItemResultKind.Rejected),
            publicItems.Count(i => i.Kind == ClassifyApplyItemResultKind.Failed),
            publicItems.Count(i => i.Kind == ClassifyApplyItemResultKind.Unresolved));
    }

    public static string ComputeLedgerResultFingerprint(
        string itemState,
        string? allocationEventId,
        string? safeErrorCode) =>
        CanonicalClassificationHasher.HashParts(itemState, allocationEventId, safeErrorCode);
}

/// <summary>Durable apply_run header row.</summary>
public sealed record ClassifyApplyRunRow(
    string ApplyId,
    string PreviewId,
    string RequestFingerprint,
    string LifecycleState,
    int UnresolvedFrontier,
    string Actor,
    string StartedAt,
    string? CompletedAt);

/// <summary>Durable apply_item row — frozen intent + terminal result fields.</summary>
public sealed record ClassifyApplyItemRow(
    string ApplyId,
    int Ordinal,
    string TransactionId,
    string LedgerOperationId,
    string CategoryId,
    string? ExpectedActiveAllocationId,
    string ExpectedTransactionRevision,
    string ExpectedRelationshipRevision,
    string ExpectedAllocationRevision,
    string? CorrectionReason,
    string LedgerRequestFingerprint,
    string LedgerIdempotencyKey,
    string ItemState,
    string? LedgerResultFingerprint,
    string? LedgerAllocationId,
    string? PriorLedgerAllocationId,
    string? SafeErrorCode);
