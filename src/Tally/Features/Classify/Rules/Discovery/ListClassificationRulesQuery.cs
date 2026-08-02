using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Discovery;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Rules.Discovery;

/// <summary>
/// classify.rule.list vertical slice
/// (FR-CLASSIFY-RULEBOOK-DISCOVERY / DD-CLASSIFY-PAGINATED-DISCOVERY / bd-2vbg).
/// High-water bound keyset over append-only rule_version; no OFFSET, private corpus, or owner prose.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ListClassificationRulesQuery
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly ClassifyStateStore stateStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly ClassificationRuleDiscoveryStore discoveryStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public ListClassificationRulesQuery(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        ClassificationRuleDiscoveryStore discoveryStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(discoveryStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.discoveryStore = discoveryStore;
        this.ruleSetStore = ruleSetStore;
        this.ledger = ledger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyRuleListResult>> HandleAsync(
        ClassifyRuleListRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyRuleListResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (!ClassifyOperatorErgonomicsContracts.TryValidate(input, out var validationError)
            || validationError is not null)
        {
            return CommandResult<ClassifyRuleListResult>.Failure(
                validationError ?? ClassifyErrors.InvalidInput);
        }

        var pageSize = input.PageSize;
        var logicalRuleId = string.IsNullOrWhiteSpace(input.LogicalRuleId) ? null : input.LogicalRuleId.Trim();
        var categoryId = string.IsNullOrWhiteSpace(input.CategoryId) ? null : input.CategoryId.Trim();
        var filterFp = ClassifyContractMapper.RuleListFilterFingerprint(
            logicalRuleId,
            input.Lifecycle,
            categoryId,
            input.ActiveMembership);

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddHours(24);

        string highWaterCreatedAt;
        string highWaterRuleVersionId;
        string authorityFingerprint;
        string categoryLifecycleFingerprint;
        int overallCount;
        IReadOnlySet<string> activeMembers;
        IReadOnlyList<string> frozenCategoryIds;
        ClassifyCursorCodec.RuleKeysetPosition? resume = null;

        // ── Resolve high-water (first page freezes; continuation reuses frozen HW) ──
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            var active = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            authorityFingerprint = ClassificationRuleDiscoveryStore.AuthorityFingerprint(
                active?.RuleSetVersionId,
                active?.ActivationEpoch ?? 0);
            activeMembers = active is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : await discoveryStore.GetActiveMemberIdsAsync(
                    connection, null, active.RuleSetVersionId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(input.Continuation))
            {
                if (!TryExtractRuleCursorFields(
                        input.Continuation!,
                        out var peekedHwCreated,
                        out var peekedHwRule,
                        out var peekedFilter,
                        out var peekedPageSize,
                        out _,
                        out _,
                        out var peekedExp,
                        out var extractError))
                {
                    return CommandResult<ClassifyRuleListResult>.Failure(
                        extractError ?? ClassifyErrors.CursorInvalid);
                }

                if (!string.Equals(peekedFilter, filterFp, StringComparison.Ordinal)
                    || peekedPageSize != pageSize)
                {
                    return CommandResult<ClassifyRuleListResult>.Failure(ClassifyErrors.CursorInvalid);
                }

                highWaterCreatedAt = peekedHwCreated!;
                highWaterRuleVersionId = peekedHwRule!;
                expiresAt = peekedExp;
            }
            else
            {
                var highWater = await discoveryStore.GetCatalogueHighWaterAsync(connection, null, cancellationToken);
                if (highWater is null)
                {
                    return CommandResult<ClassifyRuleListResult>.Success(
                        ClassifyContractMapper.ToRuleListResult(0, 0, Array.Empty<ClassifyRuleListItem>(), null));
                }

                highWaterCreatedAt = highWater.Value.CreatedAt;
                highWaterRuleVersionId = highWater.Value.RuleVersionId;
            }

            // Snapshot-bound overall total (excludes concurrent appends after freeze).
            overallCount = await discoveryStore.CountRuleVersionsBoundedAsync(
                connection, null, highWaterCreatedAt, highWaterRuleVersionId, cancellationToken);

            // Every category on a rule_version ≤ high-water can affect the frozen filtered traversal
            // (drafts and non-members included — not only active-set members).
            frozenCategoryIds = await discoveryStore.ListCategoryIdsBoundedAsync(
                connection, null, highWaterCreatedAt, highWaterRuleVersionId, cancellationToken);
        }

        // ── Ledger category display/lifecycle for fingerprint + items ────────
        var categoryInfo = new Dictionary<string, (string? Display, string Lifecycle)>(StringComparer.Ordinal);
        foreach (var catId in frozenCategoryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await ledger.GetBudgetCategoryAsync(
                catId,
                CategoryContractVersions.Current,
                actor,
                cancellationToken);
            if (!detail.IsSuccess || detail.Value is null)
            {
                // Fail closed: missing identity is treated as archived for fingerprint binding.
                categoryInfo[catId] = (null, "archived");
            }
            else
            {
                var life = detail.Value.Status == CategoryStatus.Active ? "active" : "archived";
                categoryInfo[catId] = (detail.Value.Name, life);
            }
        }

        var liveCategoryFingerprint = ClassificationRuleDiscoveryStore.CategoryLifecycleFingerprint(
            categoryInfo.Select(kv => (kv.Key, kv.Value.Lifecycle)));
        categoryLifecycleFingerprint = liveCategoryFingerprint;

        if (!string.IsNullOrWhiteSpace(input.Continuation))
        {
            if (!TryExtractRuleCursorFields(
                    input.Continuation!,
                    out var hwC,
                    out var hwR,
                    out _,
                    out _,
                    out var peekedAuth,
                    out var peekedCat,
                    out var peekedExp,
                    out var err))
            {
                return CommandResult<ClassifyRuleListResult>.Failure(err ?? ClassifyErrors.CursorInvalid);
            }

            if (!string.Equals(peekedAuth, authorityFingerprint, StringComparison.Ordinal)
                || !string.Equals(peekedCat, liveCategoryFingerprint, StringComparison.Ordinal))
            {
                return CommandResult<ClassifyRuleListResult>.Failure(ClassifyErrors.CursorStale);
            }

            highWaterCreatedAt = hwC!;
            highWaterRuleVersionId = hwR!;
            expiresAt = peekedExp;

            var binding = new ClassifyCursorCodec.RuleSnapshotBinding(
                FilterFingerprint: filterFp,
                PageSize: pageSize,
                HighWaterCreatedAt: highWaterCreatedAt,
                HighWaterRuleVersionId: highWaterRuleVersionId,
                AuthorityFingerprint: authorityFingerprint,
                CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
                ExpiresAtUtc: expiresAt);

            if (!ClassifyCursorCodec.TryDecodeRule(
                    input.Continuation,
                    binding,
                    now,
                    out resume,
                    out var cursorError))
            {
                return CommandResult<ClassifyRuleListResult>.Failure(
                    cursorError ?? ClassifyErrors.CursorInvalid);
            }
        }

        // ── Load bounded candidates ──────────────────────────────────────────
        IReadOnlyList<ClassifyRuleVersionRow> candidates;
        IReadOnlyDictionary<string, IReadOnlyList<RuleCondition>> conditionsByVersion;
        Dictionary<string, RuleLifecycleTimestamps> timestampsByVersion;
        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            // Lifecycle filtered in-process (active includes active_with_broad_apply).
            candidates = await discoveryStore.ListRuleVersionsBoundedAsync(
                connection,
                null,
                highWaterCreatedAt,
                highWaterRuleVersionId,
                logicalRuleId,
                lifecycleState: null,
                categoryId,
                cancellationToken);

            conditionsByVersion = await discoveryStore.ListConditionsForVersionsAsync(
                connection,
                null,
                candidates.Select(c => c.RuleVersionId).ToArray(),
                ruleStore,
                cancellationToken);

            timestampsByVersion = new Dictionary<string, RuleLifecycleTimestamps>(StringComparer.Ordinal);
            foreach (var row in candidates)
            {
                timestampsByVersion[row.RuleVersionId] =
                    await discoveryStore.GetLifecycleTimestampsAsync(
                        connection, null, row.RuleVersionId, cancellationToken);
            }

            // Ensure category display for all candidate categories.
            foreach (var row in candidates)
            {
                if (categoryInfo.ContainsKey(row.CategoryId))
                {
                    continue;
                }

                var detail = await ledger.GetBudgetCategoryAsync(
                    row.CategoryId,
                    CategoryContractVersions.Current,
                    actor,
                    cancellationToken);
                if (!detail.IsSuccess || detail.Value is null)
                {
                    categoryInfo[row.CategoryId] = (null, "archived");
                }
                else
                {
                    var life = detail.Value.Status == CategoryStatus.Active ? "active" : "archived";
                    categoryInfo[row.CategoryId] = (detail.Value.Name, life);
                }
            }
        }

        // AND filters: lifecycle + active membership
        var filtered = new List<ClassifyRuleVersionRow>(candidates.Count);
        foreach (var row in candidates)
        {
            var isMember = activeMembers.Contains(row.RuleVersionId);
            if (input.ActiveMembership is true && !isMember)
            {
                continue;
            }

            if (input.ActiveMembership is false && isMember)
            {
                continue;
            }

            if (input.Lifecycle is not null)
            {
                try
                {
                    // Membership-derived effective lifecycle (activation is append-only authority).
                    var effective = isMember
                        ? ClassifyRuleLifecycleFilter.Active
                        : ClassifyContractMapper.ToPublicLifecycle(row.LifecycleState);
                    if (effective != input.Lifecycle.Value)
                    {
                        continue;
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    return CommandResult<ClassifyRuleListResult>.Failure(ClassifyErrors.Integrity);
                }
            }

            filtered.Add(row);
        }

        filtered.Sort(static (a, b) =>
        {
            var cmp = string.CompareOrdinal(a.CreatedAt, b.CreatedAt);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.RuleVersionId, b.RuleVersionId);
        });

        IEnumerable<ClassifyRuleVersionRow> window = filtered;
        if (resume is not null)
        {
            window = filtered.Where(r =>
                string.CompareOrdinal(r.CreatedAt, resume.LastCreatedAt) > 0
                || (string.Equals(r.CreatedAt, resume.LastCreatedAt, StringComparison.Ordinal)
                    && string.CompareOrdinal(r.RuleVersionId, resume.LastRuleVersionId) > 0));
        }

        var pageMaterialized = window.ToArray();
        var pageRows = pageMaterialized.Take(pageSize).ToArray();
        var hasMore = pageMaterialized.Length > pageSize;

        var items = new List<ClassifyRuleListItem>(pageRows.Length);
        foreach (var row in pageRows)
        {
            conditionsByVersion.TryGetValue(row.RuleVersionId, out var conditions);
            conditions ??= Array.Empty<RuleCondition>();
            timestampsByVersion.TryGetValue(row.RuleVersionId, out var ts);
            ts ??= new RuleLifecycleTimestamps(null, null, null);
            categoryInfo.TryGetValue(row.CategoryId, out var cat);
            if (!ClassifyContractMapper.TryMapRuleListItem(
                    row,
                    conditions,
                    activeMembers.Contains(row.RuleVersionId),
                    cat.Display,
                    cat.Lifecycle ?? "archived",
                    ts,
                    out var item,
                    out var mapError))
            {
                return CommandResult<ClassifyRuleListResult>.Failure(
                    mapError ?? ClassifyErrors.Integrity);
            }

            items.Add(item);
        }

        string? continuation = null;
        if (hasMore && pageRows.Length > 0)
        {
            var last = pageRows[^1];
            var binding = new ClassifyCursorCodec.RuleSnapshotBinding(
                FilterFingerprint: filterFp,
                PageSize: pageSize,
                HighWaterCreatedAt: highWaterCreatedAt,
                HighWaterRuleVersionId: highWaterRuleVersionId,
                AuthorityFingerprint: authorityFingerprint,
                CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
                ExpiresAtUtc: expiresAt);

            if (!ClassifyCursorCodec.TryEncodeRule(
                    binding,
                    new ClassifyCursorCodec.RuleKeysetPosition(last.CreatedAt, last.RuleVersionId),
                    out continuation,
                    out var encodeError))
            {
                return CommandResult<ClassifyRuleListResult>.Failure(
                    encodeError ?? ClassifyErrors.CursorInvalid);
            }
        }

        return CommandResult<ClassifyRuleListResult>.Success(
            ClassifyContractMapper.ToRuleListResult(
                overallCount,
                filtered.Count,
                items,
                continuation));
    }

    /// <summary>
    /// Extract rule-cursor binding fields after checksum verification
    /// (CLASSIFY-CURSOR-V1 rule layout from ClassifyCursorCodec).
    /// </summary>
    private static bool TryExtractRuleCursorFields(
        string encoded,
        out string? highWaterCreatedAt,
        out string? highWaterRuleVersionId,
        out string? filterFingerprint,
        out int pageSize,
        out string? authorityFingerprint,
        out string? categoryLifecycleFingerprint,
        out DateTimeOffset expiresAt,
        out string? errorCode)
    {
        highWaterCreatedAt = null;
        highWaterRuleVersionId = null;
        filterFingerprint = null;
        pageSize = 0;
        authorityFingerprint = null;
        categoryLifecycleFingerprint = null;
        expiresAt = default;
        errorCode = null;

        byte[] raw;
        try
        {
            var s = encoded.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1:
                    errorCode = ClassifyErrors.CursorInvalid;
                    return false;
            }

            if (encoded.Contains('+', StringComparison.Ordinal)
                || encoded.Contains('/', StringComparison.Ordinal)
                || encoded.Contains('=', StringComparison.Ordinal))
            {
                errorCode = ClassifyErrors.CursorInvalid;
                return false;
            }

            raw = Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!text.EndsWith('\n') || text.Contains('\r') || text.Contains('\0'))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        var lines = text.Split('\n')[..^1];
        if (lines.Length < 2)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        var checksum = lines[^1];
        var bodyLines = lines[..^1];
        var body = string.Join('\n', bodyLines) + "\n";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (!string.Equals(expected, checksum, StringComparison.Ordinal) || checksum.Length != 64)
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (bodyLines.Length != 12
            || !string.Equals(bodyLines[0], "CLASSIFY-CURSOR-V1", StringComparison.Ordinal)
            || !string.Equals(bodyLines[1], "rule", StringComparison.Ordinal)
            || !string.Equals(bodyLines[2], ClassifyCursorCodec.RuleListOperationId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!int.TryParse(bodyLines[3], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                bodyLines[9],
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out expiresAt))
        {
            errorCode = ClassifyErrors.CursorInvalid;
            return false;
        }

        filterFingerprint = bodyLines[4];
        highWaterCreatedAt = bodyLines[5];
        highWaterRuleVersionId = bodyLines[6];
        authorityFingerprint = bodyLines[7];
        categoryLifecycleFingerprint = bodyLines[8];
        return true;
    }
}
