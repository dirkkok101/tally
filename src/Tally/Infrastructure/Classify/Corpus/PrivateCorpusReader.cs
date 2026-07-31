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

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var path = corpusPath.Trim();
            if (!TryValidateFileBoundary(path, out var boundaryError))
            {
                return PrivateCorpusReadResult.Failure(boundaryError!);
            }

            // Exact-byte fingerprint first (full sequential read), then row stream from the same path.
            CorpusFingerprint fingerprint;
            await using (var hashStream = OpenReadOnly(path))
            {
                fingerprint = await CorpusFingerprint.FromStreamAsync(
                    hashStream,
                    PrivateCorpusLimits.MaxFileUtf8Bytes,
                    cancellationToken);
            }

            var rows = new List<PrivateCorpusRow>(capacity: 64);
            var ordinals = new HashSet<int>();
            await using (var parseStream = OpenReadOnly(path))
            using (var reader = new StreamReader(parseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: false))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        // Blank lines are not valid JSONL rows (trailing newline after last row does not yield an empty line).
                        return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Malformed);
                    }

                    // Bound line size using UTF-8 byte length of the decoded line + newline budget.
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
                        var parsed = JsonSerializer.Deserialize(line, PrivateCorpusJsonContext.Default.PrivateCorpusRow);
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
        catch (OperationCanceledException)
        {
            return PrivateCorpusReadResult.Failure(PrivateCorpusErrors.Cancelled);
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

    private static bool TryValidateFileBoundary(string path, out string? errorCode)
    {
        errorCode = null;
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Private corpus reading requires Linux.");
        }

        if (!File.Exists(path))
        {
            // Distinguish missing path from directory-as-path without disclosing path text.
            errorCode = Directory.Exists(path)
                ? PrivateCorpusErrors.NotRegularFile
                : PrivateCorpusErrors.NotFound;
            return false;
        }

        // Refuse final symbolic link (ResolveLinkTarget non-null when path itself is a symlink).
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
            {
                errorCode = PrivateCorpusErrors.SymlinkRejected;
                return false;
            }
        }
        catch (IOException)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (IOException)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            errorCode = attributes.HasFlag(FileAttributes.ReparsePoint)
                ? PrivateCorpusErrors.SymlinkRejected
                : PrivateCorpusErrors.NotRegularFile;
            return false;
        }

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(path);
        }
        catch (IOException)
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        if ((mode & ForbiddenSharingBits) != 0)
        {
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        if ((mode & UnixFileMode.UserRead) == 0)
        {
            errorCode = PrivateCorpusErrors.PermissionsRejected;
            return false;
        }

        if (!TryGetFileOwnerUid(path, out var ownerUid))
        {
            errorCode = PrivateCorpusErrors.ReadFailed;
            return false;
        }

        var euid = Geteuid();
        if (ownerUid != euid)
        {
            errorCode = PrivateCorpusErrors.OwnerRejected;
            return false;
        }

        return true;
    }

    private static FileStream OpenReadOnly(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

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

    private static bool TryGetFileOwnerUid(string path, out uint uid)
    {
        uid = 0;
        if (Stat(path, out var status) != 0)
        {
            return false;
        }

        uid = status.st_uid;
        return true;
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "stat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Stat(string path, out StatBuf buf);

    // Minimal Linux x86_64 / aarch64-compatible stat layout for st_uid only.
    // Padding matches glibc struct stat enough to read st_uid on supported release hosts.
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
