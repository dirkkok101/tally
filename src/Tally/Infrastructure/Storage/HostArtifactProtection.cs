using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tally.Application.Ports;

namespace Tally.Infrastructure.Storage;

/// <summary>
/// Linux owner-only artifact guard: exact 0600 files / 0700 directories and
/// ownership by the process effective UID before trust.
/// </summary>
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
        if (!File.Exists(path)
            || File.GetUnixFileMode(path) != OwnerFile
            || !IsOwnedByEffectiveUser(path))
        {
            throw new InvalidOperationException("The artifact is not owner-only.");
        }
    }

    public void RequireOwnerOnlyDirectory(string path)
    {
        RequireLinux();
        if (!Directory.Exists(path)
            || File.GetUnixFileMode(path) != OwnerDirectory
            || !IsOwnedByEffectiveUser(path))
        {
            throw new InvalidOperationException("The directory is not owner-only.");
        }
    }

    private static bool IsOwnedByEffectiveUser(string path)
    {
        if (Lstat(path, out var status) != 0)
        {
            return false;
        }

        return status.st_uid == Geteuid();
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Ledger storage requires Linux owner-only artifact protection.");
        }
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Lstat(string path, out StatBuf buf);

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
