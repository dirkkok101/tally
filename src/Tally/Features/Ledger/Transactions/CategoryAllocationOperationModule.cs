using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Transactions;
using Tally.Contracts.System;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Categories;

namespace Tally.Features.Ledger.Transactions;

/// <summary>
/// LEDGER-owned category assignment/correction surface with released classification mutation contract
/// (DM-CLASSIFY-LEDGER-PROJECTION-CONTRACT). Descriptor release and version gate live here — not an
/// unadvertised handler constant.
/// </summary>
public sealed class CategoryAllocationOperationModule(AssignCategoryHandler assign, CorrectCategoryHandler correct)
{
    public const string AssignOperationId = "ledger.transaction.category.assign";
    public const string CorrectOperationId = "ledger.transaction.category.correct";

    /// <summary>
    /// Released classification mutation contract version advertised on assign/correct descriptors.
    /// </summary>
    public const string MutationContractVersion = CategoryAllocationMutationVersions.ClassificationV1;

    public IReadOnlyList<OperationDescriptor> Descriptors { get; } =
    [
        CreateDescriptor(AssignOperationId, LedgerJsonContext.Default.AssignCategoryInput, "Assign"),
        CreateDescriptor(CorrectOperationId, LedgerJsonContext.Default.CorrectCategoryInput, "Correct")
    ];

    public async Task<CommandResult<JsonElement>> HandleAsync(
        string operationId,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return operationId switch
            {
                AssignOperationId => await DispatchAssignAsync(request, cancellationToken),
                CorrectOperationId => await DispatchCorrectAsync(request, cancellationToken),
                _ => CommandResult<JsonElement>.Failure("operation.not_found")
            };
        }
        catch (JsonException)
        {
            return CommandResult<JsonElement>.Failure(CategoryAllocation.InvalidError);
        }
    }

    private async Task<CommandResult<JsonElement>> DispatchAssignAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.AssignCategoryInput);
        if (input is null) return CommandResult<JsonElement>.Failure(CategoryAllocation.InvalidError);

        // Version gate before any financial-data read or mutation.
        if (!IsCompatibleMutationContract(input.MutationContractVersion))
        {
            return CommandResult<JsonElement>.Failure(CategoryAllocationErrors.ContractMismatch);
        }

        return await assign.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
    }

    private async Task<CommandResult<JsonElement>> DispatchCorrectAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CorrectCategoryInput);
        if (input is null) return CommandResult<JsonElement>.Failure(CategoryAllocation.InvalidError);

        // Version gate before any financial-data read or mutation.
        if (!IsCompatibleMutationContract(input.MutationContractVersion))
        {
            return CommandResult<JsonElement>.Failure(CategoryAllocationErrors.ContractMismatch);
        }

        return await correct.HandleAsync(input, request.Actor, request.IdempotencyKey, cancellationToken);
    }

    /// <summary>
    /// Null preserves the legacy envelope path (assign without classification preconditions).
    /// Any non-null value must equal the released classification_v1 mutation contract.
    /// </summary>
    public static bool IsCompatibleMutationContract(string? mutationContractVersion) =>
        mutationContractVersion is null
        || string.Equals(mutationContractVersion, MutationContractVersion, StringComparison.Ordinal);

    private static OperationDescriptor CreateDescriptor(string operationId, JsonTypeInfo request, string target) => new(
        operationId,
        "tally " + operationId.Replace('.', ' '),
        "mutation",
        true,
        request,
        LedgerJsonContext.Default.CategoryAllocationResult,
        "CategoryAllocationOperationModule." + target,
        (services, _) => services.CategoryAllocations is { } module
            ? new CategoryAllocationOperationHandler(module, operationId)
            : new FoundationOperationHandler(),
        "tally " + operationId.Replace('.', ' ')
            + " --input -  (mutationContractVersion=" + MutationContractVersion + ")",
        DomainErrors(operationId));

    private static IReadOnlyList<ErrorSchema> DomainErrors(string operationId) =>
    [
        new(CategoryAllocation.InvalidError, "validation", 3),
        new(TransactionErrors.NotFound, "not_found", 4),
        new(CategoryErrors.NotFound, "not_found", 4),
        new(CategoryAllocationErrors.TransactionInactive, "lifecycle", 6),
        new(CategoryErrors.Archived, "lifecycle", 6),
        new(CategoryAllocationErrors.Cardinality, "conflict", 5),
        new(CategoryAllocationErrors.StalePrecondition, "conflict", 5),
        new(CategoryAllocationErrors.ContractMismatch, "compatibility", 7),
        .. (operationId.EndsWith(".correct", StringComparison.Ordinal)
            ? new ErrorSchema[]
            {
                new(CategoryAllocationErrors.NotAssigned, "lifecycle", 6),
                new(CategoryAllocationErrors.Unchanged, "conflict", 5)
            }
            : [])
    ];
}

internal sealed class CategoryAllocationOperationHandler(CategoryAllocationOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) =>
        module.HandleAsync(operationId, request, cancellationToken);
}
