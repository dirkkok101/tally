using System.Runtime.Versioning;

namespace Tally.Infrastructure.Ingest.Storage;

[SupportedOSPlatform("linux")]
public sealed class IngestArtifactProtection
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public void EnsureOwnerOnlyDirectory(string path)
    {
        RequireLinux();
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectory);
        if (File.GetUnixFileMode(path) != OwnerDirectory)
        {
            throw new InvalidOperationException("The ingest directory is not owner-only.");
        }
    }

    public void EnsureOwnerOnly(string path)
    {
        RequireLinux();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The ingest artifact must exist before it can be protected.", path);
        }

        File.SetUnixFileMode(path, OwnerFile);
        if (File.GetUnixFileMode(path) != OwnerFile)
        {
            throw new InvalidOperationException("The ingest artifact is not owner-only.");
        }
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Ingest persistence requires Linux owner-only artifact protection.");
        }
    }
}
