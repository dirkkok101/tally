using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Tally.Contracts.Classify.Operations;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Protected atomic publisher for owner-private validation JSONL corpora
/// (DD-CLASSIFY-PRIVATE-CORPUS-PUBLICATION / TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER).
/// Same-directory recognized temporary at 0600 → complete write + flush →
/// <see cref="PrivateCorpusReader"/> validation → atomic rename → parent metadata flush.
/// Never follows final-component symlinks, never overwrites a different destination, and
/// never unlinks an unknown path — only the recognized temporary this instance created.
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

            var parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent))
            {
                return PrivateCorpusPublishResult.Failure(ClassifyPrivacyOrInvalid());
            }

            // Parent boundary: absolute, owner UID, exact 0700, directory, not a symlink, nlink ok.
            if (!TryValidateParentDirectory(parent, out var parentError))
            {
                return PrivateCorpusPublishResult.Failure(parentError!);
            }

            // Destination must not exist (no overwrite). Symlink/hard-link/file all rejected as exists.
            if (PathExistsNoFollow(destination))
            {
                return PrivateCorpusPublishResult.Failure(MapDestinationExists());
            }

            // Pre-size bound: refuse before any bytes if the encoded payload would exceed limits.
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

            if (!TryCreateOwnerOnlyTemp(recognizedTempPath, out var createError))
            {
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(createError!);
            }

            ct.ThrowIfCancellationRequested();
            if (!TryWriteAllAndFlush(recognizedTempPath, payload, out var writeError))
            {
                TryDeleteRecognizedTemp(recognizedTempPath);
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(writeError!);
            }

            // Validate through the production reader before any rename (same-path, no-follow).
            ct.ThrowIfCancellationRequested();
            var validation = await reader.ReadAsync(recognizedTempPath, ct);
            if (!validation.IsSuccess || validation.Fingerprint is null)
            {
                TryDeleteRecognizedTemp(recognizedTempPath);
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(
                    validation.ErrorCode ?? PrivateCorpusErrors.Malformed);
            }

            if (validation.RowCount != rows.Count
                || validation.Fingerprint.ByteLength != payload.Length)
            {
                TryDeleteRecognizedTemp(recognizedTempPath);
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Malformed);
            }

            // Destination must still be absent immediately before rename.
            if (PathExistsNoFollow(destination))
            {
                TryDeleteRecognizedTemp(recognizedTempPath);
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(MapDestinationExists());
            }

            ct.ThrowIfCancellationRequested();
            if (!TryAtomicRename(recognizedTempPath, destination, out var renameError))
            {
                // Rename failed — remove only our recognized temporary when still present.
                TryDeleteRecognizedTemp(recognizedTempPath);
                recognizedTempPath = null;
                return PrivateCorpusPublishResult.Failure(renameError!);
            }

            // Temp name is gone after successful rename; do not unlink destination.
            recognizedTempPath = null;

            // Post-rename destination must be owner-only regular file with link count 1.
            if (!TryValidatePublishedDestination(destination, out var destError))
            {
                // Do not delete destination — external recovery / owner decision.
                return PrivateCorpusPublishResult.Failure(destError!);
            }

            // Parent directory metadata flush establishes durable directory entry.
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
            TryDeleteRecognizedTemp(recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Timeout);
        }
        catch (OperationCanceledException)
        {
            TryDeleteRecognizedTemp(recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Cancelled);
        }
        catch (IOException)
        {
            TryDeleteRecognizedTemp(recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteRecognizedTemp(recognizedTempPath);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.PermissionsRejected);
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

            // Refuse to unlink if the path is not a regular owner file (symlink/hardlink ambiguity).
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

    private static bool TryValidateParentDirectory(string parent, out string? errorCode)
    {
        errorCode = null;
        if (Lstat(parent, out var st) != 0)
        {
            errorCode = PrivateCorpusErrors.NotFound;
            return false;
        }

        if ((st.st_mode & FileTypeMask) != DirectoryFileType)
        {
            errorCode = PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        // Symlink directories: lstat on a final symlink component yields S_IFLNK, not directory.
        // Intermediate symlink components are not re-walked; containment is absolute-path based.
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

    private static bool TryCreateOwnerOnlyTemp(string tempPath, out string? errorCode)
    {
        errorCode = null;
        // O_CREAT|O_EXCL|O_WRONLY|O_NOFOLLOW|O_CLOEXEC, mode 0600
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

        // Close immediately — subsequent write reopens by path with validation.
        Close(fd);

        if (Lstat(tempPath, out var st) != 0
            || (st.st_mode & FileTypeMask) != RegularFileType
            || st.st_nlink != 1
            || st.st_uid != Geteuid())
        {
            TryDeleteRecognizedTemp(tempPath);
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        var mode = (UnixFileMode)(st.st_mode & PermissionBitsMask);
        if (mode != OwnerFileMode)
        {
            // Normalize to 0600 if umask interfered (should not with explicit mode).
            try
            {
                File.SetUnixFileMode(tempPath, OwnerFileMode);
            }
            catch
            {
                TryDeleteRecognizedTemp(tempPath);
                errorCode = PrivateCorpusErrors.PermissionsRejected;
                return false;
            }
        }

        return true;
    }

    private static bool TryWriteAllAndFlush(string tempPath, byte[] payload, out string? errorCode)
    {
        errorCode = null;
        var fd = Open(tempPath, OpenWriteOnly | OpenNoFollow | OpenCloseOnExec | OpenTruncate, mode: 0);
        if (fd < 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorTooManySymbolicLinks => PrivateCorpusErrors.SymlinkRejected,
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                _ => PrivateCorpusErrors.ReadFailed
            };
            return false;
        }

        var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        try
        {
            using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 64 * 1024, isAsync: false);
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
            if (Fsync(fd) != 0)
            {
                errorCode = PrivateCorpusErrors.ReadFailed;
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static bool TryAtomicRename(string tempPath, string destination, out string? errorCode)
    {
        errorCode = null;
        // rename(2) is atomic on the same filesystem; refuses cross-device.
        if (Rename(tempPath, destination) != 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorExists or ErrorIsDirectory => MapDestinationExists(),
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                ErrorCrossDevice => PrivateCorpusErrors.PermissionsRejected,
                ErrorNoEntry => PrivateCorpusErrors.NotFound,
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

    private static bool PathExistsNoFollow(string path) => Lstat(path, out _) == 0;

    private static string MapDestinationExists() => ClassifyErrors.DestinationExists;

    private static string ClassifyPrivacyOrInvalid() => ClassifyErrors.PrivacyRejected;

    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenTruncate = 0x200;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryFileType = 0x4000;
    private const uint PermissionBitsMask = 0x0FFF;
    private const int ErrorNoEntry = 2;
    private const int ErrorAccessDenied = 13;
    private const int ErrorExists = 17;
    private const int ErrorCrossDevice = 18;
    private const int ErrorNotDirectory = 20;
    private const int ErrorIsDirectory = 21;
    private const int ErrorTooManySymbolicLinks = 40;

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags, int mode = 0);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "rename", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Rename(string oldPath, string newPath);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

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
