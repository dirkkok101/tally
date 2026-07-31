using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Pure rule mapping helpers for classify.rule.save (DM-CLASSIFY-RULE-LIFECYCLE).
/// No I/O, no Ledger access, no business-policy decisions beyond wire/domain shape transforms.
/// </summary>
public static partial class ClassifyContractMapper
{
    public const int MaxReasonLength = 1024;

    public static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Stable attribution string for created_by / actor columns.</summary>
    public static string FormatActor(string kind, string label, string? runId)
    {
        var baseActor = string.Concat(kind.Trim(), ":", label.Trim());
        return string.IsNullOrWhiteSpace(runId) ? baseActor : string.Concat(baseActor, ":", runId.Trim());
    }

    public static bool TryNormalizeReason(string? reason, out string normalized)
    {
        normalized = reason?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaxReasonLength)
        {
            return false;
        }

        foreach (var ch in normalized)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    public static string ToFieldKey(ClassificationRuleFieldKey fieldKey) => fieldKey switch
    {
        ClassificationRuleFieldKey.DescriptionNormalized => ClassificationRuleVocabulary.DescriptionNormalized,
        ClassificationRuleFieldKey.AccountId => ClassificationRuleVocabulary.AccountId,
        ClassificationRuleFieldKey.AmountDirection => ClassificationRuleVocabulary.AmountDirection,
        ClassificationRuleFieldKey.AmountAbsoluteMinor => ClassificationRuleVocabulary.AmountAbsoluteMinor,
        _ => fieldKey.ToString()
    };

    public static string ToPredicateKind(ClassificationRulePredicateKind predicateKind) => predicateKind switch
    {
        ClassificationRulePredicateKind.Equals => ClassificationRuleVocabulary.EqualsPredicate,
        ClassificationRulePredicateKind.StartsWith => ClassificationRuleVocabulary.StartsWithPredicate,
        ClassificationRulePredicateKind.ContainsTokenSequence => ClassificationRuleVocabulary.ContainsTokenSequencePredicate,
        ClassificationRulePredicateKind.BetweenInclusive => ClassificationRuleVocabulary.BetweenInclusivePredicate,
        _ => predicateKind.ToString()
    };

    public static string? ToDirectionValue(ClassificationAmountDirectionValue? value) => value switch
    {
        ClassificationAmountDirectionValue.Inflow => ClassificationRuleVocabulary.DirectionInflow,
        ClassificationAmountDirectionValue.Outflow => ClassificationRuleVocabulary.DirectionOutflow,
        _ => null
    };

    public static ClassificationRuleFieldKey ParseFieldKey(string fieldKey) => fieldKey switch
    {
        ClassificationRuleVocabulary.DescriptionNormalized => ClassificationRuleFieldKey.DescriptionNormalized,
        ClassificationRuleVocabulary.AccountId => ClassificationRuleFieldKey.AccountId,
        ClassificationRuleVocabulary.AmountDirection => ClassificationRuleFieldKey.AmountDirection,
        ClassificationRuleVocabulary.AmountAbsoluteMinor => ClassificationRuleFieldKey.AmountAbsoluteMinor,
        _ => throw new ArgumentOutOfRangeException(nameof(fieldKey), fieldKey, "Unknown classification field key.")
    };

