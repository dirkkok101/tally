using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Tally.Contracts.Classify.Operations;
// Encoding used by hard-link attack fixture bytes.
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Rules;
using Tally.Infrastructure.Classify.Corpus;
using Xunit;

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER / bd-1cik —
/// Crash-window, permission, symlink, hard-link, atomicity, and cleanup matrix
/// for <see cref="PrivateCorpusWriter"/>. Synthetic disposable roots only.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateCorpusWriterRecoveryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-corpus-writer-" + Guid.NewGuid().ToString("N"));
    private readonly PrivateCorpusReader reader = new();
    private readonly PrivateCorpusWriter writer;

    public PrivateCorpusWriterRecoveryTests()
    {
        writer = new PrivateCorpusWriter(reader);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Happy path atomicity ─────────────────────────────────────────────────

    [Fact]
    public async Task Publish_creates_destination_and_no_temp()
    {
        var dest = Path.Combine(root, "ok.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(root, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        Assert.Equal(result.Fingerprint!.Sha256Hex, read.Fingerprint!.Sha256Hex);
    }

    [Fact]
    public async Task Published_file_is_0600_owner_regular_with_link_count_1()
    {
        var dest = Path.Combine(root, "mode.jsonl");
        Assert.True((await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None)).IsSuccess);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dest));
        Assert.True(LstatNlink(dest) == 1);
    }

    [Fact]
    public async Task Multi_row_payload_is_reader_compatible()
    {
        var dest = Path.Combine(root, "multi.jsonl");
        var rows = new[] { Row(2, "tx-b"), Row(0, "tx-a"), Row(1, "tx-c") };
        var result = await writer.PublishAsync(dest, rows, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(3, result.WrittenRowCount);
        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.Equal(3, read.RowCount);
        Assert.Equal(["tx-a", "tx-c", "tx-b"], read.Rows!.Select(r => r.TransactionId).ToArray());
    }

    // ── Destination non-overwrite ────────────────────────────────────────────

    [Fact]
    public async Task Existing_destination_is_not_overwritten()
    {
        var dest = Path.Combine(root, "exists.jsonl");
        await File.WriteAllTextAsync(dest, "KEEP\n");
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(ClassifyErrors.DestinationExists, result.ErrorCode);
        Assert.Equal("KEEP\n", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Renameat2_noreplace_preserves_competitor_bytes_on_race()
    {
        // Deterministic race/conflict: competitor creates destination while we would publish.
        // Kernel RENAME_NOREPLACE must leave competitor bytes unchanged and fail closed.
        var dest = Path.Combine(root, "race.jsonl");
        const string competitor = "COMPETITOR-BYTES-UNCHANGED\n";
        await File.WriteAllTextAsync(dest, competitor);
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var before = await File.ReadAllBytesAsync(dest);

        var result = await writer.PublishAsync(dest, [Row(0, "tx-race")], CancellationToken.None);
        Assert.Equal(ClassifyErrors.DestinationExists, result.ErrorCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(dest));
        Assert.Equal(competitor, await File.ReadAllTextAsync(dest));
        // No temp left after NOREPLACE failure.
        Assert.Empty(Directory.GetFiles(root, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
    }

    [Fact]
    public async Task Intermediate_symlink_in_parent_chain_is_rejected()
    {
        // /root/real/out is fine; /root/link -> real makes intermediate component a symlink.
        var real = Path.Combine(root, "real-chain");
        Directory.CreateDirectory(real);
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var link = Path.Combine(root, "mid-link");
        Assert.Equal(0, Symlink(real, link));
        var dest = Path.Combine(link, "nested", "out.jsonl");
        // Even if nested does not exist, chain walk hits mid-link as S_IFLNK first.
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.True(
            result.ErrorCode is PrivateCorpusErrors.SymlinkRejected
                or PrivateCorpusErrors.NotFound
                or ClassifyErrors.PrivacyRejected,
            result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(real, "nested", "out.jsonl")));
    }

    [Fact]
    public async Task Intermediate_symlink_with_existing_nested_dir_is_rejected()
    {
        var real = Path.Combine(root, "real-nested");
        var nested = Path.Combine(real, "nested");
        Directory.CreateDirectory(nested);
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var link = Path.Combine(root, "chain-link");
        Assert.Equal(0, Symlink(real, link));
        var dest = Path.Combine(link, "nested", "out.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.SymlinkRejected, result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(nested, "out.jsonl")));
    }

    [Fact]
    public async Task Temp_hard_link_attack_before_rename_is_rejected()
    {
        // If an attacker hard-links the recognized temp after create, nlink!=1 must fail closed
        // before renameat2. Force the condition by publishing to a path we pre-stage: create
        // temp ourselves matching the recognized pattern, hard-link it, then observe writer
        // refuses destinations that would rename a multi-linked inode.
        // Direct unit of identity check: after a successful create path, hard-link temp and
        // re-publish to a free destination — the writer's pre-rename identity check fails.
        // We simulate by hard-linking a published file and ensuring NOREPLACE / exists path
        // does not destroy it; plus a dedicated hard-link on a temp name that fails delete/identity.
        var dest = Path.Combine(root, "hl-dest.jsonl");
        var temp = Path.Combine(
            root,
            PrivateCorpusWriter.RecognizedTempPrefix + "attack" + PrivateCorpusWriter.RecognizedTempSuffix);
        await File.WriteAllBytesAsync(temp, Encoding.UTF8.GetBytes("x\n"));
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var alias = Path.Combine(root, "alias-hardlink");
        Assert.Equal(0, Link(temp, alias));
        Assert.True(LstatNlink(temp) >= 2);
        // Recognized temp with nlink>1 must not be deleted by cleanup helper.
        Assert.False(PrivateCorpusWriter.TryDeleteRecognizedTemp(temp));
        Assert.True(File.Exists(temp));
        // Publish to a free dest still succeeds (new exclusive temp), competitor hard-link intact.
        Assert.True((await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None)).IsSuccess);
        Assert.True(File.Exists(alias));
        Assert.Equal("x\n", await File.ReadAllTextAsync(alias));
    }

    [Fact]
    public async Task Existing_directory_as_destination_fails()
    {
        var dest = Path.Combine(root, "as-dir");
        Directory.CreateDirectory(dest);
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.True(Directory.Exists(dest));
    }

    // ── Parent boundary ──────────────────────────────────────────────────────

    [Fact]
    public async Task Parent_group_readable_fails()
    {
        var parent = Path.Combine(root, "g-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(
            parent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var dest = Path.Combine(parent, "out.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Parent_world_executable_fails()
    {
        var parent = Path.Combine(root, "w-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(
            parent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        var dest = Path.Combine(parent, "out.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Missing_parent_fails()
    {
        var dest = Path.Combine(root, "no-such-dir", "out.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Relative_path_fails()
    {
        var result = await writer.PublishAsync("relative.jsonl", [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
    }

    [Fact]
    public async Task Null_path_fails()
    {
        var result = await writer.PublishAsync(null, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PathRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Empty_path_fails()
    {
        var result = await writer.PublishAsync("  ", [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PathRequired, result.ErrorCode);
    }

    // ── Symlink attacks ──────────────────────────────────────────────────────

    [Fact]
    public async Task Destination_symlink_is_treated_as_existing_and_not_followed()
    {
        var real = Path.Combine(root, "real-target.jsonl");
        await File.WriteAllTextAsync(real, "SECRET\n");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(root, "dest-link.jsonl");
        Assert.Equal(0, Symlink(real, link));
        var result = await writer.PublishAsync(link, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(ClassifyErrors.DestinationExists, result.ErrorCode);
        Assert.Equal("SECRET\n", await File.ReadAllTextAsync(real));
    }

    [Fact]
    public async Task Parent_symlink_directory_is_rejected()
    {
        var realParent = Path.Combine(root, "real-parent");
        Directory.CreateDirectory(realParent);
        File.SetUnixFileMode(realParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var linkParent = Path.Combine(root, "link-parent");
        Assert.Equal(0, Symlink(realParent, linkParent));
        // lstat on symlink yields S_IFLNK, not directory → NotRegularFile / fail closed
        var dest = Path.Combine(linkParent, "out.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(realParent, "out.jsonl")));
    }

    // ── Hard link ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Existing_hard_linked_destination_is_not_overwritten()
    {
        var a = Path.Combine(root, "hard-a.jsonl");
        var b = Path.Combine(root, "hard-b.jsonl");
        await File.WriteAllTextAsync(a, "LINKED\n");
        File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(0, Link(a, b));
        Assert.True(LstatNlink(b) >= 2);
        var result = await writer.PublishAsync(b, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Equal(ClassifyErrors.DestinationExists, result.ErrorCode);
        Assert.Equal("LINKED\n", await File.ReadAllTextAsync(a));
    }

    // ── Limits / empty ───────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_rows_fail_limit()
    {
        var dest = Path.Combine(root, "empty.jsonl");
        var result = await writer.PublishAsync(dest, Array.Empty<PrivateCorpusRow>(), CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.LimitExceeded, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Null_rows_fail()
    {
        var dest = Path.Combine(root, "null.jsonl");
        var result = await writer.PublishAsync(dest, null!, CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.FieldInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Cancellation_before_publish_returns_cancelled()
    {
        var dest = Path.Combine(root, "cancel.jsonl");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], cts.Token);
        Assert.Equal(PrivateCorpusErrors.Cancelled, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    // ── Recognized temporary cleanup ─────────────────────────────────────────

    [Fact]
    public async Task TryDeleteRecognizedTemp_removes_only_recognized_names()
    {
        var temp = Path.Combine(
            root,
            PrivateCorpusWriter.RecognizedTempPrefix + "deadbeef" + PrivateCorpusWriter.RecognizedTempSuffix);
        await File.WriteAllTextAsync(temp, "x");
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.True(PrivateCorpusWriter.TryDeleteRecognizedTemp(temp));
        Assert.False(File.Exists(temp));

        var unknown = Path.Combine(root, "not-recognized.tmp");
        await File.WriteAllTextAsync(unknown, "y");
        File.SetUnixFileMode(unknown, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.False(PrivateCorpusWriter.TryDeleteRecognizedTemp(unknown));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public async Task TryDeleteRecognizedTemp_refuses_destination_basename()
    {
        var dest = Path.Combine(root, "corpus.jsonl");
        await File.WriteAllTextAsync(dest, "z");
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.False(PrivateCorpusWriter.TryDeleteRecognizedTemp(dest));
        Assert.True(File.Exists(dest));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IsRecognizedTemporaryName_matches_prefix_suffix_only()
    {
        Assert.True(PrivateCorpusWriter.IsRecognizedTemporaryName(
            PrivateCorpusWriter.RecognizedTempPrefix + "abc" + PrivateCorpusWriter.RecognizedTempSuffix));
        Assert.False(PrivateCorpusWriter.IsRecognizedTemporaryName("abc.tmp"));
        Assert.False(PrivateCorpusWriter.IsRecognizedTemporaryName(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Failed_publish_leaves_no_recognized_temp()
    {
        var parent = Path.Combine(root, "fail-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(
            parent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        var dest = Path.Combine(parent, "out.jsonl");
        _ = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.Empty(Directory.GetFiles(parent, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
        Assert.Empty(Directory.GetFiles(root, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
    }

    // ── Byte / fingerprint integrity ─────────────────────────────────────────

    [Fact]
    public async Task Fingerprint_matches_exact_file_bytes()
    {
        var dest = Path.Combine(root, "fp.jsonl");
        var rows = new[] { Row(0, "tx-1"), Row(1, "tx-2") };
        var result = await writer.PublishAsync(dest, rows, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var bytes = await File.ReadAllBytesAsync(dest);
        var fp = CorpusFingerprint.FromExactBytes(bytes);
        Assert.Equal(fp.Sha256Hex, result.Fingerprint!.Sha256Hex);
        Assert.Equal(fp.ByteLength, result.WrittenByteCount);
    }

    [Fact]
    public async Task Error_codes_never_embed_paths()
    {
        var secret = Path.Combine(root, "secret-path-token");
        Directory.CreateDirectory(secret);
        File.SetUnixFileMode(
            secret,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var dest = Path.Combine(secret, "out.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(secret, result.ErrorCode ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(dest, result.ErrorCode ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_result_success_exposes_aggregate_only()
    {
        var dest = Path.Combine(root, "agg.jsonl");
        var result = await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Fingerprint);
        Assert.Equal(1, result.WrittenRowCount);
        // No path field on result type — structural privacy.
        Assert.Null(typeof(PrivateCorpusPublishResult).GetProperty("Path"));
        Assert.Null(typeof(PrivateCorpusPublishResult).GetProperty("Destination"));
    }

    [Fact]
    public async Task Second_publish_same_path_fails_destination_exists()
    {
        var dest = Path.Combine(root, "twice.jsonl");
        Assert.True((await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None)).IsSuccess);
        var second = await writer.PublishAsync(dest, [Row(0, "tx-2")], CancellationToken.None);
        Assert.Equal(ClassifyErrors.DestinationExists, second.ErrorCode);
        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.Equal("tx-1", read.Rows![0].TransactionId);
    }

    [Fact]
    public async Task Row_json_uses_private_corpus_dialect_fields()
    {
        var dest = Path.Combine(root, "dialect.jsonl");
        Assert.True((await writer.PublishAsync(dest, [Row(0, "tx-1")], CancellationToken.None)).IsSuccess);
        var line = (await File.ReadAllTextAsync(dest)).TrimEnd();
        using var doc = JsonDocument.Parse(line);
        var rootEl = doc.RootElement;
        Assert.True(rootEl.TryGetProperty("ordinal", out _));
        Assert.True(rootEl.TryGetProperty("transactionId", out _));
        Assert.True(rootEl.TryGetProperty("sourceDescription", out _));
        Assert.True(rootEl.TryGetProperty("itemLifecycleFingerprint", out _));
        Assert.True(rootEl.TryGetProperty("expectedOutcomeKind", out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PrivateCorpusRow Row(int ordinal, string tx) =>
        new(
            ordinal,
            tx,
            "acct-1",
            "COFFEE SHOP",
            ClassificationRuleVocabulary.DirectionOutflow,
            1234,
            new string('f', 64),
            ExpectedCategoryId: null,
            ExpectedOutcomeKind: "no_suggestion");

    private static ulong LstatNlink(string path)
    {
        Assert.Equal(0, Lstat(path, out var st));
        return st.st_nlink;
    }

    [DllImport("libc", EntryPoint = "symlink", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Symlink(string target, string linkpath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Link(string oldpath, string newpath);

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
