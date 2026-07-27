using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.System;

namespace Tally.Features.Budget.Contract;

/// <summary>
/// Descriptor inventory for the six Public Budget Operations.
/// Handlers are pure contract stubs: no BudgetStateStore or Ledger reads
/// (FR-BUDGET-CONTRACT-DISCOVERY — discovery and unknown ops must not open data).
/// </summary>
public sealed class BudgetOperationModule
{
    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        new(
            BudgetOperationIds.DraftCreate,
            "tally budget plan draft create",
            "command",
            true,
            BudgetJsonContext.Default.CreateDraftBudgetPlanInput,
            BudgetJsonContext.Default.CreateDraftBudgetPlanResult,
            "BudgetOperationModule.DraftCreate",
            (_, _) => new BudgetStubHandler(BudgetOperationIds.DraftCreate, mutating: true),
            "tally budget plan draft create --input -",
            DraftCreateErrors),
        new(
            BudgetOperationIds.RevisionGet,
            "tally budget plan revision get",
            "query",
            false,
            BudgetJsonContext.Default.GetBudgetPlanRevisionInput,
            BudgetJsonContext.Default.BudgetPlanRevisionDetail,
            "BudgetOperationModule.RevisionGet",
            (_, _) => new BudgetStubHandler(BudgetOperationIds.RevisionGet, mutating: false),
            "tally budget plan revision get --input -",
            RevisionGetErrors),
        new(
            BudgetOperationIds.RevisionList,
            "tally budget plan revision list",
            "query",
            false,
            BudgetJsonContext.Default.ListBudgetPlanRevisionsInput,
            BudgetJsonContext.Default.ListBudgetPlanRevisionsResult,
            "BudgetOperationModule.RevisionList",
            (_, _) => new BudgetStubHandler(BudgetOperationIds.RevisionList, mutating: false),
            "tally budget plan revision list --input -",
            RevisionListErrors),
        new(
            BudgetOperationIds.RevisionActivate,
            "tally budget plan revision activate",
            "command",
            true,
            BudgetJsonContext.Default.ActivateBudgetPlanRevisionInput,
            BudgetJsonContext.Default.ActivateBudgetPlanRevisionResult,
            "BudgetOperationModule.RevisionActivate",
            (_, _) => new BudgetStubHandler(BudgetOperationIds.RevisionActivate, mutating: true),
            "tally budget plan revision activate --input -",
            RevisionActivateErrors),
        new(
            BudgetOperationIds.PositionGet,
            "tally budget position get",
            "query",
            false,
            BudgetJsonContext.Default.GetBudgetPositionInput,
            BudgetJsonContext.Default.GetBudgetPositionResult,
            "BudgetOperationModule.PositionGet",
            (_, _) => new BudgetStubHandler(BudgetOperationIds.PositionGet, mutating: false),
            "tally budget position get --input -",
            PositionGetErrors),
        new(
            BudgetOperationIds.InsightsEvidenceGet,
            "tally budget insights evidence get",
            "query",
            false,
            BudgetJsonContext.Default.GetBudgetInsightEvidenceInput,
            BudgetJsonContext.Default.GetBudgetInsightEvidenceResult,
            "BudgetOperationModule.InsightsEvidenceGet",
            (_, _) => new BudgetStubHandler(BudgetOperationIds.InsightsEvidenceGet, mutating: false),
            "tally budget insights evidence get --input -",
            InsightsEvidenceErrors)
    ];

    /// <summary>Template descriptors for schema discovery without runtime stores.</summary>
    public static BudgetOperationModule CreateDescriptorTemplates() => new();

    private static readonly IReadOnlyList<ErrorSchema> DraftCreateErrors =
    [
        new(BudgetErrors.InvalidInput, "validation", 3),
        new(BudgetErrors.InvalidPeriod, "validation", 3),
        new(BudgetErrors.InvalidAmount, "validation", 3),
        new(BudgetErrors.ActorRequired, "validation", 3),
        new(BudgetErrors.IdempotencyRequired, "validation", 3),
        new(BudgetErrors.CategoryUnknown, "not_found", 4),
        new(BudgetErrors.CategoryInactive, "lifecycle", 6),
        new(BudgetErrors.Conflict, "conflict", 5),
        new(BudgetErrors.IdempotencyConflict, "conflict", 5),
        new(BudgetErrors.LedgerIncompatible, "compatibility", 7),
        new(BudgetErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> RevisionGetErrors =
    [
        new(BudgetErrors.InvalidInput, "validation", 3),
        new(BudgetErrors.RevisionNotFound, "not_found", 4),
        new(BudgetErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> RevisionListErrors =
    [
        new(BudgetErrors.InvalidInput, "validation", 3),
        new(BudgetErrors.InvalidPeriod, "validation", 3),
        new(BudgetErrors.ResourceLimit, "host", 9),
        new(BudgetErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> RevisionActivateErrors =
    [
        new(BudgetErrors.InvalidInput, "validation", 3),
        new(BudgetErrors.ActorRequired, "validation", 3),
        new(BudgetErrors.IdempotencyRequired, "validation", 3),
        new(BudgetErrors.RevisionNotFound, "not_found", 4),
        new(BudgetErrors.CategoryInactive, "lifecycle", 6),
        new(BudgetErrors.CategoryUnknown, "not_found", 4),
        new(BudgetErrors.Conflict, "conflict", 5),
        new(BudgetErrors.IdempotencyConflict, "conflict", 5),
        new(BudgetErrors.LedgerIncompatible, "compatibility", 7),
        new(BudgetErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> PositionGetErrors =
    [
        new(BudgetErrors.InvalidInput, "validation", 3),
        new(BudgetErrors.InvalidPeriod, "validation", 3),
        new(BudgetErrors.RevisionNotFound, "not_found", 4),
        new(BudgetErrors.RevisionPeriodMismatch, "validation", 3),
        new(BudgetErrors.NoActiveBudgetPlanRevision, "lifecycle", 6),
        new(BudgetErrors.LedgerUnavailable, "host", 9),
        new(BudgetErrors.LedgerIncompatible, "compatibility", 7),
        new(BudgetErrors.SourceStateChanged, "conflict", 5),
        new(BudgetErrors.Integrity, "integrity", 8),
        new(BudgetErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> InsightsEvidenceErrors =
    [
        new(BudgetErrors.InvalidInput, "validation", 3),
        new(BudgetErrors.InvalidPeriod, "validation", 3),
        new(BudgetErrors.RevisionNotFound, "not_found", 4),
        new(BudgetErrors.ResourceLimit, "host", 9),
        new(BudgetErrors.SourceStateChanged, "conflict", 5),
        new(BudgetErrors.LedgerUnavailable, "host", 9),
        new(BudgetErrors.LedgerIncompatible, "compatibility", 7),
        new(BudgetErrors.Integrity, "integrity", 8),
        new(BudgetErrors.Unexpected, "host", 10)
    ];
}

/// <summary>
/// Contract-only stub: validates envelope/version requirements and never opens stores.
/// Real handlers land in later feature beads.
/// </summary>
internal sealed class BudgetStubHandler(string operationId, bool mutating) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Actor is null)
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(BudgetErrors.ActorRequired));
        }

        if (mutating && string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(BudgetErrors.IdempotencyRequired));
        }

        try
        {
            return Task.FromResult(ValidateVersion(request.Input, operationId));
        }
        catch (JsonException)
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput));
        }
        catch (NotSupportedException)
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput));
        }
    }

    private static CommandResult<JsonElement> ValidateVersion(JsonElement input, string operationId)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }

        if (!input.TryGetProperty("contractVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String
            || !BudgetContractMapper.IsSupportedContractVersion(versionElement.GetString()))
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.UnsupportedVersion);
        }

        // Unknown-field rejection is enforced by source-generated deserialize with UnmappedMemberHandling.Disallow.
        object? typed = operationId switch
        {
            BudgetOperationIds.DraftCreate => JsonSerializer.Deserialize(input, BudgetJsonContext.Default.CreateDraftBudgetPlanInput),
            BudgetOperationIds.RevisionGet => JsonSerializer.Deserialize(input, BudgetJsonContext.Default.GetBudgetPlanRevisionInput),
            BudgetOperationIds.RevisionList => JsonSerializer.Deserialize(input, BudgetJsonContext.Default.ListBudgetPlanRevisionsInput),
            BudgetOperationIds.RevisionActivate => JsonSerializer.Deserialize(input, BudgetJsonContext.Default.ActivateBudgetPlanRevisionInput),
            BudgetOperationIds.PositionGet => JsonSerializer.Deserialize(input, BudgetJsonContext.Default.GetBudgetPositionInput),
            BudgetOperationIds.InsightsEvidenceGet => JsonSerializer.Deserialize(input, BudgetJsonContext.Default.GetBudgetInsightEvidenceInput),
            _ => null
        };

        if (typed is null)
        {
            return CommandResult<JsonElement>.Failure(BudgetErrors.InvalidInput);
        }

        // No storage/Ledger side effects in the contract foundation bead.
        return CommandResult<JsonElement>.Failure(BudgetErrors.NotFound);
    }
}
