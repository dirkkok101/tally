using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Common;

namespace Tally.Features.Ledger.Dimensions;

public sealed class PaymentIdentityOperationModule(
    CreatePaymentInstrumentHandler createInstrument,
    GetPaymentInstrumentHandler getInstrument,
    ListPaymentInstrumentsHandler listInstruments,
    RenamePaymentInstrumentHandler renameInstrument,
    ArchivePaymentInstrumentHandler archiveInstrument,
    ReactivatePaymentInstrumentHandler reactivateInstrument,
    CreateCardholderHandler createCardholder,
    GetCardholderHandler getCardholder,
    ListCardholdersHandler listCardholders,
    RenameCardholderHandler renameCardholder,
    ArchiveCardholderHandler archiveCardholder,
    ReactivateCardholderHandler reactivateCardholder)
{
    public Task<CommandResult<JsonElement>> HandleAsync(string operationId, OperationRequest request, CancellationToken cancellationToken) => operationId switch
    {
        "ledger.instrument.create" => createInstrument.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CreatePaymentInstrumentInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.instrument.get" => getInstrument.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetPaymentInstrumentInput)!, cancellationToken),
        "ledger.instrument.list" => listInstruments.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ListPaymentInstrumentsInput)!, cancellationToken),
        "ledger.instrument.rename" => renameInstrument.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RenamePaymentInstrumentInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.instrument.archive" => archiveInstrument.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ArchivePaymentInstrumentInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.instrument.reactivate" => reactivateInstrument.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReactivatePaymentInstrumentInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.cardholder.create" => createCardholder.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.CreateCardholderInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.cardholder.get" => getCardholder.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.GetCardholderInput)!, cancellationToken),
        "ledger.cardholder.list" => listCardholders.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ListCardholdersInput)!, cancellationToken),
        "ledger.cardholder.rename" => renameCardholder.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.RenameCardholderInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.cardholder.archive" => archiveCardholder.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ArchiveCardholderInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        "ledger.cardholder.reactivate" => reactivateCardholder.HandleAsync(JsonSerializer.Deserialize(request.Input, LedgerJsonContext.Default.ReactivateCardholderInput)!, request.Actor, request.IdempotencyKey, cancellationToken),
        _ => Task.FromResult(CommandResult<JsonElement>.Failure("operation.not_found"))
    };
}

internal sealed class PaymentIdentityOperationHandler(PaymentIdentityOperationModule module, string operationId) : IOperationHandler
{
    public Task<CommandResult<JsonElement>> HandleAsync(OperationRequest request, CancellationToken cancellationToken) => module.HandleAsync(operationId, request, cancellationToken);
}
