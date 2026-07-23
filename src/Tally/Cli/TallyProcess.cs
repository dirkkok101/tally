using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Contracts.Ledger.Recovery;
using Tally.Contracts.System;

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
            return result.IsSuccess ? Success(invocation.Descriptor.OperationId, result.Value!) : ErrorForHandler(result.ErrorCode!);
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
    private static ProcessResult ErrorForHandler(string code) => code switch
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
        _ => UnexpectedFailure()
    };

    private sealed record InputSelection(IReadOnlyList<string> Arguments, string? InputPath, bool HasInput, string? ErrorCode);
    private sealed record Invocation(OperationDescriptor? Descriptor, JsonElement HandlerInput, bool UseRequestInput, int ExitCode, string? ErrorCode, string? Category, string? Message)
    {
        public static Invocation For(OperationDescriptor descriptor, JsonElement? input = null, bool useRequestInput = false) => new(descriptor, input ?? JsonSerializer.SerializeToElement(new EmptyInput(), LedgerJsonContext.Default.EmptyInput), useRequestInput, 0, null, null, null);
        public static Invocation Error(int exitCode, string code, string category, string message) => new(null, default, false, exitCode, code, category, message);
    }
}
