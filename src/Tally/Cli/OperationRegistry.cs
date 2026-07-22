using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.System;
using Tally.Features.Ledger.Evidence;
using Tally.Features.Ledger.Accounts;
using Tally.Features.Ledger.Categories;
using Tally.Features.Ledger.Dimensions;
using Tally.Domain.Ledger.Categories;
using Tally.Domain.Ledger.Dimensions;
using Tally.Features.System.Contract;

namespace Tally.Cli;

public sealed record OperationDescriptor(string OperationId, string CliPath, string Kind, bool RequiresIdempotencyKey, JsonTypeInfo RequestTypeInfo, JsonTypeInfo ResultTypeInfo, string HandlerTarget, Func<LedgerServices, OperationRegistry, IOperationHandler> HandlerFactory, string Example, IReadOnlyList<ErrorSchema>? DomainErrors = null)
{
    public OperationSchema ToSchema() => new(OperationId, CliPath, Kind, "{\"type\":\"object\",\"additionalProperties\":false}", "{\"type\":\"object\"}", RequestTypeInfo.Type.FullName!, ResultTypeInfo.Type.FullName!, [.. Errors, .. DomainErrors ?? []], 0, RequiresIdempotencyKey, "1.0", "1.0", HandlerTarget, Example);
    private static readonly IReadOnlyList<ErrorSchema> Errors =
    [
        new("usage.invalid_input_path", "usage", 2), new("validation.invalid_input", "validation", 3), new("operation.not_found", "not_found", 4),
        new("operation.conflict", "conflict", 5), new("operation.lifecycle", "lifecycle", 6), new("contract.incompatible", "compatibility", 7),
        new("operation.review_required", "integrity", 8), new("host.unavailable", "host", 9), new("host.unexpected", "host", 10)
    ];
}

