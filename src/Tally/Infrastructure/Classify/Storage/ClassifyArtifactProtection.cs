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
/// Staging writes a complete uncommitted manifest before any rename so every crash boundary
/// is recoverable. Finalization and startup only remove manifest-bound recognized staged
/// files after durable DB authority — never recursive unchecked sweeps.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyArtifactProtection
{
    public const string ManifestFileName = "manifest.json";
    public const string StagedDirectoryName = "staged";

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

    public bool AppearsLocked(string path)
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

    /// <summary>
    /// Partition recognized temporary names into removable (safe to stage) vs retained
    /// (locked / non-owner / non-regular / missing). Unknown names are rejected (not retained).
    /// </summary>
    public TemporaryPartition PartitionRecognizedTemporaries(IReadOnlyList<string> temporaryFileNames)
    {
        ArgumentNullException.ThrowIfNull(temporaryFileNames);
        EnsureClassifyLayout();

        var removable = new List<string>();
        var retained = new List<string>();
        var rejected = new List<string>();

        foreach (var raw in temporaryFileNames
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(raw))
            {
                rejected.Add(raw);
                continue;
            }

            var full = Path.Combine(paths.TemporaryDirectory, raw);
            if (!File.Exists(full))
            {
                // Already absent — neither removable nor retained.
                continue;
            }

            if (!IsContainedInClassifyRoot(full)
                || IsSymbolicLink(full)
                || !IsRegularFile(full)
                || !IsOwnerOnlyFile(full)
                || AppearsLocked(full))
            {
                retained.Add(raw);
                continue;
            }

            removable.Add(raw);
        }

        return new TemporaryPartition(removable, retained, rejected);
    }

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
                && IsContainedInClassifyRoot(path)
                && !IsSymbolicLink(path)
                && IsRegularFile(path))
            {
                // Include locked/owner-weak files in inventory for retained counting;
                // partitioning decides removability.
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Recognized temporary inventory including locked/non-owner files (for retained counts).
    /// </summary>
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
    /// Stage only already-validated removable recognized temporaries.
    /// Writes and protects a complete uncommitted manifest BEFORE the first rename so every
    /// crash boundary is recoverable. Partial move failure restores all moved names from the
    /// manifest deterministically.
    /// Returns null when any name is not removable (caller should partition first) or staging fails.
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

        var partition = PartitionRecognizedTemporaries(temporaryFileNames);
        if (partition.Rejected.Count > 0)
        {
            // Unknown names never enter staging.
            return null;
        }

        // Only stage removable; retained must not be in the staging list.
        var unique = partition.Removable
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // If caller requested names that partitioned as retained, refuse whole stage
        // (caller should pass only removable for stage). Empty stage is valid.
        var requested = temporaryFileNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (partition.Retained.Any(r => requested.Contains(r)))
        {
            return null;
        }

        if (unique.Length == 0)
        {
            // Empty successful stage — no quarantine directory needed.
            return new ClassifyArtifactQuarantine(
                this,
                directory: string.Empty,
                new QuarantineManifest(operationId.Trim(), kind.Trim(), Committed: false, Array.Empty<QuarantineEntry>()),
                isEmpty: true);
        }

        var quarantineDir = Path.Combine(QuarantineRoot, SanitizeOperationId(operationId));
        var stagedDir = Path.Combine(quarantineDir, StagedDirectoryName);

        if (Directory.Exists(quarantineDir))
        {
            return null;
        }

        if (!IsSafeQuarantineParent())
        {
            return null;
        }

        var entries = new List<QuarantineEntry>(unique.Length);
        for (var i = 0; i < unique.Length; i++)
        {
            entries.Add(new QuarantineEntry(
                unique[i],
                i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var manifest = new QuarantineManifest(
            OperationId: operationId.Trim(),
            Kind: kind.Trim(),
            Committed: false,
            Entries: entries);

        try
        {
            host.EnsureDataRoot(quarantineDir);
            if (!IsOwnerOnlyDirectory(quarantineDir) || IsSymbolicLink(quarantineDir)
                || !IsContainedInClassifyRoot(quarantineDir))
            {
                TryDeleteEmptyDirectory(quarantineDir);
                return null;
            }

            host.EnsureDataRoot(stagedDir);
            if (!IsOwnerOnlyDirectory(stagedDir) || IsSymbolicLink(stagedDir)
                || !IsContainedInClassifyRoot(stagedDir))
            {
                TryDeleteEmptyDirectory(stagedDir);
                TryDeleteEmptyDirectory(quarantineDir);
                return null;
            }

            // Crash-safe journal: complete uncommitted manifest BEFORE first rename.
            WriteManifest(quarantineDir, manifest);

            // Re-validate every source immediately before move (TOCTOU).
            foreach (var entry in entries)
            {
                var source = Path.Combine(paths.TemporaryDirectory, entry.OriginalName);
                if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(entry.OriginalName)
                    || !IsContainedInClassifyRoot(source)
                    || IsSymbolicLink(source)
                    || !IsRegularFile(source)
                    || !IsOwnerOnlyFile(source)
                    || AppearsLocked(source))
                {
                    // Nothing moved yet (or we fail before first move after recheck).
                    DiscardEmptyQuarantine(quarantineDir);
                    return null;
                }
            }

            var moved = new List<QuarantineEntry>();
            try
            {
                foreach (var entry in entries)
                {
                    var source = Path.Combine(paths.TemporaryDirectory, entry.OriginalName);
                    var dest = Path.Combine(stagedDir, entry.StagedName);
                    if (!IsContainedInClassifyRoot(dest))
                    {
                        throw new InvalidOperationException("Staged path escapes CLASSIFY root.");
                    }

                    File.Move(source, dest);
                    host.ProtectArtifact(dest);
                    if (!IsRegularFile(dest) || !IsOwnerOnlyFile(dest))
                    {
                        throw new InvalidOperationException("Staged file failed protection checks.");
                    }

                    moved.Add(entry);
                }
            }
            catch
            {
                // Deterministic restore of every moved name from the pre-written manifest.
                RestoreMovedEntries(quarantineDir, manifest, moved);
                DiscardEmptyQuarantine(quarantineDir);
                return null;
            }

            return new ClassifyArtifactQuarantine(this, quarantineDir, manifest, isEmpty: false);
        }
        catch
        {
            // Best-effort restore using manifest if present.
            try
            {
                if (TryReadManifest(quarantineDir, out var m) && m is not null)
                {
                    RestoreMovedEntries(quarantineDir, m, m.Entries);
                }
            }
            catch
            {
                // leave for startup
            }

            DiscardEmptyQuarantine(quarantineDir);
            return null;
        }
    }

    /// <summary>
    /// Startup recovery using durable DB authority only (cleanup_event / tombstone).
    /// Never trusts manifest.Committed alone. Validates quarantine tree fail-closed;
    /// restores uncommitted staged files or deletes only manifest-bound recognized staged files
    /// when durable authority is present. Unknown/malformed/symlink content is left untouched.
    /// </summary>
    public int RecoverQuarantineAtStartup(Func<string, string, bool> hasDurableAuthority)
    {
        ArgumentNullException.ThrowIfNull(hasDurableAuthority);
        EnsureClassifyLayout();
        if (!Directory.Exists(QuarantineRoot) || IsSymbolicLink(QuarantineRoot)
            || !IsOwnerOnlyDirectory(QuarantineRoot))
        {
            return 0;
        }

        var actions = 0;
        foreach (var dir in Directory.EnumerateDirectories(QuarantineRoot))
        {
            if (!TryValidateQuarantineDirectory(dir, out var manifest) || manifest is null)
            {
                // Malformed / unsafe quarantine — leave untouched (fail-closed).
                continue;
            }

            var durable = hasDurableAuthority(manifest.Kind, manifest.OperationId);
            if (durable)
            {
                if (TryDeleteManifestBoundStagedFiles(dir, manifest, requireEmptyOriginals: false)
                    && TryRemoveQuarantineShell(dir, manifest))
                {
                    actions++;
                }

                continue;
            }

            // Uncommitted: restore then remove empty shell only if safe.
            if (TryRestoreAllManifestEntries(dir, manifest)
                && TryRemoveQuarantineShell(dir, manifest))
            {
                actions++;
            }
        }

        return actions;
    }

    internal void FinalizeCommitted(ClassifyArtifactQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        if (quarantine.IsEmpty)
        {
            return;
        }

        // Permanent removal of staged files only — caller must already have durable authority.
        // Do not use Directory.Delete recursive; only manifest-bound staged files.
        if (!TryValidateQuarantineDirectory(quarantine.Directory, out var manifest)
            || manifest is null)
        {
            return;
        }

        if (!string.Equals(manifest.OperationId, quarantine.Manifest.OperationId, StringComparison.Ordinal))
        {
            return;
        }

        _ = TryDeleteManifestBoundStagedFiles(quarantine.Directory, manifest, requireEmptyOriginals: false);
        _ = TryRemoveQuarantineShell(quarantine.Directory, manifest);
    }

    /// <summary>
    /// Finalize only when durable authority is confirmed by the caller (DB evidence).
    /// </summary>
    internal void FinalizeWithDurableAuthority(
        ClassifyArtifactQuarantine quarantine,
        bool hasDurableAuthority)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        if (!hasDurableAuthority)
        {
            // Never delete without durable authority.
            return;
        }

        FinalizeCommitted(quarantine);
    }

    internal void RestoreAndDiscard(ClassifyArtifactQuarantine quarantine)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        if (quarantine.IsEmpty)
        {
            return;
        }

        if (!TryValidateQuarantineDirectory(quarantine.Directory, out var manifest) || manifest is null)
        {
            // Prefer in-memory manifest for restore when on-disk is untrusted.
            manifest = quarantine.Manifest;
            RestoreMovedEntries(quarantine.Directory, manifest, manifest.Entries);
            return;
        }

        _ = TryRestoreAllManifestEntries(quarantine.Directory, manifest);
        _ = TryRemoveQuarantineShell(quarantine.Directory, manifest);
    }

    private bool TryValidateQuarantineDirectory(string dir, out QuarantineManifest? manifest)
    {
        manifest = null;
        if (!IsContainedInClassifyRoot(dir)
            || IsSymbolicLink(dir)
            || !Directory.Exists(dir)
            || !IsOwnerOnlyDirectory(dir))
        {
            return false;
        }

        // Quarantine dir must be direct child of quarantine root.
        var parent = Path.GetFullPath(Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty);
        if (!string.Equals(parent, Path.GetFullPath(QuarantineRoot), StringComparison.Ordinal))
        {
            return false;
        }

        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!IsContainedInClassifyRoot(manifestPath)
            || IsSymbolicLink(manifestPath)
            || !IsRegularFile(manifestPath)
            || !IsOwnerOnlyFile(manifestPath)
            || AppearsLocked(manifestPath))
        {
            return false;
        }

        if (!TryReadManifest(dir, out manifest) || manifest is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.OperationId)
            || string.IsNullOrWhiteSpace(manifest.Kind)
            || manifest.Entries is null)
        {
            return false;
        }

        // Bind the manifest to its operation directory and to the two immutable database
        // authority types. A protected but rewritten manifest must not borrow an unrelated
        // cleanup/tombstone receipt to authorize deletion.
        if (!string.Equals(manifest.Kind, "cleanup", StringComparison.Ordinal)
            && !string.Equals(manifest.Kind, "abandon", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(
                Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
                SanitizeOperationId(manifest.OperationId),
                StringComparison.Ordinal))
        {
            return false;
        }

        // Every entry must be well-formed; staged name must match closed index form.
        var originalNames = new HashSet<string>(StringComparer.Ordinal);
        var stagedNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Entries.Count; i++)
        {
            var entry = manifest.Entries[i];
            if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(entry.OriginalName)
                || !originalNames.Add(entry.OriginalName)
                || !stagedNames.Add(entry.StagedName))
            {
                return false;
            }

            if (!string.Equals(
                    entry.StagedName,
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        var stagedDir = Path.Combine(dir, StagedDirectoryName);
        if (manifest.Entries.Count > 0)
        {
            if (!Directory.Exists(stagedDir)
                || IsSymbolicLink(stagedDir)
                || !IsOwnerOnlyDirectory(stagedDir)
                || !IsContainedInClassifyRoot(stagedDir))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryDeleteManifestBoundStagedFiles(
        string quarantineDir,
        QuarantineManifest manifest,
        bool requireEmptyOriginals)
    {
        var stagedDir = Path.Combine(quarantineDir, StagedDirectoryName);
        var allOk = true;
        foreach (var entry in manifest.Entries)
        {
            if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(entry.OriginalName))
            {
                allOk = false;
                continue;
            }

            var staged = Path.Combine(stagedDir, entry.StagedName);
            if (!IsContainedInClassifyRoot(staged))
            {
                allOk = false;
                continue;
            }

            if (!File.Exists(staged))
            {
                // Already gone — ok.
                continue;
            }

            if (IsSymbolicLink(staged)
                || !IsRegularFile(staged)
                || !IsOwnerOnlyFile(staged)
                || AppearsLocked(staged))
            {
                allOk = false;
                continue;
            }

            if (requireEmptyOriginals)
            {
                var original = Path.Combine(paths.TemporaryDirectory, entry.OriginalName);
                if (File.Exists(original))
                {
                    // Collision / unexpected original — do not delete staged.
                    allOk = false;
                    continue;
                }
            }

            try
            {
                File.Delete(staged);
            }
            catch
            {
                allOk = false;
            }
        }

        return allOk;
    }

    private bool TryRestoreAllManifestEntries(string quarantineDir, QuarantineManifest manifest)
    {
        var stagedDir = Path.Combine(quarantineDir, StagedDirectoryName);
        var allOk = true;
        foreach (var entry in manifest.Entries)
        {
            if (!TryRestoreOne(stagedDir, entry))
            {
                allOk = false;
            }
        }

        return allOk;
    }

    private bool TryRestoreOne(string stagedDir, QuarantineEntry entry)
    {
        if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(entry.OriginalName))
        {
            return false;
        }

        var staged = Path.Combine(stagedDir, entry.StagedName);
        if (!File.Exists(staged))
        {
            return true; // nothing to restore
        }

        if (!IsContainedInClassifyRoot(staged)
            || IsSymbolicLink(staged)
            || !IsRegularFile(staged)
            || !IsOwnerOnlyFile(staged)
            || AppearsLocked(staged))
        {
            return false;
        }

        var dest = Path.Combine(paths.TemporaryDirectory, entry.OriginalName);
        if (!IsContainedInClassifyRoot(dest))
        {
            return false;
        }

        if (File.Exists(dest))
        {
            // Collision — leave staged; block unsafe restore.
            return false;
        }

        try
        {
            File.Move(staged, dest);
            host.ProtectArtifact(dest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RestoreMovedEntries(
        string quarantineDir,
        QuarantineManifest manifest,
        IReadOnlyList<QuarantineEntry> moved)
    {
        var stagedDir = Path.Combine(quarantineDir, StagedDirectoryName);
        // Restore in reverse order for deterministic rollback.
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            _ = TryRestoreOne(stagedDir, moved[i]);
        }

        // Also attempt any remaining manifest entries not in moved list (crash mid-loop).
        foreach (var entry in manifest.Entries)
        {
            if (moved.Any(m => string.Equals(m.StagedName, entry.StagedName, StringComparison.Ordinal)))
            {
                continue;
            }

            _ = TryRestoreOne(stagedDir, entry);
        }
    }

    /// <summary>
    /// Remove empty quarantine shell (manifest + empty staged dir + empty op dir) only when safe.
    /// Never recursive-deletes unknown injected content.
    /// </summary>
    private bool TryRemoveQuarantineShell(string quarantineDir, QuarantineManifest manifest)
    {
        var stagedDir = Path.Combine(quarantineDir, StagedDirectoryName);
        var manifestPath = Path.Combine(quarantineDir, ManifestFileName);

        // Refuse if staged still has files (unknown or remaining).
        if (Directory.Exists(stagedDir))
        {
            if (IsSymbolicLink(stagedDir) || !IsOwnerOnlyDirectory(stagedDir))
            {
                return false;
            }

            if (Directory.EnumerateFileSystemEntries(stagedDir).Any())
            {
                // Unknown leftover — leave untouched.
                return false;
            }

            try
            {
                Directory.Delete(stagedDir, recursive: false);
            }
            catch
            {
                return false;
            }
        }

        // Only delete our known manifest file.
        if (File.Exists(manifestPath))
        {
            if (!IsContainedInClassifyRoot(manifestPath)
                || IsSymbolicLink(manifestPath)
                || !IsRegularFile(manifestPath)
                || !IsOwnerOnlyFile(manifestPath)
                || AppearsLocked(manifestPath))
            {
                return false;
            }

            try
            {
                File.Delete(manifestPath);
            }
            catch
            {
                return false;
            }
        }

        // Refuse if operation dir has any remaining entries (injected content).
        if (Directory.Exists(quarantineDir))
        {
            if (Directory.EnumerateFileSystemEntries(quarantineDir).Any())
            {
                return false;
            }

            try
            {
                Directory.Delete(quarantineDir, recursive: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private bool IsSafeQuarantineParent() =>
        Directory.Exists(QuarantineRoot)
        && !IsSymbolicLink(QuarantineRoot)
        && IsOwnerOnlyDirectory(QuarantineRoot)
        && IsContainedInClassifyRoot(QuarantineRoot);

    private void DiscardEmptyQuarantine(string quarantineDir)
    {
        try
        {
            var stagedDir = Path.Combine(quarantineDir, StagedDirectoryName);
            var manifestPath = Path.Combine(quarantineDir, ManifestFileName);
            if (Directory.Exists(stagedDir) && !Directory.EnumerateFileSystemEntries(stagedDir).Any())
            {
                Directory.Delete(stagedDir, recursive: false);
            }

            if (File.Exists(manifestPath) && IsRegularFile(manifestPath) && IsOwnerOnlyFile(manifestPath))
            {
                // Only if no staged files remain.
                if (!Directory.Exists(stagedDir) || !Directory.EnumerateFileSystemEntries(stagedDir).Any())
                {
                    File.Delete(manifestPath);
                }
            }

            if (Directory.Exists(quarantineDir) && !Directory.EnumerateFileSystemEntries(quarantineDir).Any())
            {
                Directory.Delete(quarantineDir, recursive: false);
            }
        }
        catch
        {
            // leave for startup
        }
    }

    private static void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private bool TryReadManifest(string quarantineDir, out QuarantineManifest? manifest)
    {
        manifest = null;
        var path = Path.Combine(quarantineDir, ManifestFileName);
        if (!IsRegularFile(path) || !IsOwnerOnlyFile(path))
        {
            return false;
        }

        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                QuarantineJsonContext.Default.QuarantineManifest);
            return manifest is not null;
        }
        catch
        {
            return false;
        }
    }

    private void WriteManifest(string quarantineDir, QuarantineManifest manifest)
    {
        var path = Path.Combine(quarantineDir, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, QuarantineJsonContext.Default.QuarantineManifest);
        var tmp = path + ".partial";
        // Never leave .partial as recoverable authority; write then replace.
        File.WriteAllText(tmp, json);
        host.ProtectArtifact(tmp);
        File.Move(tmp, path, overwrite: true);
        host.ProtectArtifact(path);
        if (!IsRegularFile(path) || !IsOwnerOnlyFile(path))
        {
            throw new InvalidOperationException("Manifest failed owner-only protection.");
        }
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
}

/// <summary>Partition of temporary names for cleanup (removable vs retained recognized).</summary>
public sealed record TemporaryPartition(
    IReadOnlyList<string> Removable,
    IReadOnlyList<string> Retained,
    IReadOnlyList<string> Rejected);

/// <summary>Opaque staged removal session — reversible until durable-authority finalization.</summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyArtifactQuarantine
{
    private readonly ClassifyArtifactProtection protection;

    internal ClassifyArtifactQuarantine(
        ClassifyArtifactProtection protection,
        string directory,
        QuarantineManifest manifest,
        bool isEmpty)
    {
        this.protection = protection;
        Directory = directory;
        Manifest = manifest;
        IsEmpty = isEmpty;
    }

    public string Directory { get; }
    public QuarantineManifest Manifest { get; }
    public bool IsEmpty { get; }
    public int StagedCount => Manifest.Entries.Count;

    /// <summary>
    /// Permanently remove staged files only after caller has durable DB authority.
    /// </summary>
    public void FinalizeWithDurableAuthority(bool hasDurableAuthority) =>
        protection.FinalizeWithDurableAuthority(this, hasDurableAuthority);

    public void FinalizeCommitted() =>
        protection.FinalizeWithDurableAuthority(this, hasDurableAuthority: true);

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
