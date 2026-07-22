using System.Text.Json;
using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Xunit;

namespace Tally.Tests.SystemFeatures.Guidance;

[SupportedOSPlatform("linux")]
public sealed class GuidanceCompatibilityTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-guidance-{Guid.NewGuid():N}");

    // TC-LEDGER-AGENT-CONTRACT-CONFORMANCE
    [Fact]
    public async Task Public_workflows_remain_discoverable_without_installed_guidance()
    {
        var version = await Run(["version"]);
        var schemas = await Run(["schema", "list"]);

        Assert.Equal(0, version.ExitCode);
        Assert.Equal(0, schemas.ExitCode);
        Assert.Contains("ledger.transaction.record", schemas.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task List_reports_supported_missing_bundles_without_writing_scope()
    {
        var result = await Run(["system", "guidance", "list", "--input", "-"], Request(new ListGuidanceInput(root)));

        Assert.Equal(0, result.ExitCode);
        var envelope = Assert.IsType<ResultEnvelope>(JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope));
        var list = Assert.IsType<GuidanceListResult>(JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.GuidanceListResult));
        Assert.Equal(2, list.Bundles.Count);
        Assert.All(list.Bundles, bundle => Assert.Equal("missing", bundle.Status));
        Assert.False(Directory.Exists(root));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public void Guidance_operations_publish_typed_registry_contracts()
    {
        var registry = OperationRegistry.Create();
        var list = Assert.IsType<OperationDescriptor>(registry.Find("system.guidance.list"));
        var check = Assert.IsType<OperationDescriptor>(registry.Find("system.guidance.check"));
        var install = Assert.IsType<OperationDescriptor>(registry.Find("system.guidance.install"));

        Assert.Equal(typeof(ListGuidanceInput), list.RequestTypeInfo.Type);
        Assert.Equal(typeof(GuidanceCheckResult), check.ResultTypeInfo.Type);
        Assert.False(list.RequiresIdempotencyKey);
        Assert.False(check.RequiresIdempotencyKey);
        Assert.True(install.RequiresIdempotencyKey);
        Assert.All(new[] { list, check, install }, operation => Assert.NotEqual("FoundationOperationHandler", operation.HandlerTarget));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Manifest_ranges_and_operations_match_the_public_registry()
    {
        var result = AssertList(await Run(["system", "guidance", "list", "--input", "-"], Request(new ListGuidanceInput(root))));
        var registry = OperationRegistry.Create();

        Assert.All(result.Bundles, bundle =>
        {
            Assert.Equal("1.0", bundle.MinimumExecutableVersion);
            Assert.Equal("1.0", bundle.MaximumExecutableVersion);
            Assert.Equal("1.0", bundle.MinimumContractVersion);
            Assert.Equal("1.0", bundle.MaximumContractVersion);
            Assert.All(bundle.OperationIds, operationId => Assert.NotNull(registry.Find(operationId)));
        });
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Unsupported_host_fails_before_scope_write()
    {
        var result = await Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput("unknown", root), "install-unknown"));

        AssertError(result, 3, GuidanceErrors.UnsupportedHost);
        Assert.False(Directory.Exists(root));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Relative_scope_is_rejected_before_write()
    {
        var result = await Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput("codex", "relative/scope"), "install-relative"));

        AssertError(result, 3, GuidanceErrors.UnsafePath);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Traversal_scope_is_rejected_before_write()
    {
        var traversal = Path.Combine(root, "child", "..", "escape");
        var result = await Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput("codex", traversal), "install-traversal"));

        AssertError(result, 3, GuidanceErrors.UnsafePath);
        Assert.False(Directory.Exists(root));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Root_scope_is_rejected_before_write()
    {
        var result = await Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput("codex", Path.GetPathRoot(root)!), "install-root"));

        AssertError(result, 3, GuidanceErrors.UnsafePath);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Symbolic_link_scope_is_rejected_before_write()
    {
        Directory.CreateDirectory(root);
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, real);

        var result = await Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput("codex", link), "install-link"));

        AssertError(result, 3, GuidanceErrors.UnsafePath);
        Assert.False(Directory.Exists(Path.Combine(real, ".agents")));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Symbolic_link_inside_install_chain_is_rejected_before_target_write()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".agents", "skills"));
        Directory.CreateSymbolicLink(Path.Combine(root, ".agents", "skills", "tally-ledger"), target);

        var result = await Install("codex", "install-linked-destination");

        AssertError(result, 3, GuidanceErrors.UnsafePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Codex_install_publishes_only_the_documented_host_scope()
    {
        var installed = AssertInstall(await Install("codex", "install-codex"));

        Assert.Equal(CodexSkillPath, installed.InstallPath);
        Assert.True(File.Exists(CodexSkillPath));
        Assert.True(File.Exists(CodexManifestPath));
        Assert.False(Directory.Exists(Path.Combine(root, ".claude")));
        Assert.Equal(installed.Checksum, await Sha256Async(CodexSkillPath));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Claude_code_install_publishes_only_the_documented_host_scope()
    {
        var installed = AssertInstall(await Install("claude-code", "install-claude"));
        var skillPath = Path.Combine(root, ".claude", "skills", "tally-ledger", "SKILL.md");

        Assert.Equal(skillPath, installed.InstallPath);
        Assert.True(File.Exists(skillPath));
        Assert.False(Directory.Exists(Path.Combine(root, ".agents")));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Identical_install_replay_returns_the_original_identity_and_permissions()
    {
        var first = AssertInstall(await Install("codex", "install-replay"));
        var second = AssertInstall(await Install("codex", "install-replay"));

        Assert.Equal(first, second);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CodexSkillPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CodexManifestPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(CodexSkillPath)!));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Retry_after_bundle_publication_reconciles_the_matching_manifest()
    {
        var first = AssertInstall(await Install("codex", "install-crash"));
        File.Delete(CodexManifestPath);

        var replay = AssertInstall(await Install("codex", "install-crash"));

        Assert.Equal(first, replay);
        Assert.True(File.Exists(CodexManifestPath));
        Assert.Equal("compatible", AssertCheck(await Check("codex")).Bundle.Status);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Check_classifies_corrupt_bundle_as_invalid()
    {
        await Install("codex", "install-corrupt");
        await File.WriteAllTextAsync(CodexSkillPath, "corrupt");

        Assert.Equal("invalid", AssertCheck(await Check("codex")).Bundle.Status);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Check_classifies_malformed_installed_manifest_as_invalid()
    {
        await Install("codex", "install-malformed");
        await File.WriteAllTextAsync(CodexManifestPath, "{");

        Assert.Equal("invalid", AssertCheck(await Check("codex")).Bundle.Status);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Check_classifies_older_bundle_without_mutation()
    {
        await Install("codex", "install-outdated");
        var installed = await ReadInstalledManifest();
        await WriteInstalledManifest(installed with { BundleVersion = "0.9.0" });

        Assert.Equal("outdated", AssertCheck(await Check("codex")).Bundle.Status);
        Assert.Equal("0.9.0", (await ReadInstalledManifest()).BundleVersion);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Newer_bundle_is_classified_and_not_downgraded()
    {
        await Install("codex", "install-newer");
        var installed = await ReadInstalledManifest();
        await WriteInstalledManifest(installed with { BundleVersion = "2.0.0" });

        Assert.Equal("newer", AssertCheck(await Check("codex")).Bundle.Status);
        AssertError(await Install("codex", "install-newer"), 7, GuidanceErrors.Incompatible);
        Assert.Equal("2.0.0", (await ReadInstalledManifest()).BundleVersion);
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Incompatible_request_contract_fails_before_write()
    {
        var input = JsonSerializer.SerializeToElement(new InstallGuidanceInput("codex", root), LedgerJsonContext.Default.InstallGuidanceInput);
        var request = JsonSerializer.Serialize(new RequestEnvelope("2.0", new SafeActor("automation", "guidance-test"), input, "install-incompatible"), LedgerJsonContext.Default.RequestEnvelope);

        AssertError(await Run(["system", "guidance", "install", "--input", "-"], request), 3, "validation.invalid_input");
        Assert.False(Directory.Exists(root));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Install_requires_an_idempotency_key_before_dispatch()
    {
        var result = await Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput("codex", root)));

        AssertError(result, 3, "validation.invalid_input");
        Assert.False(Directory.Exists(root));
    }

    // TC-LEDGER-AGENT-CONTRACT-CONFORMANCE
    [Fact]
    public async Task Bundled_content_teaches_only_schema_first_public_invocation()
    {
        await Install("codex", "install-content");
        var content = await File.ReadAllTextAsync(CodexSkillPath);
        string[] forbidden = ["mailbox", "mime", "whatsapp", "delivery", "schedule", "recipient", "acknowledgement", "sqlite", "file access"];

        Assert.Contains("tally version", content, StringComparison.Ordinal);
        Assert.Contains("tally schema list", content, StringComparison.Ordinal);
        Assert.Contains("tally schema show", content, StringComparison.Ordinal);
        Assert.All(forbidden, term => Assert.DoesNotContain(term, content, StringComparison.OrdinalIgnoreCase));
    }

    // TC-LEDGER-SKILL-COMPATIBILITY-CONTRACT
    [Fact]
    public async Task Successful_install_is_reported_compatible_by_check_and_list()
    {
        await Install("codex", "install-compatible");

        Assert.Equal("compatible", AssertCheck(await Check("codex")).Bundle.Status);
        var list = AssertList(await Run(["system", "guidance", "list", "--input", "-"], Request(new ListGuidanceInput(root))));
        Assert.Equal("compatible", Assert.Single(list.Bundles, bundle => bundle.Host == "codex").Status);
        Assert.Equal("missing", Assert.Single(list.Bundles, bundle => bundle.Host == "claude-code").Status);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<ProcessResult> Run(string[] arguments, string? input = null) =>
        await new TallyProcess(OperationRegistry.Create()).RunAsync(arguments, input, CancellationToken.None);

    private string CodexSkillPath => Path.Combine(root, ".agents", "skills", "tally-ledger", "SKILL.md");
    private string CodexManifestPath => Path.Combine(root, ".agents", "skills", "tally-ledger", ".tally-guidance.json");
    private Task<ProcessResult> Install(string host, string key) => Run(["system", "guidance", "install", "--input", "-"], Request(new InstallGuidanceInput(host, root), key));
    private Task<ProcessResult> Check(string host) => Run(["system", "guidance", "check", "--input", "-"], Request(new CheckGuidanceInput(host, root)));

    private static GuidanceListResult AssertList(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = Assert.IsType<ResultEnvelope>(JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope));
        return Assert.IsType<GuidanceListResult>(JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.GuidanceListResult));
    }

    private static GuidanceCheckResult AssertCheck(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = Assert.IsType<ResultEnvelope>(JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope));
        return Assert.IsType<GuidanceCheckResult>(JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.GuidanceCheckResult));
    }

    private static GuidanceInstallResult AssertInstall(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = Assert.IsType<ResultEnvelope>(JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope));
        return Assert.IsType<GuidanceInstallResult>(JsonSerializer.Deserialize(envelope.Result!.Value, LedgerJsonContext.Default.GuidanceInstallResult));
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        var envelope = Assert.IsType<ResultEnvelope>(JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope));
        Assert.Equal(code, envelope.Error!.Code);
    }

    private async Task<InstalledGuidanceManifest> ReadInstalledManifest() =>
        Assert.IsType<InstalledGuidanceManifest>(JsonSerializer.Deserialize(await File.ReadAllBytesAsync(CodexManifestPath), LedgerJsonContext.Default.InstalledGuidanceManifest));

    private async Task WriteInstalledManifest(InstalledGuidanceManifest manifest) =>
        await File.WriteAllBytesAsync(CodexManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, LedgerJsonContext.Default.InstalledGuidanceManifest));

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string Request<T>(T input, string? idempotencyKey = null) => JsonSerializer.Serialize(
        new RequestEnvelope("1.0", new SafeActor("automation", "guidance-test"), JsonSerializer.SerializeToElement(input, typeof(T), LedgerJsonContext.Default), idempotencyKey),
        LedgerJsonContext.Default.RequestEnvelope);
}
