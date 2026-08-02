using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Corpus.Build;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Xunit;

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER / bd-1cik —
/// Binding, publication, aggregate receipt, replay, privacy, and no-mutation cases
/// over synthetic owner-only disposable roots. Never touches live Tally data.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateCorpusBuilderTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-corpus-build-" + Guid.NewGuid().ToString("N"));
    private readonly string classifyRoot = null!;
    private readonly SafeActor actor = new("automation", "corpus-build", "run-01");
    private ClassifyStateStore stateStore = null!;
    private BuildPrivateClassificationCorpusCommand command = null!;
    private PrivateCorpusReader reader = null!;
    private int keySeq;

    private static readonly ClassificationCategoryIdentity ActiveCat =
        new("cat-active", "Groceries", "active");

    private static readonly ClassificationCategoryIdentity ArchivedCat =
        new("cat-archived", "Old", "archived");

    public PrivateCorpusBuilderTests()
    {
        classifyRoot = Path.Combine(root, "classify-data");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(classifyRoot);
        File.SetUnixFileMode(classifyRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        stateStore = new ClassifyStateStore(classifyRoot);
        await stateStore.InitializeAsync(CancellationToken.None);
        reader = new PrivateCorpusReader();
        command = new BuildPrivateClassificationCorpusCommand(
            stateStore,
            new ClassifyOperationIdempotencyStore(),
            new PrivateCorpusWriter(reader),
            reader);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Success / aggregate receipt ──────────────────────────────────────────

    [Fact]
    public async Task Single_suggestion_label_publishes_validator_compatible_corpus()
    {
        var dest = Dest("ok.jsonl");
        var item = Projection("tx-1", 0);
        var result = await BuildAsync(dest, [LabelSuggestion("tx-1")], [item]);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.Replayed);
        Assert.Equal(1, result.Value.LabelCount);
        Assert.Equal(1, result.Value.WrittenRowCount);
        Assert.True(result.Value.WrittenByteCount > 0);
        Assert.Equal(64, result.Value.CorpusFingerprint.Length);
        Assert.Equal(ClassifyCorpusBuildTerminalState.Completed, result.Value.TerminalState);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.BuildId));

        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        Assert.Equal(result.Value.CorpusFingerprint, read.Fingerprint!.Sha256Hex);
        Assert.Equal("suggestion", read.Rows![0].ExpectedOutcomeKind);
        Assert.Equal("cat-active", read.Rows[0].ExpectedCategoryId);
    }

    [Fact]
    public async Task Multiple_labels_order_by_ordinal_then_transaction_id()
    {
        var dest = Dest("order.jsonl");
        // Ordinals must be unique in the private corpus dialect (reader rejects duplicates).
        var items = new[] { Projection("tx-b", 5), Projection("tx-a", 2), Projection("tx-c", 3) };
        var labels = new[]
        {
            LabelNoSuggestion("tx-b"),
            LabelNoSuggestion("tx-c"),
            LabelNoSuggestion("tx-a")
        };
        var result = await BuildAsync(dest, labels, items);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.True(read.IsSuccess, read.ErrorCode);
        Assert.NotNull(read.Rows);
        Assert.Equal(["tx-a", "tx-c", "tx-b"], read.Rows.Select(r => r.TransactionId).ToArray());
        Assert.Equal([2, 3, 5], read.Rows.Select(r => r.Ordinal).ToArray());
    }

    [Fact]
    public async Task Aggregate_receipt_excludes_path_and_financial_payload()
    {
        var dest = Dest("privacy.jsonl");
        const string canary = "CANARY_PRIVATE_DESC_xyz";
        var item = Projection("tx-1", 0, description: canary);
        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [item]);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.DoesNotContain(dest, json, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tx-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("-12.34", json, StringComparison.Ordinal);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_does_not_mutate_classify_financial_tables()
    {
        var dest = Dest("nomut.jsonl");
        var before = await CountIdempotencyAsync();
        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]);
        Assert.True(result.IsSuccess, result.ErrorCode);
        // Only operation_idempotency gains a row — no evaluation/apply tables.
        Assert.Equal(before + 1, await CountIdempotencyAsync());
        Assert.Equal(0, await CountTableAsync("evaluation_run"));
        Assert.Equal(0, await CountTableAsync("apply_preview"));
    }

    [Fact]
    public async Task Destination_file_is_owner_only_0600_regular_file()
    {
        var dest = Dest("mode.jsonl");
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)])).IsSuccess);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dest));
        Assert.True(File.Exists(dest));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public async Task Parent_directory_must_remain_0700()
    {
        var dest = Dest("parent.jsonl");
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(root));
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)])).IsSuccess);
    }

    // ── Idempotency / replay ─────────────────────────────────────────────────

    [Fact]
    public async Task Exact_replay_returns_prior_receipt_without_rewrite()
    {
        var dest = Dest("replay.jsonl");
        var key = NextKey();
        var first = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)], key);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var mtime1 = File.GetLastWriteTimeUtc(dest);
        await Task.Delay(20);
        var second = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)], key);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.True(second.Value!.Replayed);
        Assert.Equal(first.Value!.BuildId, second.Value.BuildId);
        Assert.Equal(first.Value.CorpusFingerprint, second.Value.CorpusFingerprint);
        Assert.Equal(mtime1, File.GetLastWriteTimeUtc(dest));
    }

    [Fact]
    public async Task Idempotency_conflict_on_different_labels_same_key()
    {
        var dest = Dest("conflict.jsonl");
        var key = NextKey();
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)], key)).IsSuccess);
        var other = await BuildAsync(
            Dest("conflict-other.jsonl"),
            [LabelNoSuggestion("tx-2")],
            [Projection("tx-2", 0)],
            key);
        Assert.Equal(ClassifyErrors.IdempotencyConflict, other.ErrorCode);
    }

    [Fact]
    public async Task Recovery_after_rename_before_commit_accepts_exact_fingerprint()
    {
        // Simulate post-rename pre-commit: publish bytes first, then invoke command with same request.
        var dest = Dest("recover.jsonl");
        var item = Projection("tx-1", 0);
        var rowsOk = ClassificationProjectionCorpusMapper.TryMapLabelsToPrivateRows(
            [new ClassificationProjectionCorpusMapper.ExactLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
            [item],
            [ActiveCat],
            out var rows,
            out _);
        Assert.True(rowsOk);
        var published = await new PrivateCorpusWriter(reader).PublishAsync(dest, rows, CancellationToken.None);
        Assert.True(published.IsSuccess, published.ErrorCode);

        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [item]);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(published.Fingerprint!.Sha256Hex, result.Value!.CorpusFingerprint);
        Assert.False(result.Value.Replayed);
    }

    [Fact]
    public async Task Existing_destination_with_different_content_is_never_replaced()
    {
        var dest = Dest("existing.jsonl");
        await File.WriteAllTextAsync(dest, "{\"ordinal\":0,\"transactionId\":\"other\"}\n");
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var before = await File.ReadAllTextAsync(dest);
        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.DestinationExists, result.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(dest));
    }

    // ── Validation failures (no destination) ─────────────────────────────────

    [Fact]
    public async Task Missing_actor_fails()
    {
        var result = await command.HandleAsync(
            Request(Dest("a.jsonl"), [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]),
            actor: null,
            CancellationToken.None,
            [ActiveCat]);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
        Assert.False(File.Exists(Dest("a.jsonl")));
    }

    [Fact]
    public async Task Missing_idempotency_key_fails()
    {
        var req = Request(Dest("b.jsonl"), [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]) with
        {
            IdempotencyKey = " "
        };
        var result = await command.HandleAsync(req, actor, CancellationToken.None, [ActiveCat]);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Relative_output_path_fails_privacy()
    {
        var req = Request("relative.jsonl", [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]);
        var result = await command.HandleAsync(req, actor, CancellationToken.None, [ActiveCat]);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
    }

    [Fact]
    public async Task Empty_labels_fail_resource_limit()
    {
        var dest = Dest("empty.jsonl");
        var result = await BuildAsync(dest, Array.Empty<ClassifyCorpusBuildLabel>(), [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Duplicate_labels_fail_without_destination()
    {
        var dest = Dest("dup.jsonl");
        var result = await BuildAsync(
            dest,
            [LabelNoSuggestion("tx-1"), LabelNoSuggestion("tx-1")],
            [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.LabelInvalid, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Missing_projection_member_fails_stale()
    {
        var dest = Dest("missing.jsonl");
        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-ghost")], [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Ineligible_projection_fails_stale()
    {
        var dest = Dest("inelig.jsonl");
        var result = await BuildAsync(
            dest,
            [LabelNoSuggestion("tx-1")],
            [Projection("tx-1", 0, CategoryMutationState.Ineligible)]);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Suggestion_without_category_fails_label_invalid()
    {
        var dest = Dest("sug.jsonl");
        var result = await BuildAsync(
            dest,
            [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.Suggestion, null)],
            [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.LabelInvalid, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Suggestion_with_archived_category_fails_stale()
    {
        var dest = Dest("arch.jsonl");
        var result = await command.HandleAsync(
            Request(dest, [LabelSuggestion("tx-1", "cat-archived")], [Projection("tx-1", 0)]),
            actor,
            CancellationToken.None,
            [ArchivedCat, ActiveCat]);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task No_suggestion_with_category_fails_label_invalid()
    {
        var dest = Dest("ns.jsonl");
        var result = await BuildAsync(
            dest,
            [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion, "cat-active")],
            [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.LabelInvalid, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Wrong_projection_version_fails_ledger_incompatible()
    {
        var dest = Dest("proj.jsonl");
        var baseReq = Request(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]);
        var req = baseReq with
        {
            Projection = baseReq.Projection with { ProjectionVersion = "other_v1" }
        };
        var result = await command.HandleAsync(req, actor, CancellationToken.None, [ActiveCat]);
        Assert.Equal(ClassifyErrors.LedgerIncompatible, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Unsupported_contract_version_fails()
    {
        var dest = Dest("ver.jsonl");
        var req = Request(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]) with
        {
            ContractVersion = "9.9"
        };
        var result = await command.HandleAsync(req, actor, CancellationToken.None, [ActiveCat]);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Parent_with_group_permissions_fails_privacy()
    {
        var badParent = Path.Combine(root, "group-parent");
        Directory.CreateDirectory(badParent);
        File.SetUnixFileMode(
            badParent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var dest = Path.Combine(badParent, "out.jsonl");
        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Conflict_and_stale_outcome_labels_publish()
    {
        var dest = Dest("kinds.jsonl");
        var items = new[] { Projection("tx-c", 0), Projection("tx-s", 1) };
        var labels = new[]
        {
            new ClassifyCorpusBuildLabel("tx-c", ClassifyOutcomeKind.Conflict),
            new ClassifyCorpusBuildLabel("tx-s", ClassifyOutcomeKind.Stale)
        };
        var result = await BuildAsync(dest, labels, items);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(2, result.Value!.WrittenRowCount);
    }

    [Fact]
    public async Task Correctable_projection_items_are_eligible()
    {
        var dest = Dest("corr.jsonl");
        var item = Projection("tx-1", 0, CategoryMutationState.Correctable, currentCategory: "cat-x", currentAlloc: "a1");
        var result = await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [item]);
        Assert.True(result.IsSuccess, result.ErrorCode);
    }

    [Fact]
    public async Task Income_direction_maps_through_to_corpus_row()
    {
        var dest = Dest("income.jsonl");
        var item = Projection("tx-1", 0, amountDirection: ClassificationAmountDirection.Income, signed: "15.00");
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [item])).IsSuccess);
        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.Equal("inflow", read.Rows![0].AmountDirection);
        Assert.Equal(1500, read.Rows[0].AmountAbsoluteMinor);
    }

    [Fact]
    public async Task Request_fingerprint_excludes_output_path()
    {
        var a = ClassifyContractMapper.ToCorpusBuildFingerprintElement(
            Request("/tmp/a.jsonl", [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]));
        var b = ClassifyContractMapper.ToCorpusBuildFingerprintElement(
            Request("/tmp/b.jsonl", [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]));
        var fa = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyContractMapper.CorpusBuildOperationId,
            ClassifyOperationIds.ContractVersion,
            actor.Kind,
            actor.Label,
            actor.RunId,
            a);
        var fb = ClassifyOperationIdempotencyStore.ComputeRequestFingerprint(
            ClassifyContractMapper.CorpusBuildOperationId,
            ClassifyOperationIds.ContractVersion,
            actor.Kind,
            actor.Label,
            actor.RunId,
            b);
        Assert.Equal(fa, fb);
        Assert.DoesNotContain("/tmp/", a.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_fingerprint_is_stable_and_path_free()
    {
        var env = Request(Dest("x.jsonl"), [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)]).Projection;
        var a = ClassifyContractMapper.ComputeCorpusProjectionFingerprint(env);
        var b = ClassifyContractMapper.ComputeCorpusProjectionFingerprint(env);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Terminal_result_round_trips_without_path()
    {
        var sample = ClassifyContractMapper.ToCorpusBuildResult(
            "build-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "normalization_v1",
            1,
            1,
            100,
            new string('e', 64),
            false);
        var json = ClassifyContractMapper.SerializeCorpusBuildResult(sample);
        var back = ClassifyContractMapper.TryDeserializeCorpusBuildResult(json);
        Assert.NotNull(back);
        Assert.Equal(sample.BuildId, back!.BuildId);
        Assert.DoesNotContain("outputPath", json, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Over_10000_labels_fail_resource_limit_at_boundary()
    {
        // Contract TryValidate rejects > 10000 before mapping.
        var labels = Enumerable.Range(0, 10_001)
            .Select(i => LabelNoSuggestion("tx-" + i.ToString("D5")))
            .ToArray();
        var items = labels.Select((l, i) => Projection(l.TransactionId, i)).ToArray();
        var dest = Dest("bound.jsonl");
        var result = await BuildAsync(dest, labels, items);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task One_label_is_accepted_minimum()
    {
        var dest = Dest("min.jsonl");
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)])).IsSuccess);
    }

    [Fact]
    public async Task No_recognized_temp_left_after_success()
    {
        var dest = Dest("clean.jsonl");
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [Projection("tx-1", 0)])).IsSuccess);
        var temps = Directory.GetFiles(root, PrivateCorpusWriter.RecognizedTempPrefix + "*");
        Assert.Empty(temps);
    }

    [Fact]
    public async Task Failure_leaves_no_destination_or_unknown_files()
    {
        var dest = Dest("failclean.jsonl");
        _ = await BuildAsync(dest, [LabelNoSuggestion("tx-missing")], [Projection("tx-1", 0)]);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(root, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
    }

    [Fact]
    public async Task Map_corpus_publish_error_maps_privacy_and_destination()
    {
        Assert.Equal(
            ClassifyErrors.PrivacyRejected,
            ClassifyContractMapper.MapCorpusPublishError(PrivateCorpusErrors.SymlinkRejected));
        Assert.Equal(
            ClassifyErrors.DestinationExists,
            ClassifyContractMapper.MapCorpusPublishError(ClassifyErrors.DestinationExists));
        Assert.Equal(
            ClassifyErrors.ResourceLimit,
            ClassifyContractMapper.MapCorpusPublishError(PrivateCorpusErrors.LimitExceeded));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Suggestion_label_requires_active_catalogue_membership()
    {
        var dest = Dest("cat.jsonl");
        var result = await command.HandleAsync(
            Request(dest, [LabelSuggestion("tx-1")], [Projection("tx-1", 0)]),
            actor,
            CancellationToken.None,
            activeCategories: null);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Written_row_reuses_public_lifecycle_fingerprint()
    {
        var dest = Dest("life.jsonl");
        var item = Projection("tx-1", 0);
        Assert.True((await BuildAsync(dest, [LabelNoSuggestion("tx-1")], [item])).IsSuccess);
        var read = await reader.ReadAsync(dest, CancellationToken.None);
        Assert.Equal(
            ClassificationProjectionCorpusMapper.ComputeItemLifecycleFingerprint(item),
            read.Rows![0].ItemLifecycleFingerprint);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Tally.Application.CommandResult<ClassifyCorpusBuildResult>> BuildAsync(
        string dest,
        IReadOnlyList<ClassifyCorpusBuildLabel> labels,
        IReadOnlyList<ClassificationProjectionItem> items,
        string? key = null) =>
        await command.HandleAsync(
            Request(dest, labels, items, key),
            actor,
            CancellationToken.None,
            [ActiveCat]);

    private ClassifyCorpusBuildRequest Request(
        string dest,
        IReadOnlyList<ClassifyCorpusBuildLabel> labels,
        IReadOnlyList<ClassificationProjectionItem> items,
        string? key = null) =>
        new(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            key ?? NextKey(),
            dest,
            new ClassifyCorpusBuildProjectionEnvelope(
                ActualsContractVersions.Current,
                ClassificationProjectionVersions.ClassificationV1,
                new string('a', 64),
                "snap-1",
                "2099-01-01T00:00:00.0000000Z",
                new string('b', 64),
                "normalization_v1",
                items.ToArray()),
            labels.ToArray());

    private string Dest(string name) => Path.Combine(root, name);

    private string NextKey() => "corpus-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

    private static ClassifyCorpusBuildLabel LabelNoSuggestion(string tx) =>
        new(tx, ClassifyOutcomeKind.NoSuggestion);

    private static ClassifyCorpusBuildLabel LabelSuggestion(string tx, string category = "cat-active") =>
        new(tx, ClassifyOutcomeKind.Suggestion, category);

    private static ClassificationProjectionItem Projection(
        string transactionId,
        int ordinal,
        CategoryMutationState mutation = CategoryMutationState.Assignable,
        ClassificationAmountDirection amountDirection = ClassificationAmountDirection.Expense,
        string signed = "-12.34",
        string description = "COFFEE SHOP",
        string? currentCategory = null,
        string? currentAlloc = null) =>
        new(
            Ordinal: ordinal,
            TransactionId: transactionId,
            AccountId: "acct-1",
            EffectiveDate: "2026-07-15",
            SignedAmount: signed,
            SourceDescription: description,
            AmountDirection: amountDirection,
            CategoryMutationState: mutation,
            CurrentCategoryId: currentCategory,
            CurrentAllocationId: currentAlloc,
            TransactionRevision: "tr-" + transactionId,
            RelationshipRevision: "rr-" + transactionId,
            AllocationRevision: "ar-" + transactionId);

    private async Task<long> CountIdempotencyAsync() => await CountTableAsync("operation_idempotency");

    private async Task<long> CountTableAsync(string table)
    {
        await using var connection = await stateStore.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        var scalar = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }
}
