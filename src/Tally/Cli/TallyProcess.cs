using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Contracts.Budget;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Recovery;
using Tally.Contracts.System;
using Tally.Features.Ingest.Commit;
using Tally.Features.Ingest.Preview;
using Tally.Features.Ingest.Recovery;
using Tally.Features.Ingest.Review;

namespace Tally.Cli;

public sealed class TallyProcess(OperationRegistry registry, LedgerServices? configuredServices = null)
{
    private readonly LedgerServices services = configuredServices ?? LedgerServices.Create();

    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = ExtractInput(arguments);
            if (selection.ErrorCode is not null) return Error(2, selection.ErrorCode, "usage", "The input path must be '-' or '@file'.");
            var invocation = Resolve(selection.Arguments);
            if (invocation.ErrorCode is not null) return Error(invocation.ExitCode, invocation.ErrorCode, invocation.Category!, invocation.Message!);
            if (invocation.UseRequestInput && !selection.HasInput) return Error(3, "validation.invalid_input", "validation", "Input does not match the published schema.");
            var input = await ReadInputAsync(selection, standardInput, cancellationToken);
            var requestEnvelope = selection.HasInput ? ReadRequest(input) : null;
            if (selection.HasInput && !ValidRequest(requestEnvelope, invocation.Descriptor!)) return Error(3, "validation.invalid_input", "validation", "Input does not match the published schema.");
            var handler = invocation.Descriptor!.HandlerFactory(services, registry);
            var request = new OperationRequest(invocation.UseRequestInput ? requestEnvelope!.Input : invocation.HandlerInput, requestEnvelope?.Actor, requestEnvelope?.IdempotencyKey);
            var result = await handler.HandleAsync(request, cancellationToken);
            var operationId = invocation.Descriptor.OperationId;
            if (operationId.StartsWith("classify.", StringComparison.Ordinal))
            {
                var correlation = ResolveCorrelationRef(requestEnvelope);
                return result.IsSuccess
                    ? ClassifySuccess(operationId, result.Value!, correlation)
                    : ClassifyErrorForHandler(result.ErrorCode!, invocation.Descriptor, correlation);
            }

