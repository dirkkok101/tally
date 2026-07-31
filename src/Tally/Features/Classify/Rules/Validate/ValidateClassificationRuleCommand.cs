using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Evaluation;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
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
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(ruleStore);
        ArgumentNullException.ThrowIfNull(validationStore);
        ArgumentNullException.ThrowIfNull(corpusReader);
        ArgumentNullException.ThrowIfNull(ledger);
        this.stateStore = stateStore;
        this.ruleStore = ruleStore;
        this.validationStore = validationStore;
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
                corpus.Rows.Select(r => (r.Ordinal, r.TransactionId, r.ItemLifecycleFingerprint)));

            var requestElement = BuildFingerprintElement(
                candidateIds,
                corpus.Fingerprint.Sha256Hex,
                candidateFingerprint,
                expectedOutcomeFingerprint,
                categoryLifecycleFingerprint);
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
                CanonicalClassificationHasher.HashUtf8("classify.rule.validate"),
                validationId,
                "2099-01-01T00:00:00.0000000Z",
                categoryLifecycleFingerprint,
                NormalizationDescriptor.V1.Version,
                candidateFingerprint,
                orderedItemsFingerprint);

            var engineRules = loaded
                .Select(l => new ActiveRuleVersion(l.Version.RuleVersionId, l.Version.CategoryId, l.Conditions))
                .ToArray();
            var engineItems = corpus.Rows.Select(r => r.ToEvaluationItem()).ToArray();
            var evaluation = ClassificationEngine.Evaluate(new ClassificationEvaluationRequest(
                evaluationFingerprint,
                engineItems,
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
                corpus.Fingerprint.Sha256Hex,
                built.Report.TotalRows,
                built.Report.SuggestionCount,
                built.Report.NoSuggestionCount,
                built.Report.ConflictCount,
                built.Report.IncorrectApplicationCanaryCount,
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
                                actorText),
                            writeCt);

                        await validationStore.CompleteAsync(
                            connection,
                            transaction,
                            validationId,
                            completedAtUtc,
                            built.Report,
                            writeCt);

                        var activeAfter = await validationStore.CountActiveRuleSetAsync(
                            connection, transaction, writeCt);
                        if (activeAfter != activeBefore)
                        {
                            throw new InvalidOperationException(
                                $"{ClassifyErrors.Integrity}: rule.validate must not change active_rule_set.");
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
                                    result, ClassifyJsonContext.Default.ClassifyRuleValidateResult),
                                completedAtUtc),
                            writeCt);

                        return CommandResult<ClassifyRuleValidateResult>.Success(result);
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
        string categoryLifecycleFingerprint)
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
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
