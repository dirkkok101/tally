using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Classify.Operations;

namespace Tally.Domain.Classify.Discovery;

/// <summary>
/// Versioned, checksum-bound, base64url discovery cursor codec for outcome and rule pages
/// (DD-CLASSIFY-PAGINATED-DISCOVERY / DM-CLASSIFY-OUTCOME-PAGE / DM-CLASSIFY-RULE-DISCOVERY /
/// TASK-CLASSIFY-ERGONOMICS-CURSOR-POLICY / bd-29ch).
/// <para>
/// Deterministic fixed-order text payload — no reflection JSON, OFFSET pagination,
/// durable server state, encryption claims, or caller-provided authority. Cursors are
/// not mutation authority and carry no description, amount, path, corpus, or rule prose.
/// </para>
/// </summary>
public static class ClassifyCursorCodec
{
    public const int CursorVersion = 1;
    public const int MaxEncodedUtf8Bytes = 4096;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 500;

    public const string OutcomeListOperationId = "classify.outcome.list";
    public const string RuleListOperationId = "classify.rule.list";

    /// <summary>
    /// CLASSIFY wire UTC timestamp form used by ClassifyContractMapper.FormatUtc
    /// (yyyy-MM-ddTHH:mm:ss.fffffffZ).
    /// </summary>
    public const string CanonicalUtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private const string FormatMarker = "CLASSIFY-CURSOR-V1";
    private const string KindOutcome = "outcome";
    private const string KindRule = "rule";

    /// <summary>Strict UTF-8: invalid sequences throw rather than replace.</summary>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Snapshot binding for an outcome.list continuation (no keyset position).</summary>
    public sealed record OutcomeSnapshotBinding(
        string EvaluationId,
        string FilterFingerprint,
        int PageSize,
        string EvaluationFingerprint,
        string ResultFingerprint,
        string RuleSetFingerprint,
        string CategoryLifecycleFingerprint,
        string LedgerGeneration,
        DateTimeOffset ExpiresAtUtc);

    /// <summary>Keyset resume position for outcome pages (ordinal then transactionId).</summary>
    public sealed record OutcomeKeysetPosition(int LastOrdinal, string LastTransactionId);

    /// <summary>Snapshot / high-water binding for a rule.list continuation.</summary>
    public sealed record RuleSnapshotBinding(
        string FilterFingerprint,
        int PageSize,
        string HighWaterCreatedAt,
        string HighWaterRuleVersionId,
        string AuthorityFingerprint,
        string CategoryLifecycleFingerprint,
        DateTimeOffset ExpiresAtUtc);

    /// <summary>Keyset resume position for rule pages (createdAt then ruleVersionId).</summary>
    public sealed record RuleKeysetPosition(string LastCreatedAt, string LastRuleVersionId);

    /// <summary>
    /// Encode an outcome discovery continuation. Fails closed when bounds, keys, or size are invalid.
    /// </summary>
    public static bool TryEncodeOutcome(
        OutcomeSnapshotBinding binding,
        OutcomeKeysetPosition position,
        out string? encoded,
        out string? errorCode)
    {
        encoded = null;
        errorCode = null;

        if (!TryValidateOutcomeBinding(binding, out errorCode)
            || !TryValidateOutcomePosition(position, out errorCode))
        {
            return false;
        }

        var body = BuildOutcomeBody(binding, position);
        return TrySeal(body, out encoded, out errorCode);
    }

