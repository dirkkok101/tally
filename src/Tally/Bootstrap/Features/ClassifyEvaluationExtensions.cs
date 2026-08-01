using System.Runtime.Versioning;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Features.Classify.Evaluation.Outcome;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY evaluation composition root (no reflection / plugin scan).
/// Registers evaluate + outcome.get over owner-only state and the public Ledger client.
/// Shared process/registry wiring remains owned by later convergence beads (bd-3g6y).
/// </summary>
[SupportedOSPlatform("linux")]
public static class ClassifyEvaluationExtensions
{
    /// <summary>
    /// Compose owner-only CLASSIFY state with evaluation and outcome-explanation queries.
    /// Does not open private corpus paths; evaluate loads public projection via the input loader only.
    /// </summary>
    public static async Task<ClassifyEvaluationServices> CreateServicesAsync(
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
            ruleSetStore,
            ledgerClient,
            clock);

        // Rule lifecycle helpers for integration tests and owner workflows that prepare an active set.
        var save = new SaveClassificationRuleCommand(
            state.Store, ruleStore, ledgerClient, state.Idempotency, clock);
        var activate = new ActivateClassificationRuleCommand(
            state.Store, ruleStore, validationStore, ruleSetStore, ledgerClient,
            state.Idempotency, clock, receiptStore);
        var validate = new ValidateClassificationRuleCommand(
            state.Store, ruleStore, validationStore, new PrivateCorpusReader(), ledgerClient,
            state.Idempotency, clock, receiptStore);

        return new ClassifyEvaluationServices(
            state,
            ruleStore,
            ruleSetStore,
            evaluationStore,
            inputLoader,
            evaluate,
            outcomeGet,
            save,
            activate,
            validate,
            ledgerClient);
    }
}

/// <summary>Explicit evaluation + explanation composition produced by <see cref="ClassifyEvaluationExtensions"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed record ClassifyEvaluationServices(
    ClassifyStateServices State,
    ClassificationRuleStore RuleStore,
    RuleSetStore RuleSetStore,
    ClassificationEvaluationStore EvaluationStore,
    ClassificationEvaluationInputLoader InputLoader,
    EvaluateClassificationCommand Evaluate,
    GetClassificationOutcomeQuery OutcomeGet,
    SaveClassificationRuleCommand Save,
    ActivateClassificationRuleCommand Activate,
    ValidateClassificationRuleCommand Validate,
    LedgerContractClient LedgerClient);
