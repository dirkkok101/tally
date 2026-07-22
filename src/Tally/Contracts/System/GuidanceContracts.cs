namespace Tally.Contracts.System;

public sealed record ListGuidanceInput(string ScopePath);
public sealed record CheckGuidanceInput(string Host, string ScopePath);
public sealed record InstallGuidanceInput(string Host, string ScopePath);

public sealed record GuidanceBundleStatus(
    string Host,
    string Format,
    string BundleVersion,
    string Checksum,
    string MinimumExecutableVersion,
    string MaximumExecutableVersion,
    string MinimumContractVersion,
    string MaximumContractVersion,
    IReadOnlyList<string> OperationIds,
    string Status,
    string InstallPath);

public sealed record GuidanceListResult(IReadOnlyList<GuidanceBundleStatus> Bundles);
public sealed record GuidanceCheckResult(GuidanceBundleStatus Bundle);
public sealed record GuidanceInstallResult(string Host, string Format, string BundleVersion, string Checksum, string InstallPath);

public sealed record GuidanceManifest(
    int SchemaVersion,
    string BundleVersion,
    string MinimumExecutableVersion,
    string MaximumExecutableVersion,
    string MinimumContractVersion,
    string MaximumContractVersion,
    IReadOnlyList<string> OperationIds,
    IReadOnlyList<GuidanceBundleManifest> Bundles);

public sealed record GuidanceBundleManifest(
    string Host,
    string Format,
    string ResourceName,
    string InstallRelativePath,
    string Checksum);

public sealed record InstalledGuidanceManifest(
    int SchemaVersion,
    string Host,
    string Format,
    string BundleVersion,
    string Checksum,
    string MinimumExecutableVersion,
    string MaximumExecutableVersion,
    string MinimumContractVersion,
    string MaximumContractVersion,
    IReadOnlyList<string> OperationIds);

public static class GuidanceErrors
{
    public const string Invalid = "LEDGER-GUIDANCE-INVALID";
    public const string UnsupportedHost = "LEDGER-GUIDANCE-HOST-UNSUPPORTED";
    public const string Incompatible = "LEDGER-GUIDANCE-CONTRACT-INCOMPATIBLE";
    public const string UnsafePath = "LEDGER-GUIDANCE-PATH-UNSAFE";
    public const string InvalidBundle = "LEDGER-GUIDANCE-BUNDLE-INVALID";
}
