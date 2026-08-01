using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Evidence;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Rules.Validate;

/// <summary>
/// classify.rule.validate vertical slice (FR-CLASSIFY-RULE-VALIDATION / TASK-CLASSIFY-RULEBOOK-RULE-VALIDATION).
/// Streams private corpus through the production ClassificationEngine, persists aggregate-only evidence,
/// and never activates rules, grants broad authority, or mutates Ledger / active_rule_set.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ValidateClassificationRuleCommand
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassifyOperationIdempotencyStore idempotencyStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly ClassificationValidationStore validationStore;
    private readonly OwnerRulebookGateReceiptStore receiptStore;
    private readonly PrivateCorpusReader corpusReader;
    private readonly LedgerContractClient ledger;
    private readonly TimeProvider timeProvider;

    public ValidateClassificationRuleCommand(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        ClassificationValidationStore validationStore,
        PrivateCorpusReader corpusReader,
        LedgerContractClient ledger,
        ClassifyOperationIdempotencyStore? idempotencyStore = null,
        TimeProvider? timeProvider = null,
        OwnerRulebookGateReceiptStore? receiptStore = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(validationStore);
        ArgumentNullException.ThrowIfNull(corpusReader);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.validationStore = validationStore;
        this.receiptStore = receiptStore ?? new OwnerRulebookGateReceiptStore();
        this.corpusReader = corpusReader;
        this.ledger = ledger;
        this.idempotencyStore = idempotencyStore ?? new ClassifyOperationIdempotencyStore();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommandResult<ClassifyRuleValidateResult>> HandleAsync(
        ClassifyRuleValidateRequest input,
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
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.IdempotencyRequired);
        }

        if (!ClassifyContractMapper.IsSupportedContractVersion(input.ContractVersion))
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        if (input.CandidateIds is null || input.CandidateIds.Count == 0)
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.InvalidInput);
        }

        if (string.IsNullOrWhiteSpace(input.CorpusSource))
        {
            // Missing corpus fails closed for new authority (path never returned).
            return CommandResult<ClassifyRuleValidateResult>.Failure(PrivateCorpusErrors.PathRequired);
        }

        if (input.CandidateIds.Count > ClassifyOperationModule.V1Limits.MaxRuleCount)
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.ResourceLimit);
        }

        if (!TryParseFinalization(input, out var finalization, out var finalizationError))
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(finalizationError!);
        }

        var actorKind = actor.Kind.Trim();
        var actorLabel = actor.Label.Trim();
        var actorRunId = string.IsNullOrWhiteSpace(actor.RunId) ? null : actor.RunId.Trim();
        var actorText = ClassifyContractMapper.FormatActor(actorKind, actorLabel, actorRunId);
        var candidateIds = input.CandidateIds
            .Select(id => id?.Trim() ?? string.Empty)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (candidateIds.Length == 0 || candidateIds.Length != input.CandidateIds.Count)
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.InvalidInput);
        }

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked.Token;

        try
        {
            long activeBefore;
            await using (var probeConn = await stateStore.OpenMigratedAsync(ct))
            {
                activeBefore = await validationStore.CountActiveRuleSetAsync(probeConn, null, ct);
            }

            var loaded = new List<(ClassifyRuleVersionRow Version, IReadOnlyList<RuleCondition> Conditions)>(
                candidateIds.Length);
            await using (var connection = await stateStore.OpenMigratedAsync(ct))
            {
                foreach (var candidateId in candidateIds)
                {
                    ct.ThrowIfCancellationRequested();
                    var version = await ruleStore.GetRuleVersionAsync(connection, null, candidateId, ct);
                    if (version is null)
                    {
                        return CommandResult<ClassifyRuleValidateResult>.Failure(
                            ClassifyErrors.RuleVersionNotFound);
                    }

                    if (!string.Equals(
                            version.NormalizationVersion,
                            NormalizationDescriptor.V1.Version,
                            StringComparison.Ordinal))
                    {
                        return CommandResult<ClassifyRuleValidateResult>.Failure(
                            ClassifyErrors.UnsupportedVersion);
                    }

                    var conditions = await ruleStore.ListConditionsAsync(connection, null, candidateId, ct);
                    if (conditions.Count == 0)
                    {
                        return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.InvalidInput);
                    }

                    loaded.Add((version, conditions));
                }
            }

            var listed = await ledger.ListClassificationCategoriesAsync(
                CategoryContractVersions.Current,
                actor,
                ct,
                status: CategoryStatus.Active);
            if (!listed.IsSuccess || listed.Value is null)
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(
                    ClassifyContractMapper.MapLedgerCategoryListError(listed.Error));
            }

            var activeCategories = listed.Value.Items
                .Where(i => i.Status == CategoryStatus.Active)
                .ToArray();
            var activeCategoryIds = activeCategories
                .Select(i => i.CategoryId)
                .ToHashSet(StringComparer.Ordinal);
            var categoryLifecycleFingerprint = EvaluationFingerprint.ComputeCategoryLifecycleFingerprint(
                activeCategories.Select(i => (i.CategoryId, "active")));

            var corpus = await corpusReader.ReadAsync(input.CorpusSource, ct);
            if (!corpus.IsSuccess || corpus.Fingerprint is null || corpus.Rows is null)
            {
                // Unavailable / malformed / over-limit corpus blocks activation authority.
                // Existing active evaluation remains available (active_rule_set never touched).
                return CommandResult<ClassifyRuleValidateResult>.Failure(MapCorpusError(corpus.ErrorCode));
            }

            if (corpus.RowCount > PrivateCorpusLimits.MaxRowCount)
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.ResourceLimit);
            }

            // Complete frozen public classification_v1 projection (evaluation purpose).
            // Every private row must bind exactly once to a projection member with matching fields.
            var projection = await ledger.QueryClassificationProjectionAsync(
                ClassificationProjectionPurpose.Evaluation,
                CategoryContractVersions.Current,
                actor,
                ct);
            if (!projection.IsSuccess || projection.Value is null)
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(
                    ClassifyContractMapper.MapLedgerCategoryListError(projection.Error));
            }

            if (!string.Equals(
                    projection.Value.ProjectionVersion,
                    ClassificationProjectionVersions.ClassificationV1,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(projection.Value.SnapshotId)
                || string.IsNullOrWhiteSpace(projection.Value.ExpiresAt)
                || string.IsNullOrWhiteSpace(projection.Value.StoreGenerationFingerprint))
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.Stale);
            }

            var projectionItems = projection.Value.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>();
            if (!TryBindPrivateRowsToProjection(
                    corpus.Rows,
                    projectionItems,
                    out var boundItems,
                    out var bindError))
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(bindError!);
            }

            // Prefer catalogue fingerprint from the frozen projection when present.
            if (!string.IsNullOrWhiteSpace(projection.Value.CategoryIdentityLifecycleFingerprint))
            {
                categoryLifecycleFingerprint = projection.Value.CategoryIdentityLifecycleFingerprint!;
            }

            var candidateFingerprint = ValidationReportBuilder.ComputeCandidateFingerprint(
                loaded.Select(l => (
                    l.Version.RuleVersionId,
                    l.Version.CategoryId,
                    l.Version.ScopeHash,
                    l.Version.NormalizationVersion,
                    l.Version.RuleOrigin)).ToArray());
            var expectedOutcomeFingerprint =
                ValidationReportBuilder.ComputeExpectedOutcomeFingerprint(corpus.Rows);
            var orderedItemsFingerprint = EvaluationFingerprint.ComputeOrderedItemsFingerprint(
                boundItems.Select(r => (r.Ordinal, r.TransactionId, r.ItemLifecycleFingerprint)));

            var requestElement = BuildFingerprintElement(
                candidateIds,
                corpus.Fingerprint.Sha256Hex,
                candidateFingerprint,
                expectedOutcomeFingerprint,
                categoryLifecycleFingerprint,
                projection.Value.SnapshotId,
                projection.Value.StoreGenerationFingerprint!);
            var requestFingerprint = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
                ClassifyOperationIds.RuleValidate,
                ClassifyOperationIds.ContractVersion,
                actorKind,
                actorLabel,
                actorRunId,
                requestElement);

            var probed = await TryProbeAsync(idempotencyKey, requestFingerprint, ct);
            if (probed is not null)
            {
                return probed;
            }

            var startedAt = timeProvider.GetUtcNow();
            var validationId = ClassifyContractMapper.NewRuleVersionId(startedAt);
            var startedAtUtc = ClassifyContractMapper.FormatUtc(startedAt);

            var evaluationFingerprint = EvaluationFingerprint.Create(
                CategoryContractVersions.Current,
                ClassificationProjectionVersions.ClassificationV1,
                projection.Value.StoreGenerationFingerprint!,
                projection.Value.SnapshotId,
                projection.Value.ExpiresAt,
                categoryLifecycleFingerprint,
                NormalizationDescriptor.V1.Version,
                candidateFingerprint,
                orderedItemsFingerprint);

            var engineRules = loaded
                .Select(l => new ActiveRuleVersion(l.Version.RuleVersionId, l.Version.CategoryId, l.Conditions))
                .ToArray();
            // Evaluate bound projection-aligned items only (production engine — no second evaluator).
            var evaluation = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
                evaluationFingerprint,
                boundItems,
                engineRules,
                activeCategoryIds));

            var built = ValidationReportBuilder.Build(validationId, corpus.Rows, evaluation);
            var completedAtUtc = ClassifyContractMapper.FormatUtc(timeProvider.GetUtcNow());

            if (Process.GetCurrentProcess().WorkingSet64 > ClassifyOperationModule.V1Limits.MaxMemoryBytes)
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.ResourceLimit);
            }

            var result = new ClassifyRuleValidateResult(
                ClassifyOperationIds.ContractVersion,
                validationId,
                candidateFingerprint,
                corpus.Fingerprint.Sha256Hex,
                expectedOutcomeFingerprint,
                ClassificationProjectionVersions.ClassificationV1,
                projection.Value.SnapshotId,
                projection.Value.ExpiresAt,
                projection.Value.StoreGenerationFingerprint!,
                categoryLifecycleFingerprint,
                NormalizationDescriptor.V1.Version,
                built.Report.ReportFingerprint,
                evaluation.OutcomesCanonicalHash,
                built.Report.TotalRows,
                built.Report.AccountedRows,
                built.Report.SuggestionCount,
                built.Report.NoSuggestionCount,
                built.Report.ConflictCount,
                built.Report.StaleCount,
                built.Report.CoverageBasisPoints,
                built.Report.DriftCanaryCount,
                built.Report.IncorrectApplicationCanaryCount,
                built.Report.UnexplainedConflictCount,
                built.ActivationEligible);

            var origins = loaded.Select(l => l.Version.RuleOrigin).Distinct(StringComparer.Ordinal).ToArray();
            var ruleOrigin = origins.Length == 1
                ? origins[0]
                : ClassificationRuleStore.OriginOwnerAuthored;

            try
            {
                return await stateStore.ExecuteWriteAsync(
                    async (connection, transaction, writeCt) =>
                    {
                        var existing = await idempotencyStore.FindAsync(
                            connection, transaction, idempotencyKey, writeCt);
                        var lookup = idempotencyStore.Resolve(
                            existing,
                            ClassifyOperationIds.RuleValidate,
                            ClassifyOperationIds.ContractVersion,
                            requestFingerprint);
                        switch (lookup.Disposition)
                        {
                            case ClassifyIdempotencyDisposition.Replay:
                                return ReplayOrIntegrity(lookup.Record!);
                            case ClassifyIdempotencyDisposition.Conflict:
                                return CommandResult<ClassifyRuleValidateResult>.Failure(
                                    ClassifyErrors.IdempotencyConflict);
                            case ClassifyIdempotencyDisposition.Miss:
                                break;
                            default:
                                return CommandResult<ClassifyRuleValidateResult>.Failure(
                                    ClassifyErrors.Unexpected);
                        }

                        var activeMid = await validationStore.CountActiveRuleSetAsync(
                            connection, transaction, writeCt);
                        if (activeMid != activeBefore)
                        {
                            throw new InvalidOperationException(
                                $"{ClassifyErrors.Integrity}: active_rule_set changed before validation write.");
                        }

                        await validationStore.InsertRunningAsync(
                            connection,
                            transaction,
                            new ClassificationValidationRunRow(
                                validationId,
                                candidateFingerprint,
                                ruleOrigin,
                                corpus.Fingerprint.Sha256Hex,
                                expectedOutcomeFingerprint,
                                ClassificationProjectionVersions.ClassificationV1,
                                categoryLifecycleFingerprint,
                                NormalizationDescriptor.V1.Version,
                                startedAtUtc,
                                CompletedAt: null,
                                ClassificationValidationStore.LifecycleRunning,
                                actorText,
                                projection.Value.SnapshotId,
                                projection.Value.ExpiresAt,
                                projection.Value.StoreGenerationFingerprint!),
                            writeCt);

                        // Enrich aggregate report with reconstruction fields (schema v2) without private payload.
                        var durableReport = built.Report with
                        {
                            OutcomesCanonicalHash = evaluation.OutcomesCanonicalHash,
                            ActivationEligible = built.ActivationEligible
                        };
                        await validationStore.CompleteAsync(
                            connection,
                            transaction,
                            validationId,
                            completedAtUtc,
                            durableReport,
                            writeCt);

                        var activeAfter = await validationStore.CountActiveRuleSetAsync(
                            connection, transaction, writeCt);
                        if (activeAfter != activeBefore)
                        {
                            throw new InvalidOperationException(
                                $"{ClassifyErrors.Integrity}: rule.validate must not change active_rule_set.");
                        }

                        var finalized = result;
                        if (finalization is not null)
                        {
                            var receiptResult = await FinalizeOwnerGateAsync(
                                connection,
                                transaction,
                                holdOutValidationId: validationId,
                                finalization,
                                actorText,
                                completedAtUtc,
                                writeCt);
                            if (!receiptResult.IsSuccess)
                            {
                                return CommandResult<ClassifyRuleValidateResult>.Failure(receiptResult.ErrorCode!);
                            }

                            finalized = result with
                            {
                                OwnerRulebookGateReceiptId = receiptResult.Value!.ReceiptId,
                                OwnerRulebookGateReceiptFingerprint = receiptResult.Value.ReceiptFingerprint
                            };
                        }

                        await idempotencyStore.CommitAsync(
                            connection,
                            transaction,
                            new ClassifyOperationIdempotencyRow(
                                idempotencyKey,
                                ClassifyOperationIds.RuleValidate,
                                ClassifyOperationIds.ContractVersion,
                                requestFingerprint,
                                JsonSerializer.Serialize(
                                    finalized, ClassifyJsonContext.Default.ClassifyRuleValidateResult),
                                completedAtUtc),
                            writeCt);

                        return CommandResult<ClassifyRuleValidateResult>.Success(finalized);
                    },
                    ct);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith(ClassifyErrors.Integrity, StringComparison.Ordinal))
            {
                return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.Integrity);
            }
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.ResourceLimit);
        }
        catch (OperationCanceledException)
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(PrivateCorpusErrors.Cancelled);
        }
    }

    private sealed record OwnerGateFinalization(
        string RepresentativeValidationId,
        string IndependentReplayValidationId,
        int OwnerDecisionCountBefore,
        int OwnerDecisionCountAfter,
        double? OwnerMinutesBefore,
        double? OwnerMinutesAfter,
        string? ExplicitBenefitDecision);

    /// <summary>
    /// Optional owner-gate finalization is all-or-nothing: either no finalization fields, or
    /// completed representative + independent-replay IDs plus aggregate benefit counts.
    /// Never accepts a caller-supplied authority boolean or receipt body.
    /// </summary>
    private static bool TryParseFinalization(
        ClassifyRuleValidateRequest input,
        out OwnerGateFinalization? finalization,
        out string? errorCode)
    {
        finalization = null;
        errorCode = null;
        var hasRep = !string.IsNullOrWhiteSpace(input.RepresentativeValidationId);
        var hasReplay = !string.IsNullOrWhiteSpace(input.IndependentReplayValidationId);
        var hasBefore = input.OwnerDecisionCountBefore is not null;
        var hasAfter = input.OwnerDecisionCountAfter is not null;
        var hasDecision = input.ExplicitBenefitDecision is not null;
        var any = hasRep || hasReplay || hasBefore || hasAfter || hasDecision
            || input.OwnerMinutesBefore is not null || input.OwnerMinutesAfter is not null;
        if (!any)
        {
            return true;
        }

        if (!hasRep || !hasReplay || !hasBefore || !hasAfter)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (input.OwnerDecisionCountBefore!.Value < 0 || input.OwnerDecisionCountAfter!.Value < 0)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        if (input.OwnerMinutesBefore is < 0 || input.OwnerMinutesAfter is < 0)
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        var decision = string.IsNullOrWhiteSpace(input.ExplicitBenefitDecision)
            ? null
            : input.ExplicitBenefitDecision.Trim();
        if (decision is not null
            && !string.Equals(decision, "approve-broad", StringComparison.Ordinal)
            && !string.Equals(decision, "approve", StringComparison.Ordinal)
            && !string.Equals(decision, "defer-broad", StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        var repId = input.RepresentativeValidationId!.Trim();
        var replayId = input.IndependentReplayValidationId!.Trim();
        if (string.Equals(repId, replayId, StringComparison.Ordinal))
        {
            errorCode = ClassifyErrors.InvalidInput;
            return false;
        }

        finalization = new OwnerGateFinalization(
            repId,
            replayId,
            input.OwnerDecisionCountBefore.Value,
            input.OwnerDecisionCountAfter.Value,
            input.OwnerMinutesBefore,
            input.OwnerMinutesAfter,
            decision);
        return true;
    }

    private async Task<CommandResult<OwnerRulebookGateReceiptRow>> FinalizeOwnerGateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string holdOutValidationId,
        OwnerGateFinalization finalization,
        string actorText,
        string createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.Equals(holdOutValidationId, finalization.RepresentativeValidationId, StringComparison.Ordinal)
            || string.Equals(holdOutValidationId, finalization.IndependentReplayValidationId, StringComparison.Ordinal))
        {
            return CommandResult<OwnerRulebookGateReceiptRow>.Failure(ClassifyErrors.InvalidInput);
        }

        var repRun = await validationStore.GetRunAsync(
            connection, transaction, finalization.RepresentativeValidationId, cancellationToken);
        var repReport = await validationStore.GetReportAsync(
            connection, transaction, finalization.RepresentativeValidationId, cancellationToken);
        var replayRun = await validationStore.GetRunAsync(
            connection, transaction, finalization.IndependentReplayValidationId, cancellationToken);
        var replayReport = await validationStore.GetReportAsync(
            connection, transaction, finalization.IndependentReplayValidationId, cancellationToken);
        var holdRun = await validationStore.GetRunAsync(
            connection, transaction, holdOutValidationId, cancellationToken);
        var holdReport = await validationStore.GetReportAsync(
            connection, transaction, holdOutValidationId, cancellationToken);

        if (repRun is null || repReport is null
            || replayRun is null || replayReport is null
            || holdRun is null || holdReport is null)
        {
            return CommandResult<OwnerRulebookGateReceiptRow>.Failure(ClassifyErrors.ValidationNotFound);
        }

        if (!string.Equals(repRun.LifecycleState, ClassificationValidationStore.LifecycleCompleted, StringComparison.Ordinal)
            || !string.Equals(replayRun.LifecycleState, ClassificationValidationStore.LifecycleCompleted, StringComparison.Ordinal)
            || !string.Equals(holdRun.LifecycleState, ClassificationValidationStore.LifecycleCompleted, StringComparison.Ordinal))
        {
            return CommandResult<OwnerRulebookGateReceiptRow>.Failure(ClassifyErrors.Lifecycle);
        }

        var repResult = ClassificationValidationStore.TryReconstructValidateResult(repRun, repReport);
        var replayResult = ClassificationValidationStore.TryReconstructValidateResult(replayRun, replayReport);
        var holdResult = ClassificationValidationStore.TryReconstructValidateResult(holdRun, holdReport);
        if (repResult is null || replayResult is null || holdResult is null)
        {
            // Incomplete durable evidence cannot authorize a receipt (historical rows stay non-authoritative).
            return CommandResult<OwnerRulebookGateReceiptRow>.Failure(ClassifyErrors.Stale);
        }

        // Candidate / projection / category / normalization / store-generation must bind across the three runs.
        if (!string.Equals(repResult.CandidateFingerprint, replayResult.CandidateFingerprint, StringComparison.Ordinal)
            || !string.Equals(repResult.CandidateFingerprint, holdResult.CandidateFingerprint, StringComparison.Ordinal)
            || !string.Equals(repResult.ProjectionVersion, holdResult.ProjectionVersion, StringComparison.Ordinal)
            || !string.Equals(repResult.StoreGenerationFingerprint, holdResult.StoreGenerationFingerprint, StringComparison.Ordinal)
            || !string.Equals(repResult.CategoryLifecycleFingerprint, holdResult.CategoryLifecycleFingerprint, StringComparison.Ordinal)
            || !string.Equals(repResult.NormalizationVersion, holdResult.NormalizationVersion, StringComparison.Ordinal))
        {
            return CommandResult<OwnerRulebookGateReceiptRow>.Failure(ClassifyErrors.Stale);
        }

        var benefit = new OwnerBenefitEvidenceReceipt(
            finalization.OwnerDecisionCountBefore,
            finalization.OwnerDecisionCountAfter,
            finalization.OwnerMinutesBefore,
            finalization.OwnerMinutesAfter);
        var derived = VerifiedOwnerRulebookGateReceipt.Derive(
            repResult,
            replayResult,
            holdResult,
            benefit,
            finalization.ExplicitBenefitDecision);

        var receiptId = ClassifyContractMapper.NewRuleVersionId(timeProvider.GetUtcNow().AddTicks(3));
        var row = OwnerRulebookGateReceiptStore.FromDerived(
            derived,
            receiptId,
            finalization.RepresentativeValidationId,
            finalization.IndependentReplayValidationId,
            holdOutValidationId,
            repResult.CategoryLifecycleFingerprint,
            repResult.NormalizationVersion,
            finalization.ExplicitBenefitDecision,
            actorText,
            createdAtUtc);

        await receiptStore.InsertAsync(connection, transaction, row, cancellationToken);
        return CommandResult<OwnerRulebookGateReceiptRow>.Success(row);
    }

    private async Task<CommandResult<ClassifyRuleValidateResult>?> TryProbeAsync(
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
                ClassifyOperationIds.RuleValidate,
                ClassifyOperationIds.ContractVersion,
                requestFingerprint);
            await transaction.RollbackAsync(cancellationToken);

            return lookup.Disposition switch
            {
                ClassifyIdempotencyDisposition.Replay => ReplayOrIntegrity(lookup.Record!),
                ClassifyIdempotencyDisposition.Conflict =>
                    CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.IdempotencyConflict),
                ClassifyIdempotencyDisposition.Miss => null,
                _ => CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.Unexpected)
            };
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            throw;
        }
    }

    private static CommandResult<ClassifyRuleValidateResult> ReplayOrIntegrity(
        ClassifyOperationIdempotencyRow record)
    {
        try
        {
            var result = JsonSerializer.Deserialize(
                record.TerminalResult,
                ClassifyJsonContext.Default.ClassifyRuleValidateResult);
            return result is null
                ? CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.Integrity)
                : CommandResult<ClassifyRuleValidateResult>.Success(result);
        }
        catch (JsonException)
        {
            return CommandResult<ClassifyRuleValidateResult>.Failure(ClassifyErrors.Integrity);
        }
    }

    private static string MapCorpusError(string? code) => code switch
    {
        PrivateCorpusErrors.PathRequired => PrivateCorpusErrors.PathRequired,
        PrivateCorpusErrors.NotFound => PrivateCorpusErrors.NotFound,
        PrivateCorpusErrors.SymlinkRejected => PrivateCorpusErrors.SymlinkRejected,
        PrivateCorpusErrors.OwnerRejected => PrivateCorpusErrors.OwnerRejected,
        PrivateCorpusErrors.PermissionsRejected => PrivateCorpusErrors.PermissionsRejected,
        PrivateCorpusErrors.NotRegularFile => PrivateCorpusErrors.NotRegularFile,
        PrivateCorpusErrors.Malformed => PrivateCorpusErrors.Malformed,
        PrivateCorpusErrors.DuplicateOrdinal => PrivateCorpusErrors.DuplicateOrdinal,
        PrivateCorpusErrors.LimitExceeded => ClassifyErrors.ResourceLimit,
        PrivateCorpusErrors.Timeout => ClassifyErrors.ResourceLimit,
        PrivateCorpusErrors.Cancelled => PrivateCorpusErrors.Cancelled,
        PrivateCorpusErrors.FieldInvalid => PrivateCorpusErrors.FieldInvalid,
        PrivateCorpusErrors.ReadFailed => PrivateCorpusErrors.ReadFailed,
        _ => PrivateCorpusErrors.NotFound
    };

    private static JsonElement BuildFingerprintElement(
        IReadOnlyList<string> candidateIds,
        string corpusFingerprint,
        string candidateFingerprint,
        string expectedOutcomeFingerprint,
        string categoryLifecycleFingerprint,
        string snapshotId,
        string storeGenerationFingerprint)
    {
        // AOT-safe: manual Utf8JsonWriter — no reflection serializer.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("candidateFingerprint", candidateFingerprint);
            writer.WritePropertyName("candidateIds");
            writer.WriteStartArray();
            foreach (var id in candidateIds)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteString("categoryLifecycleFingerprint", categoryLifecycleFingerprint);
            writer.WriteString("corpusFingerprint", corpusFingerprint);
            writer.WriteString("expectedOutcomeFingerprint", expectedOutcomeFingerprint);
            writer.WriteString("normalizationVersion", NormalizationDescriptor.V1.Version);
            writer.WriteString("projectionContractVersion", ClassificationProjectionVersions.ClassificationV1);
            writer.WriteString("snapshotId", snapshotId);
            writer.WriteString("storeGenerationFingerprint", storeGenerationFingerprint);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Bind each private corpus row exactly once to a frozen public classification_v1 projection member.
    /// Requires matching account, description, direction, absolute amount, and lifecycle fingerprint
    /// derived from the public revision tuple. Failures are metadata-only (no private payload retained).
    /// </summary>
    internal static bool TryBindPrivateRowsToProjection(
        IReadOnlyList<PrivateCorpusRow> privateRows,
        IReadOnlyList<ClassificationProjectionItem> projectionItems,
        out IReadOnlyList<ClassificationEvaluationItem> boundItems,
        out string? errorCode)
    {
        boundItems = Array.Empty<ClassificationEvaluationItem>();
        errorCode = null;

        if (privateRows.Count == 0)
        {
            boundItems = Array.Empty<ClassificationEvaluationItem>();
            return true;
        }

        var byTx = new Dictionary<string, ClassificationProjectionItem>(StringComparer.Ordinal);
        foreach (var item in projectionItems)
        {
            if (!byTx.TryAdd(item.TransactionId, item))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }
        }

        var seenPrivate = new HashSet<string>(StringComparer.Ordinal);
        var bound = new List<ClassificationEvaluationItem>(privateRows.Count);
        foreach (var row in privateRows.OrderBy(r => r.Ordinal).ThenBy(r => r.TransactionId, StringComparer.Ordinal))
        {
            if (!seenPrivate.Add(row.TransactionId))
            {
                errorCode = ClassifyErrors.Integrity;
                return false;
            }

            if (!byTx.TryGetValue(row.TransactionId, out var publicItem))
            {
                // Missing from frozen evaluation projection — fail closed before authority.
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            if (!TryMatchPrivateToPublic(row, publicItem, out var matchedItem))
            {
                errorCode = ClassifyErrors.Stale;
                return false;
            }

            bound.Add(matchedItem);
        }

        // Every private row accounted; extras on the public projection are allowed (eligible universe may be larger).
        boundItems = bound;
        return true;
    }

    private static bool TryMatchPrivateToPublic(
        PrivateCorpusRow row,
        ClassificationProjectionItem publicItem,
        out ClassificationEvaluationItem evaluationItem)
    {
        evaluationItem = null!;

        if (!string.Equals(row.AccountId, publicItem.AccountId, StringComparison.Ordinal)
            || !string.Equals(row.SourceDescription, publicItem.SourceDescription, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryMapPublicAmount(publicItem, out var direction, out var absoluteMinor))
        {
            return false;
        }

        if (!string.Equals(row.AmountDirection, direction, StringComparison.Ordinal)
            || row.AmountAbsoluteMinor != absoluteMinor)
        {
            return false;
        }

        var lifecycle = ComputeItemLifecycleFingerprint(publicItem);
        if (!string.Equals(row.ItemLifecycleFingerprint, lifecycle, StringComparison.Ordinal))
        {
            return false;
        }

        evaluationItem = new ClassificationEvaluationItem(
            row.Ordinal,
            row.TransactionId,
            row.AccountId,
            row.SourceDescription,
            row.AmountDirection,
            row.AmountAbsoluteMinor,
            row.ItemLifecycleFingerprint);
        return true;
    }

    /// <summary>Public revision-tuple fingerprint used to bind private rows without retaining payloads.</summary>
    public static string ComputeItemLifecycleFingerprint(ClassificationProjectionItem item) =>
        CanonicalClassificationHasher.HashParts(
            item.TransactionRevision,
            item.RelationshipRevision,
            item.AllocationRevision);

    public static bool TryMapPublicAmount(
        ClassificationProjectionItem item,
        out string? direction,
        out long absoluteMinor)
    {
        direction = null;
        absoluteMinor = 0;
        if (!Money.TryParse(item.SignedAmount, out var money, out _))
        {
            return false;
        }

        absoluteMinor = money.MinorUnits == long.MinValue
            ? long.MaxValue
            : Math.Abs(money.MinorUnits);
        direction = item.AmountDirection switch
        {
            ClassificationAmountDirection.Expense => ClassificationRuleVocabulary.DirectionOutflow,
            ClassificationAmountDirection.Income => ClassificationRuleVocabulary.DirectionInflow,
            ClassificationAmountDirection.Zero => null,
            _ => null
        };
        return true;
    }
}
