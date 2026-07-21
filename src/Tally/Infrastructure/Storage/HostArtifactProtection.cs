using System.Runtime.Versioning;
using Tally.Application.Ports;

namespace Tally.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class HostArtifactProtection : IHostArtifactProtection
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public void EnsureDataRoot(string path)
    {
        RequireLinux();
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectory);
        RequireOwnerOnlyDirectory(path);
    }

    public void ProtectArtifact(string path)
    {
        RequireLinux();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The artifact must exist before it can be protected.", path);
        }

        File.SetUnixFileMode(path, OwnerFile);
        RequireOwnerOnlyArtifact(path);
    }

    public void RequireOwnerOnlyArtifact(string path)
    {
        RequireLinux();
        if (!File.Exists(path) || File.GetUnixFileMode(path) != OwnerFile)
        {
            throw new InvalidOperationException("The artifact is not owner-only.");
        }
    }

    public void RequireOwnerOnlyDirectory(string path)
    {
        RequireLinux();
        if (!Directory.Exists(path) || File.GetUnixFileMode(path) != OwnerDirectory)
        {
            throw new InvalidOperationException("The directory is not owner-only.");
        }
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Ledger storage requires Linux owner-only artifact protection.");
        }
    }
}
