using System.Globalization;
using System.Runtime.Versioning;
using Tally.Application;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Features.Classify.Rules.Discovery;

/// <summary>
/// classify.rule-set.active.get vertical slice
/// (FR-CLASSIFY-RULEBOOK-DISCOVERY / DD-CLASSIFY-RULE-AUTHORITY-PROVENANCE / bd-2vbg).
/// Traces current authority to immutable rule_set_version + active pointer. Never synthesizes empty authority.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class GetActiveClassificationRuleSetQuery
{
    private readonly ClassifyStateStore stateStore;
    private readonly ClassificationRuleStore ruleStore;
    private readonly ClassificationRuleDiscoveryStore discoveryStore;
    private readonly RuleSetStore ruleSetStore;
    private readonly LedgerContractClient ledger;

    public GetActiveClassificationRuleSetQuery(
        ClassifyStateStore stateStore,
        ClassificationRuleStore ruleStore,
        ClassificationRuleDiscoveryStore discoveryStore,
        RuleSetStore ruleSetStore,
        LedgerContractClient ledger)
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
    }

    public async Task<CommandResult<ClassifyRuleSetActiveGetResult>> HandleAsync(
        ClassifyRuleSetActiveGetRequest input,
        SafeActor? actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null
            || string.IsNullOrWhiteSpace(actor.Kind)
            || string.IsNullOrWhiteSpace(actor.Label))
        {
            return CommandResult<ClassifyRuleSetActiveGetResult>.Failure(ClassifyErrors.ActorRequired);
        }

        if (!ClassifyOperatorErgonomicsContracts.TryValidate(input, out var validationError)
            || validationError is not null)
        {
            return CommandResult<ClassifyRuleSetActiveGetResult>.Failure(
                validationError ?? ClassifyErrors.InvalidInput);
        }

        ClassifyActiveRuleSetPointer? pointer;
        ClassifyRuleSetVersionRow? setVersion;
        IReadOnlyList<string> memberIds;
        var broadApply = false;
        var categoryIds = new List<string>();

        await using (var connection = await stateStore.OpenMigratedAsync(cancellationToken))
        {
            pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, cancellationToken);
            if (pointer is null)
            {
                return CommandResult<ClassifyRuleSetActiveGetResult>.Failure(
                    ClassifyErrors.ActiveRuleSetNotFound);
            }

            setVersion = await ruleSetStore.GetRuleSetVersionAsync(
                connection, null, pointer.RuleSetVersionId, cancellationToken);
            if (setVersion is null)
            {
                return CommandResult<ClassifyRuleSetActiveGetResult>.Failure(ClassifyErrors.Integrity);
            }

            memberIds = await ruleSetStore.ListMemberRuleVersionIdsAsync(
                connection, null, pointer.RuleSetVersionId, cancellationToken);
            if (memberIds.Count == 0)
            {
                // Empty active membership is impossible for a normal activation; fail closed.
                return CommandResult<ClassifyRuleSetActiveGetResult>.Failure(ClassifyErrors.Integrity);
            }

            var orderedMembers = memberIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            foreach (var memberId in orderedMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = await ruleStore.GetRuleVersionAsync(connection, null, memberId, cancellationToken);
                if (version is null)
                {
                    return CommandResult<ClassifyRuleSetActiveGetResult>.Failure(ClassifyErrors.Integrity);
                }

                if (version.BroadApplyAllowed != 0)
                {
                    broadApply = true;
                }

                if (!categoryIds.Contains(version.CategoryId, StringComparer.Ordinal))
                {
                    categoryIds.Add(version.CategoryId);
                }
            }

            memberIds = orderedMembers;
        }

        var categories = new List<ClassifyActiveRuleSetCategory>(categoryIds.Count);
        foreach (var catId in categoryIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await ledger.GetBudgetCategoryAsync(
                catId,
                CategoryContractVersions.Current,
                actor,
                cancellationToken);
            if (!detail.IsSuccess || detail.Value is null)
            {
                categories.Add(new ClassifyActiveRuleSetCategory(
                    catId,
                    null,
                    ClassifyCategoryLifecycleState.Archived));
            }
            else
            {
                var life = detail.Value.Status == CategoryStatus.Active
                    ? ClassifyCategoryLifecycleState.Active
                    : ClassifyCategoryLifecycleState.Archived;
                categories.Add(new ClassifyActiveRuleSetCategory(
                    catId,
                    detail.Value.Name,
                    life));
            }
        }

        // Activation identity is the immutable rule_set_version_id (authority receipt of activation).
        var result = ClassifyContractMapper.ToActiveRuleSetResult(
            ruleSetVersionId: setVersion!.RuleSetVersionId,
            broadApplyAllowed: broadApply,
            activationId: setVersion.RuleSetVersionId,
            validationId: setVersion.ValidationRunId,
            trustedGateReceiptId: setVersion.OwnerRulebookGateReceiptId,
            trustedGateReceiptFingerprint: setVersion.OwnerRulebookGateReceiptFingerprint,
            normalizationVersion: setVersion.NormalizationVersion,
            activationEpoch: pointer!.ActivationEpoch.ToString(CultureInfo.InvariantCulture),
            activatedAt: setVersion.CreatedAt,
            retiredAt: null,
            ruleVersionIds: memberIds,
            categories: categories);

        return CommandResult<ClassifyRuleSetActiveGetResult>.Success(result);
    }
}
