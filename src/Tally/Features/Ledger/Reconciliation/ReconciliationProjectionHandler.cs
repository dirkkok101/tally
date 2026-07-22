using System.Text.Json;
using Tally.Application;
using Tally.Contracts.Ledger.Reconciliation;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Infrastructure.Storage.Reconciliation;

namespace Tally.Features.Ledger.Reconciliation;

public sealed class ReconciliationProjectionHandler(ReconciliationProjectionStore store)
{
    public async Task<CommandResult<JsonElement>> HandleAsync(
        GetReconciliationCandidatesInput input,
        CancellationToken cancellationToken)
    {
        if (!LedgerId.TryParse(input.EvidenceId, out _, out _)
            || !LedgerId.TryParse(input.ScopeId, out _, out _)
            || string.IsNullOrWhiteSpace(input.PolicyId)
            || string.IsNullOrWhiteSpace(input.PolicyVersion))
        {
            return CommandResult<JsonElement>.Failure(ReconciliationProjectionErrors.InvalidInput);
        }

        if (!ManualReviewProjectionV1.Supports(input.PolicyId, input.PolicyVersion))
        {
            return CommandResult<JsonElement>.Failure(ReconciliationProjectionErrors.UnsupportedPolicy);
        }

        var read = await store.ReadAsync(input.EvidenceId, input.ScopeId, cancellationToken);
        if (!read.IsSuccess)
        {
            return CommandResult<JsonElement>.Failure(read.ErrorCode!);
        }

        var result = ManualReviewProjectionV1.Project(read.Source!);
        return CommandResult<JsonElement>.Success(
            JsonSerializer.SerializeToElement(result, ReconciliationProjectionJsonContext.Default.ReconciliationProjectionResult));
    }
}
