using System.Reflection;

namespace Tally;

/// <summary>
/// Product (executable) versioning for Tally.
/// </summary>
/// <remarks>
/// Policy (pre-1.0):
/// <list type="bullet">
/// <item><description><b>Major 0</b> until every planned feature module is implemented, then ship <c>1.0.0</c>.</description></item>
/// <item><description><b>Minor</b> = number of completed feature modules (ledger, ingest, budget, classify, insights, …).</description></item>
/// <item><description><b>Patch</b> = increments on product commits while working within the current module wave.</description></item>
/// </list>
/// Completed modules today: ledger, ingest, budget → <c>0.3.x</c>.
/// Contract version (<c>system.version.contractVersion</c> / request envelopes) is independent and stays on the published API line (currently <c>1.0</c>).
/// Bump <c>Version</c> in repo-root <c>Directory.Build.props</c> (single source of truth for every project);
/// this type only reads the assembly informational version.
/// </remarks>
public static class ProductVersion
{
    /// <summary>Planned feature modules that count toward minor (and toward 1.0.0 when all are done).</summary>
    public static readonly IReadOnlyList<string> PlannedModules =
    [
        "ledger",
        "ingest",
        "budget",
        "classify",
        "insights"
    ];

    /// <summary>Feature modules currently implemented in this executable line.</summary>
    public static readonly IReadOnlyList<string> ImplementedModules =
    [
        "ledger",
        "ingest",
        "budget"
    ];

    /// <summary>Published API / request-envelope contract line (not the product semver).</summary>
    public const string ContractVersion = "1.0";

    /// <summary>Contract compatibility advertisement for <c>system.version</c>.</summary>
    public const string Compatibility = "1.0";

    public const string ProductName = "tally";

    /// <summary>
    /// Product semver from the assembly (set via <c>Tally.csproj</c> <c>Version</c>).
    /// SourceLink metadata after <c>+</c> is stripped when present.
    /// </summary>
    public static string Current { get; } = ReadFromAssembly();

    private static string ReadFromAssembly()
    {
        var informational = typeof(ProductVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        var version = typeof(ProductVersion).Assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
