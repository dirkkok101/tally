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
}
