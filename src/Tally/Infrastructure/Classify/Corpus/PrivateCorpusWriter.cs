using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Tally.Contracts.Classify.Operations;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Protected atomic publisher for owner-private validation JSONL corpora
/// (DD-CLASSIFY-PRIVATE-CORPUS-PUBLICATION / TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER).
/// Same-directory recognized temporary at 0600 opened with O_CREAT|O_EXCL|O_NOFOLLOW,
/// written and flushed on the retained descriptor, validated by <see cref="PrivateCorpusReader"/>
/// against the same inode identity, then published with <c>renameat2(RENAME_NOREPLACE)</c>
/// (kernel-enforced no-replace) and parent-directory fsync.
/// Full absolute parent-chain components are owner-only non-symlink directories (0700).
/// Never unlinks an unknown path — only the recognized temporary this instance created.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateCorpusWriter
{
    /// <summary>Recognized temporary name prefix (same directory as destination).</summary>
    public const string RecognizedTempPrefix = ".tally-corpus-build-";

    /// <summary>Recognized temporary name suffix.</summary>
    public const string RecognizedTempSuffix = ".tmp";

    private static readonly UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UnixFileMode OwnerFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UnixFileMode ForbiddenSharingBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly PrivateCorpusReader reader;

    public PrivateCorpusWriter(PrivateCorpusReader? reader = null)
    {
        this.reader = reader ?? new PrivateCorpusReader();
    }

    /// <summary>
    /// Atomically publish ordered private corpus rows to an absolute regular-file destination
    /// that does not already exist. Paths never appear in error codes.
    /// </summary>
    public async Task<PrivateCorpusPublishResult> PublishAsync(
        string? absoluteDestinationPath,
        IReadOnlyList<PrivateCorpusRow> rows,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(absoluteDestinationPath))
        {
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.PathRequired);
        }

        if (rows is null)
        {
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.FieldInvalid);
        }

        if (rows.Count is < 1 or > PrivateCorpusLimits.MaxRowCount)
        {
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.LimitExceeded);
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Private corpus publication requires Linux.");
        }

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(PrivateCorpusLimits.MaxProcessingTimeMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked.Token;

        string? recognizedTempPath = null;
        SafeFileHandle? tempHandle = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var destination = absoluteDestinationPath.Trim();
            if (!Path.IsPathFullyQualified(destination)
                || destination.Contains('\0', StringComparison.Ordinal)
                || destination.EndsWith(Path.DirectorySeparatorChar)
                || destination.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return PrivateCorpusPublishResult.Failure(ClassifyPrivacyOrInvalid());
            }

            // Full parent-chain containment: every intermediate component is a real (non-symlink)
            // owner-only 0700 directory. Final parent included.
            if (!TryValidateOwnerOnlyDirectoryChain(destination, out var parent, out var chainError))
            {
                return PrivateCorpusPublishResult.Failure(chainError!);
            }

            // Pre-check destination absence (advisory). Kernel NOREPLACE is the authoritative guard.
            if (PathExistsNoFollow(destination))
            {
                return PrivateCorpusPublishResult.Failure(MapDestinationExists());
            }

            var payload = EncodeJsonl(rows, out var encodeError);
            if (payload is null)
            {
                return PrivateCorpusPublishResult.Failure(encodeError!);
            }

            if (payload.Length > PrivateCorpusLimits.MaxFileUtf8Bytes)
            {
                return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.LimitExceeded);
            }

            recognizedTempPath = Path.Combine(
                parent,
                RecognizedTempPrefix + Guid.NewGuid().ToString("N") + RecognizedTempSuffix);

            // Create and retain the O_EXCL descriptor for the full write/flush/identity lifetime.
            if (!TryCreateOwnerOnlyTemp(recognizedTempPath, out tempHandle, out var createError))
            {
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(createError!);
            }

            var fd = tempHandle.DangerousGetHandle().ToInt32();
            if (!TryFstatIdentity(fd, out var tempDev, out var tempIno, out var tempNlink, out var idError)
                || tempNlink != 1)
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(idError ?? PrivateCorpusErrors.PermissionsRejected);
            }

            ct.ThrowIfCancellationRequested();
            if (!TryWriteAllAndFlushOnFd(tempHandle, payload, out var writeError))
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(writeError!);
            }

            // Close write handle only after durable flush; identity remains on the path inode.
            // Re-stat via path must match the retained create identity (detects substitution).
            tempHandle.Dispose();
            tempHandle = null;

            if (!TryLstatIdentity(recognizedTempPath, out var pathDev, out var pathIno, out var pathNlink, out var pathIdError)
                || pathDev != tempDev
                || pathIno != tempIno
                || pathNlink != 1)
            {
                // Path no longer names our O_EXCL inode (swap/hard-link attack) — refuse.
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(
                    pathIdError ?? PrivateCorpusErrors.PermissionsRejected);
            }

            // Validate through the production reader against the same path/inode.
            ct.ThrowIfCancellationRequested();
            var validation = await reader.ReadAsync(recognizedTempPath, ct);
            if (!validation.IsSuccess || validation.Fingerprint is null)
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(
                    validation.ErrorCode ?? PrivateCorpusErrors.Malformed);
            }

            if (validation.RowCount != rows.Count
                || validation.Fingerprint.ByteLength != payload.Length)
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Malformed);
            }

            // Re-bind identity after reader open/close (still our inode, still nlink==1).
            if (!TryLstatIdentity(recognizedTempPath, out pathDev, out pathIno, out pathNlink, out pathIdError)
                || pathDev != tempDev
                || pathIno != tempIno
                || pathNlink != 1)
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(
                    pathIdError ?? PrivateCorpusErrors.PermissionsRejected);
            }

            // Re-validate full parent chain immediately before publication (TOCTOU).
            if (!TryValidateOwnerOnlyDirectoryChain(destination, out _, out var rechainError))
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(rechainError!);
            }

            ct.ThrowIfCancellationRequested();
            // Kernel-enforced no-replace: renameat2(RENAME_NOREPLACE) cannot overwrite a racer.
            if (!TryRenameNoReplace(recognizedTempPath, destination, out var renameError))
            {
                CleanupTemp(ref tempHandle, ref recognizedTempPath);
                return PrivateCorpusPublishResult.Failure(renameError!);
            }

            // Temp name is gone after successful rename; do not unlink destination.
            recognizedTempPath = null;

            if (!TryValidatePublishedDestination(destination, out var destError))
            {
                return PrivateCorpusPublishResult.Failure(destError!);
            }

            if (!TryFsyncDirectory(parent, out var parentFlushError))
            {
                return PrivateCorpusPublishResult.Failure(parentFlushError!);
            }

            return PrivateCorpusPublishResult.Success(
                validation.Fingerprint,
                rows.Count,
                validation.Fingerprint.ByteLength);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            CleanupTemp(ref tempHandle, ref recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Timeout);
        }
        catch (OperationCanceledException)
        {
            CleanupTemp(ref tempHandle, ref recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Cancelled);
        }
        catch (IOException)
        {
            CleanupTemp(ref tempHandle, ref recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            CleanupTemp(ref tempHandle, ref recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.PermissionsRejected);
        }
        finally
        {
            tempHandle?.Dispose();
        }
    }

    /// <summary>
    /// True when <paramref name="fileName"/> is a recognized same-directory temporary name
    /// produced by this writer (prefix/suffix only — never a glob over arbitrary paths).
    /// </summary>
    public static bool IsRecognizedTemporaryName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);
        return name.StartsWith(RecognizedTempPrefix, StringComparison.Ordinal)
               && name.EndsWith(RecognizedTempSuffix, StringComparison.Ordinal)
               && name.Length > RecognizedTempPrefix.Length + RecognizedTempSuffix.Length;
    }

    /// <summary>
    /// Delete only a recognized temporary path this writer family creates. Refuses destination
    /// paths and unknown names. Returns false when the path was not removed (absent or refused).
    /// </summary>
    public static bool TryDeleteRecognizedTemp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsRecognizedTemporaryName(path))
        {
            return false;
        }

        try
        {
            if (!PathExistsNoFollow(path))
            {
                return false;
            }

            if (Lstat(path, out var st) != 0)
            {
                return false;
            }

            if ((st.st_mode & FileTypeMask) != RegularFileType || st.st_nlink != 1)
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupTemp(ref SafeFileHandle? handle, ref string? recognizedTempPath)
    {
        handle?.Dispose();
        handle = null;
        if (recognizedTempPath is not null)
        {
            TryDeleteRecognizedTemp(recognizedTempPath);
            recognizedTempPath = null;
        }
    }

    private static byte[]? EncodeJsonl(IReadOnlyList<PrivateCorpusRow> rows, out string? errorCode)
    {
        errorCode = null;
        using var buffer = new MemoryStream();
        foreach (var row in rows.OrderBy(r => r.Ordinal).ThenBy(r => r.TransactionId, StringComparer.Ordinal))
        {
            byte[] line;
            try
            {
                line = JsonSerializer.SerializeToUtf8Bytes(row, PrivateCorpusJsonContext.Default.PrivateCorpusRow);
            }
            catch (JsonException)
            {
                errorCode = PrivateCorpusErrors.Malformed;
                return null;
            }

            if (line.Length + 1 > PrivateCorpusLimits.MaxLineUtf8Bytes)
            {
                errorCode = PrivateCorpusErrors.LimitExceeded;
                return null;
            }

            buffer.Write(line, 0, line.Length);
            buffer.WriteByte((byte)'\n');
            if (buffer.Length > PrivateCorpusLimits.MaxFileUtf8Bytes)
            {
                errorCode = PrivateCorpusErrors.LimitExceeded;
                return null;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Walk every absolute path component from <c>/</c> through the destination parent.
    /// Intermediate components must be real (non-symlink) directories — no path walk through links.
    /// The immediate parent must additionally be owned by euid with exact mode 0700.
    /// </summary>
    private static bool TryValidateOwnerOnlyDirectoryChain(
        string absoluteFilePath,
        out string parentDirectory,
        out string? errorCode)
    {
        errorCode = null;
        parentDirectory = string.Empty;

        if (!absoluteFilePath.StartsWith('/'))
        {
            errorCode = ClassifyPrivacyOrInvalid();
            return false;
        }

        var trimmed = absoluteFilePath.TrimEnd('/');
        if (trimmed.Length == 0 || trimmed == "/")
        {
            errorCode = ClassifyPrivacyOrInvalid();
            return false;
        }

        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(fileName))
        {
            errorCode = ClassifyPrivacyOrInvalid();
            return false;
        }

        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
        {
            errorCode = ClassifyPrivacyOrInvalid();
            return false;
        }

        // Build ordered absolute prefixes: "/", "/a", "/a/b", ... parent
        var segments = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var chain = new List<string>(segments.Length + 1) { "/" };
        var accum = string.Empty;
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                // Absolute paths must already be normalized; refuse relative segments.
                errorCode = ClassifyPrivacyOrInvalid();
                return false;
            }

            accum = accum + "/" + segment;
            chain.Add(accum);
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var component = chain[i];
            var isImmediateParent = i == chain.Count - 1;
            if (!TryValidateDirectoryComponent(component, requireOwnerOnly0700: isImmediateParent, out errorCode))
            {
                return false;
            }
        }

        parentDirectory = parent == "/" ? "/" : parent;
        return true;
    }

    private static bool TryValidateDirectoryComponent(
        string path,
        bool requireOwnerOnly0700,
        out string? errorCode)
    {
        errorCode = null;
        if (Lstat(path, out var st) != 0)
        {
            errorCode = PrivateCorpusErrors.NotFound;
            return false;
        }

        var fileType = st.st_mode & FileTypeMask;
        // Intermediate and parent components must never be symlinks (containment / no-follow).
        if (fileType == SymlinkFileType)
        {
            errorCode = PrivateCorpusErrors.SymlinkRejected;
            return false;
        }

        if (fileType != DirectoryFileType)
        {
            errorCode = PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        if (!requireOwnerOnly0700)
        {
            return true;
        }

        // Immediate parent: owner UID + exact 0700 (no group/other bits).
        if (st.st_uid != Geteuid())
        {
            errorCode = PrivateCorpusErrors.OwnerRejected;
            return false;
        }

        var mode = (UnixFileMode)(st.st_mode & PermissionBitsMask);
        if (mode != OwnerDirectoryMode || (mode & ForbiddenSharingBits) != 0)
        {
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        return true;
    }

    private static bool TryValidatePublishedDestination(string destination, out string? errorCode)
    {
        errorCode = null;
        if (Lstat(destination, out var st) != 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        if ((st.st_mode & FileTypeMask) != RegularFileType)
        {
            errorCode = PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        if (st.st_nlink != 1)
        {
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        if (st.st_uid != Geteuid())
        {
            errorCode = PrivateCorpusErrors.OwnerRejected;
            return false;
        }

        var mode = (UnixFileMode)(st.st_mode & PermissionBitsMask);
        if (mode != OwnerFileMode || (mode & ForbiddenSharingBits) != 0)
        {
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        return true;
    }

    private static bool TryCreateOwnerOnlyTemp(
        string tempPath,
        out SafeFileHandle handle,
        out string? errorCode)
    {
        handle = null!;
        errorCode = null;
        // O_CREAT|O_EXCL|O_WRONLY|O_NOFOLLOW|O_CLOEXEC, mode 0600 — retain the FD.
        var fd = Open(
            tempPath,
            OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            mode: 0x180); // 0600
        if (fd < 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorTooManySymbolicLinks => PrivateCorpusErrors.SymlinkRejected,
                ErrorExists => MapDestinationExists(),
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                ErrorNoEntry or ErrorNotDirectory => PrivateCorpusErrors.NotFound,
                _ => PrivateCorpusErrors.ReadFailed
            };
            return false;
        }

        handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        if (!TryFstatIdentity(fd, out _, out _, out var nlink, out var idError)
            || nlink != 1)
        {
            handle.Dispose();
            handle = null!;
            TryDeleteRecognizedTemp(tempPath);
            errorCode = idError ?? PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        if (Fchmod(fd, 0x180) != 0)
        {
            handle.Dispose();
            handle = null!;
            TryDeleteRecognizedTemp(tempPath);
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        return true;
    }

    private static bool TryWriteAllAndFlushOnFd(
        SafeFileHandle handle,
        byte[] payload,
        out string? errorCode)
    {
        errorCode = null;
        // Write through the retained O_EXCL descriptor only — path substitution cannot redirect us.
        var fd = handle.DangerousGetHandle().ToInt32();
        var offset = 0;
        while (offset < payload.Length)
        {
            var written = Write(fd, ref payload[offset], payload.Length - offset);
            if (written <= 0)
            {
                errorCode = PrivateCorpusErrors.ReadFailed;
                return false;
            }

            offset += written;
        }

        if (Fsync(fd) != 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Kernel-enforced no-replace rename via renameat2(RENAME_NOREPLACE).
    /// A concurrent creator of the destination cannot be overwritten.
    /// </summary>
    private static bool TryRenameNoReplace(string tempPath, string destination, out string? errorCode)
    {
        errorCode = null;
        if (Renameat2(AtFdcwd, tempPath, AtFdcwd, destination, RenameNoreplace) != 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorExists or ErrorIsDirectory => MapDestinationExists(),
                ErrorNoEntry => PrivateCorpusErrors.NotFound,
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                ErrorCrossDevice => PrivateCorpusErrors.PermissionsRejected,
                ErrorInvalid => PrivateCorpusErrors.ReadFailed, // NOREPLACE unsupported / invalid
                _ => PrivateCorpusErrors.ReadFailed
            };
            return false;
        }

        return true;
    }

    private static bool TryFsyncDirectory(string directoryPath, out string? errorCode)
    {
        errorCode = null;
        var fd = Open(directoryPath, OpenReadOnly | OpenDirectory | OpenCloseOnExec | OpenNoFollow);
        if (fd < 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                errorCode = PrivateCorpusErrors.ReadFailed;
                return false;
            }

            return true;
        }
        finally
        {
            Close(fd);
        }
    }

    private static bool TryFstatIdentity(
        int fd,
        out ulong dev,
        out ulong ino,
        out ulong nlink,
        out string? errorCode)
    {
        dev = 0;
        ino = 0;
        nlink = 0;
        errorCode = null;
        if (Fstat(fd, out var st) != 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        if ((st.st_mode & FileTypeMask) != RegularFileType)
        {
            errorCode = PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        if (st.st_uid != Geteuid())
        {
            errorCode = PrivateCorpusErrors.OwnerRejected;
            return false;
        }

        dev = st.st_dev;
        ino = st.st_ino;
        nlink = st.st_nlink;
        return true;
    }

    private static bool TryLstatIdentity(
        string path,
        out ulong dev,
        out ulong ino,
        out ulong nlink,
        out string? errorCode)
    {
        dev = 0;
        ino = 0;
        nlink = 0;
        errorCode = null;
        if (Lstat(path, out var st) != 0)
        {
            errorCode = PrivateCorpusErrors.NotFound;
            return false;
        }

        if ((st.st_mode & FileTypeMask) == SymlinkFileType)
        {
            errorCode = PrivateCorpusErrors.SymlinkRejected;
            return false;
        }

        if ((st.st_mode & FileTypeMask) != RegularFileType)
        {
            errorCode = PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        if (st.st_uid != Geteuid())
        {
            errorCode = PrivateCorpusErrors.OwnerRejected;
            return false;
        }

        dev = st.st_dev;
        ino = st.st_ino;
        nlink = st.st_nlink;
        return true;
    }

    private static bool PathExistsNoFollow(string path) => Lstat(path, out _) == 0;

    private static string MapDestinationExists() => ClassifyErrors.DestinationExists;

    private static string ClassifyPrivacyOrInvalid() => ClassifyErrors.PrivacyRejected;

    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtFdcwd = -100;
    private const uint RenameNoreplace = 1; // RENAME_NOREPLACE
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryFileType = 0x4000;
    private const uint SymlinkFileType = 0xA000;
    private const uint PermissionBitsMask = 0x0FFF;
    private const int ErrorNoEntry = 2;
    private const int ErrorAccessDenied = 13;
    private const int ErrorExists = 17;
    private const int ErrorCrossDevice = 18;
    private const int ErrorNotDirectory = 20;
    private const int ErrorIsDirectory = 21;
    private const int ErrorInvalid = 22;
    private const int ErrorTooManySymbolicLinks = 40;

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags, int mode = 0);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern int Write(int fd, ref byte buffer, int count);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int fd, int mode);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Renameat2(
        int olddirfd,
        string oldpath,
        int newdirfd,
        string newpath,
        uint flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int fd, out StatBuf buf);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Lstat(string path, out StatBuf buf);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuf
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atim_sec;
        public long st_atim_nsec;
        public long st_mtim_sec;
        public long st_mtim_nsec;
        public long st_ctim_sec;
        public long st_ctim_nsec;
        public long __glibc_reserved1;
        public long __glibc_reserved2;
        public long __glibc_reserved3;
    }
}

/// <summary>Aggregate-only publication outcome — never carries paths or row payloads.</summary>
public sealed class PrivateCorpusPublishResult
{
    private PrivateCorpusPublishResult(
        bool isSuccess,
        string? errorCode,
        CorpusFingerprint? fingerprint,
        int writtenRowCount,
        long writtenByteCount)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        Fingerprint = fingerprint;
        WrittenRowCount = writtenRowCount;
        WrittenByteCount = writtenByteCount;
    }

    public bool IsSuccess { get; }
    public string? ErrorCode { get; }
    public CorpusFingerprint? Fingerprint { get; }
    public int WrittenRowCount { get; }
    public long WrittenByteCount { get; }

    public static PrivateCorpusPublishResult Success(
        CorpusFingerprint fingerprint,
        int writtenRowCount,
        long writtenByteCount) =>
        new(true, null, fingerprint, writtenRowCount, writtenByteCount);

    public static PrivateCorpusPublishResult Failure(string errorCode) =>
        new(false, errorCode, null, 0, 0);
}
