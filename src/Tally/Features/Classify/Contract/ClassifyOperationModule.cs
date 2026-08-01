using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Tally.Infrastructure.Classify.Corpus;

namespace Tally.Features.Classify.Contract;

/// <summary>
/// Feature-local descriptor inventory for the twelve Public CLASSIFY Operations (C12).
/// Handlers are pure contract stubs: no classify.db, corpus, or Ledger reads
/// (FR-CLASSIFY-CONTRACT-DISCOVERY — discovery and unknown ops must not open data).
/// Limits are attached here; shared registry/schema wiring is owned by bd-3g6y.
/// </summary>
public sealed class ClassifyOperationModule
{
    /// <summary>NFR-CLASSIFY-BOUNDED-EVALUATION / C11 published CLASSIFY v1 bounds.</summary>
    public static class V1Limits
    {
        public const long MaxTransactionCount = 10_000;
        public const long MaxRuleCount = 500;
        public const long MaxEvidenceRowCount = 100_000;
        public const long MaxCorpusRowCount = 10_000;
        public const long MaxMemoryBytes = 256L * 1024 * 1024;
        public const long MaxProcessingTimeMs = 5_000;

        public static OperationLimits Evaluation { get; } = new(
            MaxTransactionCount,
            MaxRuleCount,
            MaxEvidenceRowCount,
            MaxCorpusRowCount: OperationLimits.NotApplicable,
            MaxMemoryBytes,
            MaxProcessingTimeMs);

        public static OperationLimits RuleValidation { get; } = new(
            MaxTransactionCount: OperationLimits.NotApplicable,
            MaxRuleCount,
            MaxEvidenceRowCount,
            MaxCorpusRowCount,
            MaxMemoryBytes,
            MaxProcessingTimeMs);

        public static OperationLimits Apply { get; } = new(
            MaxTransactionCount,
            MaxRuleCount,
            MaxEvidenceRowCount: OperationLimits.NotApplicable,
            MaxCorpusRowCount: OperationLimits.NotApplicable,
            MaxMemoryBytes,
            MaxProcessingTimeMs);

        public static OperationLimits RuleMutation { get; } = new(
            MaxTransactionCount: OperationLimits.NotApplicable,
            MaxRuleCount,
            MaxEvidenceRowCount: OperationLimits.NotApplicable,
            MaxCorpusRowCount: OperationLimits.NotApplicable,
            MaxMemoryBytes,
            MaxProcessingTimeMs);

        public static OperationLimits Read { get; } = new(
            MaxTransactionCount: 1,
            MaxRuleCount: OperationLimits.NotApplicable,
            MaxEvidenceRowCount: OperationLimits.NotApplicable,
            MaxCorpusRowCount: OperationLimits.NotApplicable,
            MaxMemoryBytes,
            MaxProcessingTimeMs);

        public static OperationLimits Maintenance { get; } = new(
            MaxTransactionCount: OperationLimits.NotApplicable,
            MaxRuleCount: OperationLimits.NotApplicable,
            MaxEvidenceRowCount: OperationLimits.NotApplicable,
            MaxCorpusRowCount: OperationLimits.NotApplicable,
            MaxMemoryBytes,
            MaxProcessingTimeMs);
    }

    public IReadOnlyList<ClassifyPublishedOperation> Operations { get; } =
    [
        Publish(
            ClassifyOperationIds.Evaluate,
            "tally classify evaluate",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyEvaluateRequest,
            ClassifyJsonContext.Default.ClassifyEvaluateResult,
            "Evaluate",
            V1Limits.Evaluation,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.OutcomeGet,
            "tally classify outcome get",
            "query",
            requiresIdempotency: false,
            ClassifyJsonContext.Default.ClassifyOutcomeGetRequest,
            ClassifyJsonContext.Default.ClassifyOutcomeGetResult,
            "OutcomeGet",
            V1Limits.Read,
            OutcomeGetErrors),
        Publish(
            ClassifyOperationIds.ApplyPreview,
            "tally classify apply preview",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyApplyPreviewRequest,
            ClassifyJsonContext.Default.ClassifyApplyPreviewResult,
            "ApplyPreview",
            V1Limits.Apply,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.ApplyRun,
            "tally classify apply run",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyApplyRunRequest,
            ClassifyJsonContext.Default.ClassifyApplyRunResult,
            "ApplyRun",
            V1Limits.Apply,
            ApplyRunErrors),
        Publish(
            ClassifyOperationIds.RuleSave,
            "tally classify rule save",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyRuleSaveRequest,
            ClassifyJsonContext.Default.ClassifyRuleSaveResult,
            "RuleSave",
            V1Limits.RuleMutation,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.RuleValidate,
            "tally classify rule validate",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyRuleValidateRequest,
            ClassifyJsonContext.Default.ClassifyRuleValidateResult,
            "RuleValidate",
            V1Limits.RuleValidation,
            RuleValidateErrors),
        Publish(
            ClassifyOperationIds.RuleActivate,
            "tally classify rule activate",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyRuleActivateRequest,
            ClassifyJsonContext.Default.ClassifyRuleActivateResult,
            "RuleActivate",
            V1Limits.RuleMutation,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.RuleRetire,
            "tally classify rule retire",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyRuleRetireRequest,
            ClassifyJsonContext.Default.ClassifyRuleRetireResult,
            "RuleRetire",
            V1Limits.RuleMutation,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.FeedbackRecord,
            "tally classify feedback record",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyFeedbackRecordRequest,
            ClassifyJsonContext.Default.ClassifyFeedbackRecordResult,
            "FeedbackRecord",
            V1Limits.Maintenance,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.Status,
            "tally classify status",
            "query",
            requiresIdempotency: false,
            ClassifyJsonContext.Default.ClassifyStatusRequest,
            ClassifyJsonContext.Default.ClassifyStatusResult,
            "Status",
            V1Limits.Read,
            StatusErrors),
        Publish(
            ClassifyOperationIds.Abandon,
            "tally classify abandon",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyAbandonRequest,
            ClassifyJsonContext.Default.ClassifyAbandonResult,
            "Abandon",
            V1Limits.Maintenance,
            CommonMutationErrors),
        Publish(
            ClassifyOperationIds.Cleanup,
            "tally classify cleanup",
            "mutation",
            requiresIdempotency: true,
            ClassifyJsonContext.Default.ClassifyCleanupRequest,
            ClassifyJsonContext.Default.ClassifyCleanupResult,
            "Cleanup",
            V1Limits.Maintenance,
            CommonMutationErrors)
    ];