public sealed class OperationRegistry
{
    private readonly IReadOnlyList<OperationDescriptor> descriptors;
    private OperationRegistry(IReadOnlyList<OperationDescriptor> descriptors) => this.descriptors = descriptors;
    public IReadOnlyList<OperationDescriptor> Descriptors => descriptors;
    public static OperationRegistry Create() => new(Inventory.Select(CreateDescriptor).OrderBy(x => x.OperationId, StringComparer.Ordinal).ToArray());
    public OperationDescriptor? Find(string operationId) => descriptors.SingleOrDefault(x => x.OperationId == operationId);
    public OperationDescriptor? FindByArguments(IReadOnlyList<string> arguments) => descriptors.SingleOrDefault(descriptor =>
        descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).SequenceEqual(arguments));
    public string SchemaListJson() => JsonSerializer.Serialize(descriptors.Select(x => x.ToSchema()).ToArray(), LedgerJsonContext.Default.OperationSchemaArray);
    public string SchemaShowJson(string operationId) => Find(operationId) is { } descriptor ? JsonSerializer.Serialize(descriptor.ToSchema(), LedgerJsonContext.Default.OperationSchema) : "null";
    private static OperationDescriptor CreateDescriptor(string operationId)
    {
        var isQuery = operationId is "system.schema.list" or "system.schema.show" or "system.version" or "system.guidance.list" or "system.guidance.check"
            || operationId.EndsWith(".get", StringComparison.Ordinal) || operationId.EndsWith(".list", StringComparison.Ordinal)
            || operationId.EndsWith(".query", StringComparison.Ordinal) || operationId.EndsWith(".candidates", StringComparison.Ordinal)
            || operationId.EndsWith(".status", StringComparison.Ordinal) || operationId.EndsWith(".verify", StringComparison.Ordinal);
        return operationId switch
        {
            "system.version" => new(operationId, "tally version", "query", false, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.VersionResult, "SystemOperationModule.Version", static (services, _) => new SystemOperationHandler(services.SystemOperations, null, "system.version"), "tally version"),
            "system.schema.list" => new(operationId, "tally schema list", "query", false, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.SchemaListResult, "SystemOperationModule.List", static (services, registry) => new SystemOperationHandler(services.SystemOperations, registry, "system.schema.list"), "tally schema list"),
            "system.schema.show" => new(operationId, "tally schema show <operation-id>", "query", false, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.SchemaShowResult, "SystemOperationModule.Show", static (services, registry) => new SystemOperationHandler(services.SystemOperations, registry, "system.schema.show"), "tally schema show system.version"),
            "ledger.account.create" => new(operationId, "tally ledger account create", "mutation", true, LedgerJsonContext.Default.CreateAccountInput, LedgerJsonContext.Default.AccountDetail, "AccountOperationModule.Create", static (services, _) => services.Accounts is { } module ? new AccountOperationHandler(module, "ledger.account.create") : new FoundationOperationHandler(), "tally ledger account create --input -", AccountErrors(operationId)),
            "ledger.account.get" => new(operationId, "tally ledger account get", "query", false, LedgerJsonContext.Default.GetAccountInput, LedgerJsonContext.Default.AccountDetail, "AccountOperationModule.Get", static (services, _) => services.Accounts is { } module ? new AccountOperationHandler(module, "ledger.account.get") : new FoundationOperationHandler(), "tally ledger account get --input -", AccountErrors(operationId)),
            "ledger.account.list" => new(operationId, "tally ledger account list", "query", false, LedgerJsonContext.Default.ListAccountsInput, LedgerJsonContext.Default.AccountListResult, "AccountOperationModule.List", static (services, _) => services.Accounts is { } module ? new AccountOperationHandler(module, "ledger.account.list") : new FoundationOperationHandler(), "tally ledger account list --input -", AccountErrors(operationId)),
            "ledger.account.rename" => new(operationId, "tally ledger account rename", "mutation", true, LedgerJsonContext.Default.RenameAccountInput, LedgerJsonContext.Default.AccountLifecycleResult, "AccountOperationModule.Rename", static (services, _) => services.Accounts is { } module ? new AccountOperationHandler(module, "ledger.account.rename") : new FoundationOperationHandler(), "tally ledger account rename --input -", AccountErrors(operationId)),
            "ledger.account.archive" => new(operationId, "tally ledger account archive", "mutation", true, LedgerJsonContext.Default.ArchiveAccountInput, LedgerJsonContext.Default.AccountLifecycleResult, "AccountOperationModule.Archive", static (services, _) => services.Accounts is { } module ? new AccountOperationHandler(module, "ledger.account.archive") : new FoundationOperationHandler(), "tally ledger account archive --input -", AccountErrors(operationId)),
            "ledger.category.create" => CategoryDescriptor(operationId, LedgerJsonContext.Default.CreateCategoryInput, LedgerJsonContext.Default.CategoryDetail, "Create"),
            "ledger.category.get" => CategoryDescriptor(operationId, LedgerJsonContext.Default.GetCategoryInput, LedgerJsonContext.Default.CategoryDetail, "Get"),
            "ledger.category.list" => CategoryDescriptor(operationId, LedgerJsonContext.Default.ListCategoriesInput, LedgerJsonContext.Default.CategoryListResult, "List"),
            "ledger.category.rename" => CategoryDescriptor(operationId, LedgerJsonContext.Default.RenameCategoryInput, LedgerJsonContext.Default.CategoryLifecycleResult, "Rename"),
            "ledger.category.reparent" => CategoryDescriptor(operationId, LedgerJsonContext.Default.ReparentCategoryInput, LedgerJsonContext.Default.CategoryReparentResult, "Reparent"),
            "ledger.category.archive" => CategoryDescriptor(operationId, LedgerJsonContext.Default.ArchiveCategoryInput, LedgerJsonContext.Default.CategoryLifecycleResult, "Archive"),
            "ledger.category.reactivate" => CategoryDescriptor(operationId, LedgerJsonContext.Default.ReactivateCategoryInput, LedgerJsonContext.Default.CategoryLifecycleResult, "Reactivate"),
            "ledger.instrument.create" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.CreatePaymentInstrumentInput, LedgerJsonContext.Default.PaymentInstrumentDetail, "Create"),
            "ledger.instrument.get" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.GetPaymentInstrumentInput, LedgerJsonContext.Default.PaymentInstrumentDetail, "Get"),
            "ledger.instrument.list" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.ListPaymentInstrumentsInput, LedgerJsonContext.Default.PaymentInstrumentListResult, "List"),
            "ledger.instrument.rename" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.RenamePaymentInstrumentInput, LedgerJsonContext.Default.PaymentInstrumentLifecycleResult, "Rename"),
            "ledger.instrument.archive" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.ArchivePaymentInstrumentInput, LedgerJsonContext.Default.PaymentInstrumentLifecycleResult, "Archive"),
            "ledger.instrument.reactivate" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.ReactivatePaymentInstrumentInput, LedgerJsonContext.Default.PaymentInstrumentLifecycleResult, "Reactivate"),
            "ledger.cardholder.create" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.CreateCardholderInput, LedgerJsonContext.Default.CardholderDetail, "Create"),
            "ledger.cardholder.get" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.GetCardholderInput, LedgerJsonContext.Default.CardholderDetail, "Get"),
            "ledger.cardholder.list" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.ListCardholdersInput, LedgerJsonContext.Default.CardholderListResult, "List"),
            "ledger.cardholder.rename" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.RenameCardholderInput, LedgerJsonContext.Default.CardholderLifecycleResult, "Rename"),
            "ledger.cardholder.archive" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.ArchiveCardholderInput, LedgerJsonContext.Default.CardholderLifecycleResult, "Archive"),
            "ledger.cardholder.reactivate" => PaymentIdentityDescriptor(operationId, LedgerJsonContext.Default.ReactivateCardholderInput, LedgerJsonContext.Default.CardholderLifecycleResult, "Reactivate"),
            "ledger.evidence.register" => new(operationId, "tally ledger evidence register", "mutation", true, LedgerJsonContext.Default.RegisterEvidenceInput, LedgerJsonContext.Default.EvidenceRecordDetail, "EvidenceRegistryOperationModule.Register", static (services, _) => services.EvidenceRegistry is { } module ? new EvidenceRegistryOperationHandler(module, "ledger.evidence.register") : new FoundationOperationHandler(), "tally ledger evidence register --input -"),
            "ledger.evidence.get" => new(operationId, "tally ledger evidence get", "query", false, LedgerJsonContext.Default.GetEvidenceInput, LedgerJsonContext.Default.EvidenceRecordDetail, "EvidenceRegistryOperationModule.Get", static (services, _) => services.EvidenceRegistry is { } module ? new EvidenceRegistryOperationHandler(module, "ledger.evidence.get") : new FoundationOperationHandler(), "tally ledger evidence get --input -"),
            _ => new(operationId, "tally " + operationId.Replace('.', ' '), isQuery ? "query" : "mutation", !isQuery, LedgerJsonContext.Default.EmptyInput, LedgerJsonContext.Default.OperationUnavailableResult, "FoundationOperationHandler", static (_, _) => new FoundationOperationHandler(), "tally " + operationId.Replace('.', ' '))
        };
    }

    private static IReadOnlyList<ErrorSchema> AccountErrors(string operationId) => operationId switch
    {
        "ledger.account.create" => [new("LEDGER-ACCOUNT-DUPLICATE", "conflict", 5), new("LEDGER-ACCOUNT-TYPE-UNSUPPORTED", "validation", 3), new("LEDGER-CURRENCY-UNSUPPORTED", "validation", 3)],
        "ledger.account.get" => [new("LEDGER-ACCOUNT-NOT-FOUND", "not_found", 4)],
        "ledger.account.rename" => [new("LEDGER-ACCOUNT-NOT-FOUND", "not_found", 4), new("LEDGER-ACCOUNT-ARCHIVED", "lifecycle", 6), new("LEDGER-ACCOUNT-NAME-CONFLICT", "conflict", 5)],
        "ledger.account.archive" => [new("LEDGER-ACCOUNT-NOT-FOUND", "not_found", 4), new("LEDGER-ACCOUNT-ALREADY-ARCHIVED", "lifecycle", 6)],
        _ => []
    };

    private static OperationDescriptor CategoryDescriptor(string operationId, JsonTypeInfo request, JsonTypeInfo result, string target) => new(
        operationId, "tally " + operationId.Replace('.', ' '), operationId.EndsWith(".get", StringComparison.Ordinal) || operationId.EndsWith(".list", StringComparison.Ordinal) ? "query" : "mutation",
        !operationId.EndsWith(".get", StringComparison.Ordinal) && !operationId.EndsWith(".list", StringComparison.Ordinal), request, result,
        "CategoryOperationModule." + target, (services, _) => services.Categories is { } module ? new CategoryOperationHandler(module, operationId) : new FoundationOperationHandler(),
        "tally " + operationId.Replace('.', ' ') + " --input -", CategoryErrorsFor(operationId));

    private static IReadOnlyList<ErrorSchema> CategoryErrorsFor(string operationId) => operationId switch
    {
        "ledger.category.create" => [CategoryError(CategoryErrors.DuplicateSibling, "conflict", 5), CategoryError(CategoryErrors.ParentNotFound, "not_found", 4), CategoryError(CategoryErrors.ParentArchived, "lifecycle", 6), CategoryError(SpendCategory.InvalidError, "validation", 3)],
        "ledger.category.get" => [CategoryError(CategoryErrors.NotFound, "not_found", 4)],
        "ledger.category.list" => [CategoryError(CategoryErrors.ParentNotFound, "not_found", 4), CategoryError(CategoryErrors.ScopeInvalid, "validation", 3)],
        "ledger.category.rename" => [CategoryError(CategoryErrors.NotFound, "not_found", 4), CategoryError(CategoryErrors.Archived, "lifecycle", 6), CategoryError(CategoryErrors.DuplicateSibling, "conflict", 5)],
        "ledger.category.reparent" => [CategoryError(CategoryErrors.NotFound, "not_found", 4), CategoryError(CategoryErrors.Archived, "lifecycle", 6), CategoryError(CategoryErrors.ParentNotFound, "not_found", 4), CategoryError(CategoryErrors.ParentArchived, "lifecycle", 6), CategoryError(CategoryErrors.SelfParent, "validation", 3), CategoryError(CategoryErrors.Cycle, "lifecycle", 6), CategoryError(CategoryErrors.DuplicateSibling, "conflict", 5)],
        "ledger.category.archive" => [CategoryError(CategoryErrors.NotFound, "not_found", 4), CategoryError(CategoryErrors.AlreadyArchived, "lifecycle", 6), CategoryError(CategoryErrors.ActiveChildren, "lifecycle", 6)],
        "ledger.category.reactivate" => [CategoryError(CategoryErrors.NotFound, "not_found", 4), CategoryError(CategoryErrors.AlreadyActive, "lifecycle", 6), CategoryError(CategoryErrors.AncestorArchived, "lifecycle", 6), CategoryError(CategoryErrors.DuplicateSibling, "conflict", 5)],
        _ => []
    };

    private static ErrorSchema CategoryError(string code, string category, int exitCode) => new(code, category, exitCode);

    private static OperationDescriptor PaymentIdentityDescriptor(string operationId, JsonTypeInfo request, JsonTypeInfo result, string target) => new(
        operationId, "tally " + operationId.Replace('.', ' '), operationId.EndsWith(".get", StringComparison.Ordinal) || operationId.EndsWith(".list", StringComparison.Ordinal) ? "query" : "mutation",
        !operationId.EndsWith(".get", StringComparison.Ordinal) && !operationId.EndsWith(".list", StringComparison.Ordinal), request, result,
        "PaymentIdentityOperationModule." + target, (services, _) => services.PaymentIdentities is { } module ? new PaymentIdentityOperationHandler(module, operationId) : new FoundationOperationHandler(),
        "tally " + operationId.Replace('.', ' ') + " --input -", PaymentIdentityErrorsFor(operationId));

    private static IReadOnlyList<ErrorSchema> PaymentIdentityErrorsFor(string operationId)
    {
        var instrument = operationId.StartsWith("ledger.instrument.", StringComparison.Ordinal);
        var notFound = instrument ? PaymentIdentityErrors.InstrumentNotFound : PaymentIdentityErrors.CardholderNotFound;
        var duplicate = instrument ? PaymentIdentityErrors.InstrumentDuplicate : PaymentIdentityErrors.CardholderDuplicate;
        var archived = instrument ? PaymentIdentityErrors.InstrumentArchived : PaymentIdentityErrors.CardholderArchived;
        var alreadyArchived = instrument ? PaymentIdentityErrors.InstrumentAlreadyArchived : PaymentIdentityErrors.CardholderAlreadyArchived;
        var alreadyActive = instrument ? PaymentIdentityErrors.InstrumentAlreadyActive : PaymentIdentityErrors.CardholderAlreadyActive;
        var errors = new List<ErrorSchema> { new(PaymentIdentity.InvalidError, "validation", 3) };
        if (!operationId.EndsWith(".create", StringComparison.Ordinal) && !operationId.EndsWith(".list", StringComparison.Ordinal)) errors.Add(new(notFound, "not_found", 4));
        if (operationId.EndsWith(".create", StringComparison.Ordinal) || operationId.EndsWith(".rename", StringComparison.Ordinal) || operationId.EndsWith(".reactivate", StringComparison.Ordinal)) errors.Add(new(duplicate, "conflict", 5));
        if (operationId.EndsWith(".rename", StringComparison.Ordinal)) errors.Add(new(archived, "lifecycle", 6));
        if (operationId.EndsWith(".archive", StringComparison.Ordinal)) errors.Add(new(alreadyArchived, "lifecycle", 6));
        if (operationId.EndsWith(".reactivate", StringComparison.Ordinal)) errors.Add(new(alreadyActive, "lifecycle", 6));
        if (instrument && (operationId.EndsWith(".create", StringComparison.Ordinal) || operationId.EndsWith(".reactivate", StringComparison.Ordinal))) errors.Add(new(PaymentIdentityErrors.InstrumentAccountNotActive, "lifecycle", 6));
        return errors;
    }
    private static readonly string[] Inventory =
    [
        "ledger.account.create","ledger.account.get","ledger.account.list","ledger.account.rename","ledger.account.archive",
        "ledger.category.create","ledger.category.get","ledger.category.list","ledger.category.rename","ledger.category.reparent","ledger.category.archive","ledger.category.reactivate",
        "ledger.instrument.create","ledger.instrument.get","ledger.instrument.list","ledger.instrument.rename","ledger.instrument.archive","ledger.instrument.reactivate",
        "ledger.cardholder.create","ledger.cardholder.get","ledger.cardholder.list","ledger.cardholder.rename","ledger.cardholder.archive","ledger.cardholder.reactivate",
        "ledger.pool.create","ledger.pool.get","ledger.pool.list","ledger.pool.rename","ledger.pool.archive","ledger.pool.reactivate",
        "ledger.transaction.record","ledger.transaction.get","ledger.transaction.void","ledger.transaction.supersede","ledger.transaction.category.assign","ledger.transaction.category.correct","ledger.transaction.attribution.assign","ledger.transaction.attribution.correct","ledger.transaction.pool.assign","ledger.transaction.pool.correct",
        "ledger.evidence.register","ledger.evidence.get","ledger.evidence.link-supporting","ledger.reconciliation.candidates","ledger.reconciliation.apply","ledger.reconciliation.decision.get","ledger.reconciliation.decision.confirm","ledger.reconciliation.decision.reject","ledger.reconciliation.decision.revoke","ledger.reconciliation.decision.replace","ledger.reconciliation.coverage.complete","ledger.reconciliation.coverage.get",
        "ledger.transfer.confirm","ledger.transfer.revoke","ledger.transfer.replace","ledger.refund.confirm","ledger.refund.revoke","ledger.refund.replace","ledger.relationship.get","ledger.actuals.query","ledger.backup.create","ledger.backup.verify","ledger.restore.prepare","ledger.restore.activate","ledger.storage.status","ledger.storage.evolution.prepare","ledger.storage.evolution.activate",
        "system.schema.list","system.schema.show","system.version","system.guidance.list","system.guidance.check","system.guidance.install"
    ];
}

internal sealed class SystemOperationHandler(SystemOperationModule module, OperationRegistry? registry, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "system.version" => module.VersionAsync(request.Input, cancellationToken),
        "system.schema.list" => module.ListAsync(registry!.Descriptors.Select(x => x.ToSchema()).ToArray(), request.Input, cancellationToken),
        "system.schema.show" => module.ShowAsync(registry!.Find(request.Input.GetProperty("operationId").GetString()!)?.ToSchema(), request.Input, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class FoundationOperationHandler : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CommandResult<JsonElement>.Failure("host.unavailable"));
    }
}
