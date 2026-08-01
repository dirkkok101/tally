using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Apply;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Apply.Preview;

/// <summary>
/// classify.apply.preview vertical slice
/// (FR-CLASSIFY-APPLY-AUTHORIZATION / TASK-CLASSIFY-RULEBOOK-APPLY-PREVIEW).
/// Authorizes selected outcomes, one broad-authorized exact rule, or explicit corrections;
/// runs all-item Ledger purpose=apply_preflight; persists an expiry-bound CLASSIFY preview.
/// Never mutates Ledger. Never infers owner authority from evaluation alone.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PreviewClassificationApplyCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationEvaluationStore evaluationStore;
    private readonly ClassificationApplyPreviewStore previewStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public PreviewClassificationApplyCommand(
        ClassifyStateStore stateStore,
        ClassificationEvaluationStore evaluationStore,
        ClassificationApplyPreviewStore previewStore,
        RuleSetStore ruleSetStore,
        ClassificationRuleStore ruleStore,
        LedgerContractClient ledger,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(evaluationStore);
        ArgumentNullException.ThrowIfNull(previewStore);
        ArgumentNullException.ThrowIfNull(ruleSetStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.evaluationStore = evaluationStore;
        this.previewStore = previewStore;
        this.ruleSetStore = ruleSetStore;
        this.ruleStore = ruleStore;
        this.ledger = ledger;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyApplyPreviewResult>> HandleAsync(
        ClassifyApplyPreviewRequest input,
        SafeActor? actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (string.IsNullOrWhiteSpace(input.EvaluationId))
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.InvalidInput);
        }

        if (!ClassifyContractMapper.TryValidateApplySelection(input.Selection, out var selectionError))
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(
                selectionError ?? ClassifyErrors.SelectionInvalid);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var evaluationId = input.EvaluationId.Trim();

        var fingerprintElement = ClassifyContractMapper.ToApplyPreviewFingerprintElement(
            ClassifyOperationIds.ContractVersion,
            evaluationId,
            input.Selection);
        var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyOperationIds.ApplyPreview,
            ClassifyOperationIds.ContractVersion,
            actorKind,
            actorLabel,
            actorRunId,
            fingerprintElement);

        var probed = await TryProbeAsync(idempotencyKey, requestFingerprint, cancellationToken);
        if (probed is not null)
        {
            return probed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs));
        var ct = timeout.Token;

        try
        {
            // ── Load retained evaluation evidence (CLASSIFY only) ─────────────
            ClassifyEvaluationRunRow run;
            IReadOnlyList<ClassifyOutcomeRow> outcomes;
            Dictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>> evidenceByOutcome;
            HashSet<string> activeRuleVersionIds;
            HashSet<string> broadApplyRuleVersionIds;
            string? currentRuleSetVersionId;

            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                var loadedRun = await evaluationStore.GetRunAsync(connection, null, evaluationId, ct);
                if (loadedRun is null)
                {
                    return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.EvaluationNotFound);
                }

                if (!string.Equals(
                        loadedRun.LifecycleState,
                        ClassifyContractMapper.EvaluationLifecycleCompleted,
                        StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Lifecycle);
                }

                run = loadedRun;
                outcomes = await evaluationStore.ListOutcomesAsync(connection, null, evaluationId, ct);

                evidenceByOutcome = new Dictionary<string, IReadOnlyList<ClassifyMatchEvidenceRow>>(
                    StringComparer.Ordinal);
                foreach (var outcome in outcomes)
                {
                    ct.ThrowIfCancellationRequested();
                    evidenceByOutcome[outcome.OutcomeId] =
                        await evaluationStore.ListEvidenceForOutcomeAsync(
                            connection, null, outcome.OutcomeId, ct);
                }

                var active = await ruleSetStore.GetActiveRuleSetAsync(connection, null, ct);
                currentRuleSetVersionId = active?.RuleSetVersionId;
                activeRuleVersionIds = new HashSet<string>(StringComparer.Ordinal);
                broadApplyRuleVersionIds = new HashSet<string>(StringComparer.Ordinal);

                if (active is not null)
                {
                    var members = await ruleSetStore.ListMemberRuleVersionIdsAsync(
                        connection, null, active.RuleSetVersionId, ct);
                    foreach (var memberId in members)
                    {
                        activeRuleVersionIds.Add(memberId);
                        var events = await ruleSetStore.ListLifecycleEventsForSubjectAsync(
                            connection, null, memberId, ct);
                        if (ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(events))
                        {
                            broadApplyRuleVersionIds.Add(memberId);
                        }
                    }

                    // Rule-set subject events also record broad apply on activation.
                    var setEvents = await ruleSetStore.ListLifecycleEventsForSubjectAsync(
                        connection, null, active.RuleSetVersionId, ct);
                    if (ApplyAuthorizationPolicy.HasImmutableBroadApplyAuthority(setEvents))
                    {
                        foreach (var memberId in members)
                        {
                            broadApplyRuleVersionIds.Add(memberId);
                        }
                    }
                }
            }

            // Evaluation snapshot expiry blocks apply of retained outcomes.
            if (!TryParseUtc(run.SnapshotExpiresAt, out var retainedExpiresAt)
                || timeProvider.GetUtcNow() >= retainedExpiresAt)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Stale);
            }

            // ── Pure selection / authority ───────────────────────────────────
            var authorization = ApplyAuthorizationPolicy.Authorize(
                input.Selection,
                run,
                outcomes,
                evidenceByOutcome,
                broadApplyRuleVersionIds,
                activeRuleVersionIds);

            if (!authorization.IsAuthorized || authorization.ErrorCode is not null)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(
                    authorization.ErrorCode ?? ClassifyErrors.SelectionInvalid);
            }

            if (authorization.Candidates.Count == 0)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.SelectionInvalid);
            }

            if (authorization.Candidates.Count > ClassificationProjectionVersions.MaxApplyPreflightIds)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.ResourceLimit);
            }

            // ── Current public Ledger state (read-only) ──────────────────────
            var transactionIds = authorization.Candidates
                .Select(c => c.TransactionId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var preflight = await ledger.QueryClassificationProjectionAsync(
                ClassificationProjectionPurpose.ApplyPreflight,
                run.LedgerContractVersion,
                actor,
                ct,
                transactionIds: transactionIds);

            if (!preflight.IsSuccess || preflight.Value is null)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(
                    ClassifyContractMapper.MapLedgerCategoryListError(preflight.Error));
            }

            var page = preflight.Value;
            if (!string.Equals(page.ProjectionVersion, ClassificationProjectionVersions.ClassificationV1, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint)
                || string.IsNullOrWhiteSpace(page.SnapshotId)
                || string.IsNullOrWhiteSpace(page.ExpiresAt))
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.LedgerIncompatible);
            }

            if (!TryParseUtc(page.ExpiresAt, out var preflightExpiresAt)
                || timeProvider.GetUtcNow() >= preflightExpiresAt)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Stale);
            }

            // Evaluation-purpose page for ordered-items / category lifecycle fingerprint recheck.
            var evaluationProjection = await ledger.QueryClassificationProjectionAsync(
                ClassificationProjectionPurpose.Evaluation,
                run.LedgerContractVersion,
                actor,
                ct);
            if (!evaluationProjection.IsSuccess || evaluationProjection.Value is null)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(
                    ClassifyContractMapper.MapLedgerCategoryListError(evaluationProjection.Error));
            }

            var evalPage = evaluationProjection.Value;
            var categoryLifecycleFingerprint = !string.IsNullOrWhiteSpace(evalPage.CategoryIdentityLifecycleFingerprint)
                ? evalPage.CategoryIdentityLifecycleFingerprint
                : EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
                    (evalPage.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
                        .Select(c => (c.CategoryId, c.LifecycleState)));

            var orderedItemsFingerprint = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
                (evalPage.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>())
                    .OrderBy(i => i.Ordinal)
                    .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
                    .Select(i => (
                        i.Ordinal,
                        i.TransactionId,
                        ClassificationEvaluationInputLoader.ComputeItemLifecycleFingerprint(i))));

            var retainedFingerprint = ClassifyContractMapper.ToRetainedEvaluationFingerprint(run);
            var currentStoreGen = evalPage.StoreGenerationFingerprint ?? page.StoreGenerationFingerprint;
            var currentLedgerContract = evalPage.LedgerContractVersion ?? page.LedgerContractVersion;
            var currentProjection = evalPage.ProjectionVersion ?? page.ProjectionVersion;

            // Global evaluation dimensions for assignment previews (item lifecycle per candidate below).
            // Explicit corrections authorize against current preflight allocation/revisions and still
            // bind the retained evaluation fingerprint into the preview for apply.run revalidation;
            // they skip ordered_items/store-generation equality because the target is already categorized.
            if (authorization.Mode != ClassifyApplySelectionMode.ExplicitCorrections)
            {
                if (!string.Equals(retainedFingerprint.LedgerContractVersion, currentLedgerContract, StringComparison.Ordinal)
                    || !string.Equals(retainedFingerprint.ProjectionVersion, currentProjection, StringComparison.Ordinal)
                    || !string.Equals(retainedFingerprint.StoreGenerationFingerprint, currentStoreGen, StringComparison.Ordinal)
                    || !string.Equals(retainedFingerprint.CategoryLifecycleFingerprint, categoryLifecycleFingerprint, StringComparison.Ordinal)
                    || !string.Equals(retainedFingerprint.NormalizationVersion, NormalizationDescriptor.V1.Version, StringComparison.Ordinal)
                    || (currentRuleSetVersionId is not null
                        && !string.Equals(retainedFingerprint.RuleSetVersionId, currentRuleSetVersionId, StringComparison.Ordinal))
                    || !string.Equals(
                        retainedFingerprint.OrderedItemsFingerprint,
                        orderedItemsFingerprint,
                        StringComparison.Ordinal))
                {
                    return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Stale);
                }
            }
            else
            {
                // Corrections still fail closed on contract/projection/rule-set identity drift.
                if (!string.Equals(retainedFingerprint.LedgerContractVersion, currentLedgerContract, StringComparison.Ordinal)
                    || !string.Equals(retainedFingerprint.ProjectionVersion, currentProjection, StringComparison.Ordinal)
                    || !string.Equals(retainedFingerprint.NormalizationVersion, NormalizationDescriptor.V1.Version, StringComparison.Ordinal)
                    || (currentRuleSetVersionId is not null
                        && !string.Equals(retainedFingerprint.RuleSetVersionId, currentRuleSetVersionId, StringComparison.Ordinal)))
                {
                    return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Stale);
                }
            }

            var itemsByTx = (page.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>())
                .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
            var missing = new HashSet<string>(
                page.MissingTransactionIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);

            var activeCategoryIds = (page.ActiveCategories ?? evalPage.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>())
                .Where(c => string.Equals(c.LifecycleState, "active", StringComparison.Ordinal))
                .Select(c => c.CategoryId)
                .ToHashSet(StringComparer.Ordinal);
            if (activeCategoryIds.Count == 0
                && evalPage.ActiveCategories is { Count: > 0 })
            {
                activeCategoryIds = evalPage.ActiveCategories
                    .Where(c => string.Equals(c.LifecycleState, "active", StringComparison.Ordinal))
                    .Select(c => c.CategoryId)
                    .ToHashSet(StringComparer.Ordinal);
            }

            var finalCandidates = new List<ApplyAuthorizationPolicy.AuthorizedCandidate>();
            var staleExclusions = 0;
            foreach (var candidate in authorization.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                itemsByTx.TryGetValue(candidate.TransactionId, out var preflightItem);
                var isMissing = missing.Contains(candidate.TransactionId) || preflightItem is null;
                if (!ClassifyContractMapper.TryMatchPreflightItem(
                        candidate,
                        preflightItem,
                        isMissing,
                        activeCategoryIds,
                        out var matchError))
                {
                    // Stale / ineligible after preflight: exclude for exact-rule mode; fail closed for explicit selection.
                    if (authorization.Mode == ClassifyApplySelectionMode.ExactRule
                        && string.Equals(matchError, ClassifyErrors.Stale, StringComparison.Ordinal))
                    {
                        staleExclusions++;
                        continue;
                    }

                    return CommandResult<ClassifyApplyPreviewResult>.Failure(
                        matchError ?? ClassifyErrors.SelectionInvalid);
                }

                finalCandidates.Add(candidate);
            }

            if (finalCandidates.Count == 0)
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(
                    staleExclusions > 0 ? ClassifyErrors.Stale : ClassifyErrors.SelectionInvalid);
            }

            // All-item preflight requires every remaining selected ID present (no partial authorize).
            if (finalCandidates.Any(c => missing.Contains(c.TransactionId) || !itemsByTx.ContainsKey(c.TransactionId)))
            {
                return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.SelectionInvalid);
            }

            var selectionHash = ClassifyContractMapper.ComputeSelectionHash(input.Selection);
            var targetCategoryFingerprint = ClassifyContractMapper.ComputeTargetCategoryFingerprint(finalCandidates);
            var ruleAuthorityFingerprint = ClassifyContractMapper.ComputeRuleAuthorityFingerprint(
                authorization with { Candidates = finalCandidates });
            var evaluationFingerprint = retainedFingerprint.CanonicalHash;

            var now = timeProvider.GetUtcNow();
            var previewId = ClassifyContractMapper.NewRuleVersionId(now);
            var createdAtUtc = ClassifyContractMapper.FormatUtc(now);
            // Preview expires with the preflight snapshot bound (never outlives preflight evidence).
            var expiresAtUtc = page.ExpiresAt;

            var assignableCount = finalCandidates.Count(c =>
                string.Equals(c.Mode, ApplyAuthorizationPolicy.ModeAssign, StringComparison.Ordinal));
            var correctableCount = finalCandidates.Count(c =>
                string.Equals(c.Mode, ApplyAuthorizationPolicy.ModeCorrect, StringComparison.Ordinal));

            var exclusionCount = authorization.ExclusionCount + staleExclusions;
            // Prefer evaluation-run partition totals for no-suggestion/conflict disclosure on the preview.
            var noSuggestionCount = Math.Max(authorization.ExcludedNoSuggestionCount, run.NoSuggestionCount);
            var conflictCount = Math.Max(authorization.ExcludedConflictCount, run.ConflictCount);

            var orderedItems = new List<ClassifyApplyPreviewItemRow>(finalCandidates.Count);
            var ordinal = 0;
            foreach (var candidate in finalCandidates
                         .OrderBy(c => c.TransactionId, StringComparer.Ordinal)
                         .ThenBy(c => c.OutcomeId, StringComparer.Ordinal))
            {
                var item = itemsByTx[candidate.TransactionId];
                orderedItems.Add(ClassifyContractMapper.ToApplyPreviewItemRow(previewId, ordinal++, candidate, item));
            }

            var previewRow = ClassifyContractMapper.ToApplyPreviewRow(
                previewId,
                idempotencyKey,
                evaluationId,
                evaluationFingerprint,
                authorization.Mode,
                selectionHash,
                page.LedgerContractVersion,
                page.ProjectionVersion!,
                page.StoreGenerationFingerprint!,
                page.SnapshotId,
                page.ExpiresAt,
                categoryLifecycleFingerprint,
                targetCategoryFingerprint,
                ruleAuthorityFingerprint,
                expiresAtUtc,
                finalCandidates.Count,
                exclusionCount,
                noSuggestionCount,
                conflictCount,
                actorText,
                createdAtUtc);

            var publicResult = ClassifyContractMapper.ToApplyPreviewResult(
                previewId,
                evaluationId,
                expiresAtUtc,
                finalCandidates.Count,
                assignableCount,
                correctableCount,
                selectionHash);

            return await stateStore.ExecuteWriteAsync(
                async (connection, transaction, writeCt) =>
                {
                    var existing = await idempotencyStore.FindAsync(
                        connection, transaction, idempotencyKey, writeCt);
                    var lookup = idempotencyStore.Resolve(
                        existing,
                        ClassifyOperationIds.ApplyPreview,
                        ClassifyOperationIds.ContractVersion,
                        requestFingerprint);
                    switch (lookup.Disposition)
                    {
                        case ClassifyIdempotencyDisposition.Replay:
                            return ReplayOrIntegrity(lookup.Record!);
                        case ClassifyIdempotencyDisposition.Conflict:
                            return CommandResult<ClassifyApplyPreviewResult>.Failure(
                                ClassifyErrors.IdempotencyConflict);
                        case ClassifyIdempotencyDisposition.Miss:
                            break;
                        default:
                            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Unexpected);
                    }

                    // Idempotency terminal first (preview FK may reference the key).
                    await idempotencyStore.CommitAsync(
                        connection,
                        transaction,
                        new ClassifyOperationIdempotencyRow(
                            idempotencyKey,
                            ClassifyOperationIds.ApplyPreview,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint,
                            SerializeResult(publicResult),
                            createdAtUtc),
                        writeCt);

                    await previewStore.PersistAsync(
                        connection,
                        transaction,
                        previewRow,
                        orderedItems,
                        writeCt);

                    // Defensive: no Ledger mutation can have occurred from this path.
                    var stored = await previewStore.GetPreviewAsync(
                        connection, transaction, previewId, writeCt);
                    if (stored is null || stored.SelectedCount != finalCandidates.Count)
                    {
                        throw new InvalidOperationException(
                            $"{ClassifyErrors.Integrity}: preview persistence incomplete.");
                    }

                    return CommandResult<ClassifyApplyPreviewResult>.Success(publicResult);
                },
                ct);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Unexpected);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal)
            || ex.Message.Contains("preview", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private async Task<CommandResult<ClassifyApplyPreviewResult>?> TryProbeAsync(
        string idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await stateStore.OpenMigratedAsync(cancellationToken);
        await using var transaction = stateStore.BeginImmediate(connection);
        try
        {
            var existing = await idempotencyStore.FindAsync(
                connection, transaction, idempotencyKey, cancellationToken);
            var lookup = idempotencyStore.Resolve(
                existing,
                ClassifyOperationIds.ApplyPreview,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyApplyPreviewResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
            return result is null
                ? CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyApplyPreviewResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyApplyPreviewResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string SerializeResult(ClassifyApplyPreviewResult result) =>
        JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);

    private static bool TryParseUtc(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);
}
