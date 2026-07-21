namespace Tally.Application.Ports;

public interface IAuthoritativeStoreActivator
{
    Task ActivateAsync(string generationId, string verificationFingerprint, CancellationToken cancellationToken);
}

public interface IHostArtifactProtection
{
    void EnsureDataRoot(string path);
    void ProtectArtifact(string path);
    void RequireOwnerOnlyArtifact(string path);
    void RequireOwnerOnlyDirectory(string path);
}
