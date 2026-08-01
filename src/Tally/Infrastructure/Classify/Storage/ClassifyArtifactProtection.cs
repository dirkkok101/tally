using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// Fixed owner-only path protection and same-filesystem quarantine staging for CLASSIFY
/// (DD-CLASSIFY-ARTIFACT-RETENTION / NFR-CLASSIFY-LOCAL-DATA-PROTECTION).
/// Fail-closed on linux-x64: never follows symlinks, never globs outside the CLASSIFY root,
/// never removes unknown names, never deletes before durable authority is committed.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyArtifactProtection
{
    private readonly ClassifyStorePaths paths;
    private readonly HostArtifactProtection host;

    public ClassifyArtifactProtection(ClassifyStorePaths paths, HostArtifactProtection? host = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        this.paths = paths;
        this.host = host ?? new HostArtifactProtection();
    }

    public ClassifyArtifactProtection(string dataRoot, HostArtifactProtection? host = null)
        : this(new ClassifyStorePaths(dataRoot), host)
    {
    }

    public ClassifyStorePaths Paths => paths;

    public string QuarantineRoot => Path.Combine(paths.ClassifyDirectory, "quarantine");

    public void EnsureClassifyLayout()
    {
        host.EnsureDataRoot(paths.DataRoot);
        host.EnsureDataRoot(paths.ClassifyDirectory);
        host.EnsureDataRoot(paths.TemporaryDirectory);
        host.EnsureDataRoot(paths.ReportsDirectory);
        host.EnsureDataRoot(QuarantineRoot);
    }

    public bool IsSymbolicLink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null
                || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    public bool IsRegularFile(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path) || IsSymbolicLink(path))
        {
            return false;
        }

        try
        {
            var attrs = File.GetAttributes(path);
            return !attrs.HasFlag(FileAttributes.Directory)
                && !attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    public bool IsOwnerOnlyFile(string path)
    {
        try
        {
            host.RequireOwnerOnlyArtifact(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsOwnerOnlyDirectory(string path)
    {
        try
        {
            host.RequireOwnerOnlyDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsContainedInClassifyRoot(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(paths.ClassifyDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(candidatePath);
            return full.StartsWith(root, StringComparison.Ordinal)
                || string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(paths.ClassifyDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public bool IsOutsideClassifyRoot(string candidatePath) =>
        !IsContainedInClassifyRoot(candidatePath);

    public IReadOnlyList<string> ListRecognizedTemporaryFileNames()
    {
        EnsureClassifyLayout();
        if (!Directory.Exists(paths.TemporaryDirectory) || IsSymbolicLink(paths.TemporaryDirectory))
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     paths.TemporaryDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(name)
                && IsRegularFile(path)
                && IsContainedInClassifyRoot(path)
                && IsOwnerOnlyFile(path))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Count of recognized temporary files currently retained under classify/tmp.</summary>
    public int CountRecognizedTemporaryArtifacts() => ListRecognizedTemporaryFileNames().Count;

    public string CreateRecognizedTemporaryForTests(string fileName, byte[] content)
    {
        if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(fileName))
        {
            throw new InvalidOperationException("Test temporary name is not a recognized CLASSIFY temporary.");
        }

        EnsureClassifyLayout();
        var full = Path.Combine(paths.TemporaryDirectory, fileName);
        if (!IsContainedInClassifyRoot(full))
        {
            throw new InvalidOperationException("Temporary path escapes CLASSIFY root.");
        }

        File.WriteAllBytes(full, content);
        host.ProtectArtifact(full);
        return full;
    }

    /// <summary>
    /// Prevalidate and stage recognized temporary names into an owner-only same-filesystem quarantine.
    /// Never deletes originals until durable authority is committed and FinalizeCommitted is called.
    /// On partial failure, restores already-staged names and returns null.
    /// </summary>
    public ClassifyArtifactQuarantine? TryStageRecognizedTemporaries(
        string operationId,
        string kind,
        IReadOnlyList<string> temporaryFileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(temporaryFileNames);
        EnsureClassifyLayout();

        // Full prevalidation before any rename.
        var unique = temporaryFileNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        foreach (var name in unique)
        {
            if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(name))
            {
                return null;
            }

            var full = Path.Combine(paths.TemporaryDirectory, name);
            if (!IsContainedInClassifyRoot(full)
                || IsSymbolicLink(full)
                || !IsRegularFile(full)
                || !IsOwnerOnlyFile(full)
                || AppearsLocked(full))
            {
                return null;
            }
        }

        var quarantineDir = Path.Combine(QuarantineRoot, SanitizeOperationId(operationId));
        var stagedDir = Path.Combine(quarantineDir, "staged");
        try
        {
            if (Directory.Exists(quarantineDir))
            {
                // Never clobber an existing quarantine operation.
                return null;
            }

            host.EnsureDataRoot(quarantineDir);
            host.EnsureDataRoot(stagedDir);

            var entries = new List<QuarantineEntry>(unique.Length);
            for (var i = 0; i < unique.Length; i++)
            {
                var name = unique[i];
                var source = Path.Combine(paths.TemporaryDirectory, name);
                var stagedName = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var dest = Path.Combine(stagedDir, stagedName);
                File.Move(source, dest);
                host.ProtectArtifact(dest);
                entries.Add(new QuarantineEntry(name, stagedName));
            }

            var manifest = new QuarantineManifest(
                OperationId: operationId.Trim(),
                Kind: kind.Trim(),
                Committed: false,
                Entries: entries);
            WriteManifest(quarantineDir, manifest);
            return new ClassifyArtifactQuarantine(this, quarantineDir, manifest);
        }
        catch
        {
            // Best-effort restore any partial stage.
            try
            {
                if (Directory.Exists(stagedDir))
                {
                    foreach (var staged in Directory.EnumerateFiles(stagedDir))
                    {
                        // Cannot map without manifest — leave for startup recovery.
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }

    /// <summary>
    /// Startup recovery: restore uncommitted quarantine or delete committed quarantine
    /// using durable authority evidence supplied by the caller (tombstone/cleanup-event presence).
    /// </summary>
    public int RecoverQuarantineAtStartup(Func<string, string, bool> hasDurableAuthority)
    {
        ArgumentNullException.ThrowIfNull(hasDurableAuthority);
        EnsureClassifyLayout();
        if (!Directory.Exists(QuarantineRoot) || IsSymbolicLink(QuarantineRoot))
        {
            return 0;
        }

        var actions = 0;
        foreach (var dir in Directory.EnumerateDirectories(QuarantineRoot))
        {
            if (IsSymbolicLink(dir) || !IsContainedInClassifyRoot(dir))
            {
                continue;
            }

            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!IsRegularFile(manifestPath))
            {
                continue;
            }

            QuarantineManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath),
                    QuarantineJsonContext.Default.QuarantineManifest);
            }
            catch
            {
                continue;
            }

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.OperationId))
            {
                continue;
            }

            var durable = hasDurableAuthority(manifest.Kind, manifest.OperationId)
                || manifest.Committed;
            if (durable)
            {
                // Committed: drop staged content permanently (authority already durable).
                try
                {
                    Directory.Delete(dir, recursive: true);
                    actions++;
                }
                catch
                {
                    // fail closed leave for next startup
                }
            }
            else
            {
                // Uncommitted: restore names then remove quarantine dir.
                try
                {
                    RestoreFromDirectory(dir, manifest);
                    Directory.Delete(dir, recursive: true);
                    actions++;
                }
                catch
                {
                    // leave for next startup
                }
            }
        }

        return actions;
    }

    internal void FinalizeCommitted(ClassifyArtifactQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        var updated = quarantine.Manifest with { Committed = true };
        WriteManifest(quarantine.Directory, updated);
        // Permanent delete of staged content after durable authority.
        try
        {
            Directory.Delete(quarantine.Directory, recursive: true);
        }
        catch
        {
            // Startup will delete committed quarantine via durable evidence.
        }
    }

    internal void RestoreAndDiscard(ClassifyArtifactQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        RestoreFromDirectory(quarantine.Directory, quarantine.Manifest);
        try
        {
            Directory.Delete(quarantine.Directory, recursive: true);
        }
        catch
        {
            // startup recovers remaining
        }
    }

    private void RestoreFromDirectory(string quarantineDir, QuarantineManifest manifest)
    {
        var stagedDir = Path.Combine(quarantineDir, "staged");
        foreach (var entry in manifest.Entries)
        {
            var staged = Path.Combine(stagedDir, entry.StagedName);
            if (!IsRegularFile(staged))
            {
                continue;
            }

            if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(entry.OriginalName))
            {
                continue;
            }

            var dest = Path.Combine(paths.TemporaryDirectory, entry.OriginalName);
            if (!IsContainedInClassifyRoot(dest))
            {
                continue;
            }

            if (File.Exists(dest))
            {
                // Collision: leave staged for operator; do not overwrite.
                continue;
            }

            File.Move(staged, dest);
            host.ProtectArtifact(dest);
        }
    }

    private void WriteManifest(string quarantineDir, QuarantineManifest manifest)
    {
        var path = Path.Combine(quarantineDir, "manifest.json");
        var json = JsonSerializer.Serialize(manifest, QuarantineJsonContext.Default.QuarantineManifest);
        var tmp = path + ".partial";
        File.WriteAllText(tmp, json);
        host.ProtectArtifact(tmp);
        File.Move(tmp, path, overwrite: true);
        host.ProtectArtifact(path);
    }

    private static string SanitizeOperationId(string operationId)
    {
        var trimmed = operationId.Trim();
        Span<char> buffer = stackalloc char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            buffer[i] = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_';
        }

        return new string(buffer);
    }

    private static bool AppearsLocked(string path)
    {
        var lockCompanion = path + ".lock";
        if (File.Exists(lockCompanion))
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>Opaque staged removal session — reversible until FinalizeCommitted.</summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyArtifactQuarantine
{
    private readonly ClassifyArtifactProtection protection;

    internal ClassifyArtifactQuarantine(
        ClassifyArtifactProtection protection,
        string directory,
        QuarantineManifest manifest)
    {
        this.protection = protection;
        Directory = directory;
        Manifest = manifest;
    }

    public string Directory { get; }
    public QuarantineManifest Manifest { get; }
    public int StagedCount => Manifest.Entries.Count;

    public void FinalizeCommitted() => protection.FinalizeCommitted(this);

    public void RestoreAndDiscard() => protection.RestoreAndDiscard(this);
}

public sealed record QuarantineManifest(
    string OperationId,
    string Kind,
    bool Committed,
    IReadOnlyList<QuarantineEntry> Entries);

public sealed record QuarantineEntry(string OriginalName, string StagedName);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(QuarantineManifest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(QuarantineEntry))]
internal partial class QuarantineJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
