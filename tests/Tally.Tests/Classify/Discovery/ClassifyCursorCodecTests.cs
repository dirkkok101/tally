using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Tally.Contracts.Classify.Operations;
using Tally.Domain.Classify.Discovery;
using Xunit;

namespace Tally.Tests.Classify.Discovery;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-CURSOR-POLICY / bd-29ch —
/// Deterministic cursor round-trip, tamper, expiry, mismatch, boundary, and disclosure proofs.
/// Synthetic values only; no Ledger/SQLite/live data.
/// </summary>
public sealed class ClassifyCursorCodecTests
{
    private static readonly DateTimeOffset Expires = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    // ── Filter fingerprints ──────────────────────────────────────────────────

    [Fact]
    public void Outcome_filter_fingerprint_is_deterministic_and_order_stable()
    {
        var a = ClassifyDiscoveryFilterFingerprint.ForOutcomeList(
            "eval-1",
            ClassifyOutcomeKind.Suggestion,
            "cat-a",
            "rule-v1",
            ClassifyOutcomeStaleFilter.Fresh,
            "tx-9");
        var b = ClassifyDiscoveryFilterFingerprint.ForOutcomeList(
            "eval-1",
            ClassifyOutcomeKind.Suggestion,
            "cat-a",
            "rule-v1",
            ClassifyOutcomeStaleFilter.Fresh,
            "tx-9");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Outcome_filter_fingerprint_changes_when_any_and_clause_changes()
    {
        var baseline = ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1");
        Assert.NotEqual(
            baseline,
            ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-2"));
        Assert.NotEqual(
            baseline,
            ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1", ClassifyOutcomeKind.Conflict));
        Assert.NotEqual(
            baseline,
            ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1", suggestedCategoryId: "c"));
        Assert.NotEqual(
            baseline,
            ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1", contributingRuleVersionId: "r"));
        Assert.NotEqual(
            baseline,
            ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1", staleState: ClassifyOutcomeStaleFilter.Stale));
        Assert.NotEqual(
            baseline,
            ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1", transactionId: "t"));
    }

    [Fact]
    public void Outcome_filter_null_is_distinct_from_empty_string()
    {
        var withNull = ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval", suggestedCategoryId: null);
        var withEmpty = ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval", suggestedCategoryId: "");
        Assert.NotEqual(withNull, withEmpty);
    }

    [Fact]
    public void Rule_filter_fingerprint_is_deterministic_and_sensitive()
    {
        var a = ClassifyDiscoveryFilterFingerprint.ForRuleList("logical", ClassifyRuleLifecycleFilter.Active, "cat", true);
        var b = ClassifyDiscoveryFilterFingerprint.ForRuleList("logical", ClassifyRuleLifecycleFilter.Active, "cat", true);
        Assert.Equal(a, b);
        Assert.NotEqual(
            a,
            ClassifyDiscoveryFilterFingerprint.ForRuleList("logical", ClassifyRuleLifecycleFilter.Draft, "cat", true));
        Assert.NotEqual(
            a,
            ClassifyDiscoveryFilterFingerprint.ForRuleList("logical", ClassifyRuleLifecycleFilter.Active, "cat", false));
        Assert.NotEqual(
            a,
            ClassifyDiscoveryFilterFingerprint.ForRuleList(null, null, null, null));
    }

    // ── Encode / decode round-trip ───────────────────────────────────────────

    [Fact]
    public void Outcome_round_trip_is_byte_stable()
    {
        var binding = SampleOutcomeBinding();
        var position = new ClassifyCursorCodec.OutcomeKeysetPosition(7, "tx-0007");
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(binding, position, out var c1, out var e1));
        Assert.Null(e1);
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(binding, position, out var c2, out _));
        Assert.Equal(c1, c2);
        Assert.True(ClassifyCursorCodec.IsWithinEncodedSizeLimit(c1!));
        Assert.True(ClassifyCursorCodec.TryDecodeOutcome(c1, binding, Now, out var decoded, out var e2));
        Assert.Null(e2);
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void Rule_round_trip_is_byte_stable()
    {
        var binding = SampleRuleBinding();
        var position = new ClassifyCursorCodec.RuleKeysetPosition("2026-01-01T00:00:00.0000000Z", "rv-42");
        Assert.True(ClassifyCursorCodec.TryEncodeRule(binding, position, out var c1, out _));
        Assert.True(ClassifyCursorCodec.TryEncodeRule(binding, position, out var c2, out _));
        Assert.Equal(c1, c2);
        Assert.True(ClassifyCursorCodec.TryDecodeRule(c1, binding, Now, out var decoded, out var e));
        Assert.Null(e);
        Assert.Equal(position, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void Outcome_page_size_bounds_encode_and_decode(int pageSize)
    {
        var binding = SampleOutcomeBinding() with { PageSize = pageSize };
        var position = new ClassifyCursorCodec.OutcomeKeysetPosition(0, "tx-0");
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(binding, position, out var encoded, out _));
        Assert.True(ClassifyCursorCodec.TryDecodeOutcome(encoded, binding, Now, out var decoded, out _));
        Assert.Equal(position, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(-1)]
    public void Outcome_page_size_one_under_and_over_rejected_on_encode(int pageSize)
    {
        var binding = SampleOutcomeBinding() with { PageSize = pageSize };
        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(0, "tx"),
            out var encoded,
            out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    // ── Keyset traversal completeness ────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void Outcome_keyset_traversal_has_no_duplicate_or_omitted_key(int pageSize)
    {
        // Synthetic ordered universe: 1003 keys (covers multi-page for both bounds).
        var keys = Enumerable.Range(0, 1003)
            .Select(i => (Ordinal: i, Tx: "tx-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(k => k.Ordinal)
            .ThenBy(k => k.Tx, StringComparer.Ordinal)
            .ToArray();

        var binding = SampleOutcomeBinding() with { PageSize = pageSize };
        ClassifyCursorCodec.OutcomeKeysetPosition? cursor = null;
        var seen = new HashSet<(int, string)>();
        var offset = 0;

        while (offset < keys.Length)
        {
            var take = Math.Min(pageSize, keys.Length - offset);
            var page = keys.AsSpan(offset, take).ToArray();
            foreach (var k in page)
            {
                Assert.True(seen.Add((k.Ordinal, k.Tx)), "duplicate key under pageSize " + pageSize);
            }

            offset += take;
            if (offset >= keys.Length)
            {
                break;
            }

            var last = page[^1];
            var position = new ClassifyCursorCodec.OutcomeKeysetPosition(last.Ordinal, last.Tx);
            Assert.True(ClassifyCursorCodec.TryEncodeOutcome(binding, position, out var encoded, out _));
            Assert.True(ClassifyCursorCodec.TryDecodeOutcome(encoded, binding, Now, out cursor, out _));
            Assert.NotNull(cursor);

            // Next page starts strictly after keyset position.
            var next = keys.SkipWhile(k =>
                    k.Ordinal < cursor!.LastOrdinal
                    || (k.Ordinal == cursor.LastOrdinal
                        && string.CompareOrdinal(k.Tx, cursor.LastTransactionId) <= 0))
                .ToArray();
            Assert.Equal(keys.Length - offset, next.Length);
            Assert.Equal(keys[offset], next[0]);
        }

        Assert.Equal(keys.Length, seen.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void Rule_keyset_traversal_has_no_duplicate_or_omitted_key(int pageSize)
    {
        var keys = Enumerable.Range(0, 1003)
            .Select(i => (
                Created: "2026-01-01T00:00:00.0000000Z",
                Rule: "rv-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(k => k.Created, StringComparer.Ordinal)
            .ThenBy(k => k.Rule, StringComparer.Ordinal)
            .ToArray();

        var binding = SampleRuleBinding() with { PageSize = pageSize };
        ClassifyCursorCodec.RuleKeysetPosition? cursor = null;
        var seen = new HashSet<(string, string)>();
        var offset = 0;

        while (offset < keys.Length)
        {
            var take = Math.Min(pageSize, keys.Length - offset);
            var page = keys.AsSpan(offset, take).ToArray();
            foreach (var k in page)
            {
                Assert.True(seen.Add((k.Created, k.Rule)));
            }

            offset += take;
            if (offset >= keys.Length)
            {
                break;
            }

            var last = page[^1];
            var position = new ClassifyCursorCodec.RuleKeysetPosition(last.Created, last.Rule);
            Assert.True(ClassifyCursorCodec.TryEncodeRule(binding, position, out var encoded, out _));
            Assert.True(ClassifyCursorCodec.TryDecodeRule(encoded, binding, Now, out cursor, out _));
            Assert.NotNull(cursor);

            var next = keys.SkipWhile(k =>
                    string.CompareOrdinal(k.Created, cursor!.LastCreatedAt) < 0
                    || (string.Equals(k.Created, cursor.LastCreatedAt, StringComparison.Ordinal)
                        && string.CompareOrdinal(k.Rule, cursor.LastRuleVersionId) <= 0))
                .ToArray();
            Assert.Equal(keys.Length - offset, next.Length);
            Assert.Equal(keys[offset], next[0]);
        }

        Assert.Equal(keys.Length, seen.Count);
    }

    // ── Tamper / mismatch / expiry ───────────────────────────────────────────

    [Fact]
    public void Malformed_base64url_is_rejected_with_null_position()
    {
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            "%%%not-base64%%%",
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Standard_base64_alphabet_rejected()
    {
        // Produce a valid cursor then force +/ into the string (invalid base64url form).
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            SampleOutcomeBinding(),
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        // If the encoded form has no +/, inject an illegal character instead.
        var tampered = encoded! + "+";
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            tampered,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Checksum_mismatch_is_rejected()
    {
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            SampleOutcomeBinding(),
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        var raw = Base64UrlDecode(encoded!);
        var text = Encoding.UTF8.GetString(raw);
        // Flip last hex nibble of checksum line (second-to-last char before final newline).
        var chars = text.ToCharArray();
        var idx = text.Length - 3;
        chars[idx] = chars[idx] == 'a' ? 'b' : 'a';
        var tampered = Base64UrlEncode(Encoding.UTF8.GetBytes(new string(chars)));
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            tampered,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Unknown_version_marker_is_rejected()
    {
        var body = "CLASSIFY-CURSOR-V99\noutcome\nclassify.outcome.list\n10\n"
                   + Fp() + "\neval\n" + Fp() + "\n" + Fp() + "\n" + Fp() + "\n" + Fp() + "\n" + Fp() + "\n"
                   + Expires.ToString("O") + "\n1\ntx\n";
        var sealedPayload = Seal(body);
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            sealedPayload,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Cross_operation_cursor_is_rejected()
    {
        Assert.True(ClassifyCursorCodec.TryEncodeRule(
            SampleRuleBinding(),
            new ClassifyCursorCodec.RuleKeysetPosition("2026-01-01T00:00:00.0000000Z", "rv"),
            out var ruleCursor,
            out _));
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            ruleCursor,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Filter_mismatch_is_rejected()
    {
        var binding = SampleOutcomeBinding();
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        var other = binding with { FilterFingerprint = Fp("other-filter") };
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(encoded, other, Now, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Page_size_mismatch_is_rejected()
    {
        var binding = SampleOutcomeBinding() with { PageSize = 10 };
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        var other = binding with { PageSize = 11 };
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(encoded, other, Now, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Evaluation_id_mismatch_is_rejected()
    {
        var binding = SampleOutcomeBinding();
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        var other = binding with { EvaluationId = "eval-other" };
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(encoded, other, Now, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Ledger_generation_drift_is_stale()
    {
        var binding = SampleOutcomeBinding();
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        var other = binding with { LedgerGeneration = Fp("drifted-generation") };
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(encoded, other, Now, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorStale, error);
    }

    [Fact]
    public void Result_fingerprint_drift_is_stale()
    {
        var binding = SampleOutcomeBinding();
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        var other = binding with { ResultFingerprint = Fp("drifted-result") };
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(encoded, other, Now, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorStale, error);
    }

    [Fact]
    public void Expired_cursor_is_stale_with_null_position()
    {
        var binding = SampleOutcomeBinding() with { ExpiresAtUtc = Now };
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            binding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var encoded,
            out _));
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            encoded,
            binding,
            nowUtc: Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorStale, error);
    }

    [Fact]
    public void Rule_high_water_mismatch_is_stale()
    {
        var binding = SampleRuleBinding();
        Assert.True(ClassifyCursorCodec.TryEncodeRule(
            binding,
            new ClassifyCursorCodec.RuleKeysetPosition(binding.HighWaterCreatedAt, "rv"),
            out var encoded,
            out _));
        var other = binding with { HighWaterRuleVersionId = "rv-new" };
        Assert.False(ClassifyCursorCodec.TryDecodeRule(encoded, other, Now, out var position, out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorStale, error);
    }

    [Fact]
    public void Impossible_negative_ordinal_rejected_on_encode()
    {
        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            SampleOutcomeBinding(),
            new ClassifyCursorCodec.OutcomeKeysetPosition(-1, "tx"),
            out var encoded,
            out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Impossible_blank_transaction_id_rejected_on_encode()
    {
        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            SampleOutcomeBinding(),
            new ClassifyCursorCodec.OutcomeKeysetPosition(0, "  "),
            out var encoded,
            out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Impossible_blank_rule_position_rejected_on_encode()
    {
        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            SampleRuleBinding(),
            new ClassifyCursorCodec.RuleKeysetPosition("", "rv"),
            out var encoded,
            out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Null_or_empty_encoded_cursor_rejected()
    {
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            null,
            SampleOutcomeBinding(),
            Now,
            out var p1,
            out var e1));
        Assert.Null(p1);
        Assert.Equal(ClassifyErrors.CursorInvalid, e1);

        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            "",
            SampleOutcomeBinding(),
            Now,
            out var p2,
            out var e2));
        Assert.Null(p2);
        Assert.Equal(ClassifyErrors.CursorInvalid, e2);
    }

    [Fact]
    public void Extra_field_line_is_rejected_as_unknown_structure()
    {
        var body = BuildValidOutcomeBody() + "extra\n";
        var sealedPayload = Seal(body);
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            sealedPayload,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Oversize_encoded_cursor_rejected_on_decode()
    {
        var oversize = new string('A', ClassifyCursorCodec.MaxEncodedUtf8Bytes + 1);
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            oversize,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Encoded_cursor_stays_within_4096_utf8_bytes_for_normal_payloads()
    {
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            SampleOutcomeBinding(),
            new ClassifyCursorCodec.OutcomeKeysetPosition(12345, "tx-" + new string('x', 64)),
            out var encoded,
            out _));
        Assert.True(ClassifyCursorCodec.IsWithinEncodedSizeLimit(encoded!));
        Assert.True(Encoding.UTF8.GetByteCount(encoded!) <= ClassifyCursorCodec.MaxEncodedUtf8Bytes);
    }

    // ── Disclosure / privacy shape ───────────────────────────────────────────

    [Fact]
    public void Encoded_outcome_cursor_contains_no_sensitive_or_authority_tokens()
    {
        Assert.True(ClassifyCursorCodec.TryEncodeOutcome(
            SampleOutcomeBinding(),
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx-1"),
            out var encoded,
            out _));
        var raw = Encoding.UTF8.GetString(Base64UrlDecode(encoded!));
        Assert.DoesNotContain("description", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalized", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corpus", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authority", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutation", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apply", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proposal", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", raw, StringComparison.Ordinal);
        // Operation id is intentional binding, not authority.
        Assert.Contains(ClassifyCursorCodec.OutcomeListOperationId, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_types_expose_no_offset_authority_or_secret_members()
    {
        foreach (var type in new[]
                 {
                     typeof(ClassifyCursorCodec.OutcomeSnapshotBinding),
                     typeof(ClassifyCursorCodec.OutcomeKeysetPosition),
                     typeof(ClassifyCursorCodec.RuleSnapshotBinding),
                     typeof(ClassifyCursorCodec.RuleKeysetPosition)
                 })
        {
            var names = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("Offset", names);
            Assert.DoesNotContain("Description", names);
            Assert.DoesNotContain("Amount", names);
            Assert.DoesNotContain("Secret", names);
            Assert.DoesNotContain("AuthorityFlag", names);
            Assert.DoesNotContain("MutationToken", names);
            Assert.DoesNotContain("RuleProse", names);
            Assert.DoesNotContain("Path", names);
        }
    }

    [Fact]
    public void Failed_decode_never_returns_position_stand_in()
    {
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            "not-a-cursor",
            SampleOutcomeBinding(),
            Now,
            out var position,
            out _));
        Assert.Null(position);
    }

    [Fact]
    public void Operation_ids_are_stable_discovery_names()
    {
        Assert.Equal("classify.outcome.list", ClassifyCursorCodec.OutcomeListOperationId);
        Assert.Equal("classify.rule.list", ClassifyCursorCodec.RuleListOperationId);
        Assert.Equal(1, ClassifyCursorCodec.CursorVersion);
        Assert.Equal(4096, ClassifyCursorCodec.MaxEncodedUtf8Bytes);
        Assert.Equal(1, ClassifyCursorCodec.MinPageSize);
        Assert.Equal(500, ClassifyCursorCodec.MaxPageSize);
    }

    [Fact]
    public void Wire_names_for_filters_match_closed_contract_vocabulary()
    {
        Assert.Equal("suggestion", ClassifyDiscoveryFilterFingerprint.OutcomeKindWire(ClassifyOutcomeKind.Suggestion));
        Assert.Equal("no_suggestion", ClassifyDiscoveryFilterFingerprint.OutcomeKindWire(ClassifyOutcomeKind.NoSuggestion));
        Assert.Equal("any", ClassifyDiscoveryFilterFingerprint.StaleFilterWire(ClassifyOutcomeStaleFilter.Any));
        Assert.Equal("active", ClassifyDiscoveryFilterFingerprint.LifecycleWire(ClassifyRuleLifecycleFilter.Active));
    }

    // ── Field safety / control characters ────────────────────────────────────

    [Theory]
    [InlineData("eval\nid")]
    [InlineData("eval\rid")]
    [InlineData("eval\0id")]
    [InlineData("eval\tid")]
    public void Outcome_binding_rejects_control_characters_in_every_serialized_field(string bad)
    {
        var baseBinding = SampleOutcomeBinding();
        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            baseBinding with { EvaluationId = bad },
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var e1,
            out var err1));
        Assert.Null(e1);
        Assert.Equal(ClassifyErrors.CursorInvalid, err1);

        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            baseBinding with { FilterFingerprint = bad },
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var e2,
            out var err2));
        Assert.Null(e2);
        Assert.Equal(ClassifyErrors.CursorInvalid, err2);

        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            baseBinding with { LedgerGeneration = bad },
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, "tx"),
            out var e3,
            out var err3));
        Assert.Null(e3);
        Assert.Equal(ClassifyErrors.CursorInvalid, err3);

        Assert.False(ClassifyCursorCodec.TryEncodeOutcome(
            baseBinding,
            new ClassifyCursorCodec.OutcomeKeysetPosition(1, bad),
            out var e4,
            out var err4));
        Assert.Null(e4);
        Assert.Equal(ClassifyErrors.CursorInvalid, err4);
    }

    [Theory]
    [InlineData("fp\n")]
    [InlineData("fp\r")]
    [InlineData("fp\0")]
    [InlineData("rv\trule")]
    public void Rule_binding_rejects_control_characters_in_serialized_fields(string bad)
    {
        var baseBinding = SampleRuleBinding();
        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            baseBinding with { FilterFingerprint = bad },
            new ClassifyCursorCodec.RuleKeysetPosition(baseBinding.HighWaterCreatedAt, "rv-ok"),
            out var e1,
            out var err1));
        Assert.Null(e1);
        Assert.Equal(ClassifyErrors.CursorInvalid, err1);

        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            baseBinding with { HighWaterRuleVersionId = bad },
            new ClassifyCursorCodec.RuleKeysetPosition(baseBinding.HighWaterCreatedAt, "rv-ok"),
            out var e2,
            out var err2));
        Assert.Null(e2);
        Assert.Equal(ClassifyErrors.CursorInvalid, err2);

        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            baseBinding with { AuthorityFingerprint = bad },
            new ClassifyCursorCodec.RuleKeysetPosition(baseBinding.HighWaterCreatedAt, "rv-ok"),
            out var e3,
            out var err3));
        Assert.Null(e3);
        Assert.Equal(ClassifyErrors.CursorInvalid, err3);

        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            baseBinding,
            new ClassifyCursorCodec.RuleKeysetPosition(baseBinding.HighWaterCreatedAt, bad),
            out var e4,
            out var err4));
        Assert.Null(e4);
        Assert.Equal(ClassifyErrors.CursorInvalid, err4);
    }

    [Fact]
    public void Safe_cursor_field_rejects_cr_lf_nul_and_other_controls()
    {
        Assert.True(ClassifyCursorCodec.IsSafeCursorField("plain-id"));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField("a\nb"));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField("a\rb"));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField("a\0b"));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField("a\tb"));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField("a\u007fb"));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField("   "));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField(""));
        Assert.False(ClassifyCursorCodec.IsSafeCursorField(null));
    }

    // ── Rule high-water / canonical timestamps ───────────────────────────────

    [Fact]
    public void Canonical_utc_timestamp_accepts_classify_format_only()
    {
        Assert.True(ClassifyCursorCodec.IsCanonicalUtcTimestamp("2026-01-01T00:00:00.0000000Z"));
        Assert.False(ClassifyCursorCodec.IsCanonicalUtcTimestamp("2026-01-01T00:00:00Z")); // missing fractional
        Assert.False(ClassifyCursorCodec.IsCanonicalUtcTimestamp("2026-01-01T00:00:00.0000000+00:00"));
        Assert.False(ClassifyCursorCodec.IsCanonicalUtcTimestamp("2026-01-01 00:00:00.0000000Z"));
        Assert.False(ClassifyCursorCodec.IsCanonicalUtcTimestamp("not-a-timestamp"));
        Assert.False(ClassifyCursorCodec.IsCanonicalUtcTimestamp("2026-01-01T00:00:00.0000000Z\n"));
    }

    [Fact]
    public void Rule_encode_rejects_noncanonical_high_water_created_at()
    {
        var binding = SampleRuleBinding() with { HighWaterCreatedAt = "2026-01-01T00:00:00Z" };
        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            binding,
            new ClassifyCursorCodec.RuleKeysetPosition("2026-01-01T00:00:00.0000000Z", "rv-a"),
            out var encoded,
            out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Rule_encode_rejects_noncanonical_resume_created_at()
    {
        Assert.False(ClassifyCursorCodec.TryEncodeRule(
            SampleRuleBinding(),
            new ClassifyCursorCodec.RuleKeysetPosition("2026-01-01T00:00:00Z", "rv-a"),
            out var encoded,
            out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Rule_resume_equal_to_high_water_is_accepted()
    {
        var binding = SampleRuleBinding();
        var position = new ClassifyCursorCodec.RuleKeysetPosition(
            binding.HighWaterCreatedAt,
            binding.HighWaterRuleVersionId);
        Assert.True(ClassifyCursorCodec.TryEncodeRule(binding, position, out var encoded, out var error));
        Assert.Null(error);
        Assert.True(ClassifyCursorCodec.TryDecodeRule(encoded, binding, Now, out var decoded, out _));
        Assert.Equal(position, decoded);
        Assert.Equal(0, ClassifyCursorCodec.CompareRuleKeyset(
            position.LastCreatedAt,
            position.LastRuleVersionId,
            binding.HighWaterCreatedAt,
            binding.HighWaterRuleVersionId));
    }

    [Fact]
    public void Rule_resume_strictly_before_high_water_is_accepted()
    {
        // Same createdAt, ruleVersionId ordinally less than high-water rule id.
        var binding = SampleRuleBinding() with
        {
            HighWaterCreatedAt = "2026-06-01T12:00:00.0000000Z",
            HighWaterRuleVersionId = "rv-b"
        };
        var position = new ClassifyCursorCodec.RuleKeysetPosition(
            "2026-06-01T12:00:00.0000000Z",
            "rv-a");
        Assert.True(string.CompareOrdinal("rv-a", "rv-b") < 0);
        Assert.True(ClassifyCursorCodec.TryEncodeRule(binding, position, out var encoded, out _));
        Assert.True(ClassifyCursorCodec.TryDecodeRule(encoded, binding, Now, out var decoded, out _));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void Rule_resume_beyond_high_water_by_created_at_is_rejected()
    {
        var binding = SampleRuleBinding() with
        {
            HighWaterCreatedAt = "2026-06-01T12:00:00.0000000Z",
            HighWaterRuleVersionId = "rv-z"
        };
        var position = new ClassifyCursorCodec.RuleKeysetPosition(
            "2026-06-01T12:00:00.0000001Z",
            "rv-a");
        Assert.True(ClassifyCursorCodec.CompareRuleKeyset(
            position.LastCreatedAt,
            position.LastRuleVersionId,
            binding.HighWaterCreatedAt,
            binding.HighWaterRuleVersionId) > 0);
        Assert.False(ClassifyCursorCodec.TryEncodeRule(binding, position, out var encoded, out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    [Fact]
    public void Rule_resume_beyond_high_water_by_rule_version_id_is_rejected()
    {
        var binding = SampleRuleBinding() with
        {
            HighWaterCreatedAt = "2026-06-01T12:00:00.0000000Z",
            HighWaterRuleVersionId = "rv-a"
        };
        var position = new ClassifyCursorCodec.RuleKeysetPosition(
            "2026-06-01T12:00:00.0000000Z",
            "rv-b");
        Assert.True(string.CompareOrdinal("rv-b", "rv-a") > 0);
        Assert.False(ClassifyCursorCodec.TryEncodeRule(binding, position, out var encoded, out var error));
        Assert.Null(encoded);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);
    }

    // ── Strict UTF-8 ─────────────────────────────────────────────────────────

    [Fact]
    public void Malformed_utf8_payload_is_rejected_with_null_position()
    {
        // Valid-looking base64url of bytes that are not valid UTF-8 (orphan continuation 0x80).
        var invalidUtf8 = new byte[] { 0x80, 0x61, 0x0A };
        var encoded = Convert.ToBase64String(invalidUtf8).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(ClassifyCursorCodec.TryDecodeOutcome(
            encoded,
            SampleOutcomeBinding(),
            Now,
            out var position,
            out var error));
        Assert.Null(position);
        Assert.Equal(ClassifyErrors.CursorInvalid, error);

        Assert.False(ClassifyCursorCodec.TryDecodeRule(
            encoded,
            SampleRuleBinding(),
            Now,
            out var rulePos,
            out var ruleErr));
        Assert.Null(rulePos);
        Assert.Equal(ClassifyErrors.CursorInvalid, ruleErr);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ClassifyCursorCodec.OutcomeSnapshotBinding SampleOutcomeBinding() =>
        new(
            EvaluationId: "eval-1",
            FilterFingerprint: ClassifyDiscoveryFilterFingerprint.ForOutcomeList("eval-1"),
            PageSize: 10,
            EvaluationFingerprint: Fp("eval-fp"),
            ResultFingerprint: Fp("result-fp"),
            RuleSetFingerprint: Fp("ruleset-fp"),
            CategoryLifecycleFingerprint: Fp("cat-fp"),
            LedgerGeneration: Fp("ledger-gen"),
            ExpiresAtUtc: Expires);

    private static ClassifyCursorCodec.RuleSnapshotBinding SampleRuleBinding() =>
        new(
            FilterFingerprint: ClassifyDiscoveryFilterFingerprint.ForRuleList(),
            PageSize: 10,
            // Canonical CLASSIFY UTC; high-water rule id is ordinally after synthetic rv-###### keys.
            HighWaterCreatedAt: "2026-01-01T00:00:00.0000000Z",
            HighWaterRuleVersionId: "rv-hw",
            AuthorityFingerprint: Fp("authority-fp"),
            CategoryLifecycleFingerprint: Fp("cat-fp"),
            ExpiresAtUtc: Expires);

    private static string Fp(string? seed = null) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? "seed")));

    private static string BuildValidOutcomeBody()
    {
        var b = SampleOutcomeBinding();
        return "CLASSIFY-CURSOR-V1\n"
               + "outcome\n"
               + "classify.outcome.list\n"
               + "10\n"
               + b.FilterFingerprint + "\n"
               + b.EvaluationId + "\n"
               + b.EvaluationFingerprint + "\n"
               + b.ResultFingerprint + "\n"
               + b.RuleSetFingerprint + "\n"
               + b.CategoryLifecycleFingerprint + "\n"
               + b.LedgerGeneration + "\n"
               + Expires.ToString("O") + "\n"
               + "1\n"
               + "tx\n";
    }

    private static string Seal(string body)
    {
        var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return Base64UrlEncode(Encoding.UTF8.GetBytes(body + checksum + "\n"));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
