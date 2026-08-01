using System.Runtime.Versioning;
using Tally.Features.Classify.Apply.Preview;
using Tally.Features.Classify.Apply.Run;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Features.Classify.Evaluation.Outcome;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY apply composition root (no reflection / plugin scan).
/// Registers apply.preview + apply.run over owner-only state and the public Ledger client.
/// Shared process/registry wiring remains owned by later convergence beads (bd-3g6y).
/// </summary>
[SupportedOSPlatform("linux")]
public static class ClassifyApplyExtensions
{
    /// <summary>
    /// Compose owner-only CLASSIFY state with evaluation helpers and apply preview/run commands.
    /// Does not open private corpus paths for apply; Ledger is accessed only via public client methods.
    /// </summary>
    public static async Task<ClassifyApplyServices> CreateServicesAsync(
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
        var applyLock = new ClassificationApplyLock(state.Store.Paths, state.Protection);
        var clock = timeProvider ?? TimeProvider.System;

        var inputLoader = new ClassificationEvaluationInputLoader(ledgerClient, clock);
        var evaluate = new EvaluateClassificationCommand(
            state.Store,
            evaluationStore,
            inputLoader,
            ruleSetStore,
            ruleStore,
            state.Idempotency,
            clock);
        var outcomeGet = new GetClassificationOutcomeQuery(
            state.Store,
            evaluationStore,
            ruleStore,
            ruleSetStore,
            ledgerClient,
            clock);
        var save = new SaveClassificationRuleCommand(
            state.Store, ruleStore, ledgerClient, state.Idempotency, clock);
        var activate = new ActivateClassificationRuleCommand(
            state.Store, ruleStore, validationStore, ruleSetStore, ledgerClient,
            state.Idempotency, clock, receiptStore);
        var validate = new ValidateClassificationRuleCommand(
            state.Store, ruleStore, validationStore, new PrivateCorpusReader(), ledgerClient,
            state.Idempotency, clock, receiptStore);

        var preview = new PreviewClassificationApplyCommand(
            state.Store,
            evaluationStore,
            previewStore,
            ruleSetStore,
            ruleStore,
            ledgerClient,
            state.Idempotency,
            clock);
        var run = new RunClassificationApplyCommand(
            state.Store,
            previewStore,
            runStore,
            applyLock,
            ledgerClient,
            state.Idempotency,
            clock);

        return new ClassifyApplyServices(
            state,
            ruleStore,
            ruleSetStore,
            evaluationStore,
            previewStore,
            runStore,
            applyLock,
            evaluate,
            outcomeGet,
            save,
            activate,
            validate,
            preview,
            run,
            ledgerClient);
    }
}

/// <summary>Explicit apply composition produced by <see cref="ClassifyApplyExtensions"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed record ClassifyApplyServices(
    ClassifyStateServices State,
    ClassificationRuleStore RuleStore,
    RuleSetStore RuleSetStore,
    ClassificationEvaluationStore EvaluationStore,
    ClassificationApplyPreviewStore PreviewStore,
    ClassificationApplyRunStore RunStore,
    ClassificationApplyLock ApplyLock,
    EvaluateClassificationCommand Evaluate,
    GetClassificationOutcomeQuery OutcomeGet,
    SaveClassificationRuleCommand Save,
    ActivateClassificationRuleCommand Activate,
    ValidateClassificationRuleCommand Validate,
    PreviewClassificationApplyCommand Preview,
    RunClassificationApplyCommand Run,
    LedgerContractClient LedgerClient);
