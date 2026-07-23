using System.Reflection;
using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Recovery;
using Xunit;

namespace Tally.Tests.Process;

public sealed class RecoveryErrorProcessTests
{
    [Theory]
    [MemberData(nameof(DeclaredRecoveryErrors))]
    public void Declared_recovery_errors_map_to_their_public_process_contract(string code, int exitCode, string category)
    {
        var mapper = typeof(TallyProcess).GetMethod("ErrorForHandler", BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<ProcessResult>(mapper!.Invoke(null, [code]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
    }

    public static TheoryData<string, int, string> DeclaredRecoveryErrors => new()
    {
        { BackupErrors.Invalid, 3, "validation" },
        { BackupErrors.NotFound, 4, "not_found" },
        { BackupErrors.TargetExists, 5, "conflict" },
        { BackupErrors.Busy, 5, "conflict" },
        { BackupErrors.Incompatible, 7, "compatibility" },
        { BackupErrors.ChecksumMismatch, 8, "integrity" },
        { BackupErrors.Integrity, 8, "integrity" },
        { BackupErrors.HostProtection, 9, "host" },
        { BackupErrors.Permission, 9, "host" },
        { BackupErrors.Disk, 9, "host" },
        { RestoreErrors.Invalid, 3, "validation" },
        { RestoreErrors.NotAuthorized, 3, "validation" },
        { RestoreErrors.CandidateConflict, 5, "conflict" },
        { RestoreErrors.ActivationConflict, 5, "conflict" },
        { RestoreErrors.Busy, 5, "conflict" },
        { RestoreErrors.StaleCurrent, 6, "lifecycle" },
        { RestoreErrors.StaleCandidate, 6, "lifecycle" },
        { RestoreErrors.Incompatible, 7, "compatibility" },
        { RestoreErrors.Integrity, 8, "integrity" },
        { RestoreErrors.HostProtection, 9, "host" },
        { RestoreErrors.Permission, 9, "host" },
        { RestoreErrors.Disk, 9, "host" }
    };
}
