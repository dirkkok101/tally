using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Tally.Infrastructure.Artifacts;

namespace Tally.Features.System.Guidance;

public sealed class GuidanceService()
{
    private const string ExecutableVersion = "1.0";
    private const string ContractVersion = "1.0";
    private const string InstalledManifestName = ".tally-guidance.json";
    private static readonly string[] SupportedHosts = ["claude-code", "codex"];
    private static readonly string[] ForbiddenContent = ["mailbox", "mime", "whatsapp", "delivery", "schedule", "recipient", "acknowledgement", "sqlite", "file access"];

    public async Task<CommandResult<GuidanceListResult>> ListAsync(ListGuidanceInput input, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var catalog = LoadCatalog(registry);
        if (!catalog.IsSuccess) return CommandResult<GuidanceListResult>.Failure(catalog.ErrorCode!);
        if (!TryResolveScope(input.ScopePath, out var scope)) return CommandResult<GuidanceListResult>.Failure(GuidanceErrors.UnsafePath);

        var bundles = new List<GuidanceBundleStatus>(catalog.Manifest!.Bundles.Count);
        foreach (var bundle in catalog.Manifest.Bundles.OrderBy(x => x.Host, StringComparer.Ordinal))
        {
            bundles.Add(await InspectAsync(scope, catalog.Manifest, bundle, cancellationToken));
        }

        return CommandResult<GuidanceListResult>.Success(new GuidanceListResult(bundles));
    }

    public async Task<CommandResult<GuidanceCheckResult>> CheckAsync(CheckGuidanceInput input, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var catalog = LoadCatalog(registry);
        if (!catalog.IsSuccess) return CommandResult<GuidanceCheckResult>.Failure(catalog.ErrorCode!);
        var bundle = FindBundle(catalog.Manifest!, input.Host);
        if (bundle is null) return CommandResult<GuidanceCheckResult>.Failure(GuidanceErrors.UnsupportedHost);
        if (!TryResolveScope(input.ScopePath, out var scope)) return CommandResult<GuidanceCheckResult>.Failure(GuidanceErrors.UnsafePath);

        return CommandResult<GuidanceCheckResult>.Success(new GuidanceCheckResult(await InspectAsync(scope, catalog.Manifest!, bundle, cancellationToken)));
    }

