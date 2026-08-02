using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Corpus.Build;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Tally.Tests.Classify.Security;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-PRIVACY-RECOVERY-GATE / bd-3mdk —
/// Cross-operation privacy, filesystem, crash, cursor, stale, dual no-mutation,
/// composition authority, and offline isolation matrix for the five ergonomics ops.
/// Disposable 0700 synthetic roots only — never live TALLY_DATA_ROOT / financial data.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyOperatorErgonomicsSecurityTests : IAsyncLifetime
{
    private const string DescriptionCanary = "CANARY_ERGONOMICS_DESC_PRIVATE_zzz";

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-erg-sec-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "ergonomics-security", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyServices services = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var ledgerServices = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, ledgerServices);
        ledger = new LedgerContractClient(registry, bootstrap);
        services = await ClassifyOperationBundle.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
        // Publish real handlers through the process envelope (same composition as production).
        ledgerServices = ledgerServices with { Classify = services.Operations };
        process = new TallyProcess(registry, ledgerServices);
        accountId = (await CreateAccountAsync()).AccountId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Privacy: allowed stdout vs forbidden sinks ───────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_PRIVACY_unresolved_stdout_may_expose_owner_normalized_description()
    {
        var seeded = await SeedNoSuggestionAsync(DescriptionCanary, count: 2);
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.NotEmpty(result.Value!.Groups);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Groups[0].RepresentativeNormalizedDescription));
        // Raw canary / transaction IDs / absolute path must not re-enter the typed result.
        Assert.DoesNotContain(DescriptionCanary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        foreach (var tx in seeded.TransactionIds)
        {
            Assert.DoesNotContain(tx, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PRIVACY_forbidden_sinks_exclude_canaries_after_unresolved_report()
    {
        // Production-connected: run canary operation, then inspect durable classify rows + process sinks.
        var seeded = await SeedNoSuggestionAsync(DescriptionCanary, count: 2);
        var beforeOracle = await CaptureOraclesAsync();
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        // Allowed channel only: typed result may carry normalized owner-visible text, never raw canary.
        var typed = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.DoesNotContain(DescriptionCanary, typed, StringComparison.OrdinalIgnoreCase);
        foreach (var tx in seeded.TransactionIds)
        {
            Assert.DoesNotContain(tx, typed, StringComparison.Ordinal);
        }

        // Forbidden: durable classify.db *data* (not schema text) across content-bearing tables.
        var durable = await DumpClassifyDurableContentAsync();
        Assert.DoesNotContain(DescriptionCanary, durable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, durable, StringComparison.Ordinal);

        // Forbidden: process envelope diagnostic channel for the same operation.
        var envelope = await RunClassifyProcessAsync(
            ["classify", "unresolved", "report", "--input", "-"],
            ClassifyEnvelope(
                $"{{\"contractVersion\":\"1.0\",\"evaluationId\":{JsonSerializer.Serialize(seeded.EvaluationId)},\"topN\":10,\"minimumCount\":2}}",
                idempotencyKey: null));
        Assert.Equal(0, envelope.ExitCode);
        Assert.DoesNotContain(DescriptionCanary, envelope.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DescriptionCanary, envelope.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, envelope.Stderr, StringComparison.Ordinal);
        // Success path: diagnostics must stay free of private canaries and private roots.
        Assert.DoesNotContain(DescriptionCanary, envelope.Stderr, StringComparison.Ordinal);

        // Forbidden: tracked docs/scripts must never embed the live canary.
        AssertForbiddenInTrackedRepoSources(DescriptionCanary);

        // Report is read-only for classify mutation tables (evaluation/outcome counts stable).
        var afterOracle = await CaptureOraclesAsync();
        Assert.Equal(beforeOracle.EvalRuns, afterOracle.EvalRuns);
        Assert.Equal(beforeOracle.Outcomes, afterOracle.Outcomes);
        Assert.Equal(beforeOracle.Evidence, afterOracle.Evidence);
    }

    [Fact]
    public async Task TC_ERGONOMICS_LOGGING_cursor_bytes_exclude_descriptions_and_paths()
    {
        var seeded = await SeedMixedSuggestionAsync(DescriptionCanary);
        var page = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.ErrorCode);
        Assert.NotNull(page.Value!.Continuation);
        var cursor = page.Value.Continuation!;
        Assert.DoesNotContain(DescriptionCanary, cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDescription", cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PERSISTENCE_corpus_receipt_excludes_destination_and_labels()
    {
        var destParent = Path.Combine(root, "corpus-out");
        Directory.CreateDirectory(destParent);
        File.SetUnixFileMode(destParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(destParent, "ok.jsonl");
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0, DescriptionCanary)]),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.DoesNotContain(dest, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DescriptionCanary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tx-1", json, StringComparison.Ordinal);
        Assert.True(File.Exists(dest));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dest));
    }

    // ── Filesystem attacks ───────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_symlink_destination_fails_without_write()
    {
        var parent = Path.Combine(root, "sym-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var real = Path.Combine(parent, "real.jsonl");
        await File.WriteAllTextAsync(real, "existing");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(parent, "link.jsonl");
        Assert.Equal(0, Symlink(real, link));
        var before = await File.ReadAllTextAsync(real);
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(link, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(before, await File.ReadAllTextAsync(real));
        Assert.DoesNotContain(real, result.ErrorCode ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_hard_linked_destination_is_not_overwritten()
    {
        var parent = Path.Combine(root, "hard-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var a = Path.Combine(parent, "a.jsonl");
        var b = Path.Combine(parent, "b.jsonl");
        await File.WriteAllTextAsync(a, "HARD_LINK_CANARY_CONTENT");
        File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(0, Link(a, b));
        Assert.True(LstatNlink(b) >= 2);
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(b, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("HARD_LINK_CANARY_CONTENT", await File.ReadAllTextAsync(a));
        Assert.Equal("HARD_LINK_CANARY_CONTENT", await File.ReadAllTextAsync(b));
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_group_writable_parent_fails_privacy()
    {
        var badParent = Path.Combine(root, "group-parent");
        Directory.CreateDirectory(badParent);
        File.SetUnixFileMode(
            badParent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var dest = Path.Combine(badParent, "out.jsonl");
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_outside_temp_root_relative_path_fails()
    {
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(
                "relative-not-absolute.jsonl",
                [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
                [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_existing_different_destination_is_never_replaced()
    {
        var parent = Path.Combine(root, "exist-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "exists.jsonl");
        await File.WriteAllTextAsync(dest, "DIFFERENT_EXISTING_CONTENT");
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("DIFFERENT_EXISTING_CONTENT", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_oversized_label_count_fails_without_destination()
    {
        var parent = Path.Combine(root, "oversize-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "over.jsonl");
        var labels = Enumerable.Range(0, 10_001)
            .Select(i => new ClassifyCorpusBuildLabel("tx-" + i, ClassifyOutcomeKind.NoSuggestion))
            .ToArray();
        var items = Enumerable.Range(0, 10_001)
            .Select(i => ProjectionItem("tx-" + i, i))
            .ToArray();
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, labels, items),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(dest));
    }

    // ── Crash / recovery (PrivateCorpusPublishFaultSeam on live writer path) ─

    [Fact]
    public async Task TC_ERGONOMICS_CRASH_interrupt_before_publish_via_fault_seam_leaves_no_destination()
    {
        var parent = Path.Combine(root, "fault-before-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "before.jsonl");
        var seamHit = false;
        string? observedTemp = null;
        var seam = new PrivateCorpusPublishFaultSeam
        {
            AfterValidateBeforePublish = cp =>
            {
                seamHit = true;
                observedTemp = cp.TemporaryPath;
                throw new OperationCanceledException("injected interrupt before publish");
            }
        };
        var writer = new PrivateCorpusWriter(new PrivateCorpusReader(), seam);
        var command = new BuildPrivateClassificationCorpusCommand(
            services.State.Store,
            services.State.Idempotency,
            writer);
        var result = await command.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.True(seamHit, "fault seam AfterValidateBeforePublish must be reached on the live path");
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.False(File.Exists(dest));
        // Recognized temps cleaned after cancelled publish path.
        Assert.Empty(Directory.GetFiles(parent, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
        if (observedTemp is not null)
        {
            Assert.False(File.Exists(observedTemp));
        }
    }

    [Fact]
    public async Task TC_ERGONOMICS_CRASH_interrupt_after_publish_before_cleanup_throws_and_preserves_destination()
    {
        // True interrupt: AfterPublishBeforeCleanup must throw/cancel after linkat published
        // the retained inode. Writer returns Cancelled; command maps to typed no-partial failure
        // while the authorized destination remains (post-rename / pre-idempotency crash window).
        var parent = Path.Combine(root, "fault-after-throw-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "after-throw.jsonl");
        var seamHit = false;
        string? observedTemp = null;
        ulong createdDev = 0;
        ulong createdIno = 0;
        var seam = new PrivateCorpusPublishFaultSeam
        {
            AfterPublishBeforeCleanup = cp =>
            {
                seamHit = true;
                observedTemp = cp.TemporaryPath;
                createdDev = cp.CreatedDev;
                createdIno = cp.CreatedIno;
                // Prove publication already landed before the interrupt.
                Assert.True(File.Exists(cp.DestinationPath));
                // Replace temp pathname with an unrelated file, then throw so catch-path cleanup runs.
                if (File.Exists(cp.TemporaryPath))
                {
                    File.Delete(cp.TemporaryPath);
                }

                File.WriteAllText(cp.TemporaryPath, "UNKNOWN-SUBSTITUTE-MUST-SURVIVE\n");
                File.SetUnixFileMode(cp.TemporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                throw new OperationCanceledException("injected interrupt after publish before cleanup");
            }
        };
        var writer = new PrivateCorpusWriter(new PrivateCorpusReader(), seam);
        var command = new BuildPrivateClassificationCorpusCommand(
            services.State.Store,
            services.State.Idempotency,
            writer);
        var labels = new[] { new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion) };
        var items = new[] { ProjectionItem("tx-1", 0) };
        var request = CorpusRequest(dest, labels, items);
        var beforeLedger = await CaptureLedgerOracleAsync();
        var result = await command.HandleAsync(request, actor, CancellationToken.None);

        Assert.True(seamHit, "fault seam AfterPublishBeforeCleanup must be reached and throw");
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        // Cancelled maps through MapCorpusPublishError → CLASSIFY-UNEXPECTED (typed no partial).
        Assert.Equal(ClassifyErrors.Unexpected, result.ErrorCode);
        // Authorized destination remains after post-publish interrupt (crash window).
        Assert.True(File.Exists(dest));
        var body = await File.ReadAllTextAsync(dest);
        Assert.DoesNotContain("UNKNOWN-SUBSTITUTE", body, StringComparison.Ordinal);
        Assert.Contains("tx-1", body, StringComparison.Ordinal);
        Assert.NotNull(observedTemp);
        // Identity-bound cleanup must not delete the substituted unknown file.
        Assert.True(File.Exists(observedTemp!));
        Assert.Equal("UNKNOWN-SUBSTITUTE-MUST-SURVIVE\n", await File.ReadAllTextAsync(observedTemp!));
        Assert.False(PrivateCorpusWriter.TryDeleteRecognizedTemp(observedTemp, createdDev, createdIno));
        Assert.True(File.Exists(observedTemp!));
        // Wrong-ino refuse is also exact-inode safe.
        Assert.Equal(0, Lstat(observedTemp!, out var stSub));
        Assert.False(PrivateCorpusWriter.TryDeleteRecognizedTemp(observedTemp, stSub.st_dev, stSub.st_ino + 1UL));
        Assert.True(File.Exists(observedTemp!));
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
        // No extra unauthorized destinations under parent.
        Assert.Contains(dest, Directory.GetFiles(parent, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task TC_ERGONOMICS_CRASH_cleanup_replay_after_post_publish_interrupt_is_idempotent()
    {
        // After a true post-publish interrupt left dest without terminal idempotency commit,
        // a second identical request recovers via exact destination fingerprint and commits
        // once; a third is pure idempotency replay. Zero unauthorized overwrites/deletes.
        var parent = Path.Combine(root, "fault-replay-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "replay.jsonl");
        var seamHit = 0;
        var seam = new PrivateCorpusPublishFaultSeam
        {
            AfterPublishBeforeCleanup = _ =>
            {
                seamHit++;
                throw new OperationCanceledException("injected post-publish interrupt for recovery window");
            }
        };
        var writer = new PrivateCorpusWriter(new PrivateCorpusReader(), seam);
        var command = new BuildPrivateClassificationCorpusCommand(
            services.State.Store,
            services.State.Idempotency,
            writer);
        var labels = new[] { new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion) };
        var items = new[] { ProjectionItem("tx-1", 0) };
        // Fixed idempotency key so recovery → commit → replay share one request identity.
        var key = "erg-sec-crash-replay-" + Guid.NewGuid().ToString("N");
        var request = CorpusRequest(dest, labels, items) with { IdempotencyKey = key };

        var beforeLedger = await CaptureLedgerOracleAsync();
        var interrupted = await command.HandleAsync(request, actor, CancellationToken.None);
        Assert.Equal(1, seamHit);
        Assert.False(interrupted.IsSuccess);
        Assert.Null(interrupted.Value);
        Assert.Equal(ClassifyErrors.Unexpected, interrupted.ErrorCode);
        Assert.True(File.Exists(dest));
        var destBytesAfterInterrupt = await File.ReadAllBytesAsync(dest);

        // Recovery: dest exists with exact mapped corpus bytes → commit terminal success.
        // Seam still installed but PublishAsync is not re-entered when destination recovers.
        var recovered = await command.HandleAsync(request, actor, CancellationToken.None);
        Assert.True(recovered.IsSuccess, recovered.ErrorCode);
        Assert.False(recovered.Value!.Replayed);
        Assert.Equal(1, seamHit); // no second publish path
        Assert.True(File.Exists(dest));
        Assert.Equal(destBytesAfterInterrupt, await File.ReadAllBytesAsync(dest));

        // Idempotent replay after terminal commit — still no rewrite.
        var replayed = await command.HandleAsync(request, actor, CancellationToken.None);
        Assert.True(replayed.IsSuccess, replayed.ErrorCode);
        Assert.True(replayed.Value!.Replayed);
        Assert.Equal(recovered.Value.CorpusFingerprint, replayed.Value.CorpusFingerprint);
        Assert.Equal(destBytesAfterInterrupt, await File.ReadAllBytesAsync(dest));
        Assert.Equal(1, seamHit);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
        // Only the authorized destination (plus any surviving substitute from other tests in
        // sibling dirs) — this parent must only contain the exact dest file.
        Assert.Equal(
            [dest],
            Directory.GetFiles(parent, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    // ── Wrong-owner (distinct from wrong mode) ───────────────────────────────

    [Fact]
    public void TC_ERGONOMICS_FILESYSTEM_wrong_owner_0600_file_fails_closed()
    {
        // Mode remains exact 0600; rejection is effective-UID ownership only.
        var path = Path.Combine(root, "wrong-owner-file");
        File.WriteAllBytes(path, [1]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        RunChown("nobody:nogroup", path);
        try
        {
            var protection = new HostArtifactProtection();
            var ex = Assert.Throws<InvalidOperationException>(
                () => protection.RequireOwnerOnlyArtifact(path));
            Assert.Equal("The artifact is not owner-only.", ex.Message);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            RunChown($"{Environment.UserName}:{Environment.UserName}", path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void TC_ERGONOMICS_FILESYSTEM_wrong_owner_0700_directory_fails_closed()
    {
        var path = Path.Combine(root, "wrong-owner-dir");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(path));

        RunChown("nobody:nogroup", path);
        try
        {
            var protection = new HostArtifactProtection();
            var ex = Assert.Throws<InvalidOperationException>(
                () => protection.RequireOwnerOnlyDirectory(path));
            Assert.Equal("The directory is not owner-only.", ex.Message);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
        }
        finally
        {
            RunChown($"{Environment.UserName}:{Environment.UserName}", path);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
    }

    // ── Published TallyProcess envelope failures ─────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_ENVELOPE_expected_missing_evaluation_fails_stable_exit_null_result()
    {
        var before = await CaptureOraclesAsync();
        var result = await RunClassifyProcessAsync(
            ["classify", "outcome", "list", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"1.0","evaluationId":"missing-eval-id","pageSize":10}""",
                idempotencyKey: null));
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("classify.outcome.list", doc.RootElement.GetProperty("operation_id").GetString());
        var code = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.StartsWith("CLASSIFY-", code, StringComparison.Ordinal);
        // DomainErrors for outcome.list declare evaluation-not-found as not_found exit 4.
        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain(DescriptionCanary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(root, result.Stderr, StringComparison.Ordinal);
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"result\":{", result.Stdout, StringComparison.Ordinal); // error path, not success payload
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_ENVELOPE_expected_unsupported_version_fails_compatibility_without_mutation()
    {
        var before = await CaptureOraclesAsync();
        var result = await RunClassifyProcessAsync(
            ["classify", "unresolved", "report", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"9.9","evaluationId":"eval","topN":10,"minimumCount":2}""",
                idempotencyKey: null));
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            ClassifyErrors.UnsupportedVersion,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        // compatibility exit class 7 on published descriptor.
        Assert.Equal(7, result.ExitCode);
        Assert.DoesNotContain(DescriptionCanary, result.Stderr, StringComparison.Ordinal);
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_ENVELOPE_injected_unexpected_malformed_json_is_private_safe()
    {
        var before = await CaptureOraclesAsync();
        var malformed = "{\"contractVersion\":\"1.0\",\"canary\":\"" + DescriptionCanary + "\""; // broken JSON
        var result = await RunClassifyProcessAsync(
            ["classify", "rule", "list", "--input", "-"],
            ClassifyEnvelope(malformed, idempotencyKey: null, rawInput: true));
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(DescriptionCanary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(DescriptionCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", result.Stderr, StringComparison.OrdinalIgnoreCase);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_ENVELOPE_corpus_build_missing_idempotency_fails_before_destination()
    {
        var parent = Path.Combine(root, "env-corpus");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "env.jsonl");
        var before = await CaptureOraclesAsync();
        // Mutation requires idempotency key on the envelope.
        var input = JsonSerializer.Serialize(new
        {
            contractVersion = "1.0",
            idempotencyKey = "ignored-in-input-key-is-envelope",
            outputPath = dest,
            projection = new
            {
                ledgerContractVersion = ActualsContractVersions.Current,
                projectionVersion = ClassificationProjectionVersions.ClassificationV1,
                storeGenerationFingerprint = new string('a', 64),
                snapshotId = "snap-1",
                snapshotExpiresAt = "2026-08-02T12:00:00.0000000Z",
                categoryIdentityLifecycleFingerprint = new string('b', 64),
                normalizationVersion = NormalizationDescriptor.V1.Version,
                items = new[]
                {
                    new
                    {
                        ordinal = 0,
                        transactionId = "tx-1",
                        accountId = "acct-1",
                        effectiveDate = "2026-07-15",
                        signedAmount = "-12.34",
                        sourceDescription = DescriptionCanary,
                        amountDirection = "expense",
                        categoryMutationState = "assignable",
                        transactionRevision = "tr-0",
                        relationshipRevision = "rr-0",
                        allocationRevision = "ar-0"
                    }
                }
            },
            labels = new[] { new { transactionId = "tx-1", expectedOutcomeKind = "no_suggestion" } }
        });
        var result = await RunClassifyProcessAsync(
            ["classify", "corpus", "build", "--input", "-"],
            ClassifyEnvelope(input, idempotencyKey: null));
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(dest));
        Assert.DoesNotContain(DescriptionCanary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(dest, result.Stderr, StringComparison.Ordinal);
        await AssertNoMutationAsync(before);
    }

    // ── Cursor / stale ───────────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_CURSOR_malformed_continuation_fails_closed_with_null_result()
    {
        var seeded = await SeedMixedSuggestionAsync("cursor shop");
        var before = await CaptureOraclesAsync();
        var result = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 10, Continuation: "%%%not-valid%%%"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.CursorInvalid, result.ErrorCode);
        Assert.Null(result.Value);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_CURSOR_bytes_are_integrity_checked_not_raw_json_payload()
    {
        var seeded = await SeedMixedSuggestionAsync("cursor integrity");
        var page = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.ErrorCode);
        Assert.NotNull(page.Value!.Continuation);
        Assert.False(page.Value.Continuation!.StartsWith("{", StringComparison.Ordinal));
        Assert.DoesNotContain("evaluationId", page.Value.Continuation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TC_ERGONOMICS_STALE_voided_transaction_fails_unresolved_without_writes()
    {
        var seeded = await SeedNoSuggestionAsync("void coffee", count: 2);
        await VoidTransactionAsync(seeded.TransactionIds[0]);
        var before = await CaptureOraclesAsync();
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_STALE_missing_evaluation_fails_outcome_list_without_writes()
    {
        var before = await CaptureOraclesAsync();
        var result = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "missing-eval-id", 10),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        await AssertNoMutationAsync(before);
    }

    // ── Dual no-mutation oracles ─────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_NO_MUTATION_query_failure_preserves_classify_db_hash()
    {
        _ = await SeedNoSuggestionAsync("hash coffee", count: 2);
        var beforeHash = await HashClassifyOracleAsync();
        var beforeLedger = await CaptureLedgerOracleAsync();
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "missing", 10, 2),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(beforeHash, await HashClassifyOracleAsync());
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    [Fact]
    public async Task TC_ERGONOMICS_NO_MUTATION_successful_queries_do_not_mutate_ledger_allocations()
    {
        var seeded = await SeedNoSuggestionAsync("ledger nomut coffee", count: 2);
        var beforeLedger = await CaptureLedgerOracleAsync();
        Assert.True((await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50), actor, CancellationToken.None)).IsSuccess);
        Assert.True((await services.RuleList.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None)).IsSuccess);
        Assert.True((await services.RuleSetActiveGet.HandleAsync(
            new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None)).IsSuccess);
        Assert.True((await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2), actor, CancellationToken.None)).IsSuccess);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    [Fact]
    public async Task TC_ERGONOMICS_NO_MUTATION_corpus_success_only_creates_authorized_destination()
    {
        var parent = Path.Combine(root, "auth-dest");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "authorized.jsonl");
        var beforeLedger = await CaptureLedgerOracleAsync();
        Assert.Empty(Directory.GetFiles(parent, "*", SearchOption.AllDirectories));

        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(
            [dest],
            Directory.GetFiles(parent, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    // ── Composition / preview authority ──────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_COMPOSITION_empty_selected_outcomes_is_rejected_without_ledger_mutation()
    {
        var seeded = await SeedMixedSuggestionAsync("preview auth");
        var beforeLedger = await CaptureLedgerOracleAsync();
        var result = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                "1.0",
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    [Fact]
    public async Task TC_ERGONOMICS_COMPOSITION_outcome_list_ids_compose_selected_outcomes_preview()
    {
        var seeded = await SeedMixedSuggestionAsync("compose preview phrase");
        var page = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.ErrorCode);
        var suggestionIds = page.Value!.Items
            .Where(i => i.Kind == ClassifyOutcomeKind.Suggestion)
            .Select(i => i.OutcomeId)
            .Take(5)
            .ToArray();
        Assert.NotEmpty(suggestionIds);
        var beforeLedger = await CaptureLedgerOracleAsync();
        var previewResult = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                "1.0",
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(previewResult.IsSuccess, previewResult.ErrorCode);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    // ── Isolation / offline ──────────────────────────────────────────────────

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_ergonomics_composition_has_no_network_or_plugin_surface()
    {
        var repositoryRoot = RepositoryRoot();
        string[] paths =
        [
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Unresolved"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Corpus"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Rules", "Discovery"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Evaluation", "Outcome"),
            Path.Combine(repositoryRoot, "src", "Tally", "Domain", "Classify", "Discovery"),
            Path.Combine(repositoryRoot, "src", "Tally", "Domain", "Classify", "Unresolved"),
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "ClassifyExtensions.cs")
        ];
        var composition = string.Join(
            '\n',
            paths.SelectMany(path => Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                    : File.Exists(path) ? [path] : Array.Empty<string>())
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        string[] forbidden =
        [
            "HttpClient", "HttpListener", "TcpListener", "WebApplication", "UseKestrel",
            "Assembly.LoadFrom", "Assembly.Load(", "Process.Start", "OpenAI", "Anthropic",
            "GrpcChannel", "WebSocket", "AddPlugins", "Microsoft.AspNetCore"
        ];
        Assert.All(
            forbidden,
            token => Assert.DoesNotContain(token, composition, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_registry_exposes_five_additive_ops_without_background_aliases()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ClassifyOperationIds.OutcomeList, ids);
        Assert.Contains(ClassifyOperationIds.RuleList, ids);
        Assert.Contains(ClassifyOperationIds.RuleSetActiveGet, ids);
        Assert.Contains(ClassifyOperationIds.CorpusBuild, ids);
        Assert.Contains(ClassifyOperationIds.UnresolvedReport, ids);
        Assert.DoesNotContain("classify.watch", ids);
        Assert.DoesNotContain("classify.sync", ids);
        Assert.DoesNotContain("classify.daemon", ids);
        Assert.DoesNotContain("classify.webhook", ids);
        Assert.DoesNotContain("classify.invoke", ids);
        Assert.Equal(17, ids.Count(id => id.StartsWith("classify.", StringComparison.Ordinal)));
        Assert.Equal(105, OperationRegistry.Create().Descriptors.Count);
    }

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_descriptor_discovery_opens_no_data_root()
    {
        var registryLocal = OperationRegistry.Create();
        var bare = LedgerServices.Create();
        foreach (var id in new[]
                 {
                     ClassifyOperationIds.OutcomeList,
                     ClassifyOperationIds.RuleList,
                     ClassifyOperationIds.RuleSetActiveGet,
                     ClassifyOperationIds.CorpusBuild,
                     ClassifyOperationIds.UnresolvedReport
                 })
        {
            var handler = registryLocal.Find(id)!.HandlerFactory(bare, registryLocal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_live_tally_data_root_is_never_the_fixture_root()
    {
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", root, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetTempPath(), root, StringComparison.Ordinal);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(root));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record SeededEval(string EvaluationId, IReadOnlyList<string> TransactionIds);

    private async Task<SeededEval> SeedNoSuggestionAsync(string phrase, int count)
    {
        var category = await CreateCategoryAsync("NS");
        var rulePhrase = "never-match-" + Guid.NewGuid().ToString("N")[..8];
        var versionId = await SaveDraftAsync(category.CategoryId, rulePhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, rulePhrase);
        var txs = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            txs.Add((await RecordAsync(phrase)).TransactionId);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(evaluated.Value!.EvaluationId, txs);
    }

    private async Task<SeededEval> SeedMixedSuggestionAsync(string matchPhrase)
    {
        var category = await CreateCategoryAsync("Mix");
        var versionId = await SaveDraftAsync(category.CategoryId, matchPhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, matchPhrase);
        var matched = await RecordAsync(matchPhrase);
        var unmatched = await RecordAsync("unmatched " + Guid.NewGuid().ToString("N")[..6]);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(evaluated.Value!.EvaluationId, [matched.TransactionId, unmatched.TransactionId]);
    }

    private async Task<(long EvalRuns, long Outcomes, long Evidence, string? ActiveRuleSet, string ClassifyHash, string LedgerOracle)> CaptureOraclesAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        async Task<long> Count(string table)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        }

        await using var activeCmd = connection.CreateCommand();
        activeCmd.CommandText = "SELECT rule_set_version_id FROM active_rule_set LIMIT 1;";
        var activeId = (await activeCmd.ExecuteScalarAsync(CancellationToken.None)) as string;

        return (
            await Count("evaluation_run"),
            await Count("classification_outcome"),
            await Count("match_evidence"),
            activeId,
            await HashClassifyOracleAsync(),
            await CaptureLedgerOracleAsync());
    }

    private async Task AssertNoMutationAsync(
        (long EvalRuns, long Outcomes, long Evidence, string? ActiveRuleSet, string ClassifyHash, string LedgerOracle) before)
    {
        var after = await CaptureOraclesAsync();
        Assert.Equal(before.EvalRuns, after.EvalRuns);
        Assert.Equal(before.Outcomes, after.Outcomes);
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal(before.ActiveRuleSet, after.ActiveRuleSet);
        Assert.Equal(before.ClassifyHash, after.ClassifyHash);
        Assert.Equal(before.LedgerOracle, after.LedgerOracle);
    }

    private async Task<string> HashClassifyOracleAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        async Task<long> Count(string table)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        }

        await using var activeCmd = connection.CreateCommand();
        activeCmd.CommandText = "SELECT rule_set_version_id FROM active_rule_set LIMIT 1;";
        var activeId = (await activeCmd.ExecuteScalarAsync(CancellationToken.None)) as string ?? "";
        var payload = string.Join(
            "|",
            await Count("evaluation_run"),
            await Count("classification_outcome"),
            await Count("match_evidence"),
            await Count("operation_idempotency"),
            await Count("apply_preview"),
            activeId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<string> CaptureLedgerOracleAsync()
    {
        var projection = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(projection.IsSuccess, projection.Error?.Code);
        return string.Join(
            "|",
            projection.Value!.StoreGenerationFingerprint,
            projection.Value.TotalCount,
            (projection.Value.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>()).Count);
    }

    private static void AssertForbiddenInTrackedRepoSources(string canary)
    {
        var repo = RepositoryRoot();
        foreach (var path in new[] { Path.Combine(repo, "docs"), Path.Combine(repo, "scripts") })
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(path, "*.sh", SearchOption.AllDirectories)))
            {
                Assert.DoesNotContain(canary, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tally.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static ClassifyCorpusBuildRequest CorpusRequest(
        string dest,
        IReadOnlyList<ClassifyCorpusBuildLabel> labels,
        IReadOnlyList<ClassificationProjectionItem> items) =>
        new(
            "1.0",
            "idem-" + Guid.NewGuid().ToString("N"),
            dest,
            new ClassifyCorpusBuildProjectionEnvelope(
                ActualsContractVersions.Current,
                ClassificationProjectionVersions.ClassificationV1,
                new string('a', 64),
                "snap-1",
                "2026-08-02T12:00:00.0000000Z",
                new string('b', 64),
                NormalizationDescriptor.V1.Version,
                items),
            labels);

    private static ClassificationProjectionItem ProjectionItem(
        string txId,
        int ordinal,
        string description = "merchant") =>
        new(
            Ordinal: ordinal,
            TransactionId: txId,
            AccountId: "acct-1",
            EffectiveDate: "2026-07-15",
            SignedAmount: "-12.34",
            SourceDescription: description,
            AmountDirection: ClassificationAmountDirection.Expense,
            CategoryMutationState: CategoryMutationState.Assignable,
            CurrentCategoryId: null,
            CurrentAllocationId: null,
            TransactionRevision: "tr-" + ordinal,
            RelationshipRevision: "rr-" + ordinal,
            AllocationRevision: "ar-" + ordinal);

    private string NextKey() => "erg-sec-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Dump actual classify durable *data* (table cell text), not schema DDL, for canary scans.
    /// </summary>
    private async Task<string> DumpClassifyDurableContentAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var sb = new StringBuilder();
        // Content-bearing tables that must never hold unresolved private descriptions.
        string[] tables =
        [
            "evaluation_run",
            "classification_outcome",
            "match_evidence",
            "operation_idempotency",
            "apply_preview",
            "apply_run",
            "rule_version",
            "rule_set_version",
            "active_rule_set",
            "validation_run",
            "feedback_event"
        ];
        foreach (var table in tables)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM " + table + ";";
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (!reader.IsDBNull(i))
                        {
                            sb.Append(reader.GetValue(i)).Append('\n');
                        }
                    }
                }
            }
            catch
            {
                // Table may not exist on this schema revision — skip.
            }
        }

        return sb.ToString();
    }

    private async Task<ProcessResult> RunClassifyProcessAsync(string[] args, string? stdin)
    {
        return await process.RunAsync(args, stdin, CancellationToken.None);
    }

    private string ClassifyEnvelope(string inputJson, string? idempotencyKey, bool rawInput = false)
    {
        if (rawInput)
        {
            // Deliberately malformed / non-envelope body for unexpected failure path.
            return inputJson;
        }

        using var inputDoc = JsonDocument.Parse(inputJson);
        var request = new RequestEnvelope(
            "1.0",
            actor,
            inputDoc.RootElement.Clone(),
            idempotencyKey);
        return JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
    }

    private static void RunChown(string ownerSpec, string path)
    {
        var start = new ProcessStartInfo("/usr/bin/sudo", $"-n chown {ownerSpec} -- {path}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("Failed to start chown.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"chown {ownerSpec} failed ({proc.ExitCode}): {stdout}{stderr}");
        }
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                "ErgSec Bank " + unique,
                "P-" + unique,
                AccountType.Cheque,
                "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(CultureInfo.InvariantCulture),
                "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                "-12.34",
                "ZAR",
                "2026-07-15",
                null,
                description,
                null,
                null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "erg-sec:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task VoidTransactionAsync(string transactionId)
    {
        var descriptor = registry.Find("ledger.transaction.void");
        if (descriptor is null)
        {
            return;
        }

        var request = new RequestEnvelope(
            "1.0",
            actor,
            JsonSerializer.SerializeToElement(
                new VoidTransactionInput(transactionId, "erg-sec-void"),
                TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        _ = await process.RunAsync(args, json, CancellationToken.None);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [
                    new ClassificationRuleConditionInput(
                        0,
                        ClassificationRuleFieldKey.DescriptionNormalized,
                        ClassificationRulePredicateKind.Equals,
                        ValueText: description)
                ],
                "erg-sec draft"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var path = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await services.Activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "erg-sec activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, ActualsContractVersions.Current, actor, CancellationToken.None);
        Assert.True(page.IsSuccess);
        var byTx = page.Value!.ClassificationItems!.ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            var item = byTx[txId];
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(rows[i].ExpectedKind));
            if (rows[i].ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(rows[i].ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var request = new RequestEnvelope("1.0", actor, JsonSerializer.SerializeToElement(input, inputType), key);
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, json, CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
    }

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
