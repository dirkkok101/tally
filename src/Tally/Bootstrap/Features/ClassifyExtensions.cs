using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Features.Classify.Apply.Preview;
using Tally.Features.Classify.Apply.Run;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Features.Classify.Evaluation.Outcome;
using Tally.Features.Classify.Feedback.Record;
using Tally.Features.Classify.Recovery.Abandon;
using Tally.Features.Classify.Recovery.Cleanup;
using Tally.Features.Classify.Recovery.Status;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Retire;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Feedback;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Complete explicit CLASSIFY composition root (no reflection / plugin scan).
/// Converges the twelve C12 operations for registry inventory and data-root runtime
/// (TASK-CLASSIFY-RULEBOOK-GATE-INT-PUBLIC-CONTRACT / bd-3g6y).
/// Consumes the approved bd-56yx validation bridge patterns without duplicating them.
/// Descriptor-only discovery never opens classify.db, corpus, or Ledger.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyOperationBundle
{
    public ClassifyOperationBundle(
        IReadOnlyList<OperationDescriptor> descriptors,
        ClassifyStateServices? state = null)
    {
        Descriptors = descriptors
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
        State = state;
    }

    public IReadOnlyList<OperationDescriptor> Descriptors { get; }

    public ClassifyStateServices? State { get; }

    /// <summary>
    /// Descriptor-only inventory for schema discovery. Attaches non-null OperationLimits from
    /// <see cref="ClassifyOperationModule"/>; handlers remain contract stubs (no store opens).
    /// </summary>
    public static ClassifyOperationBundle CreateDescriptorTemplates()
    {
        var module = ClassifyOperationModule.CreateDescriptorTemplates();
        var descriptors = module.Operations
            .Select(operation => operation.Descriptor with { Limits = operation.Limits })
            .ToArray();
        return new ClassifyOperationBundle(descriptors);
    }

    /// <summary>
    /// Full runtime composition: owner-only CLASSIFY state and explicit adapters for all twelve operations.
    /// </summary>
    public static async Task<ClassifyServices> CreateServicesAsync(
        string dataRoot,
        LedgerContractClient ledgerClient,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(ledgerClient);

        var state = await ClassifyStateExtensions.CreateStateAsync(dataRoot, cancellationToken);
        var ruleStore = new ClassificationRuleStore();
        var validationStore = new ClassificationValidationStore();
        var receiptStore = new OwnerRulebookGateReceiptStore();
        var ruleSetStore = new RuleSetStore();
        var evaluationStore = new ClassificationEvaluationStore();
        var previewStore = new ClassificationApplyPreviewStore();
        var runStore = new ClassificationApplyRunStore();
        var feedbackStore = new ClassificationFeedbackStore();
        var recoveryStore = new ClassificationRecoveryStore();
        var applyLock = new ClassificationApplyLock(state.Store.Paths, state.Protection);
        var artifacts = state.Artifacts
            ?? new ClassifyArtifactProtection(state.Store.Paths, state.Protection);
        var clock = timeProvider ?? TimeProvider.System;
        var corpusReader = ClassifyCorpusExtensions.CreateReader();
        var inputLoader = new ClassificationEvaluationInputLoader(ledgerClient, clock);

        var evaluate = new EvaluateClassificationCommand(
            state.Store, evaluationStore, inputLoader, ruleSetStore, ruleStore, state.Idempotency, clock);
        var outcomeGet = new GetClassificationOutcomeQuery(
            state.Store, evaluationStore, ruleStore, ruleSetStore, ledgerClient, clock);
        var preview = new PreviewClassificationApplyCommand(
            state.Store, evaluationStore, previewStore, ruleSetStore, ruleStore, ledgerClient,
            state.Idempotency, clock);
        var run = new RunClassificationApplyCommand(
            state.Store, previewStore, runStore, evaluationStore, ruleSetStore, applyLock, ledgerClient,
            state.Idempotency, clock);
        var save = new SaveClassificationRuleCommand(
            state.Store, ruleStore, ledgerClient, state.Idempotency, clock);
        var validate = new ValidateClassificationRuleCommand(
            state.Store, ruleStore, validationStore, corpusReader, ledgerClient,
            state.Idempotency, clock, receiptStore);
        var activate = new ActivateClassificationRuleCommand(
            state.Store,
            ruleStore,
            validationStore,
            ruleSetStore,
            ledgerClient,
            state.Idempotency,
            clock,
            receiptStore,
            recoveryStore);
        var retire = new RetireClassificationRuleCommand(
            state.Store, ruleStore, ruleSetStore, ledgerClient, state.Idempotency, clock);
        var feedback = new RecordClassificationFeedbackCommand(
            state.Store, evaluationStore, feedbackStore, ruleStore, state.Idempotency, clock);
        var status = new GetClassificationStatusQuery(
            state.Store, ruleStore, ruleSetStore, validationStore, evaluationStore,
            previewStore, runStore, feedbackStore, recoveryStore, clock);
        var abandon = new AbandonClassificationStateCommand(
            state.Store, recoveryStore, artifacts, state.Idempotency, clock);
        var cleanup = new CleanupClassificationStateCommand(
            state.Store, recoveryStore, artifacts, state.Idempotency, clock);

        var module = ClassifyOperationModule.CreateDescriptorTemplates();
        var handlers = new Dictionary<string, IOperationHandler>(StringComparer.Ordinal)
        {
            [ClassifyOperationIds.Evaluate] = new ClassifyJsonHandler<ClassifyEvaluateRequest, ClassifyEvaluateResult>(
                ClassifyJsonContext.Default.ClassifyEvaluateRequest,
                ClassifyJsonContext.Default.ClassifyEvaluateResult,
                (input, actor, key, ct) => evaluate.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.OutcomeGet] = new ClassifyJsonHandler<ClassifyOutcomeGetRequest, ClassifyOutcomeGetResult>(
                ClassifyJsonContext.Default.ClassifyOutcomeGetRequest,
                ClassifyJsonContext.Default.ClassifyOutcomeGetResult,
                (input, actor, _, ct) => outcomeGet.HandleAsync(input, actor, ct)),
            [ClassifyOperationIds.ApplyPreview] = new ClassifyJsonHandler<ClassifyApplyPreviewRequest, ClassifyApplyPreviewResult>(
                ClassifyJsonContext.Default.ClassifyApplyPreviewRequest,
                ClassifyJsonContext.Default.ClassifyApplyPreviewResult,
                (input, actor, key, ct) => preview.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.ApplyRun] = new ClassifyJsonHandler<ClassifyApplyRunRequest, ClassifyApplyRunResult>(
                ClassifyJsonContext.Default.ClassifyApplyRunRequest,
                ClassifyJsonContext.Default.ClassifyApplyRunResult,
                (input, actor, key, ct) => run.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.RuleSave] = new ClassifyJsonHandler<ClassifyRuleSaveRequest, ClassifyRuleSaveResult>(
                ClassifyJsonContext.Default.ClassifyRuleSaveRequest,
                ClassifyJsonContext.Default.ClassifyRuleSaveResult,
                (input, actor, key, ct) => save.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.RuleValidate] = new ClassifyJsonHandler<ClassifyRuleValidateRequest, ClassifyRuleValidateResult>(
                ClassifyJsonContext.Default.ClassifyRuleValidateRequest,
                ClassifyJsonContext.Default.ClassifyRuleValidateResult,
                (input, actor, key, ct) => validate.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.RuleActivate] = new ClassifyJsonHandler<ClassifyRuleActivateRequest, ClassifyRuleActivateResult>(
                ClassifyJsonContext.Default.ClassifyRuleActivateRequest,
                ClassifyJsonContext.Default.ClassifyRuleActivateResult,
                (input, actor, key, ct) => activate.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.RuleRetire] = new ClassifyJsonHandler<ClassifyRuleRetireRequest, ClassifyRuleRetireResult>(
                ClassifyJsonContext.Default.ClassifyRuleRetireRequest,
                ClassifyJsonContext.Default.ClassifyRuleRetireResult,
                (input, actor, key, ct) => retire.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.FeedbackRecord] = new ClassifyJsonHandler<ClassifyFeedbackRecordRequest, ClassifyFeedbackRecordResult>(
                ClassifyJsonContext.Default.ClassifyFeedbackRecordRequest,
                ClassifyJsonContext.Default.ClassifyFeedbackRecordResult,
                (input, actor, key, ct) => feedback.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.Status] = new ClassifyJsonHandler<ClassifyStatusRequest, ClassifyStatusResult>(
                ClassifyJsonContext.Default.ClassifyStatusRequest,
                ClassifyJsonContext.Default.ClassifyStatusResult,
                (input, actor, _, ct) => status.HandleAsync(input, actor, ct)),
            [ClassifyOperationIds.Abandon] = new ClassifyJsonHandler<ClassifyAbandonRequest, ClassifyAbandonResult>(
                ClassifyJsonContext.Default.ClassifyAbandonRequest,
                ClassifyJsonContext.Default.ClassifyAbandonResult,
                (input, actor, key, ct) => abandon.HandleAsync(input, actor, key, ct)),
            [ClassifyOperationIds.Cleanup] = new ClassifyJsonHandler<ClassifyCleanupRequest, ClassifyCleanupResult>(
                ClassifyJsonContext.Default.ClassifyCleanupRequest,
                ClassifyJsonContext.Default.ClassifyCleanupResult,
                (input, actor, key, ct) => cleanup.HandleAsync(input, actor, key, ct))
        };

        var descriptors = module.Operations
            .Select(operation =>
            {
                var handler = handlers[operation.Descriptor.OperationId];
                return operation.Descriptor with
                {
                    Limits = operation.Limits,
                    HandlerFactory = (_, _) => handler,
                    HandlerTarget = "ClassifyOperationBundle." + operation.Descriptor.OperationId
                };
            })
            .ToArray();

        var bundle = new ClassifyOperationBundle(descriptors, state);
        return new ClassifyServices(
            bundle,
            state,
            evaluate,
            outcomeGet,
            preview,
            run,
            save,
            validate,
            activate,
            retire,
            feedback,
            status,
            abandon,
            cleanup,
            ledgerClient);
    }
}

