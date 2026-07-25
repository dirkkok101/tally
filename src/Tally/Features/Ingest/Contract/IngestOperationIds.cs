namespace Tally.Features.Ingest.Contract;

/// <summary>
/// Stable operation ids and contract version for the eight public INGEST operations.
/// FR-INGEST-CONTRACT-DISCOVERY: no generic action discriminator — each transition is its own named operation.
/// </summary>
public static class IngestOperationIds
{
    public const string ContractVersion = "1.0";

    public const string Preview = "ingest.preview";
    public const string Inspect = "ingest.inspect";
    public const string Approve = "ingest.approve";
    public const string Commit = "ingest.commit";
    public const string Resume = "ingest.resume";
    public const string Status = "ingest.status";
    public const string Abandon = "ingest.abandon";
    public const string Cleanup = "ingest.cleanup";

    public static readonly IReadOnlyList<string> All =
    [
        Preview, Inspect, Approve, Commit, Resume, Status, Abandon, Cleanup
    ];
}
