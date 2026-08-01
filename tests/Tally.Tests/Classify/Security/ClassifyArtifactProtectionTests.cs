using System.Runtime.Versioning;
using Tally.Domain.Classify.Recovery;
using Tally.Infrastructure.Classify.Storage;
using Xunit;

namespace Tally.Tests.Classify.Security;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-ABANDON-CLEANUP / bd-3hcn — filesystem attack matrix.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyArtifactProtectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-art-prot-" + Guid.NewGuid().ToString("N"));
    private readonly ClassifyArtifactProtection protection;

    public ClassifyArtifactProtectionTests()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        protection = new ClassifyArtifactProtection(root);
        protection.EnsureClassifyLayout();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recognized_temporary_name_closed_set()
    {
        Assert.True(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("tmp-abc"));
        Assert.True(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("eval-1.tmp"));
        Assert.True(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("crash-residue"));
        Assert.True(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("foo.partial"));
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("secret.json"));
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("../escape.tmp"));
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("a/b.tmp"));
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(""));
    }

    [Fact]
    public void May_remove_requires_all_safety_bits()
    {
        Assert.True(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            true, true, true, true, false, false));
        Assert.False(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            false, true, true, true, false, false));
        Assert.False(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            true, false, true, true, false, false));
        Assert.False(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            true, true, false, true, false, false));
        Assert.False(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            true, true, true, false, false, false));
        Assert.False(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            true, true, true, true, true, false));
        Assert.False(ClassifyRetentionPolicy.MayRemoveTemporaryArtifact(
            true, true, true, true, false, true));
    }

    [Fact]
    public void Stage_and_finalize_removes_recognized_temporary()
    {
        var path = protection.CreateRecognizedTemporaryForTests("tmp-safe-1", [1, 2, 3]);
        Assert.True(File.Exists(path));
        var q = protection.TryStageRecognizedTemporaries("op-safe", "cleanup", ["tmp-safe-1"]);
        Assert.NotNull(q);
        q!.FinalizeCommitted();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Stage_unknown_name_is_rejected()
    {
        var paths = new ClassifyStorePaths(root);
        var unknown = Path.Combine(paths.TemporaryDirectory, "unknown-secret.bin");
        File.WriteAllBytes(unknown, [9]);
        File.SetUnixFileMode(unknown, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(protection.TryStageRecognizedTemporaries(
            "op-unknown", "cleanup", ["unknown-secret.bin"]));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Outside_root_path_is_not_contained()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "x");
        try
        {
            Assert.True(protection.IsOutsideClassifyRoot(outside));
            Assert.False(protection.IsContainedInClassifyRoot(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Symlink_is_not_regular_file_and_not_deleted()
    {
        var paths = new ClassifyStorePaths(root);
        var target = Path.Combine(paths.TemporaryDirectory, "tmp-target");
        File.WriteAllBytes(target, [1]);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(paths.TemporaryDirectory, "tmp-link");
        File.CreateSymbolicLink(link, target);
        Assert.True(protection.IsSymbolicLink(link));
        Assert.False(protection.IsRegularFile(link));
        Assert.Null(protection.TryStageRecognizedTemporaries("op-link", "cleanup", ["tmp-link"]));
        Assert.True(File.Exists(link) || File.Exists(target));
    }

    [Fact]
    public void Symlink_escape_outside_classify_is_rejected()
    {
        var paths = new ClassifyStorePaths(root);
        var outside = Path.Combine(Path.GetTempPath(), "escape-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret");
        try
        {
            var link = Path.Combine(paths.TemporaryDirectory, "tmp-escape");
            File.CreateSymbolicLink(link, outside);
            Assert.Null(protection.TryStageRecognizedTemporaries("op-escape", "cleanup", ["tmp-escape"]));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Stage_then_finalize_removes_recognized_temps()
    {
        protection.CreateRecognizedTemporaryForTests("crash-old", [1]);
        protection.CreateRecognizedTemporaryForTests("eval-x.tmp", [2]);
        var paths = new ClassifyStorePaths(root);
        var unknown = Path.Combine(paths.TemporaryDirectory, "not-recognized.dat");
        File.WriteAllBytes(unknown, [3]);
        File.SetUnixFileMode(unknown, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var q = protection.TryStageRecognizedTemporaries(
            "op-stage-1", "cleanup", ["crash-old", "eval-x.tmp"]);
        Assert.NotNull(q);
        Assert.Equal(2, q!.StagedCount);
        Assert.True(File.Exists(unknown));
        Assert.DoesNotContain("crash-old", protection.ListRecognizedTemporaryFileNames());
        q.FinalizeCommitted();
        Assert.True(File.Exists(unknown));
        Assert.DoesNotContain("crash-old", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public void Stage_restore_puts_files_back()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-back", [9]);
        var q = protection.TryStageRecognizedTemporaries("op-restore-1", "abandon", ["tmp-back"]);
        Assert.NotNull(q);
        q!.RestoreAndDiscard();
        Assert.Contains("tmp-back", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public void Stage_rejects_unknown_names()
    {
        var q = protection.TryStageRecognizedTemporaries(
            "op-bad", "cleanup", ["not-a-recognized-name.bin"]);
        Assert.Null(q);
    }

    [Fact]
    public void Layout_directories_are_owner_only()
    {
        var paths = new ClassifyStorePaths(root);
        Assert.True(protection.IsOwnerOnlyDirectory(paths.ClassifyDirectory));
        Assert.True(protection.IsOwnerOnlyDirectory(paths.TemporaryDirectory));
        Assert.True(protection.IsOwnerOnlyDirectory(paths.ReportsDirectory));
    }

    [Fact]
    public void Parent_directory_escape_name_is_not_recognized()
    {
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName(".."));
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("."));
        Assert.False(ClassifyRetentionPolicy.IsRecognizedTemporaryFileName("tmp-../x"));
    }

    [Fact]
    public void List_recognized_ignores_unknown_files()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-listed", [1]);
        var paths = new ClassifyStorePaths(root);
        File.WriteAllBytes(Path.Combine(paths.TemporaryDirectory, "notes.txt"), [1]);
        File.SetUnixFileMode(
            Path.Combine(paths.TemporaryDirectory, "notes.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var names = protection.ListRecognizedTemporaryFileNames();
        Assert.Contains("tmp-listed", names);
        Assert.DoesNotContain("notes.txt", names);
    }

    [Fact]
    public void Contained_tmp_path_is_inside_classify_root()
    {
        var paths = new ClassifyStorePaths(root);
        var full = Path.Combine(paths.TemporaryDirectory, "tmp-in");
        File.WriteAllBytes(full, [0]);
        File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.True(protection.IsContainedInClassifyRoot(full));
    }

    // ── Crash-safe staging / partition / fail-closed quarantine (bd-3hcn) ───

    [Fact]
    public void Stage_writes_complete_uncommitted_manifest_mapping_originals()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-a", [1]);
        protection.CreateRecognizedTemporaryForTests("tmp-b", [2]);
        var q = protection.TryStageRecognizedTemporaries(
            "op-manifest-1", "cleanup", ["tmp-a", "tmp-b"]);
        Assert.NotNull(q);
        Assert.False(q!.IsEmpty);
        Assert.Equal(2, q.StagedCount);
        Assert.False(q.Manifest.Committed);

        var manifestPath = Path.Combine(q.Directory, ClassifyArtifactProtection.ManifestFileName);
        Assert.True(File.Exists(manifestPath));
        Assert.True(protection.IsRegularFile(manifestPath));
        Assert.True(protection.IsOwnerOnlyFile(manifestPath));

        var text = File.ReadAllText(manifestPath);
        Assert.Contains("tmp-a", text, StringComparison.Ordinal);
        Assert.Contains("tmp-b", text, StringComparison.Ordinal);
        Assert.Contains("\"committed\":false", text, StringComparison.Ordinal);
        // Numbered staged names are recoverable from the journal.
        Assert.Contains("\"stagedName\":\"0\"", text, StringComparison.Ordinal);
        Assert.Contains("\"stagedName\":\"1\"", text, StringComparison.Ordinal);

        var stagedDir = Path.Combine(q.Directory, ClassifyArtifactProtection.StagedDirectoryName);
        Assert.True(File.Exists(Path.Combine(stagedDir, "0")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "1")));
        Assert.DoesNotContain("tmp-a", protection.ListRecognizedTemporaryFileNames());
        Assert.DoesNotContain("tmp-b", protection.ListRecognizedTemporaryFileNames());

        q.RestoreAndDiscard();
        Assert.Contains("tmp-a", protection.ListRecognizedTemporaryFileNames());
        Assert.Contains("tmp-b", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public void Partial_stage_crash_boundary_restores_moved_names_via_manifest()
    {
        // Simulate crash after complete uncommitted manifest + only one of two renames.
        protection.CreateRecognizedTemporaryForTests("tmp-partial-1", [1]);
        protection.CreateRecognizedTemporaryForTests("tmp-partial-2", [2]);
        var paths = new ClassifyStorePaths(root);
        var quarantineDir = Path.Combine(protection.QuarantineRoot, "op-partial-crash");
        var stagedDir = Path.Combine(quarantineDir, ClassifyArtifactProtection.StagedDirectoryName);
        Directory.CreateDirectory(quarantineDir);
        Directory.CreateDirectory(stagedDir);
        File.SetUnixFileMode(
            quarantineDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(
            stagedDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var manifestJson =
            """{"operationId":"op-partial-crash","kind":"cleanup","committed":false,"entries":[{"originalName":"tmp-partial-1","stagedName":"0"},{"originalName":"tmp-partial-2","stagedName":"1"}]}""";
        var manifestPath = Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName);
        File.WriteAllText(manifestPath, manifestJson);
        File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Only the first file was moved before crash.
        File.Move(
            Path.Combine(paths.TemporaryDirectory, "tmp-partial-1"),
            Path.Combine(stagedDir, "0"));
        File.SetUnixFileMode(Path.Combine(stagedDir, "0"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.True(File.Exists(Path.Combine(paths.TemporaryDirectory, "tmp-partial-2")));

        var actions = protection.RecoverQuarantineAtStartup((_, _) => false);
        Assert.True(actions >= 1);
        Assert.Contains("tmp-partial-1", protection.ListRecognizedTemporaryFileNames());
        Assert.Contains("tmp-partial-2", protection.ListRecognizedTemporaryFileNames());
        Assert.False(Directory.Exists(quarantineDir));
    }

    [Fact]
    public void Partition_locked_recognized_is_retained_not_staged()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-unlocked", [1]);
        protection.CreateRecognizedTemporaryForTests("tmp-locked", [2]);
        var paths = new ClassifyStorePaths(root);
        var lockPath = Path.Combine(paths.TemporaryDirectory, "tmp-locked.lock");
        File.WriteAllText(lockPath, "held");
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.True(protection.AppearsLocked(Path.Combine(paths.TemporaryDirectory, "tmp-locked")));

        var partition = protection.PartitionRecognizedTemporaries(["tmp-unlocked", "tmp-locked"]);
        Assert.Contains("tmp-unlocked", partition.Removable);
        Assert.Contains("tmp-locked", partition.Retained);
        Assert.Empty(partition.Rejected);

        // Staging only removable succeeds; locked file stays put.
        var q = protection.TryStageRecognizedTemporaries(
            "op-part-lock", "cleanup", partition.Removable);
        Assert.NotNull(q);
        Assert.Equal(1, q!.StagedCount);
        Assert.True(File.Exists(Path.Combine(paths.TemporaryDirectory, "tmp-locked")));

        // Passing locked name into stage refuses whole stage (caller must partition first).
        Assert.Null(protection.TryStageRecognizedTemporaries(
            "op-part-lock-bad", "cleanup", ["tmp-locked"]));
        Assert.True(File.Exists(Path.Combine(paths.TemporaryDirectory, "tmp-locked")));

        q.RestoreAndDiscard();
    }

    [Fact]
    public void Startup_without_durable_authority_restores_even_when_manifest_committed_true()
    {
        // manifest.Committed alone must never authorize deletion.
        protection.CreateRecognizedTemporaryForTests("tmp-auth-bound", [4]);
        var paths = new ClassifyStorePaths(root);
        var quarantineDir = Path.Combine(protection.QuarantineRoot, "op-auth-false");
        var stagedDir = Path.Combine(quarantineDir, ClassifyArtifactProtection.StagedDirectoryName);
        Directory.CreateDirectory(quarantineDir);
        Directory.CreateDirectory(stagedDir);
        File.SetUnixFileMode(
            quarantineDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(
            stagedDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        File.Move(
            Path.Combine(paths.TemporaryDirectory, "tmp-auth-bound"),
            Path.Combine(stagedDir, "0"));
        File.SetUnixFileMode(Path.Combine(stagedDir, "0"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var manifestJson =
            """{"operationId":"op-auth-false","kind":"cleanup","committed":true,"entries":[{"originalName":"tmp-auth-bound","stagedName":"0"}]}""";
        var manifestPath = Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName);
        File.WriteAllText(manifestPath, manifestJson);
        File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var actions = protection.RecoverQuarantineAtStartup((_, _) => false);
        Assert.True(actions >= 1);
        Assert.Contains("tmp-auth-bound", protection.ListRecognizedTemporaryFileNames());
        Assert.False(Directory.Exists(quarantineDir));
    }

    [Fact]
    public void Startup_with_durable_authority_deletes_only_manifest_bound_staged()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-auth-del", [5]);
        var paths = new ClassifyStorePaths(root);
        var quarantineDir = Path.Combine(protection.QuarantineRoot, "op-auth-true");
        var stagedDir = Path.Combine(quarantineDir, ClassifyArtifactProtection.StagedDirectoryName);
        Directory.CreateDirectory(quarantineDir);
        Directory.CreateDirectory(stagedDir);
        File.SetUnixFileMode(
            quarantineDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(
            stagedDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        File.Move(
            Path.Combine(paths.TemporaryDirectory, "tmp-auth-del"),
            Path.Combine(stagedDir, "0"));
        File.SetUnixFileMode(Path.Combine(stagedDir, "0"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Inject unknown file next to staged entry — must remain and block shell sweep.
        var unknown = Path.Combine(stagedDir, "injected.bin");
        File.WriteAllBytes(unknown, [9]);
        File.SetUnixFileMode(unknown, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var manifestJson =
            """{"operationId":"op-auth-true","kind":"cleanup","committed":false,"entries":[{"originalName":"tmp-auth-del","stagedName":"0"}]}""";
        File.WriteAllText(
            Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName),
            manifestJson);
        File.SetUnixFileMode(
            Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var actions = protection.RecoverQuarantineAtStartup((_, _) => true);
        // Shell not fully removed due to injected content — do not count as clean action.
        Assert.Equal(0, actions);
        Assert.False(File.Exists(Path.Combine(stagedDir, "0")));
        Assert.True(File.Exists(unknown));
        Assert.DoesNotContain("tmp-auth-del", protection.ListRecognizedTemporaryFileNames());
        Assert.True(Directory.Exists(quarantineDir));
    }

    [Fact]
    public void Malformed_manifest_quarantine_is_left_untouched()
    {
        var quarantineDir = Path.Combine(protection.QuarantineRoot, "op-malformed");
        Directory.CreateDirectory(quarantineDir);
        File.SetUnixFileMode(
            quarantineDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var manifestPath = Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName);
        File.WriteAllText(manifestPath, "{not-json");
        File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var injected = Path.Combine(quarantineDir, "keep-me.txt");
        File.WriteAllText(injected, "stay");
        File.SetUnixFileMode(injected, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var actions = protection.RecoverQuarantineAtStartup((_, _) => true);
        Assert.Equal(0, actions);
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(injected));
        Assert.True(Directory.Exists(quarantineDir));
    }

    [Fact]
    public void Symlink_manifest_quarantine_is_left_untouched()
    {
        var quarantineDir = Path.Combine(protection.QuarantineRoot, "op-symlink-manifest");
        Directory.CreateDirectory(quarantineDir);
        File.SetUnixFileMode(
            quarantineDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var real = Path.Combine(root, "outside-manifest.json");
        File.WriteAllText(real, """{"operationId":"x","kind":"cleanup","committed":true,"entries":[]}""");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName);
        File.CreateSymbolicLink(link, real);
        var injected = Path.Combine(quarantineDir, "payload.bin");
        File.WriteAllBytes(injected, [1]);
        File.SetUnixFileMode(injected, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var actions = protection.RecoverQuarantineAtStartup((_, _) => true);
        Assert.Equal(0, actions);
        Assert.True(File.Exists(injected));
        Assert.True(Directory.Exists(quarantineDir));
        Assert.True(File.Exists(real));
    }

    [Fact]
    public void Unknown_original_name_in_manifest_blocks_startup_mutation()
    {
        var quarantineDir = Path.Combine(protection.QuarantineRoot, "op-unknown-entry");
        var stagedDir = Path.Combine(quarantineDir, ClassifyArtifactProtection.StagedDirectoryName);
        Directory.CreateDirectory(quarantineDir);
        Directory.CreateDirectory(stagedDir);
        File.SetUnixFileMode(
            quarantineDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(
            stagedDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllBytes(Path.Combine(stagedDir, "0"), [1]);
        File.SetUnixFileMode(Path.Combine(stagedDir, "0"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(
            Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName),
            """{"operationId":"op-unknown-entry","kind":"cleanup","committed":true,"entries":[{"originalName":"secret.bin","stagedName":"0"}]}""");
        File.SetUnixFileMode(
            Path.Combine(quarantineDir, ClassifyArtifactProtection.ManifestFileName),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var actions = protection.RecoverQuarantineAtStartup((_, _) => true);
        Assert.Equal(0, actions);
        Assert.True(File.Exists(Path.Combine(stagedDir, "0")));
        Assert.True(Directory.Exists(quarantineDir));
    }

    [Fact]
    public void Finalize_without_durable_authority_does_not_delete()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-no-auth", [1]);
        var q = protection.TryStageRecognizedTemporaries(
            "op-no-auth", "cleanup", ["tmp-no-auth"]);
        Assert.NotNull(q);
        q!.FinalizeWithDurableAuthority(hasDurableAuthority: false);
        // Staged files remain; originals still absent until restore.
        Assert.DoesNotContain("tmp-no-auth", protection.ListRecognizedTemporaryFileNames());
        Assert.True(Directory.Exists(q.Directory));
        q.RestoreAndDiscard();
        Assert.Contains("tmp-no-auth", protection.ListRecognizedTemporaryFileNames());
    }

    [Fact]
    public void Colliding_original_blocks_restore()
    {
        protection.CreateRecognizedTemporaryForTests("tmp-collide", [1]);
        var q = protection.TryStageRecognizedTemporaries(
            "op-collide", "cleanup", ["tmp-collide"]);
        Assert.NotNull(q);
        // Recreate original under same name while staged — restore must not overwrite.
        protection.CreateRecognizedTemporaryForTests("tmp-collide", [9]);
        q!.RestoreAndDiscard();
        // Original collision content remains (write of [9]); staged left if restore blocked.
        var paths = new ClassifyStorePaths(root);
        var original = Path.Combine(paths.TemporaryDirectory, "tmp-collide");
        Assert.True(File.Exists(original));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(original));
    }
}
