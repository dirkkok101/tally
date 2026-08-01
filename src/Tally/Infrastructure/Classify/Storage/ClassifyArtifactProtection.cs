using System.Runtime.Versioning;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Storage;

namespace Tally.Infrastructure.Classify.Storage;

/// <summary>
/// Fixed owner-only path and temporary-artifact protection for CLASSIFY
/// (DD-CLASSIFY-ARTIFACT-RETENTION / NFR-CLASSIFY-LOCAL-DATA-PROTECTION).
/// Fail-closed on linux-x64: never follows symlinks, never globs outside the CLASSIFY root,
/// never removes unknown names. Does not invent a generic filesystem service.
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

    /// <summary>
    /// Ensure classify directories exist with owner-only modes (0700).
    /// </summary>
    public void EnsureClassifyLayout()
    {
        host.EnsureDataRoot(paths.DataRoot);
        host.EnsureDataRoot(paths.ClassifyDirectory);
        host.EnsureDataRoot(paths.TemporaryDirectory);
        host.EnsureDataRoot(paths.ReportsDirectory);
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
            // Fail closed: treat unreadable link metadata as unsafe.
            return true;
        }
    }

    public bool IsRegularFile(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }

        if (IsSymbolicLink(path))
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

    /// <summary>
    /// True when the candidate path is strictly under the CLASSIFY directory (no .. escape).
    /// Uses full paths; does not follow the candidate if it is a symlink out of tree.
    /// </summary>
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

    /// <summary>
    /// Enumerate only recognized temporary files directly under classify/tmp (no recursion, no links).
    /// Returns file names only for policy checks; callers that delete must re-validate full path.
    /// </summary>
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
                && IsContainedInClassifyRoot(path))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Attempt to delete one recognized temporary by file name under classify/tmp.
    /// Returns true only when the file was removed after all safety checks.
    /// Never accepts absolute paths or parent segments.
    /// </summary>
    public bool TryDeleteRecognizedTemporary(string fileName)
    {
        if (!ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(fileName))
        {
            return false;
        }

        EnsureClassifyLayout();
        var full = Path.Combine(paths.TemporaryDirectory, fileName);
        if (!IsContainedInClassifyRoot(full)
            || IsSymbolicLink(full)
            || !IsRegularFile(full)
            || !IsOwnerOnlyFile(full)
            || AppearsLocked(full))
        {
            return false;
        }

        if (!ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
                isRecognizedName: true,
                isContainedInClassifyRoot: true,
                isRegularFile: true,
                isOwnerOnly: true,
                isSymlink: false,
                appearsLocked: false))
        {
            return false;
        }

        try
        {
            File.Delete(full);
            return !File.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Startup / cleanup recovery: remove recognized unlocked crash residue under classify/tmp only.
    /// Returns the number of files removed — never returns paths.
    /// </summary>
    public int RecoverRecognizedTemporaryResidue()
    {
        var removed = 0;
        foreach (var name in ListRecognizedTemporaryFileNames())
        {
            if (TryDeleteRecognizedTemporary(name))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Create a recognized temporary for tests/harnesses with owner-only mode.
    /// </summary>
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

    private static bool AppearsLocked(string path)
    {
        // Companion lock: same name + ".lock" or exclusive open failure.
        var lockCompanion = path + ".lock";
        if (File.Exists(lockCompanion))
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
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
