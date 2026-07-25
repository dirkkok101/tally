using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tally.Tests.Ingest.Fixtures;

public sealed class PrivateStatementFixtureSet
{
    public const string ManifestEnvironmentVariable = "TALLY_INGEST_PRIVATE_FIXTURE_MANIFEST";

    private PrivateStatementFixtureSet(IReadOnlyList<PrivateStatementFixture> fixtures)
    {
        Fixtures = fixtures;
    }

    public IReadOnlyList<PrivateStatementFixture> Fixtures { get; }

    public static PrivateStatementFixtureSet? TryLoadFromEnvironment(string repositoryRoot)
    {
        var manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironmentVariable);
        if (string.IsNullOrEmpty(manifestPath))
        {
            return null;
        }

        try
        {
            return Load(repositoryRoot, manifestPath);
        }
        catch (PrivateFixtureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-VALIDATION-FAILED");
        }
    }

    private static PrivateStatementFixtureSet Load(string repositoryRoot, string manifestPath)
    {
        var canonicalRepositoryRoot = Path.GetFullPath(repositoryRoot);
        var canonicalFixtureRoot = Path.GetFullPath(Path.Combine(canonicalRepositoryRoot, "docs", "statements"));
        RequireOwnerOnlyDirectory(canonicalFixtureRoot);
        RequireIgnoredFixtureRoot(canonicalRepositoryRoot);

        var canonicalManifestPath = Path.GetFullPath(manifestPath);
        var fixtureRootPrefix = canonicalFixtureRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalFixtureRoot
            : canonicalFixtureRoot + Path.DirectorySeparatorChar;
        if (!canonicalManifestPath.StartsWith(fixtureRootPrefix, StringComparison.Ordinal))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-MANIFEST-SCOPE");
        }

        RequireOwnerOnlyRegularFile(canonicalManifestPath, "PRIVATE-FIXTURE-MANIFEST-PROTECTION");

        using var document = JsonDocument.Parse(File.ReadAllBytes(canonicalManifestPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.GetInt32() != 1 ||
            !root.TryGetProperty("fixtures", out var fixturesElement) ||
            fixturesElement.ValueKind != JsonValueKind.Array ||
            fixturesElement.GetArrayLength() != 3)
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-MANIFEST-SCHEMA");
        }

        var fixtures = new List<PrivateStatementFixture>(3);
        var locators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixtureElement in fixturesElement.EnumerateArray())
        {
            var locator = RequiredString(fixtureElement, "sourcePath");
            var variantId = RequiredString(fixtureElement, "variantId");
            var expectedDigest = RequiredString(fixtureElement, "sourceSha256");
            if (!locators.Add(locator))
            {
                throw new PrivateFixtureException("PRIVATE-FIXTURE-DUPLICATE-LOCATOR");
            }

            var sourcePath = ResolveStrictFixturePath(canonicalRepositoryRoot, canonicalFixtureRoot, locator);
            RequireOwnerOnlyPath(canonicalFixtureRoot, sourcePath);
            var sourceBytes = File.ReadAllBytes(sourcePath);
            VerifyDigest(sourceBytes, expectedDigest);

            if (!fixtureElement.TryGetProperty("expected", out var expected) || expected.ValueKind != JsonValueKind.Object ||
                !expected.TryGetProperty("accountEvidence", out var accountEvidence) || accountEvidence.ValueKind != JsonValueKind.Object ||
                !accountEvidence.TryGetProperty("permissionEncrypted", out var permissionEncrypted) ||
                permissionEncrypted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new PrivateFixtureException("PRIVATE-FIXTURE-EXPECTED-SCHEMA");
            }

            fixtures.Add(new PrivateStatementFixture(
                ImmutableArray.Create(sourceBytes),
                variantId,
                permissionEncrypted.GetBoolean(),
                expected.Clone()));
        }

        if (fixtures.Select(fixture => fixture.VariantId).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-VARIANT-COUNT");
        }

        return new PrivateStatementFixtureSet(fixtures);
    }

    private static string ResolveStrictFixturePath(string repositoryRoot, string fixtureRoot, string locator)
    {
        if (Path.IsPathRooted(locator))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-LOCATOR-ROOTED");
        }

        var segments = locator.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments.Any(segment => segment is "." or "..") ||
            !string.Equals(segments[0], "docs", StringComparison.Ordinal) ||
            !string.Equals(segments[1], "statements", StringComparison.Ordinal))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-LOCATOR-SCOPE");
        }

        var canonicalPath = Path.GetFullPath(Path.Combine(repositoryRoot, locator));
        var requiredPrefix = fixtureRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fixtureRoot
            : fixtureRoot + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-LOCATOR-SCOPE");
        }

        return canonicalPath;
    }

    private static void RequireOwnerOnlyPath(string fixtureRoot, string sourcePath)
    {
        var current = Path.GetDirectoryName(sourcePath);
        while (current is not null && current.StartsWith(fixtureRoot, StringComparison.Ordinal))
        {
            RequireOwnerOnlyDirectory(current);
            if (string.Equals(current, fixtureRoot, StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        RequireOwnerOnlyRegularFile(sourcePath, "PRIVATE-FIXTURE-SOURCE-PROTECTION");
    }

    private static void RequireOwnerOnlyDirectory(string path)
    {
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-DIRECTORY-PROTECTION");
        }
    }

    private static void RequireOwnerOnlyRegularFile(string path, string errorCode)
    {
        if (!File.Exists(path))
        {
            throw new PrivateFixtureException($"{errorCode}-MISSING");
        }

        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new PrivateFixtureException($"{errorCode}-TYPE");
        }

        if (GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new PrivateFixtureException($"{errorCode}-MODE");
        }
    }

    private static void RequireIgnoredFixtureRoot(string repositoryRoot)
    {
        var ignoreLines = File.ReadAllLines(Path.Combine(repositoryRoot, ".gitignore"));
        if (!ignoreLines.Contains("docs/statements/", StringComparer.Ordinal))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-IGNORE-POLICY");
        }
    }

    private static UnixFileMode GetUnixFileMode(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-PLATFORM");
        }

        return File.GetUnixFileMode(path);
    }

    private static void VerifyDigest(byte[] sourceBytes, string expectedDigest)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedDigest);
        }
        catch (FormatException)
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-DIGEST-SCHEMA");
        }

        if (expected.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(sourceBytes), expected))
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-DIGEST-MISMATCH");
        }
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new PrivateFixtureException("PRIVATE-FIXTURE-MANIFEST-SCHEMA");
        }

        return property.GetString() ?? throw new PrivateFixtureException("PRIVATE-FIXTURE-MANIFEST-SCHEMA");
    }
}

public sealed class PrivateStatementFixture
{
    public PrivateStatementFixture(
        ImmutableArray<byte> sourceBytes,
        string variantId,
        bool permissionEncrypted,
        JsonElement expected)
    {
        SourceBytes = sourceBytes;
        VariantId = variantId;
        PermissionEncrypted = permissionEncrypted;
        Expected = expected;
    }

    public ImmutableArray<byte> SourceBytes { get; }

    public string VariantId { get; }

    public bool PermissionEncrypted { get; }

    public JsonElement Expected { get; }
}

public sealed class PrivateFixtureException(string errorCode) : InvalidOperationException(errorCode);