    public async Task<CommandResult<GuidanceInstallResult>> InstallAsync(InstallGuidanceInput input, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var catalog = LoadCatalog(registry);
        if (!catalog.IsSuccess) return CommandResult<GuidanceInstallResult>.Failure(catalog.ErrorCode!);
        var bundle = FindBundle(catalog.Manifest!, input.Host);
        if (bundle is null) return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.UnsupportedHost);
        if (!TryResolveScope(input.ScopePath, out var scope)) return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.UnsafePath);
        if (!OperatingSystem.IsLinux()) return CommandResult<GuidanceInstallResult>.Failure("host.unavailable");

        var installPath = ResolveInstallPath(scope, bundle.InstallRelativePath);
        if (installPath is null || !SafeInstallDestination(installPath)) return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.UnsafePath);

        var before = await InspectAsync(scope, catalog.Manifest!, bundle, cancellationToken);
        if (before.Status == "newer") return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.Incompatible);

        var content = ReadResource(bundle.ResourceName);
        if (content is null || !string.Equals(Sha256(content), bundle.Checksum, StringComparison.Ordinal)) return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.InvalidBundle);

        var artifactReconciler = new ArtifactReconciler();
        await artifactReconciler.ReconcileAsync(installPath, content, bundle.Checksum, cancellationToken);
        if (!SafeInstallDestination(installPath)) return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.UnsafePath);

        var installed = new InstalledGuidanceManifest(
            catalog.Manifest!.SchemaVersion,
            bundle.Host,
            bundle.Format,
            catalog.Manifest.BundleVersion,
            bundle.Checksum,
            catalog.Manifest.MinimumExecutableVersion,
            catalog.Manifest.MaximumExecutableVersion,
            catalog.Manifest.MinimumContractVersion,
            catalog.Manifest.MaximumContractVersion,
            catalog.Manifest.OperationIds);
        var installedBytes = JsonSerializer.SerializeToUtf8Bytes(installed, LedgerJsonContext.Default.InstalledGuidanceManifest);
        var installedPath = Path.Combine(Path.GetDirectoryName(installPath)!, InstalledManifestName);
        await artifactReconciler.ReconcileAsync(installedPath, installedBytes, Sha256(installedBytes), cancellationToken);

        var verified = await InspectAsync(scope, catalog.Manifest, bundle, cancellationToken);
        if (verified.Status != "compatible") return CommandResult<GuidanceInstallResult>.Failure(GuidanceErrors.InvalidBundle);
        return CommandResult<GuidanceInstallResult>.Success(new GuidanceInstallResult(bundle.Host, bundle.Format, catalog.Manifest.BundleVersion, bundle.Checksum, installPath));
    }

    private static CatalogResult LoadCatalog(OperationRegistry registry)
    {
        var manifestBytes = ReadResource("Tally.Guidance.Manifest");
        if (manifestBytes is null) return CatalogResult.Failure(GuidanceErrors.InvalidBundle);

        GuidanceManifest? manifest;
        try { manifest = JsonSerializer.Deserialize(manifestBytes, LedgerJsonContext.Default.GuidanceManifest); }
        catch (JsonException) { return CatalogResult.Failure(GuidanceErrors.InvalidBundle); }

        if (manifest is null || manifest.SchemaVersion != 1 || !TryVersion(manifest.BundleVersion, out _)
            || !RangeContains(manifest.MinimumExecutableVersion, manifest.MaximumExecutableVersion, ExecutableVersion)
            || !RangeContains(manifest.MinimumContractVersion, manifest.MaximumContractVersion, ContractVersion))
        {
            return CatalogResult.Failure(GuidanceErrors.Incompatible);
        }

        if (manifest.OperationIds.Count == 0
            || manifest.OperationIds.Count != manifest.OperationIds.Distinct(StringComparer.Ordinal).Count()
            || manifest.OperationIds.Any(operationId => registry.Find(operationId) is null)
            || manifest.Bundles.Count != SupportedHosts.Length
            || manifest.Bundles.Select(x => x.Host).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(SupportedHosts, StringComparer.Ordinal) is false)
        {
            return CatalogResult.Failure(GuidanceErrors.InvalidBundle);
        }

        foreach (var bundle in manifest.Bundles)
        {
            if (!ValidBundleDefinition(bundle)) return CatalogResult.Failure(GuidanceErrors.InvalidBundle);
            var content = ReadResource(bundle.ResourceName);
            if (content is null || !string.Equals(Sha256(content), bundle.Checksum, StringComparison.Ordinal) || ContainsForbiddenContent(content))
            {
                return CatalogResult.Failure(GuidanceErrors.InvalidBundle);
            }
        }

        return CatalogResult.Success(manifest);
    }

    private static bool ValidBundleDefinition(GuidanceBundleManifest bundle) => bundle switch
    {
        { Host: "codex", Format: "codex-skill-v1", ResourceName: "Tally.Guidance.Codex.Skill", InstallRelativePath: ".agents/skills/tally-ledger/SKILL.md" } => ValidChecksum(bundle.Checksum),
        { Host: "claude-code", Format: "claude-code-skill-v1", ResourceName: "Tally.Guidance.ClaudeCode.Skill", InstallRelativePath: ".claude/skills/tally-ledger/SKILL.md" } => ValidChecksum(bundle.Checksum),
        _ => false
    };

    private static bool ValidChecksum(string checksum) => checksum.Length == 64 && checksum.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GuidanceBundleManifest? FindBundle(GuidanceManifest manifest, string host) =>
        string.IsNullOrWhiteSpace(host) ? null : manifest.Bundles.SingleOrDefault(bundle => string.Equals(bundle.Host, host, StringComparison.Ordinal));

    private static async Task<GuidanceBundleStatus> InspectAsync(string scope, GuidanceManifest manifest, GuidanceBundleManifest bundle, CancellationToken cancellationToken)
    {
        var installPath = ResolveInstallPath(scope, bundle.InstallRelativePath)!;
        var installedPath = Path.Combine(Path.GetDirectoryName(installPath)!, InstalledManifestName);
        var status = SafeInstallDestination(installPath)
            ? await ClassifyAsync(installPath, installedPath, manifest, bundle, cancellationToken)
            : "invalid";
        return new GuidanceBundleStatus(
            bundle.Host,
            bundle.Format,
            manifest.BundleVersion,
            bundle.Checksum,
            manifest.MinimumExecutableVersion,
            manifest.MaximumExecutableVersion,
            manifest.MinimumContractVersion,
            manifest.MaximumContractVersion,
            manifest.OperationIds,
            status,
            installPath);
    }

    private static async Task<string> ClassifyAsync(string installPath, string installedPath, GuidanceManifest manifest, GuidanceBundleManifest bundle, CancellationToken cancellationToken)
    {
        var contentExists = File.Exists(installPath);
        var manifestExists = File.Exists(installedPath);
        if (!contentExists && !manifestExists) return "missing";
        if (!contentExists || !manifestExists) return "invalid";

        InstalledGuidanceManifest? installed;
        try
        {
            var bytes = await File.ReadAllBytesAsync(installedPath, cancellationToken);
            installed = JsonSerializer.Deserialize(bytes, LedgerJsonContext.Default.InstalledGuidanceManifest);
        }
        catch (JsonException) { return "invalid"; }
        catch (IOException) { return "invalid"; }
        catch (UnauthorizedAccessException) { return "invalid"; }

        if (installed is null || installed.SchemaVersion != manifest.SchemaVersion
            || installed.Host != bundle.Host || installed.Format != bundle.Format
            || !TryVersion(installed.BundleVersion, out var installedVersion)
            || !TryVersion(manifest.BundleVersion, out var bundledVersion)) return "invalid";
        if (installedVersion < bundledVersion) return "outdated";
        if (installedVersion > bundledVersion) return "newer";
        if (installed.Checksum != bundle.Checksum
            || installed.MinimumExecutableVersion != manifest.MinimumExecutableVersion
            || installed.MaximumExecutableVersion != manifest.MaximumExecutableVersion
            || installed.MinimumContractVersion != manifest.MinimumContractVersion
            || installed.MaximumContractVersion != manifest.MaximumContractVersion
            || !installed.OperationIds.SequenceEqual(manifest.OperationIds, StringComparer.Ordinal)) return "invalid";

        try
        {
            await using var stream = new FileStream(installPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return string.Equals(checksum, bundle.Checksum, StringComparison.Ordinal) ? "compatible" : "invalid";
        }
        catch (IOException) { return "invalid"; }
        catch (UnauthorizedAccessException) { return "invalid"; }
    }

    private static bool TryResolveScope(string value, out string scope)
    {
        scope = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        if (value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment == "..")) return false;
        try { scope = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return scope != Path.GetPathRoot(scope) && SafeExistingPath(scope);
    }

    private static string? ResolveInstallPath(string scope, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath)) return null;
        var destination = Path.GetFullPath(Path.Combine(scope, relativePath));
        return destination.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? destination : null;
    }

    private static bool SafeExistingPath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || File.Exists(current)) return false;
        }
        return true;
    }

    private static bool SafeInstallDestination(string installPath)
    {
        var directory = Path.GetDirectoryName(installPath)!;
        var installedManifestPath = Path.Combine(directory, InstalledManifestName);
        return SafeExistingPath(directory) && !IsSymbolicLink(installPath) && !IsSymbolicLink(installedManifestPath);
    }

    private static bool IsSymbolicLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { return true; }
    }

    private static byte[]? ReadResource(string resourceName)
    {
        using var stream = typeof(GuidanceService).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool ContainsForbiddenContent(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        return ForbiddenContent.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    private static bool RangeContains(string minimum, string maximum, string current) =>
        TryVersion(minimum, out var min) && TryVersion(maximum, out var max) && TryVersion(current, out var value) && min <= value && value <= max;
    private static bool TryVersion(string value, out Version version) => Version.TryParse(value, out version!);

    private sealed record CatalogResult(GuidanceManifest? Manifest, string? ErrorCode)
    {
        public bool IsSuccess => ErrorCode is null;
        public static CatalogResult Success(GuidanceManifest manifest) => new(manifest, null);
        public static CatalogResult Failure(string errorCode) => new(null, errorCode);
    }
}