    public IReadOnlyList<OperationDescriptor> Descriptors =>
        Operations.Select(operation => operation.Descriptor).ToArray();

    /// <summary>Template descriptors for schema discovery without runtime stores.</summary>
    public static ClassifyOperationModule CreateDescriptorTemplates() => new();

    public OperationLimits LimitsFor(string operationId) =>
        Operations.Single(operation => operation.Descriptor.OperationId == operationId).Limits;

    private static ClassifyPublishedOperation Publish(
        string operationId,
        string cliPath,
        string kind,
        bool requiresIdempotency,
        JsonTypeInfo request,
        JsonTypeInfo result,
        string target,
        OperationLimits limits,
        IReadOnlyList<ErrorSchema> errors) =>
        new(
            new OperationDescriptor(
                operationId,
                cliPath,
                kind,
                requiresIdempotency,
                request,
                result,
                "ClassifyOperationModule." + target,
                (_, _) => new ClassifyStubOperationHandler(operationId, requiresIdempotency),
                cliPath + " --input -",
                errors),
            limits);

    private static readonly IReadOnlyList<ErrorSchema> CommonMutationErrors =
    [
        new(ClassifyErrors.InvalidInput, "validation", 3),
        new(ClassifyErrors.ActorRequired, "validation", 3),
        new(ClassifyErrors.IdempotencyRequired, "validation", 3),
        new(ClassifyErrors.SelectionInvalid, "validation", 3),
        new(ClassifyErrors.NotFound, "not_found", 4),
        new(ClassifyErrors.Conflict, "conflict", 5),
        new(ClassifyErrors.IdempotencyConflict, "conflict", 5),
        new(ClassifyErrors.Stale, "conflict", 5),
        new(ClassifyErrors.Lifecycle, "lifecycle", 6),
        new(ClassifyErrors.ResourceLimit, "host", 9),
        new(ClassifyErrors.UnsupportedVersion, "compatibility", 7),
        new(ClassifyErrors.LedgerUnavailable, "host", 9),
        new(ClassifyErrors.LedgerIncompatible, "compatibility", 7),
        new(ClassifyErrors.Integrity, "integrity", 8),
        new(ClassifyErrors.Unexpected, "host", 10)
    ];

    /// <summary>
    /// classify.rule.validate is the only C12 surface that opens a private corpus.
    /// Publish the concrete PrivateCorpusErrors the validate handler can emit so the process
    /// envelope maps them to stable module-scoped codes/categories/exits (not host.unexpected).
    /// Other operations keep CommonMutationErrors unchanged.
    /// </summary>
    private static readonly IReadOnlyList<ErrorSchema> RuleValidateErrors =
    [
        ..CommonMutationErrors,
        new(PrivateCorpusErrors.PathRequired, "validation", 3),
        new(PrivateCorpusErrors.NotFound, "not_found", 4),
        new(PrivateCorpusErrors.SymlinkRejected, "validation", 3),
        new(PrivateCorpusErrors.OwnerRejected, "validation", 3),
        new(PrivateCorpusErrors.PermissionsRejected, "validation", 3),
        new(PrivateCorpusErrors.NotRegularFile, "validation", 3),
        new(PrivateCorpusErrors.Malformed, "validation", 3),
        new(PrivateCorpusErrors.DuplicateOrdinal, "validation", 3),
        new(PrivateCorpusErrors.FieldInvalid, "validation", 3),
        new(PrivateCorpusErrors.Cancelled, "lifecycle", 6),
        new(PrivateCorpusErrors.ReadFailed, "host", 9)
    ];

