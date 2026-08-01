using System.Runtime.Versioning;
using Tally.Features.Classify.Recovery.Abandon;
using Tally.Features.Classify.Recovery.Cleanup;
using Tally.Features.Classify.Recovery.Status;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Feedback;
using Tally.Infrastructure.Classify.Storage.Recovery;
using Tally.Infrastructure.Classify.Storage.Rules;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY recovery composition root (no reflection / plugin scan).
/// Registers abandon, cleanup, and status over owner-only state and recovery stores.
/// Shared process/registry wiring remains owned by later convergence beads (bd-3g6y).
/// </summary>
[SupportedOSPlatform("linux")]
public static class ClassifyRecoveryExtensions
{
    /// <summary>
    /// Compose owner-only CLASSIFY state with recovery commands and bounded status query.
    /// Status is read-only over durable rows — no corpus or LEDGER payload rereads.
    /// </summary>
    public static async Task<ClassifyRecoveryServices> CreateServicesAsync(
        string dataRoot,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var state = await ClassifyStateExtensions.CreateStateAsync(dataRoot, cancellationToken);
        var ruleStore = new ClassificationRuleStore();
        var ruleSetStore = new RuleSetStore();
        var validationStore = new ClassificationValidationStore();
        var evaluationStore = new ClassificationEvaluationStore();
        var previewStore = new ClassificationApplyPreviewStore();
        var runStore = new ClassificationApplyRunStore();
        var feedbackStore = new ClassificationFeedbackStore();
        var recoveryStore = new ClassificationRecoveryStore();
        var clock = timeProvider ?? TimeProvider.System;
        var artifacts = state.Artifacts
            ?? new ClassifyArtifactProtection(state.Store.Paths, state.Protection);

        var abandon = new AbandonClassificationStateCommand(
            state.Store, recoveryStore, artifacts, state.Idempotency, clock);
        var cleanup = new CleanupClassificationStateCommand(
            state.Store, recoveryStore, artifacts, state.Idempotency, clock);
        var status = new GetClassificationStatusQuery(
            state.Store,
            ruleStore,
            ruleSetStore,
            validationStore,
            evaluationStore,
            previewStore,
            runStore,
            feedbackStore,
            recoveryStore,
            clock);

        return new ClassifyRecoveryServices(
            state,
            ruleStore,
            ruleSetStore,
            validationStore,
            evaluationStore,
            previewStore,
            runStore,
            feedbackStore,
            recoveryStore,
            artifacts,
            abandon,
            cleanup,
            status);
    }
}

[SupportedOSPlatform("linux")]
public sealed record ClassifyRecoveryServices(
    ClassifyStateServices State,
    ClassificationRuleStore RuleStore,
    RuleSetStore RuleSetStore,
    ClassificationValidationStore ValidationStore,
    ClassificationEvaluationStore EvaluationStore,
    ClassificationApplyPreviewStore PreviewStore,
    ClassificationApplyRunStore RunStore,
    ClassificationFeedbackStore FeedbackStore,
    ClassificationRecoveryStore RecoveryStore,
    ClassifyArtifactProtection Artifacts,
    AbandonClassificationStateCommand Abandon,
    CleanupClassificationStateCommand Cleanup,
    GetClassificationStatusQuery Status);
