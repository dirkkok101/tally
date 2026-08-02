using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Tally.Contracts.Classify.Operations;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Optional production-path fault injection points for adversarial tests.
/// Null in production; when set, callbacks run at the named seam on the live publish path.
/// </summary>
public sealed class PrivateCorpusPublishFaultSeam
{
    /// <summary>After durable write+fsync of the retained O_EXCL fd, before content validation.</summary>
    public Action<PrivateCorpusPublishCheckpoint>? AfterWriteBeforeValidate { get; set; }

    /// <summary>After FD-bound content validation, before atomic publication from the retained inode.</summary>
    public Action<PrivateCorpusPublishCheckpoint>? AfterValidateBeforePublish { get; set; }

    /// <summary>After successful publication of the retained inode, before temp-name cleanup.</summary>
    public Action<PrivateCorpusPublishCheckpoint>? AfterPublishBeforeCleanup { get; set; }
}

/// <summary>Live publish checkpoint exposed to fault seams (paths + exact created identity).</summary>
public sealed record PrivateCorpusPublishCheckpoint(
    string TemporaryPath,
    string DestinationPath,
    string ParentDirectory,
    ulong CreatedDev,
    ulong CreatedIno);

/// <summary>
/// Protected atomic publisher for owner-private validation JSONL corpora
/// (DD-CLASSIFY-PRIVATE-CORPUS-PUBLICATION / TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER).
/// The O_CREAT|O_EXCL descriptor is retained through write, fsync, content validation, and
/// publication. Publication uses <c>linkat(AT_EMPTY_PATH)</c> from that descriptor so the
/// published inode is exactly the created one (not a pathname that can be swapped). Destination
/// already-present is refused with EEXIST (no-replace). Cleanup unlinks a temporary name only
/// when openat+fstat proves the directory entry still names the exact created dev/ino.
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
    private readonly PrivateCorpusPublishFaultSeam? faultSeam;

    public PrivateCorpusWriter(
        PrivateCorpusReader? reader = null,
        PrivateCorpusPublishFaultSeam? faultSeam = null)
    {
        this.reader = reader ?? new PrivateCorpusReader();
        this.faultSeam = faultSeam;
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
        string? tempFileName = null;
        SafeFileHandle? fileHandle = null;
        SafeFileHandle? parentHandle = null;
        ulong createdDev = 0;
        ulong createdIno = 0;
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

            if (!TryValidateOwnerOnlyDirectoryChain(destination, out var parent, out var chainError))
            {
                return PrivateCorpusPublishResult.Failure(chainError!);
            }

            var destFileName = Path.GetFileName(destination);
            if (string.IsNullOrEmpty(destFileName))
            {
                return PrivateCorpusPublishResult.Failure(ClassifyPrivacyOrInvalid());
            }

            // Hold the parent directory descriptor for openat/linkat/unlinkat (no intermediate walk).
            if (!TryOpenParentDirectory(parent, out parentHandle, out var parentOpenError))
            {
                return PrivateCorpusPublishResult.Failure(parentOpenError!);
            }

            // Advisory pre-check; linkat EEXIST is authoritative no-replace.
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

            tempFileName = RecognizedTempPrefix + Guid.NewGuid().ToString("N") + RecognizedTempSuffix;
            recognizedTempPath = Path.Combine(parent, tempFileName);

            // Create and RETAIN the O_EXCL descriptor for write → validate → publish.
            if (!TryCreateOwnerOnlyTemp(
                    parentHandle,
                    tempFileName,
                    recognizedTempPath,
                    out fileHandle,
                    out createdDev,
                    out createdIno,
                    out var createError))
            {
                recognizedTempPath = null;
                tempFileName = null;
                return PrivateCorpusPublishResult.Failure(createError!);
            }

            ct.ThrowIfCancellationRequested();
            if (!TryWriteAllAndFlushOnFd(fileHandle, payload, out var writeError))
            {
                CleanupCreatedTemp(
                    parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
                return PrivateCorpusPublishResult.Failure(writeError!);
            }

            var checkpoint = new PrivateCorpusPublishCheckpoint(
                recognizedTempPath,
                destination,
                parent,
                createdDev,
                createdIno);
            faultSeam?.AfterWriteBeforeValidate?.Invoke(checkpoint);

            // Content validation against the retained descriptor (never re-open the mutable pathname).
            ct.ThrowIfCancellationRequested();
            if (!TryValidateContentFromFd(
                    fileHandle,
                    rows.Count,
                    payload.Length,
                    out var fingerprint,
                    out var validateError))
            {
                CleanupCreatedTemp(
                    parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
                return PrivateCorpusPublishResult.Failure(validateError!);
            }

            // Optional cross-check: production reader via /proc self-fd is NOT used (symlink);
            // retained-fd validation is the authoritative bound check. Reader type remains for
            // destination post-checks and recovery paths outside this writer.
            _ = reader;

            faultSeam?.AfterValidateBeforePublish?.Invoke(checkpoint);

            // Re-validate parent chain + held parent fd before publication.
            if (!TryValidateOwnerOnlyDirectoryChain(destination, out _, out var rechainError))
            {
                CleanupCreatedTemp(
                    parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
                return PrivateCorpusPublishResult.Failure(rechainError!);
            }

            ct.ThrowIfCancellationRequested();
            // Publish the exact retained inode (not a pathname). EEXIST ⇒ destination exists.
            if (!TryLinkAtEmptyPath(fileHandle, parentHandle, destFileName, out var publishError))
            {
                CleanupCreatedTemp(
                    parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
                return PrivateCorpusPublishResult.Failure(publishError!);
            }

            faultSeam?.AfterPublishBeforeCleanup?.Invoke(checkpoint);

            // Destination now names our inode. Best-effort identity-bound temp name removal only.
            TryUnlinkExactCreatedName(
                parentHandle,
                tempFileName,
                createdDev,
                createdIno);
            recognizedTempPath = null;
            tempFileName = null;

            // Release the original descriptor after publication (inode remains via dest name).
            fileHandle.Dispose();
            fileHandle = null;

            if (!TryValidatePublishedDestination(destination, createdDev, createdIno, out var destError))
            {
                return PrivateCorpusPublishResult.Failure(destError!);
            }

            if (!TryFsyncDirectoryFd(parentHandle, out var parentFlushError))
            {
                return PrivateCorpusPublishResult.Failure(parentFlushError!);
            }

            return PrivateCorpusPublishResult.Success(
                fingerprint!,
                rows.Count,
                fingerprint!.ByteLength);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            CleanupCreatedTemp(
                parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Timeout);
        }
        catch (OperationCanceledException)
        {
            CleanupCreatedTemp(
                parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.Cancelled);
        }
        catch (IOException)
        {
            CleanupCreatedTemp(
                parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            CleanupCreatedTemp(
                parentHandle, tempFileName, recognizedTempPath, createdDev, createdIno, ref fileHandle);
            return PrivateCorpusPublishResult.Failure(PrivateCorpusErrors.PermissionsRejected);
        }
        finally
        {
            fileHandle?.Dispose();
            parentHandle?.Dispose();
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
    /// Delete a recognized temporary path only when it still names the exact created inode
    /// (<paramref name="expectedDev"/> / <paramref name="expectedIno"/>). Never deletes by
    /// prefix recognition alone. Returns false when absent, mismatched, or refused.
    /// </summary>
    public static bool TryDeleteRecognizedTemp(
        string? path,
        ulong expectedDev,
        ulong expectedIno)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !IsRecognizedTemporaryName(path)
            || expectedDev == 0
            || expectedIno == 0)
        {
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            {
                return false;
            }

            var parentFd = Open(parent, OpenReadOnly | OpenDirectory | OpenCloseOnExec | OpenNoFollow);
            if (parentFd < 0)
            {
                return false;
            }

            try
            {
                return TryUnlinkExactCreatedName(
                    new SafeFileHandle((nint)parentFd, ownsHandle: false),
                    name,
                    expectedDev,
                    expectedIno);
            }
            finally
            {
                Close(parentFd);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Legacy overload without identity — always refuses deletion (no unknown-file delete).
    /// Callers must use the identity overload.
    /// </summary>
    public static bool TryDeleteRecognizedTemp(string? path) => false;

    private static void CleanupCreatedTemp(
        SafeFileHandle? parentHandle,
        string? tempFileName,
        string? recognizedTempPath,
        ulong createdDev,
        ulong createdIno,
        ref SafeFileHandle? fileHandle)
    {
        fileHandle?.Dispose();
        fileHandle = null;

        if (parentHandle is not null
            && !string.IsNullOrEmpty(tempFileName)
            && createdDev != 0
            && createdIno != 0)
        {
            TryUnlinkExactCreatedName(parentHandle, tempFileName, createdDev, createdIno);
            return;
        }

        // No identity — do not delete by path alone.
        _ = recognizedTempPath;
    }

    /// <summary>
    /// Unlink <paramref name="fileName"/> in <paramref name="parentHandle"/> only if openat+fstat
    /// shows the exact created dev/ino. On mismatch, leaves the entry untouched.
    /// </summary>
    private static bool TryUnlinkExactCreatedName(
        SafeFileHandle parentHandle,
        string fileName,
        ulong expectedDev,
        ulong expectedIno)
    {
        if (parentHandle.IsInvalid || string.IsNullOrEmpty(fileName) || expectedDev == 0 || expectedIno == 0)
        {
            return false;
        }

        var parentFd = parentHandle.DangerousGetHandle().ToInt32();
        var fd = Openat(
            parentFd,
            fileName,
            OpenReadOnly | OpenNoFollow | OpenCloseOnExec | OpenPath,
            mode: 0);
        // Prefer O_PATH for identity probe; fall back to O_RDONLY if O_PATH unavailable semantics.
        if (fd < 0)
        {
            fd = Openat(parentFd, fileName, OpenReadOnly | OpenNoFollow | OpenCloseOnExec, mode: 0);
        }

        if (fd < 0)
        {
            return false;
        }

        try
        {
            if (Fstat(fd, out var st) != 0)
            {
                return false;
            }

            if ((st.st_mode & FileTypeMask) != RegularFileType)
            {
                return false;
            }

            if (st.st_dev != expectedDev || st.st_ino != expectedIno)
            {
                // Directory entry no longer names our created inode — never unlink it.
                return false;
            }

            // Identity matched on the open descriptor. Unlink the name; residual TOCTOU after
            // this open is an OS limitation, but we never authorize by filename alone.
            if (Unlinkat(parentFd, fileName, 0) != 0)
            {
                return false;
            }

            return true;
        }
        finally
        {
            Close(fd);
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

        if (string.IsNullOrEmpty(Path.GetFileName(trimmed)))
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

        var segments = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var chain = new List<string>(segments.Length + 1) { "/" };
        var accum = string.Empty;
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                errorCode = ClassifyPrivacyOrInvalid();
                return false;
            }

            accum = accum + "/" + segment;
            chain.Add(accum);
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var isImmediateParent = i == chain.Count - 1;
            if (!TryValidateDirectoryComponent(chain[i], requireOwnerOnly0700: isImmediateParent, out errorCode))
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

    private static bool TryOpenParentDirectory(
        string parent,
        out SafeFileHandle handle,
        out string? errorCode)
    {
        handle = null!;
        errorCode = null;
        var fd = Open(parent, OpenReadOnly | OpenDirectory | OpenCloseOnExec | OpenNoFollow);
        if (fd < 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorTooManySymbolicLinks => PrivateCorpusErrors.SymlinkRejected,
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                ErrorNoEntry or ErrorNotDirectory => PrivateCorpusErrors.NotFound,
                _ => PrivateCorpusErrors.ReadFailed
            };
            return false;
        }

        handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        return true;
    }

    private static bool TryCreateOwnerOnlyTemp(
        SafeFileHandle parentHandle,
        string tempFileName,
        string fullTempPath,
        out SafeFileHandle handle,
        out ulong createdDev,
        out ulong createdIno,
        out string? errorCode)
    {
        handle = null!;
        createdDev = 0;
        createdIno = 0;
        errorCode = null;
        var parentFd = parentHandle.DangerousGetHandle().ToInt32();
        // O_RDWR so the retained descriptor can be rewound and validated without reopening by path.
        var fd = Openat(
            parentFd,
            tempFileName,
            OpenReadWrite | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
            mode: 0x180);
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
        if (Fchmod(fd, 0x180) != 0)
        {
            handle.Dispose();
            handle = null!;
            // Best-effort: we have no confirmed identity yet if fstat fails — try path only if openat match later.
            _ = fullTempPath;
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        if (Fstat(fd, out var st) != 0
            || (st.st_mode & FileTypeMask) != RegularFileType
            || st.st_nlink != 1
            || st.st_uid != Geteuid())
        {
            handle.Dispose();
            handle = null!;
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        createdDev = st.st_dev;
        createdIno = st.st_ino;
        return true;
    }

    private static bool TryWriteAllAndFlushOnFd(
        SafeFileHandle handle,
        byte[] payload,
        out string? errorCode)
    {
        errorCode = null;
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
    /// Validate JSONL content by reading the retained O_EXCL descriptor (not the pathname).
    /// Mirrors PrivateCorpusReader bounds/dialect without reopening a swappable path.
    /// </summary>
    private static bool TryValidateContentFromFd(
        SafeFileHandle handle,
        int expectedRowCount,
        long expectedByteLength,
        out CorpusFingerprint? fingerprint,
        out string? errorCode)
    {
        fingerprint = null;
        errorCode = null;
        var fd = handle.DangerousGetHandle().ToInt32();
        if (Lseek(fd, 0, SeekSet) < 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        // Dup so FileStream can own a handle without disposing the retained publish FD.
        var readFd = Dup(fd);
        if (readFd < 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        try
        {
            // FileStream takes ownership of the dup'd handle and closes it on dispose.
            using var stream = new FileStream(
                new SafeFileHandle((nint)readFd, ownsHandle: true),
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: false);
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            if (buffered.Length > PrivateCorpusLimits.MaxFileUtf8Bytes)
            {
                errorCode = PrivateCorpusErrors.LimitExceeded;
                return false;
            }

            if (buffered.Length != expectedByteLength)
            {
                errorCode = PrivateCorpusErrors.Malformed;
                return false;
            }

            var bytes = buffered.ToArray();
            fingerprint = CorpusFingerprint.FromExactBytes(bytes);

            // Parse JSONL dialect (same fields as PrivateCorpusReader).
            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');
            // Trailing newline yields a final empty split entry — ignore only a pure trailing empty.
            var dataLines = lines.Length > 0 && lines[^1].Length == 0
                ? lines[..^1]
                : lines;
            if (dataLines.Length != expectedRowCount)
            {
                errorCode = PrivateCorpusErrors.Malformed;
                return false;
            }

            var ordinals = new HashSet<int>();
            foreach (var line in dataLines)
            {
                if (line.Length == 0)
                {
                    errorCode = PrivateCorpusErrors.Malformed;
                    return false;
                }

                if (Encoding.UTF8.GetByteCount(line) > PrivateCorpusLimits.MaxLineUtf8Bytes)
                {
                    errorCode = PrivateCorpusErrors.LimitExceeded;
                    return false;
                }

                PrivateCorpusRow? row;
                try
                {
                    row = JsonSerializer.Deserialize(line, PrivateCorpusJsonContext.Default.PrivateCorpusRow);
                }
                catch (JsonException)
                {
                    errorCode = PrivateCorpusErrors.Malformed;
                    return false;
                }

                if (row is null || !ordinals.Add(row.Ordinal))
                {
                    errorCode = row is null
                        ? PrivateCorpusErrors.Malformed
                        : PrivateCorpusErrors.DuplicateOrdinal;
                    return false;
                }
            }

            return true;
        }
        catch (DecoderFallbackException)
        {
            errorCode = PrivateCorpusErrors.Malformed;
            return false;
        }
        catch (IOException)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }
    }

    /// <summary>
    /// Publish the exact retained inode as a new directory entry via linkat(AT_EMPTY_PATH).
    /// Fails with EEXIST when the destination name already exists (no-replace).
    /// </summary>
    private static bool TryLinkAtEmptyPath(
        SafeFileHandle fileHandle,
        SafeFileHandle parentHandle,
        string destFileName,
        out string? errorCode)
    {
        errorCode = null;
        var fileFd = fileHandle.DangerousGetHandle().ToInt32();
        var parentFd = parentHandle.DangerousGetHandle().ToInt32();
        if (Linkat(fileFd, string.Empty, parentFd, destFileName, AtEmptyPath) != 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorExists => MapDestinationExists(),
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                ErrorCrossDevice => PrivateCorpusErrors.PermissionsRejected,
                ErrorNoEntry => PrivateCorpusErrors.NotFound,
                ErrorInvalid => PrivateCorpusErrors.ReadFailed,
                ErrorOperationNotPermitted => PrivateCorpusErrors.PermissionsRejected,
                _ => PrivateCorpusErrors.ReadFailed
            };
            return false;
        }

        return true;
    }

    private static bool TryValidatePublishedDestination(
        string destination,
        ulong expectedDev,
        ulong expectedIno,
        out string? errorCode)
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

        // After temp unlink, nlink should be 1; if temp cleanup failed, nlink may be 2 — still our inode.
        if (st.st_dev != expectedDev || st.st_ino != expectedIno)
        {
            errorCode = ClassifyErrors.Integrity;
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

    private static bool TryFsyncDirectoryFd(SafeFileHandle parentHandle, out string? errorCode)
    {
        errorCode = null;
        var fd = parentHandle.DangerousGetHandle().ToInt32();
        if (Fsync(fd) != 0)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        return true;
    }

    private static bool PathExistsNoFollow(string path) => Lstat(path, out _) == 0;

    private static string MapDestinationExists() => ClassifyErrors.DestinationExists;

    private static string ClassifyPrivacyOrInvalid() => ClassifyErrors.PrivacyRejected;

    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenPath = 0x200000; // O_PATH
    private const int AtEmptyPath = 0x1000; // AT_EMPTY_PATH
    private const int SeekSet = 0;
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
    private const int ErrorInvalid = 22;
    private const int ErrorOperationNotPermitted = 1;
    private const int ErrorTooManySymbolicLinks = 40;

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags, int mode = 0);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Openat(int dirfd, string path, int flags, int mode = 0);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern int Write(int fd, ref byte buffer, int count);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int fd, int mode);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int fd, out StatBuf buf);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Lstat(string path, out StatBuf buf);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern long Lseek(int fd, long offset, int whence);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fd);

    [DllImport("libc", EntryPoint = "linkat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Linkat(
        int olddirfd,
        string oldpath,
        int newdirfd,
        string newpath,
        int flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Unlinkat(int dirfd, string path, int flags);

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