    public static ClassificationRulePredicateKind ParsePredicateKind(string predicateKind) => predicateKind switch
    {
        ClassificationRuleVocabulary.EqualsPredicate => ClassificationRulePredicateKind.Equals,
        ClassificationRuleVocabulary.StartsWithPredicate => ClassificationRulePredicateKind.StartsWith,
        ClassificationRuleVocabulary.ContainsTokenSequencePredicate => ClassificationRulePredicateKind.ContainsTokenSequence,
        ClassificationRuleVocabulary.BetweenInclusivePredicate => ClassificationRulePredicateKind.BetweenInclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(predicateKind), predicateKind, "Unknown classification predicate.")
    };

    public static ClassificationAmountDirectionValue? ParseDirectionValue(string? enumValue) => enumValue switch
    {
        ClassificationRuleVocabulary.DirectionInflow => ClassificationAmountDirectionValue.Inflow,
        ClassificationRuleVocabulary.DirectionOutflow => ClassificationAmountDirectionValue.Outflow,
        _ => null
    };

    public static IReadOnlyList<(int Ordinal, string FieldKey, string PredicateKind, string? ValueText, long? ValueMinorMin, long? ValueMinorMax, string? EnumValue)>
        ToVocabularyInputs(IReadOnlyList<ClassificationRuleConditionInput> conditions) =>
        conditions
            .Select(c => (
                c.Ordinal,
                ToFieldKey(c.FieldKey),
                ToPredicateKind(c.PredicateKind),
                c.ValueText,
                c.ValueMinorMin,
                c.ValueMinorMax,
                ToDirectionValue(c.EnumValue) ?? c.ValueText))
            .ToArray();

    public static ClassificationRuleConditionInput ToConditionInput(RuleCondition condition) =>
        new(
            condition.Ordinal,
            ParseFieldKey(condition.FieldKey),
            ParsePredicateKind(condition.PredicateKind),
            condition.ValueText,
            condition.ValueMinorMin,
            condition.ValueMinorMax,
            ParseDirectionValue(condition.EnumValue));

    public static IReadOnlyList<ClassificationRuleConditionInput> ToConditionInputs(
        IReadOnlyList<RuleCondition> conditions) =>
        conditions
            .OrderBy(c => c.Ordinal)
            .ThenBy(c => c.FieldKey, StringComparer.Ordinal)
            .Select(ToConditionInput)
            .ToArray();

    /// <summary>
    /// Scope hash over ordered canonical condition semantics (field + predicate + typed operands).
    /// Category and normalization are stored as separate immutable columns.
    /// </summary>
    public static string ComputeScopeHash(IReadOnlyList<RuleCondition> conditions)
    {
        var payload = string.Join(
            '\n',
            conditions
                .OrderBy(c => c.Ordinal)
                .ThenBy(c => c.FieldKey, StringComparer.Ordinal)
                .Select(c => c.ToSemanticJson()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Canonical request fingerprint payload for classify.rule.save (excludes idempotency key / timestamps).
    /// Uses domain-normalized conditions so equivalent normalizations share one fingerprint.
    /// </summary>
    public static JsonElement ToRuleSaveFingerprintElement(
        string ruleId,
        string? priorVersionId,
        string categoryId,
        string normalizationVersion,
        IReadOnlyList<RuleCondition> conditions,
        string reason)
    {
        var request = new ClassifyRuleSaveRequest(
            ClassifyOperationIds.ContractVersion,
            ruleId,
            priorVersionId,
            categoryId,
            normalizationVersion,
            ToConditionInputs(conditions),
            reason);
        return JsonSerializer.SerializeToElement(request, ClassifyJsonContext.Default.ClassifyRuleSaveRequest);
    }

    public static string SerializeRuleSaveResult(ClassifyRuleSaveResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyRuleSaveResult);

    public static ClassifyRuleSaveResult? TryDeserializeRuleSaveResult(string terminalResult)
    {
        try
        {
            return JsonSerializer.Deserialize(terminalResult, ClassifyJsonContext.Default.ClassifyRuleSaveResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsSupportedNormalizationVersion(string? version) =>
        string.Equals(version, NormalizationDescriptor.V1.Version, StringComparison.Ordinal)
        || string.Equals(version, ClassificationNormalizationVersions.V1, StringComparison.Ordinal);

    /// <summary>
    /// Map Ledger category-list failures for rule.save: compatibility → LedgerIncompatible;
    /// known host unavailability → LedgerUnavailable; otherwise Integrity (fail closed).
    /// </summary>
    public static string MapLedgerCategoryListError(ProcessError? error)
    {
        if (error is null)
        {
            return ClassifyErrors.LedgerUnavailable;
        }

        if (string.Equals(error.Category, "compatibility", StringComparison.Ordinal)
            || string.Equals(error.Code, "contract.incompatible", StringComparison.Ordinal)
            || string.Equals(error.Code, ClassifyErrors.LedgerIncompatible, StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerIncompatible;
        }

        if (string.Equals(error.Code, ClassifyErrors.LedgerUnavailable, StringComparison.Ordinal)
            || string.Equals(error.Code, "host.unavailable", StringComparison.Ordinal))
        {
            return ClassifyErrors.LedgerUnavailable;
        }

        return ClassifyErrors.Integrity;
    }

    public static string NewRuleVersionId(DateTimeOffset timestamp)
    {
        // Compact deterministic-length id (32 hex chars) from time + entropy — no shared identity type required.
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        RandomNumberGenerator.Fill(bytes[6..]);
        return Convert.ToHexStringLower(bytes);
    }
}
