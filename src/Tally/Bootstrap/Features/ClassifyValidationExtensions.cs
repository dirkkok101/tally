using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Integration.Ledger;

namespace Tally.Bootstrap.Features;

/// <summary>
/// Explicit validation-only CLASSIFY composition root (no reflection / plugin scan).
/// Provisional bridge for bd-56yx: publish twelve descriptor templates for discovery,
/// route only <c>classify.rule.validate</c> to <see cref="ValidateClassificationRuleCommand"/>.
/// Full twelve-handler convergence remains bd-3g6y.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyValidationBundle
{
    public ClassifyValidationBundle(
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
    /// Descriptor-only bundle for registry inventory (handlers not executed for discovery).
    /// Reuses <see cref="ClassifyOperationModule"/> contract stubs — no classify.db / corpus opens.
    /// </summary>
    public static ClassifyValidationBundle CreateDescriptorTemplates()
    {
        var module = ClassifyOperationModule.CreateDescriptorTemplates();
        return new ClassifyValidationBundle(module.Descriptors);
    }

    /// <summary>
    /// Validation-only runtime composition: owner-only CLASSIFY state, private corpus reader,
    /// and a real adapter for <c>classify.rule.validate</c> only. All other CLASSIFY operations
    /// remain explicit fail-closed stubs until bd-3g6y.
    /// </summary>
    public static async Task<ClassifyValidationServices> CreateServicesAsync(
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
        var corpusReader = ClassifyCorpusExtensions.CreateReader();
        var clock = timeProvider ?? TimeProvider.System;
        var validate = new ValidateClassificationRuleCommand(
            state.Store,
            ruleStore,
            validationStore,
            corpusReader,
            ledgerClient,
            state.Idempotency,
            clock);

        var template = ClassifyOperationModule.CreateDescriptorTemplates();
        var descriptors = template.Descriptors
            .Select(descriptor => string.Equals(
                    descriptor.OperationId,
                    ClassifyOperationIds.RuleValidate,
                    StringComparison.Ordinal)
                ? descriptor with
                {
                    HandlerFactory = (_, _) => new ClassifyRuleValidateOperationHandler(validate)
                }
                : descriptor)
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .ToArray();

        var operations = new ClassifyValidationBundle(descriptors, state);
        return new ClassifyValidationServices(operations, state, validate, ledgerClient);
    }
}

/// <summary>Validation-only CLASSIFY composition produced by explicit registration.</summary>
[SupportedOSPlatform("linux")]
public sealed record ClassifyValidationServices(
    ClassifyValidationBundle Operations,
    ClassifyStateServices State,
    ValidateClassificationRuleCommand Validate,
    LedgerContractClient LedgerClient);

/// <summary>
/// Source-generated adapter: public process envelope → <see cref="ValidateClassificationRuleCommand"/>.
/// Never opens stores beyond the command; never logs private paths or payloads.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class ClassifyRuleValidateOperationHandler(ValidateClassificationRuleCommand command) : IOperationHandler
{
    public async Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(
                request.Input,
                ClassifyJsonContext.Default.ClassifyRuleValidateRequest);
            if (input is null)
            {
                return CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput);
            }

            var result = await command.HandleAsync(
                input,
                request.Actor,
                request.IdempotencyKey,
                cancellationToken);
            return result.IsSuccess
                ? CommandResult<JsonElement>.Success(
                    JsonSerializer.SerializeToElement(
                        result.Value!,
                        ClassifyJsonContext.Default.ClassifyRuleValidateResult))
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
