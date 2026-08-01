using System.Runtime.Versioning;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Retire;
using Tally.Features.Classify.Rules.Save;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit CLASSIFY rule lifecycle composition (no reflection / plugin scan).
/// Registers save / activate / retire commands over owner-only state and the public Ledger client.
/// Shared process/registry wiring remains owned by later convergence beads.
/// </summary>
[SupportedOSPlatform("linux")]
public static class ClassifyRuleExtensions
{
    /// <summary>
    /// Compose owner-only CLASSIFY state with draft-save, activation, and retirement commands.
    /// Does not open private corpus paths or mutate Ledger.
    /// </summary>
    public static async Task<ClassifyRuleServices> CreateServicesAsync(
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
        var clock = timeProvider ?? TimeProvider.System;

        var save = new SaveClassificationRuleCommand(
            state.Store,
            ruleStore,
            ledgerClient,
            state.Idempotency,
            clock);
        var activate = new ActivateClassificationRuleCommand(
            state.Store,
            ruleStore,
            validationStore,
            ruleSetStore,
            ledgerClient,
            state.Idempotency,
            clock,
            receiptStore);
        var retire = new RetireClassificationRuleCommand(
            state.Store,
            ruleStore,
            ruleSetStore,
            ledgerClient,
            state.Idempotency,
            clock);

        return new ClassifyRuleServices(
            state,
            ruleStore,
            validationStore,
            receiptStore,
            ruleSetStore,
            save,
            activate,
            retire,
            ledgerClient);
    }
}

/// <summary>Explicit rule lifecycle composition produced by <see cref="ClassifyRuleExtensions"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed record ClassifyRuleServices(
    ClassifyStateServices State,
    ClassificationRuleStore RuleStore,
    ClassificationValidationStore ValidationStore,
    OwnerRulebookGateReceiptStore ReceiptStore,
    RuleSetStore RuleSetStore,
    SaveClassificationRuleCommand Save,
    ActivateClassificationRuleCommand Activate,
    RetireClassificationRuleCommand Retire,
    LedgerContractClient LedgerClient);
