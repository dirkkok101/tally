using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Budget.Projection;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.Activate;
using Tally.Features.Budget.Plans.CreateDraft;
using Tally.Features.Budget.Plans.GetRevision;
using Tally.Features.Budget.Plans.ListRevisions;
using Tally.Features.Budget.Position.Get;
using Tally.Features.Budget.Projection;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit BUDGET composition root (no reflection / plugin scan).
/// GATE-INT-PUBLIC-CONTRACT: six operations + state + Ledger client + INSIGHTS capability.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetOperationBundle
{
    public BudgetOperationBundle(
        IReadOnlyList<OperationDescriptor> descriptors,
        BudgetReadCapabilityDescriptor readCapability,
        BudgetStateServices? state = null)
    {
        Descriptors = descriptors
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();
        ReadCapability = readCapability;
        State = state;
    }

    public IReadOnlyList<OperationDescriptor> Descriptors { get; }

    /// <summary>Published INSIGHTS read-only capability (exactly three allowed operations).</summary>
    public BudgetReadCapabilityDescriptor ReadCapability { get; }

    public BudgetStateServices? State { get; }

    /// <summary>
    /// Descriptor-only bundle for registry inventory (handlers not executed for discovery).
    /// Reuses <see cref="BudgetOperationModule"/> contract stubs — no BudgetStateStore opens.
    /// </summary>
    public static BudgetOperationBundle CreateDescriptorTemplates()
    {
        var module = BudgetOperationModule.CreateDescriptorTemplates();
        return new BudgetOperationBundle(
            module.Descriptors,
            BudgetReadProjectionModule.CreateDescriptorTemplate());
    }

    /// <summary>
    /// Full runtime composition: owner-only state, mutation executor, six concrete handlers,
    /// and the mutation-free INSIGHTS read capability over the same owner inventory.
    /// </summary>
    public static async Task<BudgetServices> CreateServicesAsync(
        string dataRoot,
        LedgerContractClient ledgerClient,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(ledgerClient);

        var state = await BudgetStateExtensions.CreateStateAsync(dataRoot, cancellationToken);
        var executor = new BudgetMutationExecutor(state.Store, state.Idempotency);
        var clock = timeProvider ?? TimeProvider.System;

        var draftCreate = new CreateBudgetDraftCommand(executor, ledgerClient, clock);
        var revisionActivate = new ActivateBudgetPlanRevisionCommand(executor, ledgerClient, clock);
        var revisionGet = new GetBudgetPlanRevisionQuery(state.Store, ledgerClient, clock);
        var revisionList = new ListBudgetPlanRevisionsQuery(state.Store, clock);
        var positionGet = new GetBudgetPositionQuery(state.Store, ledgerClient, clock);
        var insightsEvidence = new GetBudgetInsightEvidenceQuery(state.Store, ledgerClient, revisionGet, clock);

        var template = BudgetOperationModule.CreateDescriptorTemplates();
        var descriptors = template.Descriptors
            .Select(descriptor => descriptor with
            {
                HandlerFactory = (_, _) => CreateRuntimeHandler(
                    descriptor.OperationId,
                    draftCreate,
                    revisionActivate,
                    revisionGet,
                    revisionList,
                    positionGet,
                    insightsEvidence)
            })
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();

        var readCapability = new BudgetReadProjectionModule(template).CreateCapability();
        var operations = new BudgetOperationBundle(descriptors, readCapability, state);
        return new BudgetServices(operations, state, executor, ledgerClient, readCapability);
    }

    private static IOperationHandler CreateRuntimeHandler(
        string operationId,
        CreateBudgetDraftCommand draftCreate,
        ActivateBudgetPlanRevisionCommand revisionActivate,
        GetBudgetPlanRevisionQuery revisionGet,
        ListBudgetPlanRevisionsQuery revisionList,
        GetBudgetPositionQuery positionGet,
        GetBudgetInsightEvidenceQuery insightsEvidence) => operationId switch
    {
        BudgetOperationIds.DraftCreate => new BudgetDraftCreateOperationHandler(draftCreate),
        BudgetOperationIds.RevisionGet => new BudgetRevisionGetOperationHandler(revisionGet),
        BudgetOperationIds.RevisionList => new BudgetRevisionListOperationHandler(revisionList),
        BudgetOperationIds.RevisionActivate => new BudgetRevisionActivateOperationHandler(revisionActivate),
        BudgetOperationIds.PositionGet => new BudgetPositionGetOperationHandler(positionGet),
        BudgetOperationIds.InsightsEvidenceGet => new BudgetInsightsEvidenceGetOperationHandler(insightsEvidence),
        _ => new FoundationOperationHandler()
    };
}

/// <summary>Complete BUDGET public-contract composition produced by explicit registration.</summary>
[SupportedOSPlatform("linux")]
public sealed record BudgetServices(
    BudgetOperationBundle Operations,
    BudgetStateServices State,
    BudgetMutationExecutor Executor,
    LedgerContractClient LedgerClient,
    BudgetReadCapabilityDescriptor ReadCapability);

[SupportedOSPlatform("linux")]
internal sealed class BudgetDraftCreateOperationHandler(CreateBudgetDraftCommand command) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, BudgetJsonContext.Default.CreateDraftBudgetPlanInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
            }

            var result = await command.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, BudgetJsonContext.Default.CreateDraftBudgetPlanResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BudgetRevisionGetOperationHandler(GetBudgetPlanRevisionQuery query) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, BudgetJsonContext.Default.GetBudgetPlanRevisionInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
            }

            var result = await query.HandleAsync(input, request.Actor, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, BudgetJsonContext.Default.BudgetPlanRevisionDetail))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BudgetRevisionListOperationHandler(ListBudgetPlanRevisionsQuery query) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, BudgetJsonContext.Default.ListBudgetPlanRevisionsInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
            }

            var result = await query.HandleAsync(input, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, BudgetJsonContext.Default.ListBudgetPlanRevisionsResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BudgetRevisionActivateOperationHandler(ActivateBudgetPlanRevisionCommand command) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, BudgetJsonContext.Default.ActivateBudgetPlanRevisionInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
            }

            var result = await command.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, BudgetJsonContext.Default.ActivateBudgetPlanRevisionResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BudgetPositionGetOperationHandler(GetBudgetPositionQuery query) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, BudgetJsonContext.Default.GetBudgetPositionInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
            }

            var result = await query.HandleAsync(input, request.Actor, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, BudgetJsonContext.Default.GetBudgetPositionResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class BudgetInsightsEvidenceGetOperationHandler(GetBudgetInsightEvidenceQuery query) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(request.Input, BudgetJsonContext.Default.GetBudgetInsightEvidenceInput);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
            }

            var result = await query.HandleAsync(input, request.Actor, cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(result.Value!, BudgetJsonContext.Default.GetBudgetInsightEvidenceResult))
                : CommandResult<JsonElement>.Failure(result.ErrorCode!);
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
        catch (NotSupportedException)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }
    }
}