    private static readonly IReadOnlyList<ErrorSchema> OutcomeGetErrors =
    [
        new(ClassifyErrors.InvalidInput, "validation", 3),
        new(ClassifyErrors.ActorRequired, "validation", 3),
        new(ClassifyErrors.EvaluationNotFound, "not_found", 4),
        new(ClassifyErrors.OutcomeNotFound, "not_found", 4),
        new(ClassifyErrors.Stale, "conflict", 5),
        new(ClassifyErrors.UnsupportedVersion, "compatibility", 7),
        new(ClassifyErrors.LedgerUnavailable, "host", 9),
        new(ClassifyErrors.LedgerIncompatible, "compatibility", 7),
        new(ClassifyErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> ApplyRunErrors =
    [
        new(ClassifyErrors.InvalidInput, "validation", 3),
        new(ClassifyErrors.ActorRequired, "validation", 3),
        new(ClassifyErrors.IdempotencyRequired, "validation", 3),
        new(ClassifyErrors.PreviewNotFound, "not_found", 4),
        new(ClassifyErrors.Stale, "conflict", 5),
        new(ClassifyErrors.Conflict, "conflict", 5),
        new(ClassifyErrors.IdempotencyConflict, "conflict", 5),
        new(ClassifyErrors.Lifecycle, "lifecycle", 6),
        new(ClassifyErrors.ResourceLimit, "host", 9),
        new(ClassifyErrors.UnsupportedVersion, "compatibility", 7),
        new(ClassifyErrors.LedgerUnavailable, "host", 9),
        new(ClassifyErrors.LedgerIncompatible, "compatibility", 7),
        new(ClassifyErrors.Integrity, "integrity", 8),
        new(ClassifyErrors.Unexpected, "host", 10)
    ];

    private static readonly IReadOnlyList<ErrorSchema> StatusErrors =
    [
        new(ClassifyErrors.InvalidInput, "validation", 3),
        new(ClassifyErrors.ActorRequired, "validation", 3),
        new(ClassifyErrors.NotFound, "not_found", 4),
        new(ClassifyErrors.UnsupportedVersion, "compatibility", 7),
        new(ClassifyErrors.Unexpected, "host", 10)
    ];
}

/// <summary>
/// Feature-local published operation: shared <see cref="OperationDescriptor"/> plus deterministic
/// <see cref="OperationLimits"/>. Registry schema merge of limits is owned by bd-3g6y.
/// </summary>
public sealed record ClassifyPublishedOperation(OperationDescriptor Descriptor, OperationLimits Limits);

/// <summary>
/// Contract-only stub: validates envelope/version requirements and never opens stores.
/// Real handlers land in later feature beads.
/// </summary>
internal sealed class ClassifyStubOperationHandler(string operationId, bool mutating) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Actor is null)
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(ClassifyErrors.ActorRequired));
        }

        if (mutating && string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(ClassifyErrors.IdempotencyRequired));
        }

        try
        {
            return Task.FromResult(ValidateVersion(request.Input, operationId));
        }
        catch (JsonException)
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput));
        }
        catch (NotSupportedException)
        {
            return Task.FromResult(CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput));
        }
    }

    private static CommandResult<JsonElement> ValidateVersion(JsonElement input, string operationId)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput);
        }

        if (!input.TryGetProperty("contractVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String
            || !ClassifyContractMapper.IsSupportedContractVersion(versionElement.GetString()))
        {
            return CommandResult<JsonElement>.Failure(ClassifyErrors.UnsupportedVersion);
        }

        // Unknown-field rejection is enforced by source-generated deserialize with UnmappedMemberHandling.Disallow.
        object? typed = operationId switch
        {
            ClassifyOperationIds.Evaluate => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyEvaluateRequest),
            ClassifyOperationIds.OutcomeGet => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyOutcomeGetRequest),
            ClassifyOperationIds.ApplyPreview => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyApplyPreviewRequest),
            ClassifyOperationIds.ApplyRun => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyApplyRunRequest),
            ClassifyOperationIds.RuleSave => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyRuleSaveRequest),
            ClassifyOperationIds.RuleValidate => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyRuleValidateRequest),
            ClassifyOperationIds.RuleActivate => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyRuleActivateRequest),
            ClassifyOperationIds.RuleRetire => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyRuleRetireRequest),
            ClassifyOperationIds.FeedbackRecord => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyFeedbackRecordRequest),
            ClassifyOperationIds.Status => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyStatusRequest),
            ClassifyOperationIds.Abandon => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyAbandonRequest),
            ClassifyOperationIds.Cleanup => JsonSerializer.Deserialize(input, ClassifyJsonContext.Default.ClassifyCleanupRequest),
            _ => null
        };

        if (typed is null)
        {
            return CommandResult<JsonElement>.Failure(ClassifyErrors.InvalidInput);
        }

        if (typed is ClassifyApplyPreviewRequest preview
            && !ClassifyContractMapper.TryValidateApplySelection(preview.Selection, out var selectionError))
        {
            return CommandResult<JsonElement>.Failure(selectionError!);
        }

        // No storage/Ledger side effects in the contract foundation bead.
        return CommandResult<JsonElement>.Failure(ClassifyErrors.NotFound);
    }
}
