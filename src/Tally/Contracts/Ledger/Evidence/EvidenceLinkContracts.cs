using System.Text.Json.Serialization;
using Tally.Contracts.Ledger.Transactions;

namespace Tally.Contracts.Ledger.Evidence;

public sealed record LinkSupportingEvidenceInput(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] string Reason);

public sealed record EvidenceLinkResult(
    TransactionDetail Transaction,
    EvidenceRecordDetail Evidence,
    string LinkEventId);