            return result.IsSuccess
                ? Success(operationId, result.Value!)
                : ErrorForHandler(result.ErrorCode!, invocation.Descriptor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return UnexpectedFailure(); }
    }

    private Invocation Resolve(IReadOnlyList<string> arguments) => arguments switch
    {
        ["version"] => Invocation.For(registry.Find("system.version")!),
        ["help"] or ["schema", "list"] => Invocation.For(registry.Find("system.schema.list")!),
        ["schema", "show", var operationId] when registry.Find(operationId) is not null => Invocation.For(registry.Find("system.schema.show")!, JsonSerializer.SerializeToElement(new SchemaShowRequest(operationId), LedgerJsonContext.Default.SchemaShowRequest)),
        ["schema", "show", _] => Invocation.Error(4, "operation.not_found", "not_found", "The requested operation is not part of the public contract."),
        _ when registry.FindByArguments(arguments) is { } descriptor => Invocation.For(descriptor, useRequestInput: true),
        _ => Invocation.Error(2, "operation.unknown", "usage", "The requested operation is not part of the public contract.")
    };

    private static InputSelection ExtractInput(IReadOnlyList<string> arguments)
    {
        var index = Enumerable.Range(0, arguments.Count).FirstOrDefault(i => arguments[i] == "--input", -1);
        if (index < 0) return new(arguments, null, false, null);
        if (index + 1 != arguments.Count - 1) return new(arguments, null, true, "usage.invalid_input_path");
        var inputPath = arguments[index + 1];
        if (inputPath != "-" && (!inputPath.StartsWith('@') || inputPath.Length == 1)) return new(arguments, null, true, "usage.invalid_input_path");
        return new(arguments.Take(index).ToArray(), inputPath, true, null);
    }

    private static async Task<string?> ReadInputAsync(InputSelection selection, string? standardInput, CancellationToken cancellationToken) => selection.InputPath switch
    {
        null => standardInput,
        "-" => standardInput,
        var path => await File.ReadAllTextAsync(path![1..], cancellationToken)
    };

    private static RequestEnvelope? ReadRequest(string? input)
    {
        if (input is null) return null;
        try { return JsonSerializer.Deserialize(input!, LedgerJsonContext.Default.RequestEnvelope); }
        catch (JsonException) { return null; }
    }

    private static bool ValidRequest(RequestEnvelope? request, OperationDescriptor descriptor)
    {
        try
        {
            return request is not null && request.ContractVersion == "1.0"
                && request.Actor is { Kind: "automation" or "human" or "system" }
                && IsSafeLabel(request.Actor.Label)
                && (request.Actor.RunId is null || IsSafeLabel(request.Actor.RunId))
                && request.Input.ValueKind == JsonValueKind.Object
                && JsonSerializer.Deserialize(request.Input, descriptor.RequestTypeInfo) is not null
                && (descriptor.RequiresIdempotencyKey ? !string.IsNullOrWhiteSpace(request.IdempotencyKey) : request.IdempotencyKey is null);
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsSafeLabel(string value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private static ProcessResult Success(string operationId, JsonElement result) => new(0, JsonSerializer.Serialize(new ResultEnvelope("1.0", operationId, "success", result, null), LedgerJsonContext.Default.ResultEnvelope), string.Empty);
    private static ProcessResult Error(int exitCode, string code, string category, string message) => new(exitCode, JsonSerializer.Serialize(new ResultEnvelope("1.0", "system.process", "error", null, new ProcessError(code, category, message)), LedgerJsonContext.Default.ResultEnvelope), "tally: " + code);
    public static ProcessResult UnexpectedFailure() => Error(10, "host.unexpected", "host", "The operation could not be completed.");

    /// <summary>
    /// CLASSIFY typed envelope: contract_version, operation_id, outcome, result_or_error, correlation_ref.
    /// Non-CLASSIFY paths continue to emit the established ResultEnvelope bytes.
    /// </summary>
    private static ProcessResult ClassifySuccess(
        string operationId,
        JsonElement result,
        string? correlationRef)
    {
        var envelope = new ClassifyResultEnvelope(
            "1.0",
            operationId,
            "success",
            result,
            correlationRef);
        var stdout = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.ClassifyResultEnvelope);
        var stderr = string.IsNullOrWhiteSpace(correlationRef)
            ? string.Empty
            : "tally: classify correlation_ref=" + correlationRef;
        return new ProcessResult(0, stdout, stderr);
    }

    private static ProcessResult ClassifyError(
        int exitCode,
        string code,
        string category,
        string message,
        string operationId,
        string? correlationRef)
    {
        var error = new ProcessError(code, category, message);
        var errorElement = JsonSerializer.SerializeToElement(error, LedgerJsonContext.Default.ProcessError);
        var envelope = new ClassifyResultEnvelope(
            "1.0",
            operationId,
            "error",
            errorElement,
            correlationRef);
        var stdout = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.ClassifyResultEnvelope);
        var stderr = string.IsNullOrWhiteSpace(correlationRef)
            ? "tally: " + code
            : "tally: " + code + " correlation_ref=" + correlationRef + " operation_id=" + operationId;
        return new ProcessResult(exitCode, stdout, stderr);
    }

    private static ProcessResult ClassifyErrorForHandler(
        string code,
        OperationDescriptor? descriptor,
        string? correlationRef)
    {
        var operationId = descriptor?.OperationId ?? "classify";
        if (descriptor?.DomainErrors?.FirstOrDefault(declared =>
                string.Equals(declared.Code, code, StringComparison.Ordinal)) is { } schema)
        {
            return ClassifyError(
                schema.ExitCode,
                code,
                schema.Category,
                CategoryMessage(schema.Category),
                operationId,
                correlationRef);
        }

        // Fall through to shared fallback mapping, then re-wrap as CLASSIFY envelope.
        var fallback = FallbackErrorForHandler(code);
        try
        {
            var legacy = JsonSerializer.Deserialize(fallback.Stdout, LedgerJsonContext.Default.ResultEnvelope);
            if (legacy?.Error is not null)
            {
                return ClassifyError(
                    fallback.ExitCode,
                    legacy.Error.Code,
                    legacy.Error.Category,
                    legacy.Error.Message,
                    operationId,
                    correlationRef);
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return ClassifyError(10, "host.unexpected", "host", "The operation could not be completed.", operationId, correlationRef);
    }

    private static string? ResolveCorrelationRef(RequestEnvelope? request)
    {
        if (request is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.CorrelationRef))
        {
            return request.CorrelationRef.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return request.IdempotencyKey.Trim();
        }

        return string.IsNullOrWhiteSpace(request.Actor.RunId) ? null : request.Actor.RunId.Trim();
    }

    // The invoked descriptor's declared DomainErrors are the published contract source
    // (DD-LEDGER/INGEST/BUDGET-CLI-OPERATION-CONTRACT: codes, exits, and categories are the
    // compatibility commitment — message prose is not). Declared codes map from the ErrorSchema;
    // the switch below remains only as fallback for codes a handler emits that are not declared
    // on the invoked descriptor (host codes, cross-cutting codes, idempotency, drift).
    private static ProcessResult ErrorForHandler(string code, OperationDescriptor? descriptor = null) =>
        descriptor?.DomainErrors?.FirstOrDefault(declared => string.Equals(declared.Code, code, StringComparison.Ordinal)) is { } schema
            ? Error(schema.ExitCode, code, schema.Category, CategoryMessage(schema.Category))
            : FallbackErrorForHandler(code);

    // Deterministic, metadata-only message per published category — no per-code prose.
    private static string CategoryMessage(string category) => category switch
    {
        "usage" => "The request usage is invalid.",
        "validation" => "The request input is invalid.",
        "not_found" => "The requested target was not found.",
        "conflict" => "The request conflicts with current state.",
        "lifecycle" => "The lifecycle state does not allow the operation.",
        "compatibility" => "The request is not compatible with this executable contract.",
        "integrity" => "The operation could not preserve its integrity contract.",
        "host" => "The host could not safely complete the operation.",
        "unsupported" => "The statement source is not supported by this executable.",
        "unsafe_source" => "The statement source could not be read safely.",
        "overlap" => "The preview is blocked by overlap policy.",
        "reconciliation" => "The preview is blocked by reconciliation policy.",
        "resource" => "The statement source exceeds safe resource limits.",
        "ledger" => "The commit could not verify ledger outcomes.",
        "interrupted" => "The commit was interrupted and may be resumed.",
        "unexpected" => "The operation could not be completed.",
        _ => "The operation could not be completed."
    };

    private static ProcessResult FallbackErrorForHandler(string code) => code switch
    {
        "operation.not_found" => Error(4, code, "not_found", "The requested operation is not part of the public contract."),
        "validation.invalid_input" => Error(3, code, "validation", "Input does not match the published schema."),
        "LEDGER-ACCOUNT-TYPE-UNSUPPORTED" or "LEDGER-CURRENCY-UNSUPPORTED" => Error(3, code, "validation", "The account input is not supported."),
        "LEDGER-ACCOUNT-NOT-FOUND" => Error(4, code, "not_found", "The account was not found."),
        "LEDGER-ACCOUNT-DUPLICATE" or "LEDGER-ACCOUNT-NAME-CONFLICT" => Error(5, code, "conflict", "The account conflicts with existing state."),
        "LEDGER-ACCOUNT-ARCHIVED" or "LEDGER-ACCOUNT-ALREADY-ARCHIVED" => Error(6, code, "lifecycle", "The account lifecycle does not allow the operation."),
        "LEDGER-CATEGORY-INVALID" or "LEDGER-CATEGORY-SELF-PARENT" or "LEDGER-CATEGORY-SCOPE-INVALID" => Error(3, code, "validation", "The category input is invalid."),
        "LEDGER-CATEGORY-NOT-FOUND" or "LEDGER-CATEGORY-PARENT-NOT-FOUND" => Error(4, code, "not_found", "The category was not found."),
        "LEDGER-CATEGORY-DUPLICATE-SIBLING" => Error(5, code, "conflict", "The category conflicts with an active sibling."),
        "LEDGER-CATEGORY-PARENT-ARCHIVED" or "LEDGER-CATEGORY-ARCHIVED" or "LEDGER-CATEGORY-CYCLE" or "LEDGER-CATEGORY-ACTIVE-CHILDREN" or "LEDGER-CATEGORY-ALREADY-ARCHIVED" or "LEDGER-CATEGORY-ALREADY-ACTIVE" or "LEDGER-CATEGORY-ANCESTOR-ARCHIVED" => Error(6, code, "lifecycle", "The category lifecycle does not allow the operation."),
        "LEDGER-PAYMENT-IDENTITY-INVALID" => Error(3, code, "validation", "The payment identity input is invalid."),
        "LEDGER-PAYMENT-INSTRUMENT-NOT-FOUND" or "LEDGER-CARDHOLDER-NOT-FOUND" => Error(4, code, "not_found", "The payment identity was not found."),
        "LEDGER-PAYMENT-INSTRUMENT-DUPLICATE" or "LEDGER-CARDHOLDER-DUPLICATE" => Error(5, code, "conflict", "The payment identity conflicts with active catalogue state."),
        "LEDGER-PAYMENT-INSTRUMENT-ACCOUNT-NOT-ACTIVE" or "LEDGER-PAYMENT-INSTRUMENT-ARCHIVED" or "LEDGER-CARDHOLDER-ARCHIVED" or "LEDGER-PAYMENT-INSTRUMENT-ALREADY-ARCHIVED" or "LEDGER-CARDHOLDER-ALREADY-ARCHIVED" or "LEDGER-PAYMENT-INSTRUMENT-ALREADY-ACTIVE" or "LEDGER-CARDHOLDER-ALREADY-ACTIVE" => Error(6, code, "lifecycle", "The payment identity lifecycle does not allow the operation."),
        "LEDGER-SPEND-POOL-INVALID" => Error(3, code, "validation", "The Spend Pool input is invalid."),
        "LEDGER-SPEND-POOL-NOT-FOUND" => Error(4, code, "not_found", "The Spend Pool was not found."),
        "LEDGER-SPEND-POOL-DUPLICATE" => Error(5, code, "conflict", "The Spend Pool conflicts with active catalogue state."),
        "LEDGER-SPEND-POOL-ARCHIVED" or "LEDGER-SPEND-POOL-ALREADY-ARCHIVED" or "LEDGER-SPEND-POOL-ALREADY-ACTIVE" => Error(6, code, "lifecycle", "The Spend Pool lifecycle does not allow the operation."),
        "LEDGER-TRANSACTION-INVALID" or "LEDGER-TRANSACTION-EVIDENCE-INCOMPATIBLE" or "amount.invalid" or "amount.zero" or "currency.unsupported" or "date.invalid" => Error(3, code, "validation", "The transaction input is invalid."),
        "LEDGER-TRANSACTION-CORRECTION-INVALID" => Error(3, code, "validation", "The transaction correction input is invalid."),
        "LEDGER-TRANSACTION-NOT-FOUND" => Error(4, code, "not_found", "The transaction was not found."),
        "LEDGER-TRANSACTION-EVIDENCE-CONFLICT" => Error(5, code, "conflict", "The transaction evidence conflicts with existing state."),
        "LEDGER-EVIDENCE-LINK-INVALID" => Error(3, code, "validation", "The evidence link input is invalid."),
        "LEDGER-EVIDENCE-LINK-EVIDENCE-NOT-FOUND" => Error(4, code, "not_found", "The evidence record was not found."),
        "LEDGER-EVIDENCE-LINK-CONFLICT" => Error(5, code, "conflict", "The evidence record is already linked to conflicting state."),
        "LEDGER-EVIDENCE-LINK-TRANSACTION-INACTIVE" => Error(6, code, "lifecycle", "The transaction lifecycle does not allow evidence linkage."),
        "LEDGER-SCOPE-STATEMENT-EVIDENCE-REQUIRED" or "LEDGER-SCOPE-INCOMPLETE-OBSERVATION" => Error(3, code, "validation", "The statement scope evidence is invalid."),
        "LEDGER-SCOPE-EVIDENCE-NOT-FOUND" => Error(4, code, "not_found", "The statement scope evidence was not found."),
        "LEDGER-SCOPE-ACCOUNT-DATE-CONFLICT" or "LEDGER-SCOPE-EVIDENCE-ALREADY-SCOPED" or "LEDGER-SCOPE-ACCOUNT-PERIOD-CONFLICT" => Error(5, code, "conflict", "The statement scope conflicts with existing state."),
        ReconciliationProjectionErrors.StatementEvidenceRequired or ReconciliationProjectionErrors.IncompleteObservation => Error(3, code, "validation", "The reconciliation evidence is invalid."),
        ReconciliationProjectionErrors.EvidenceNotFound or ReconciliationProjectionErrors.ScopeNotFound => Error(4, code, "not_found", "The reconciliation evidence or scope was not found."),
        ReconciliationProjectionErrors.ScopeConflict => Error(5, code, "conflict", "The reconciliation scope conflicts with current state."),
        ReconciliationProjectionErrors.ScopeInactive => Error(6, code, "lifecycle", "The reconciliation scope lifecycle does not allow the operation."),
        ReconciliationProjectionErrors.UnsupportedPolicy => Error(7, code, "compatibility", "The reconciliation policy is not supported by this contract."),
        ReconciliationApplyErrors.EvidenceFingerprintChanged or ReconciliationApplyErrors.ProjectionChanged or ReconciliationApplyErrors.CandidateSetChanged or ReconciliationApplyErrors.ProjectionConflict or ReconciliationApplyErrors.StatementFactMismatch or "LEDGER-RECONCILIATION-CORRECTION-CONFLICT" => Error(5, code, "conflict", "The reconciliation request conflicts with current state."),
        ReconciliationApplyErrors.DispositionIncompatible => Error(6, code, "lifecycle", "The reconciliation disposition is incompatible with current state."),
        ReconciliationApplyErrors.UnsupportedAutomaticAuthority or ReconciliationApplyErrors.UnsupportedStatementCorrection or ReconciliationApplyErrors.TargetNotCandidate => Error(8, code, "integrity", "The reconciliation request requires review or cannot preserve integrity."),
        ReconciliationDecisionErrors.NotFound or ReconciliationDecisionErrors.CandidateNotFound => Error(4, code, "not_found", "The reconciliation decision or candidate was not found."),
        ReconciliationDecisionErrors.StalePredecessor or ReconciliationDecisionErrors.CandidateAlreadyReconciled or ReconciliationDecisionErrors.LinkConflict => Error(5, code, "conflict", "The reconciliation decision conflicts with current state."),
        ReconciliationDecisionErrors.TransitionIncompatible or ReconciliationDecisionErrors.CandidateInactive => Error(6, code, "lifecycle", "The reconciliation decision lifecycle does not allow the operation."),
        ReconciliationDecisionErrors.CandidateIncompatible => Error(8, code, "integrity", "The reconciliation candidate cannot preserve decision integrity."),
        ReconciliationCoverageErrors.ScopeNotFound or ReconciliationCoverageErrors.NotFound => Error(4, code, "not_found", "The reconciliation coverage scope or summary was not found."),
        ReconciliationCoverageErrors.ScopeConflict or ReconciliationCoverageErrors.EvidenceSetChanged or ReconciliationCoverageErrors.DuplicateTransactionOutcome or ReconciliationCoverageErrors.AlreadyCompleted => Error(5, code, "conflict", "The reconciliation coverage request conflicts with current state."),
        ReconciliationCoverageErrors.ScopeInactive => Error(6, code, "lifecycle", "The reconciliation coverage scope lifecycle does not allow the operation."),
        ReconciliationCoverageErrors.PolicyUnsupported => Error(7, code, "compatibility", "The reconciliation coverage policy is not supported by this contract."),
        ReconciliationCoverageErrors.ScopeIncomplete or ReconciliationCoverageErrors.MissingOutcome => Error(8, code, "integrity", "The reconciliation coverage scope is incomplete or missing a durable outcome."),
        "LEDGER-TRANSACTION-ATTRIBUTION-INCOMPATIBLE" => Error(6, code, "lifecycle", "The transaction payment attribution is incompatible."),
        "LEDGER-CATEGORY-ALLOCATION-INVALID" => Error(3, code, "validation", "The category assignment input is invalid."),
        "LEDGER-CATEGORY-ALLOCATION-CARDINALITY" or "LEDGER-CATEGORY-ALLOCATION-UNCHANGED" => Error(5, code, "conflict", "The category assignment conflicts with current state."),
        "LEDGER-CATEGORY-ALLOCATION-NOT-ASSIGNED" or "LEDGER-TRANSACTION-INACTIVE" => Error(6, code, "lifecycle", "The transaction category lifecycle does not allow the operation."),
        "LEDGER-PAYMENT-ATTRIBUTION-INVALID" => Error(3, code, "validation", "The payment attribution input is invalid."),
        "LEDGER-PAYMENT-ATTRIBUTION-STALE" or "LEDGER-PAYMENT-ATTRIBUTION-ALREADY-ASSIGNED" or "LEDGER-PAYMENT-ATTRIBUTION-UNCHANGED" => Error(5, code, "conflict", "The payment attribution conflicts with current state."),
        "LEDGER-PAYMENT-ATTRIBUTION-TRANSACTION-INACTIVE" or "LEDGER-PAYMENT-ATTRIBUTION-ACCOUNT-INCOMPATIBLE" => Error(6, code, "lifecycle", "The payment attribution lifecycle does not allow the operation."),
        "LEDGER-POOL-ASSIGNMENT-INVALID" => Error(3, code, "validation", "The Spend Pool assignment input is invalid."),
        "LEDGER-POOL-ASSIGNMENT-STALE" or "LEDGER-POOL-ASSIGNMENT-ALREADY-ASSIGNED" or "LEDGER-POOL-ASSIGNMENT-UNCHANGED" => Error(5, code, "conflict", "The Spend Pool assignment conflicts with current state."),
        "LEDGER-POOL-ASSIGNMENT-TRANSACTION-INACTIVE" => Error(6, code, "lifecycle", "The Spend Pool assignment lifecycle does not allow the operation."),
        ActualsErrors.InvalidFilter => Error(3, code, "validation", "The actuals query filter is invalid."),
        ActualsErrors.SnapshotNotFound => Error(4, code, "not_found", "The actuals query snapshot was not found."),
        ActualsErrors.SnapshotBusy => Error(5, code, "conflict", "The actuals query snapshot conflicts with current state."),
        ActualsErrors.SnapshotExpired => Error(6, code, "lifecycle", "The actuals query snapshot has expired."),
        ActualsErrors.CursorInvalid or ActualsErrors.ContractMismatch or ActualsErrors.CursorFilterMismatch or ActualsErrors.GenerationMismatch or ActualsErrors.HierarchyMismatch => Error(7, code, "compatibility", "The actuals query cursor is not compatible with this request."),
        ActualsErrors.Invariant => Error(8, code, "integrity", "The actuals query could not preserve its integrity contract."),
        BackupErrors.Invalid => Error(3, code, "validation", "The backup request is invalid."),
        BackupErrors.NotFound => Error(4, code, "not_found", "The backup artifact was not found."),
        BackupErrors.TargetExists or BackupErrors.Busy => Error(5, code, "conflict", "The backup request conflicts with current state."),
        BackupErrors.Incompatible => Error(7, code, "compatibility", "The backup artifact is not compatible with this executable contract."),
        BackupErrors.ChecksumMismatch or BackupErrors.Integrity => Error(8, code, "integrity", "The backup artifact did not satisfy its integrity contract."),
        BackupErrors.HostProtection or BackupErrors.Permission or BackupErrors.Disk => Error(9, code, "host", "The host could not safely complete the backup operation."),
        RestoreErrors.Invalid or RestoreErrors.NotAuthorized => Error(3, code, "validation", "The restore request is invalid or is not authorized."),
        RestoreErrors.CandidateConflict or RestoreErrors.ActivationConflict or RestoreErrors.Busy => Error(5, code, "conflict", "The restore request conflicts with current state."),
        RestoreErrors.StaleCurrent or RestoreErrors.StaleCandidate => Error(6, code, "lifecycle", "The restore candidate is stale for the current Ledger lifecycle."),
        RestoreErrors.Incompatible => Error(7, code, "compatibility", "The restore candidate is not compatible with this executable contract."),
        RestoreErrors.Integrity => Error(8, code, "integrity", "The restore candidate did not satisfy its integrity contract."),
        RestoreErrors.HostProtection or RestoreErrors.Permission or RestoreErrors.Disk => Error(9, code, "host", "The host could not safely complete the restore operation."),
        StorageEvolutionErrors.Invalid or StorageEvolutionErrors.NotAuthorized => Error(3, code, "validation", "The storage evolution request is invalid or is not authorized."),
        StorageEvolutionErrors.CandidateConflict or StorageEvolutionErrors.ActivationConflict or StorageEvolutionErrors.Busy => Error(5, code, "conflict", "The storage evolution request conflicts with current state."),
        StorageEvolutionErrors.AlreadyCurrent or StorageEvolutionErrors.StaleCurrent or StorageEvolutionErrors.StaleCandidate => Error(6, code, "lifecycle", "The storage evolution candidate is stale for the current Ledger lifecycle."),
        StorageEvolutionErrors.Incompatible => Error(7, code, "compatibility", "The storage evolution source or candidate is not compatible with this executable contract."),
        StorageEvolutionErrors.Integrity => Error(8, code, "integrity", "The storage evolution candidate did not satisfy its integrity contract."),
        StorageEvolutionErrors.HostProtection or StorageEvolutionErrors.Permission or StorageEvolutionErrors.Disk or StorageEvolutionErrors.InsufficientSpace => Error(9, code, "host", "The host could not safely complete the storage evolution operation."),
        "LEDGER-TRANSFER-INVALID" or "LEDGER-TRANSFER-SAME-ACCOUNT" or "LEDGER-TRANSFER-SIGN" or "LEDGER-TRANSFER-AMOUNT" or "LEDGER-TRANSFER-CURRENCY" => Error(3, code, "validation", "The transfer does not satisfy the financial relationship contract."),
        "LEDGER-REFUND-INVALID" or "LEDGER-REFUND-ACCOUNT" or "LEDGER-REFUND-SIGN" or "LEDGER-REFUND-AMOUNT" or "LEDGER-REFUND-CURRENCY" => Error(3, code, "validation", "The refund does not satisfy the full-amount financial relationship contract."),
        "LEDGER-RELATIONSHIP-NOT-FOUND" => Error(4, code, "not_found", "The financial relationship was not found."),
        "LEDGER-RELATIONSHIP-LIFECYCLE-INVALID" => Error(3, code, "validation", "The relationship lifecycle input is invalid."),
        "LEDGER-RELATIONSHIP-ALREADY-RETIRED" or "LEDGER-RELATIONSHIP-TYPE-MISMATCH" => Error(6, code, "lifecycle", "The financial relationship lifecycle does not allow the operation."),
        "LEDGER-RELATIONSHIP-ACTIVE-ROLE-CONFLICT" => Error(5, code, "conflict", "A transaction already participates in an active financial relationship."),
        "LEDGER-TRANSFER-TRANSACTION-INACTIVE" => Error(6, code, "lifecycle", "The transaction lifecycle does not allow transfer confirmation."),
        "LEDGER-REFUND-TRANSACTION-INACTIVE" => Error(6, code, "lifecycle", "The transaction lifecycle does not allow refund confirmation."),
        "LEDGER-GUIDANCE-INVALID" or "LEDGER-GUIDANCE-HOST-UNSUPPORTED" or "LEDGER-GUIDANCE-PATH-UNSAFE" => Error(3, code, "validation", "The guidance request is invalid."),
        "LEDGER-GUIDANCE-CONTRACT-INCOMPATIBLE" or "LEDGER-GUIDANCE-BUNDLE-INVALID" => Error(7, code, "compatibility", "The guidance bundle is incompatible with this executable contract."),
        "LEDGER-IDEMPOTENCY-001" or "operation.conflict" => Error(5, code, "conflict", "The operation conflicts with existing state."),
        "operation.review_required" => Error(8, code, "integrity", "The operation requires explicit review before any financial effect changes."),
        "host.unavailable" => Error(9, code, "host", "The requested operation is not available in this foundation."),
        // INGEST published domain errors (ErrorSchema lists on operation modules).
        // Source-reader codes are string-literal so the process mapper stays platform-agnostic
        // (CallerOwnedSourceReader is [SupportedOSPlatform("linux")] for IO, not for the code contract).
        PreviewErrors.InvalidInput or PreviewErrors.AccountInactive or PreviewErrors.AccountCurrency
            or "INGEST-PREVIEW-SOURCE-PATH-INVALID"
            or InspectErrors.InvalidInput
            or ApproveErrors.InvalidInput or ApproveErrors.NotCommittable or ApproveErrors.Blocked
            or CommitErrors.InvalidInput or CommitErrors.NotApproved or CommitErrors.NotCommittable or CommitErrors.AccountInactive or CommitErrors.LedgerRejected
            or ResumeErrors.InvalidInput or ResumeErrors.NotResumable
            or StatusErrors.InvalidInput
            or AbandonErrors.InvalidInput or AbandonErrors.NotAbandonable
            or CleanupErrors.InvalidInput or CleanupErrors.RetainedForRecovery
            => Error(3, code, "validation", "The ingest request is invalid."),
        PreviewErrors.AccountNotFound or InspectErrors.NotFound or ApproveErrors.NotFound or CommitErrors.NotFound
            or ResumeErrors.NotFound or StatusErrors.BatchNotFound or StatusErrors.SnapshotNotFound
            or AbandonErrors.NotFound or CleanupErrors.NotFound
            => Error(4, code, "not_found", "The ingest target was not found."),
        ApproveErrors.DigestMismatch or CommitErrors.DigestMismatch or CommitErrors.LockHeld or CommitErrors.LedgerConflict
            or StatusErrors.SnapshotBusy or AbandonErrors.LockHeld or CleanupErrors.LockHeld
            => Error(5, code, "conflict", "The ingest request conflicts with current state."),
        PreviewErrors.Unsupported or PreviewErrors.AmbiguousAdapter
            => Error(5, code, "unsupported", "The statement source is not supported by this executable."),
        "INGEST-PREVIEW-SOURCE-UNREADABLE" or "INGEST-PREVIEW-SOURCE-CHANGED"
            => Error(5, code, "unsafe_source", "The statement source could not be read safely."),
        PreviewErrors.OverlapBlocked => Error(5, code, "overlap", "The preview is blocked by overlap policy."),
        PreviewErrors.ReconciliationBlocked => Error(5, code, "reconciliation", "The preview is blocked by reconciliation policy."),
        "INGEST-PREVIEW-SOURCE-TOO-LARGE" => Error(6, code, "resource", "The statement source exceeds safe resource limits."),
        StatusErrors.SnapshotExpired => Error(6, code, "lifecycle", "The ingest status snapshot has expired."),
        CommitErrors.VerificationFailed => Error(6, code, "ledger", "The commit could not verify ledger outcomes."),
        CommitErrors.Interrupted => Error(6, code, "interrupted", "The commit was interrupted and may be resumed."),
        CommitErrors.VersionIncompatible or StatusErrors.CursorInvalid or StatusErrors.ContractMismatch or StatusErrors.GenerationMismatch
            => Error(7, code, "compatibility", "The ingest request is not compatible with this executable contract."),
        PreviewErrors.Unexpected => Error(10, code, "unexpected", "The ingest operation could not be completed."),
        // BUDGET published domain errors (ErrorSchema lists on BudgetOperationModule).
        BudgetErrors.InvalidInput or BudgetErrors.InvalidPeriod or BudgetErrors.InvalidAmount
            or BudgetErrors.UnknownField or BudgetErrors.ActorRequired or BudgetErrors.IdempotencyRequired
            or BudgetErrors.RevisionPeriodMismatch
            => Error(3, code, "validation", "The budget request is invalid."),
        BudgetErrors.NotFound or BudgetErrors.PlanNotFound or BudgetErrors.RevisionNotFound
            or BudgetErrors.CategoryUnknown
            => Error(4, code, "not_found", "The budget target was not found."),
        BudgetErrors.Conflict or BudgetErrors.IdempotencyConflict or BudgetErrors.SourceStateChanged
            => Error(5, code, "conflict", "The budget request conflicts with current state."),
        BudgetErrors.CategoryInactive or BudgetErrors.NoActiveBudgetPlanRevision
            => Error(6, code, "lifecycle", "The budget lifecycle does not allow the operation."),
        BudgetErrors.UnsupportedVersion or BudgetErrors.LedgerIncompatible
            => Error(7, code, "compatibility", "The budget request is not compatible with this executable contract."),
        BudgetErrors.Integrity
            => Error(8, code, "integrity", "The budget request could not preserve its integrity contract."),
        BudgetErrors.LedgerUnavailable or BudgetErrors.ResourceLimit
            => Error(9, code, "host", "The budget operation could not access a required host resource."),
        BudgetErrors.Unexpected
            => Error(10, code, "host", "The budget operation could not be completed."),
        // CLASSIFY published domain errors (ErrorSchema lists on ClassifyOperationModule).
        ClassifyErrors.InvalidInput or ClassifyErrors.ActorRequired or ClassifyErrors.IdempotencyRequired
            or ClassifyErrors.SelectionInvalid
            => Error(3, code, "validation", "The classify request is invalid."),
        ClassifyErrors.NotFound or ClassifyErrors.EvaluationNotFound or ClassifyErrors.OutcomeNotFound
            or ClassifyErrors.PreviewNotFound or ClassifyErrors.RuleNotFound or ClassifyErrors.RuleVersionNotFound
            or ClassifyErrors.ValidationNotFound
            => Error(4, code, "not_found", "The classify target was not found."),
        ClassifyErrors.Conflict or ClassifyErrors.IdempotencyConflict or ClassifyErrors.Stale
            => Error(5, code, "conflict", "The classify request conflicts with current state."),
        ClassifyErrors.Lifecycle
            => Error(6, code, "lifecycle", "The classify lifecycle does not allow the operation."),
        ClassifyErrors.UnsupportedVersion or ClassifyErrors.LedgerIncompatible
            => Error(7, code, "compatibility", "The classify request is not compatible with this executable contract."),
        ClassifyErrors.Integrity
            => Error(8, code, "integrity", "The classify request could not preserve its integrity contract."),
        ClassifyErrors.LedgerUnavailable or ClassifyErrors.ResourceLimit
            => Error(9, code, "host", "The classify operation could not access a required host resource."),
        ClassifyErrors.Unexpected
            => Error(10, code, "host", "The classify operation could not be completed."),
        _ => UnexpectedFailure()
    };

    private sealed record InputSelection(IReadOnlyList<string> Arguments, string? InputPath, bool HasInput, string? ErrorCode);
    private sealed record Invocation(OperationDescriptor? Descriptor, JsonElement HandlerInput, bool UseRequestInput, int ExitCode, string? ErrorCode, string? Category, string? Message)
    {
        public static Invocation For(OperationDescriptor descriptor, JsonElement? input = null, bool useRequestInput = false) => new(descriptor, input ?? JsonSerializer.SerializeToElement(new EmptyInput(), LedgerJsonContext.Default.EmptyInput), useRequestInput, 0, null, null, null);
        public static Invocation Error(int exitCode, string code, string category, string message) => new(null, default, false, exitCode, code, category, message);
    }
}