    /// <summary>
    /// Decode and fully validate an outcome continuation against the expected request/snapshot
    /// binding and current time. On any failure returns a typed error and null position
    /// (never an empty-page stand-in). Checksum is verified before field interpretation.
    /// </summary>
    public static bool TryDecodeOutcome(
        string? encoded,
        OutcomeSnapshotBinding expected,
        DateTimeOffset nowUtc,
        out OutcomeKeysetPosition? position,
        out string? errorCode)
    {
        position = null;
        errorCode = null;

        if (!TryValidateOutcomeBinding(expected, out errorCode))
        {
            return false;
        }

        if (!TryOpen(encoded, out var lines, out errorCode))
        {
            return false;
        }

        // Checksum already stripped by TryOpen.
        // 0 marker, 1 kind, 2 op, 3 pageSize, 4 filter, 5 evalId,
        // 6 evalFp, 7 resultFp, 8 ruleSetFp, 9 catFp, 10 ledgerGen, 11 exp, 12 ord, 13 tx
        if (lines.Length != 14)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Reject control characters in every decoded field before semantic use.
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsSafeCursorField(lines[i], allowEmpty: false))
            {
                errorCode = ClassifyErrors.CursorInvalid;
                return false;
            }
        }

        if (!string.Equals(lines[0], FormatMarker, StringComparison.Ordinal)
            || !string.Equals(lines[1], KindOutcome, StringComparison.Ordinal)
            || !string.Equals(lines[2], OutcomeListOperationId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!int.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize)
            || pageSize is < MinPageSize or > MaxPageSize)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!int.TryParse(lines[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastOrdinal)
            || lastOrdinal < 0)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                lines[11],
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt)
            || !string.Equals(lines[11], FormatExpires(expiresAt), StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Cross-request binding: operation/filter/page-size/evaluation identity.
        if (pageSize != expected.PageSize
            || !string.Equals(lines[4], expected.FilterFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[5], expected.EvaluationId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Expiry and snapshot / generation drift.
        if (nowUtc >= expiresAt
            || !string.Equals(lines[6], expected.EvaluationFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[7], expected.ResultFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[8], expected.RuleSetFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[9], expected.CategoryLifecycleFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[10], expected.LedgerGeneration, StringComparison.Ordinal)
            || !string.Equals(lines[11], FormatExpires(expected.ExpiresAtUtc), StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorStale;
            return false;
        }

        position = new OutcomeKeysetPosition(lastOrdinal, lines[13]);
        return true;
    }

    /// <summary>Encode a rule discovery continuation bound to first-page high-water.</summary>
    public static bool TryEncodeRule(
        RuleSnapshotBinding binding,
        RuleKeysetPosition position,
        out string? encoded,
        out string? errorCode)
    {
        encoded = null;
        errorCode = null;

        if (!TryValidateRuleBinding(binding, out errorCode)
            || !TryValidateRulePosition(binding, position, out errorCode))
        {
            return false;
        }

        var body = BuildRuleBody(binding, position);
        return TrySeal(body, out encoded, out errorCode);
    }

    /// <summary>
    /// Decode and fully validate a rule continuation. Any failure yields typed error and null position.
    /// Checksum is verified before field interpretation.
    /// </summary>
    public static bool TryDecodeRule(
        string? encoded,
        RuleSnapshotBinding expected,
        DateTimeOffset nowUtc,
        out RuleKeysetPosition? position,
        out string? errorCode)
    {
        position = null;
        errorCode = null;

        if (!TryValidateRuleBinding(expected, out errorCode))
        {
            return false;
        }

        if (!TryOpen(encoded, out var lines, out errorCode))
        {
            return false;
        }

        // Checksum already stripped by TryOpen.
        // 0 marker, 1 kind, 2 op, 3 pageSize, 4 filter, 5 hwCreated, 6 hwRule,
        // 7 authority, 8 cat, 9 exp, 10 lastCreated, 11 lastRule
        if (lines.Length != 12)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsSafeCursorField(lines[i], allowEmpty: false))
            {
                errorCode = ClassifyErrors.CursorInvalid;
                return false;
            }
        }

        if (!string.Equals(lines[0], FormatMarker, StringComparison.Ordinal)
            || !string.Equals(lines[1], KindRule, StringComparison.Ordinal)
            || !string.Equals(lines[2], RuleListOperationId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!int.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize)
            || pageSize is < MinPageSize or > MaxPageSize)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!IsCanonicalUtcTimestamp(lines[5])
            || !IsCanonicalUtcTimestamp(lines[10])
            || !IsSafeCursorField(lines[6], allowEmpty: false)
            || !IsSafeCursorField(lines[11], allowEmpty: false))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                lines[9],
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt)
            || !string.Equals(lines[9], FormatExpires(expiresAt), StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Resume must not strictly exceed the frozen first-page high-water tuple.
        if (CompareRuleKeyset(lines[10], lines[11], lines[5], lines[6]) > 0)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (pageSize != expected.PageSize
            || !string.Equals(lines[4], expected.FilterFingerprint, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (nowUtc >= expiresAt
            || !string.Equals(lines[5], expected.HighWaterCreatedAt, StringComparison.Ordinal)
            || !string.Equals(lines[6], expected.HighWaterRuleVersionId, StringComparison.Ordinal)
            || !string.Equals(lines[7], expected.AuthorityFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[8], expected.CategoryLifecycleFingerprint, StringComparison.Ordinal)
            || !string.Equals(lines[9], FormatExpires(expected.ExpiresAtUtc), StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorStale;
            return false;
        }

        position = new RuleKeysetPosition(lines[10], lines[11]);
        return true;
    }

    /// <summary>True when encoded UTF-8 length is within the hard cursor size bound.</summary>
    public static bool IsWithinEncodedSizeLimit(string encoded) =>
        Encoding.UTF8.GetByteCount(encoded) <= MaxEncodedUtf8Bytes;

    /// <summary>
    /// Canonical field safety for newline-delimited cursor payloads: nonblank and free of
    /// CR/LF/NUL and every other C0/C1-style control (including TAB and DEL).
    /// </summary>
    public static bool IsSafeCursorField(string? value, bool allowEmpty = false)
    {
        if (value is null)
        {
            return false;
        }

        if (value.Length == 0)
        {
            return allowEmpty;
        }

        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            // Reject all C0 controls (0x00-0x1F) and DEL (0x7F). Format uses LF only as
            // the external line delimiter, never inside a field.
            if (c <= 0x1F || c == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> is exactly CLASSIFY canonical UTC
    /// (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>) including round-trip equality.
    /// </summary>
    public static bool IsCanonicalUtcTimestamp(string? value)
    {
        if (!IsSafeCursorField(value, allowEmpty: false))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                CanonicalUtcTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        var reformatted = parsed.UtcDateTime.ToString(CanonicalUtcTimestampFormat, CultureInfo.InvariantCulture);
        return string.Equals(value, reformatted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordinal keyset compare for rule pages: createdAt then ruleVersionId.
    /// Negative when left &lt; right; zero when equal; positive when left &gt; right.
    /// </summary>
    public static int CompareRuleKeyset(
        string leftCreatedAt,
        string leftRuleVersionId,
        string rightCreatedAt,
        string rightRuleVersionId)
    {
        var cmp = string.CompareOrdinal(leftCreatedAt, rightCreatedAt);
        return cmp != 0
            ? cmp
            : string.CompareOrdinal(leftRuleVersionId, rightRuleVersionId);
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static string BuildOutcomeBody(OutcomeSnapshotBinding binding, OutcomeKeysetPosition position)
    {
        // Fixed order; no field names (unknown-field rejection is structural).
        var sb = new StringBuilder(512);
        AppendLine(sb, FormatMarker);
        AppendLine(sb, KindOutcome);
        AppendLine(sb, OutcomeListOperationId);
        AppendLine(sb, binding.PageSize.ToString(CultureInfo.InvariantCulture));
        AppendLine(sb, binding.FilterFingerprint);
        AppendLine(sb, binding.EvaluationId);
        AppendLine(sb, binding.EvaluationFingerprint);
        AppendLine(sb, binding.ResultFingerprint);
        AppendLine(sb, binding.RuleSetFingerprint);
        AppendLine(sb, binding.CategoryLifecycleFingerprint);
        AppendLine(sb, binding.LedgerGeneration);
        AppendLine(sb, FormatExpires(binding.ExpiresAtUtc));
        AppendLine(sb, position.LastOrdinal.ToString(CultureInfo.InvariantCulture));
        AppendLine(sb, position.LastTransactionId);
        return sb.ToString();
    }

    private static string BuildRuleBody(RuleSnapshotBinding binding, RuleKeysetPosition position)
    {
        var sb = new StringBuilder(512);
        AppendLine(sb, FormatMarker);
        AppendLine(sb, KindRule);
        AppendLine(sb, RuleListOperationId);
        AppendLine(sb, binding.PageSize.ToString(CultureInfo.InvariantCulture));
        AppendLine(sb, binding.FilterFingerprint);
        AppendLine(sb, binding.HighWaterCreatedAt);
        AppendLine(sb, binding.HighWaterRuleVersionId);
        AppendLine(sb, binding.AuthorityFingerprint);
        AppendLine(sb, binding.CategoryLifecycleFingerprint);
        AppendLine(sb, FormatExpires(binding.ExpiresAtUtc));
        AppendLine(sb, position.LastCreatedAt);
        AppendLine(sb, position.LastRuleVersionId);
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string value)
    {
        sb.Append(value);
        sb.Append('\n');
    }

    private static string FormatExpires(DateTimeOffset expiresAtUtc) =>
        expiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TrySeal(string body, out string? encoded, out string? errorCode)
    {
        encoded = null;
        errorCode = null;

        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var sealedPayload = body + checksum + "\n";
        encoded = Base64UrlEncode(Encoding.UTF8.GetBytes(sealedPayload));

        if (Encoding.UTF8.GetByteCount(encoded) > MaxEncodedUtf8Bytes)
        {
            encoded = null;
            errorCode = ClassifyErrors.ResourceLimit;
            return false;
        }

        return true;
    }

    private static bool TryOpen(string? encoded, out string[] lines, out string? errorCode)
    {
        lines = [];
        errorCode = null;

        if (string.IsNullOrWhiteSpace(encoded)
            || Encoding.UTF8.GetByteCount(encoded) > MaxEncodedUtf8Bytes)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        byte[] raw;
        try
        {
            raw = Base64UrlDecode(encoded);
        }
        catch (FormatException)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        string text;
        try
        {
            // Strict UTF-8: invalid sequences throw DecoderFallbackException (no replacement).
            text = StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Reject CR/NUL anywhere in the sealed payload (format uses LF delimiters only).
        if (text.Contains('\r', StringComparison.Ordinal) || text.Contains('\0', StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Split preserving empty trailing semantics: require trailing newline on every line.
        if (!text.EndsWith('\n'))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        var split = text.Split('\n');
        // Split yields a final empty entry after the trailing newline.
        if (split.Length < 3)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        lines = split[..^1];
        var checksum = lines[^1];
        if (checksum.Length != 64 || !IsHex(checksum))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Checksum verification BEFORE field validation / semantic interpretation.
        var bodyBuilder = new StringBuilder();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            bodyBuilder.Append(lines[i]);
            bodyBuilder.Append('\n');
        }

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bodyBuilder.ToString())));
        if (!string.Equals(expected, checksum, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Drop checksum from field array for callers.
        lines = lines[..^1];
        return true;
    }

    private static bool TryValidateOutcomeBinding(OutcomeSnapshotBinding binding, out string? errorCode)
    {
        errorCode = null;
        if (binding is null || binding.PageSize is < MinPageSize or > MaxPageSize)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!IsSafeCursorField(binding.EvaluationId)
            || !IsSafeCursorField(binding.FilterFingerprint)
            || !IsSafeCursorField(binding.EvaluationFingerprint)
            || !IsSafeCursorField(binding.ResultFingerprint)
            || !IsSafeCursorField(binding.RuleSetFingerprint)
            || !IsSafeCursorField(binding.CategoryLifecycleFingerprint)
            || !IsSafeCursorField(binding.LedgerGeneration))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Expires is typed; ensure its serialized form is also field-safe.
        if (!IsSafeCursorField(FormatExpires(binding.ExpiresAtUtc)))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        return true;
    }

    private static bool TryValidateOutcomePosition(OutcomeKeysetPosition position, out string? errorCode)
    {
        errorCode = null;
        if (position is null
            || position.LastOrdinal < 0
            || !IsSafeCursorField(position.LastTransactionId))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        return true;
    }

    private static bool TryValidateRuleBinding(RuleSnapshotBinding binding, out string? errorCode)
    {
        errorCode = null;
        if (binding is null || binding.PageSize is < MinPageSize or > MaxPageSize)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!IsSafeCursorField(binding.FilterFingerprint)
            || !IsSafeCursorField(binding.HighWaterRuleVersionId)
            || !IsSafeCursorField(binding.AuthorityFingerprint)
            || !IsSafeCursorField(binding.CategoryLifecycleFingerprint)
            || !IsCanonicalUtcTimestamp(binding.HighWaterCreatedAt)
            || !IsSafeCursorField(FormatExpires(binding.ExpiresAtUtc)))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        return true;
    }

    private static bool TryValidateRulePosition(
        RuleSnapshotBinding binding,
        RuleKeysetPosition position,
        out string? errorCode)
    {
        errorCode = null;
        if (position is null
            || !IsCanonicalUtcTimestamp(position.LastCreatedAt)
            || !IsSafeCursorField(position.LastRuleVersionId))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        // Resume must not strictly exceed the first-page high-water (createdAt, ruleVersionId).
        if (CompareRuleKeyset(
                position.LastCreatedAt,
                position.LastRuleVersionId,
                binding.HighWaterCreatedAt,
                binding.HighWaterRuleVersionId) > 0)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        return true;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
            case 0: break;
        }

        // Reject standard base64 alphabet that is not url-safe when original used +/ ;
        // already remapped. Also reject padding in the original encoded form.
        if (encoded.Contains('+', StringComparison.Ordinal)
            || encoded.Contains('/', StringComparison.Ordinal)
            || encoded.Contains('=', StringComparison.Ordinal)
            || encoded.Contains('\n', StringComparison.Ordinal)
            || encoded.Contains(' ', StringComparison.Ordinal))
        {
            throw new FormatException("Malformed base64url.");
        }

        return Convert.FromBase64String(s);
    }

    private static bool IsHex(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c is >= '0' and <= '9')
                     || (c is >= 'a' and <= 'f');
            if (!ok) return false;
        }

        return true;
    }
}
