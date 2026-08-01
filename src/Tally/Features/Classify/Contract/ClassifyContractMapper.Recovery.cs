using System.Text.Json;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Recovery;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure abandon/cleanup mapping (DM-CLASSIFY-STATE-STORE / TASK-CLASSIFY-RULEBOOK-ABANDON-CLEANUP).
/// Never maps private paths, payloads, descriptions, amounts, or tokens into results.
/// </summary>
public static partial class ClassifyContractMapper
{
    public static JsonElement ToAbandonFingerprintElement(
        string contractVersion,
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        string reason)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("reason", reason.Trim());
            writer.WriteString("subjectId", subjectId.Trim());
            writer.WriteString("subjectType", ClassifyRetentionPolicy.FormatSubjectType(subjectType));
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement ToCleanupFingerprintElement(string contractVersion, string policyVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteString("policyVersion", policyVersion.Trim());
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static ClassifyAbandonResult ToAbandonResult(
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        bool abandoned) =>
        new(ClassifyOperationIds.ContractVersion, subjectType, subjectId, abandoned);

    public static ClassifyCleanupResult ToCleanupResult(
        string cleanupId,
        string policyVersion,
        int removedArtifactCount,
        int retainedArtifactCount,
        int removedTemporaryCount,
        int removedExpiredPreviewCount,
        int removedAbandonedPayloadCount) =>
        new(
            ClassifyOperationIds.ContractVersion,
            cleanupId,
            policyVersion,
            removedArtifactCount,
            retainedArtifactCount,
            removedTemporaryCount,
            removedExpiredPreviewCount,
            removedAbandonedPayloadCount);

    public static ClassifyAbandonmentTombstoneRow ToTombstoneRow(
        string tombstoneId,
        ClassifyStatusSubjectType subjectType,
        string subjectId,
        string reason,
        string actor,
        string abandonedAtUtc,
        int removedPayloadCount) =>
        new(
            tombstoneId,
            ClassifyRetentionPolicy.FormatSubjectType(subjectType),
            subjectId.Trim(),
            reason,
            actor,
            abandonedAtUtc,
            removedPayloadCount);

    public static ClassifyCleanupEventReceiptRow ToCleanupEventRow(
        string cleanupId,
        string policyVersion,
        int recognizedRemovedCount,
        int expiredPreviewCount,
        int abandonedPayloadCount,
        string actor,
        string occurredAtUtc,
        int removedArtifactCount,
        int retainedArtifactCount) =>
        new(
            cleanupId,
            policyVersion,
            recognizedRemovedCount,
            expiredPreviewCount,
            abandonedPayloadCount,
            actor,
            occurredAtUtc,
            removedArtifactCount,
            retainedArtifactCount);
}
