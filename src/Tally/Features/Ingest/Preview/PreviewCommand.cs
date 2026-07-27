using Tally.Contracts.Common;

namespace Tally.Features.Ingest.Preview;

public sealed record PreviewCommand(
    string ContractVersion,
    string SourcePath,
    string AccountId,
    SafeActor Actor);
