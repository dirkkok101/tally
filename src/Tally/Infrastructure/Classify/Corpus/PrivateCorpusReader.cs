using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Tally.Domain.Classify.Rules;

namespace Tally.Infrastructure.Classify.Corpus;

/// <summary>
/// Production owner-only JSONL private corpus reader
/// (DD-CLASSIFY-PRIVATE-VALIDATION / TASK-CLASSIFY-RULEBOOK-PRIVATE-CORPUS-READER).
/// Opens one regular owner-readable file read-only, refuses final symlinks / wrong owner /
/// permissive modes, fingerprints exact bytes, then streams rows via source-generated JSON.
/// Never copies corpus into classify.db, temps, logs, or diagnostics.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateCorpusReader
{
    private static readonly UnixFileMode ForbiddenSharingBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Read and validate a private corpus at <paramref name="corpusPath"/>.
    /// The path exists only as structured input — never returned in error codes or durable results.
    /// </summary>
    public async Task<PrivateCorpusReadResult> ReadAsync(
        string? corpusPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(corpusPath))
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.PathRequired);
        }

        using var timeoutCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(PrivateCorpusLimits.MaxProcessingTimeMs));
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var operationToken = operationCancellation.Token;

        try
        {
            operationToken.ThrowIfCancellationRequested();
            var path = corpusPath.Trim();
            if (!TryOpenValidatedFile(path, out var corpusStream, out var boundaryError))
            {
                return PrivateCorpusReadResult.Failure(boundaryError!);
            }

            // Fingerprint and parse the same no-follow file descriptor. This prevents a path swap
            // between boundary validation, hashing, and row streaming.
            await using (corpusStream)
            {
                var fingerprint = await CorpusFingerprint.FromStreamAsync(
                    corpusStream,
                    PrivateCorpusLimits.MaxFileUtf8Bytes,
                    operationToken);
                corpusStream.Seek(0, SeekOrigin.Begin);

                var rows = new List<PrivateCorpusRow>(capacity: 64);
                var ordinals = new HashSet<int>();
                using (var reader = new StreamReader(
                    corpusStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 16 * 1024,
                    leaveOpen: true))
                {
                    while (true)
                    {
                        operationToken.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync(operationToken);
                        if (line is null)
                        {
                            break;
                        }

                        if (line.Length == 0)
                        {
                            // Blank lines are not valid JSONL rows (a trailing newline does not yield an empty line).
                            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Malformed);
                        }

                        var lineUtf8Bytes = Encoding.UTF8.GetByteCount(line);
                        if (lineUtf8Bytes > PrivateCorpusLimits.MaxLineUtf8Bytes)
                        {
                            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.LimitExceeded);
                        }

                        if (rows.Count >= PrivateCorpusLimits.MaxRowCount)
                        {
                            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.LimitExceeded);
                        }

                        PrivateCorpusRow row;
                        try
                        {
                            var parsed = JsonSerializer.Deserialize(
                                line,
                                PrivateCorpusJsonContext.Default.PrivateCorpusRow);
                            if (parsed is null)
                            {
                                return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Malformed);
                            }

                            row = parsed;
                        }
                        catch (JsonException)
                        {
                            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Malformed);
                        }

                        if (!TryValidateRow(row, out var rowError))
                        {
                            return PrivateCorpusReadResult.Failure(rowError!);
                        }

                        if (!ordinals.Add(row.Ordinal))
                        {
                            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.DuplicateOrdinal);
                        }

                        rows.Add(row);
                    }
                }

                // Ordered by ordinal for deterministic streaming into ClassificationEngine.
                var ordered = rows
                    .OrderBy(r => r.Ordinal)
                    .ThenBy(r => r.TransactionId, StringComparer.Ordinal)
                    .ToArray();

                return PrivateCorpusReadResult.Success(fingerprint, ordered);
            }
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Timeout);
        }
        catch (OperationCanceledException)
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Cancelled);
        }
        catch (DecoderFallbackException)
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Malformed);
        }
        catch (PrivateCorpusLimitException ex)
        {
            return PrivateCorpusReadResult.Failure(ex.ErrorCode);
        }
        catch (IOException)
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.ReadFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.PermissionsRejected);
        }
    }

    private static bool TryOpenValidatedFile(
        string path,
        out FileStream stream,
        out string? errorCode)
    {
        stream = null!;
        errorCode = null;
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Private corpus reading requires Linux.");
        }

        var fd = Open(path, OpenReadOnly | OpenCloseOnExec | OpenNoFollow);
        if (fd < 0)
        {
            errorCode = Marshal.GetLastPInvokeError() switch
            {
                ErrorTooManySymbolicLinks => PrivateCorpusErrors.SymlinkRejected,
                ErrorNoEntry or ErrorNotDirectory => PrivateCorpusErrors.NotFound,
                ErrorAccessDenied => PrivateCorpusErrors.PermissionsRejected,
                _ => PrivateCorpusErrors.ReadFailed
            };
            return false;
        }

        var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        if (Fstat(fd, out var status) != 0)
        {
            handle.Dispose();
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        if ((status.st_mode & FileTypeMask) != RegularFileType)
        {
            handle.Dispose();
            errorCode = PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        var mode = (UnixFileMode)(status.st_mode & PermissionBitsMask);
        if ((mode & ForbiddenSharingBits) != 0
            || (mode & UnixFileMode.UserRead) == 0)
        {
            handle.Dispose();
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        if (status.st_uid != Geteuid())
        {
            handle.Dispose();
            errorCode = PrivateCorpusErrors.OwnerRejected;
            return false;
        }

        try
        {
            // A descriptor returned by open(2) is synchronous; FileStream still exposes the
            // cancellable ReadAsync API without falsely marking the handle as overlapped I/O.
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
            return true;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool TryValidateRow(PrivateCorpusRow row, out string? errorCode)
    {
        errorCode = null;
        if (row.Ordinal < 0)
        {
            errorCode = PrivateCorpusErrors.FieldInvalid;
            return false;
        }

        if (!IsBoundedIdentifier(row.TransactionId)
            || !IsBoundedIdentifier(row.AccountId)
            || !IsBoundedIdentifier(row.ItemLifecycleFingerprint))
        {
            errorCode = PrivateCorpusErrors.FieldInvalid;
            return false;
        }

        if (row.SourceDescription is null
            || row.SourceDescription.Length > PrivateCorpusLimits.MaxDescriptionLength)
        {
            errorCode = row.SourceDescription is null
                ? PrivateCorpusErrors.Malformed
                : PrivateCorpusErrors.LimitExceeded;
            return false;
        }

        if (row.AmountAbsoluteMinor < 0)
        {
            errorCode = PrivateCorpusErrors.FieldInvalid;
            return false;
        }

        if (row.AmountDirection is not null
            && row.AmountDirection is not (
                ClassificationRuleVocabulary.DirectionInflow
                or ClassificationRuleVocabulary.DirectionOutflow))
        {
            errorCode = PrivateCorpusErrors.FieldInvalid;
            return false;
        }

        if (row.ExpectedCategoryId is not null && !IsBoundedIdentifier(row.ExpectedCategoryId))
        {
            errorCode = PrivateCorpusErrors.FieldInvalid;
            return false;
        }

        if (row.ExpectedOutcomeKind is not null
            && row.ExpectedOutcomeKind is not (
                "suggestion" or "no_suggestion" or "conflict" or "stale"))
        {
            errorCode = PrivateCorpusErrors.FieldInvalid;
            return false;
        }

        return true;
    }

    private static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= PrivateCorpusLimits.MaxIdentifierLength
        && value.Trim().Length == value.Length;

    private const int OpenReadOnly = 0;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint PermissionBitsMask = 0x0FFF;
    private const int ErrorNoEntry = 2;
    private const int ErrorAccessDenied = 13;
    private const int ErrorNotDirectory = 20;
    private const int ErrorTooManySymbolicLinks = 40;

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int fd, out StatBuf buf);

    // Linux x86_64 / aarch64 glibc struct stat layout used by supported release hosts.
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