/// <summary>Complete CLASSIFY runtime composition (all twelve operations).</summary>
[SupportedOSPlatform("linux")]
public sealed record ClassifyServices(
    ClassifyOperationBundle Operations,
    ClassifyStateServices State,
    EvaluateClassificationCommand Evaluate,
    GetClassificationOutcomeQuery OutcomeGet,
    PreviewClassificationApplyCommand Preview,
    RunClassificationApplyCommand Run,
    SaveClassificationRuleCommand Save,
    ValidateClassificationRuleCommand Validate,
    ActivateClassificationRuleCommand Activate,
    RetireClassificationRuleCommand Retire,
    RecordClassificationFeedbackCommand Feedback,
    GetClassificationStatusQuery Status,
    AbandonClassificationStateCommand Abandon,
    CleanupClassificationStateCommand Cleanup,
    LedgerContractClient LedgerClient);

/// <summary>
/// Source-generated JSON adapter from process envelope to a typed CLASSIFY command/query.
/// Never logs private paths or payloads.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class ClassifyJsonHandler<TRequest, TResult>(
    System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestInfo,
    System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo,
    Func<TRequest, SafeActor?, string?, CancellationToken, Task<CommandResult<TResult>>> invoke)
    : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, requestInfo);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput);
            }

            var result = await invoke(input, request.Actor, request.IdempotencyKey, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, resultInfo))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput);
        }
    }
}
