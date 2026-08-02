using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Ledger.Actuals;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify.Contract;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-CONTRACT-FOUNDATION / bd-1gly (+ bd-rly1 phase transition) —
/// Additive request/result/enum/error shapes under source-generated JSON with
/// pure contract validation, closed enums, FR/DM field reconciliation,
/// frozen 0.3.3 compatibility fingerprints, and published 105/17 inventory proofs.
/// </summary>
public sealed class ClassifyOperatorErgonomicsContractTests
{
    // ── Source generation / round-trip ───────────────────────────────────────

    [Fact]
    public void Outcome_list_request_round_trips_under_source_generation()
    {
        var request = new ClassifyOutcomeListRequest(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            "eval-1",
            PageSize: 50,
            OutcomeKind: ClassifyOutcomeKind.Suggestion,
            SuggestedCategoryId: "cat-1",
            StaleState: ClassifyOutcomeStaleFilter.Fresh,
            Continuation: "opaque-token");
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
        Assert.NotNull(roundTrip);
        Assert.Equal(ClassifyOperatorErgonomicsContracts.ContractVersion, roundTrip!.ContractVersion);
        Assert.Equal(50, roundTrip.PageSize);
        Assert.Equal(ClassifyOutcomeStaleFilter.Fresh, roundTrip.StaleState);
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(roundTrip, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Rule_list_and_active_get_round_trip_closed_enums()
    {
        var list = new ClassifyRuleListRequest(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            PageSize: 100,
            Lifecycle: ClassifyRuleLifecycleFilter.Active,
            ActiveMembership: true);
        var listJson = JsonSerializer.Serialize(list, ClassifyJsonContext.Default.ClassifyRuleListRequest);
        Assert.Contains("\"active\"", listJson, StringComparison.Ordinal);
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(list, out _));

        var active = new ClassifyRuleSetActiveGetRequest(ClassifyOperatorErgonomicsContracts.ContractVersion);
        var activeJson = JsonSerializer.Serialize(active, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetRequest);
        Assert.False(JsonDocument.Parse(activeJson).RootElement.TryGetProperty("authorityGranted", out _));
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(active, out _));
    }

    [Fact]
    public void Corpus_build_uses_released_classification_projection_item_shape()
    {
        var request = SampleCorpusBuildRequest();
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyCorpusBuildRequest);
        Assert.Contains("\"amountDirection\":\"expense\"", json, StringComparison.Ordinal);
        Assert.Contains("\"signedAmount\"", json, StringComparison.Ordinal);
        Assert.Contains("\"transactionRevision\"", json, StringComparison.Ordinal);
        Assert.Contains("\"categoryMutationState\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("amountAbsoluteMinor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("itemLifecycleFingerprint", json, StringComparison.OrdinalIgnoreCase);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyCorpusBuildRequest);
        Assert.NotNull(roundTrip);
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(roundTrip, out var error));
        Assert.Null(error);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, roundTrip!.Projection.ProjectionVersion);
        Assert.Equal(ClassificationAmountDirection.Expense, roundTrip.Projection.Items[0].AmountDirection);
    }

    [Fact]
    public void Unresolved_report_uses_ledger_amount_direction_vocabulary()
    {
        var request = new ClassifyUnresolvedReportRequest(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            "eval-1",
            TopN: 25,
            MinimumCount: 2,
            AccountId: "acct-1",
            AmountDirection: ClassificationAmountDirection.Expense);
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest);
        Assert.Contains("\"expense\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("outflow", json, StringComparison.Ordinal);
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(request, out _));
    }

    [Fact]
    public void Result_round_trips_closed_lifecycle_and_terminal_states()
    {
        var active = SampleActiveRuleSetResult();
        var activeJson = JsonSerializer.Serialize(active, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        Assert.Contains("\"lifecycleStatus\":\"active\"", activeJson, StringComparison.Ordinal);
        var activeRt = JsonSerializer.Deserialize(activeJson, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        Assert.Equal(ClassifyActiveRuleSetLifecycleStatus.Active, activeRt!.LifecycleStatus);

        var corpus = SampleCorpusBuildResult();
        var corpusJson = JsonSerializer.Serialize(corpus, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.Contains("\"terminalState\":\"completed\"", corpusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("outputPath", corpusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/owner/", corpusJson, StringComparison.Ordinal);
        var corpusRt = JsonSerializer.Deserialize(corpusJson, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.Equal(ClassifyCorpusBuildTerminalState.Completed, corpusRt!.TerminalState);
    }

    [Fact]
    public void Unresolved_report_result_exposes_full_fr_accounting_identity()
    {
        var result = SampleUnresolvedReportResult();
        // FR identity: noSuggestion == joined == candidate + belowMinimum
        Assert.Equal(result.NoSuggestionOutcomeCount, result.JoinedRowCount);
        Assert.Equal(
            result.NoSuggestionOutcomeCount,
            result.CandidateRowCount + result.BelowMinimumRowCount);
        Assert.Equal(result.ReturnedGroupCount, result.Groups.Count);
        Assert.Equal(
            result.DistinctGroupCount,
            result.ReturnedGroupCount + result.OmittedGroupCount);
        Assert.Equal(25, result.BoundedRequestTopN);
        Assert.Equal(2, result.BoundedRequestMinimumCount);

        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.Contains("noSuggestionOutcomeCount", json, StringComparison.Ordinal);
        Assert.Contains("joinedRowCount", json, StringComparison.Ordinal);
        Assert.Contains("candidateRowCount", json, StringComparison.Ordinal);
        Assert.Contains("belowMinimumRowCount", json, StringComparison.Ordinal);
        Assert.Contains("distinctGroupCount", json, StringComparison.Ordinal);
        Assert.Contains("returnedGroupCount", json, StringComparison.Ordinal);
        Assert.Contains("omittedGroupCount", json, StringComparison.Ordinal);
        Assert.Contains("boundedRequestTopN", json, StringComparison.Ordinal);
        Assert.Contains("boundedRequestMinimumCount", json, StringComparison.Ordinal);
        Assert.DoesNotContain("transactionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("continuation", json, StringComparison.OrdinalIgnoreCase);

        var names = PropertyNames(ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.Contains("noSuggestionOutcomeCount", names);
        Assert.Contains("joinedRowCount", names);
        Assert.Contains("candidateRowCount", names);
        Assert.Contains("belowMinimumRowCount", names);
        Assert.Contains("distinctGroupCount", names);
        Assert.Contains("returnedGroupCount", names);
        Assert.Contains("omittedGroupCount", names);
        Assert.Contains("boundedRequestTopN", names);
        Assert.Contains("boundedRequestMinimumCount", names);
        Assert.DoesNotContain("eligibleNoSuggestionCount", names);
        Assert.DoesNotContain("matchedFreshRowCount", names);
    }

    // ── Contract validation: version + exact bounds ──────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2.0")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    public void Requests_reject_unsupported_contract_versions(string? version)
    {
        Assert.False(ClassifyOperatorErgonomicsContracts.IsSupportedContractVersion(version));

        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyOutcomeListRequest(version!, "eval", 10), out var e1));
        Assert.Equal(ClassifyErrors.UnsupportedVersion, e1);

        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyRuleListRequest(version!, 10), out var e2));
        Assert.Equal(ClassifyErrors.UnsupportedVersion, e2);

        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyRuleSetActiveGetRequest(version!), out var e3));
        Assert.Equal(ClassifyErrors.UnsupportedVersion, e3);

        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            SampleCorpusBuildRequest() with { ContractVersion = version! }, out var e4));
        Assert.Equal(ClassifyErrors.UnsupportedVersion, e4);

        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyUnresolvedReportRequest(version!, "eval", 10, 2), out var e5));
        Assert.Equal(ClassifyErrors.UnsupportedVersion, e5);
    }

    [Fact]
    public void Supported_contract_version_is_exactly_one_dot_zero()
    {
        Assert.True(ClassifyOperatorErgonomicsContracts.IsSupportedContractVersion("1.0"));
        Assert.Equal("1.0", ClassifyOperatorErgonomicsContracts.ContractVersion);
        Assert.Equal(ClassifyOperationIds.ContractVersion, ClassifyOperatorErgonomicsContracts.ContractVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    [InlineData(1000)]
    public void Page_size_rejects_one_under_and_one_over_bounds(int pageSize)
    {
        Assert.False(ClassifyOperatorErgonomicsContracts.IsValidPageSize(pageSize));
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyOutcomeListRequest("1.0", "eval", pageSize), out var e1));
        Assert.Equal(ClassifyErrors.ResourceLimit, e1);
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyRuleListRequest("1.0", pageSize), out var e2));
        Assert.Equal(ClassifyErrors.ResourceLimit, e2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void Page_size_accepts_exact_lower_and_upper_bounds(int pageSize)
    {
        Assert.True(ClassifyOperatorErgonomicsContracts.IsValidPageSize(pageSize));
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyOutcomeListRequest("1.0", "eval", pageSize), out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Unresolved_top_n_rejects_one_under_and_one_over(int topN)
    {
        Assert.False(ClassifyOperatorErgonomicsContracts.IsValidTopN(topN));
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyUnresolvedReportRequest("1.0", "eval", topN, 2), out var error));
        Assert.Equal(ClassifyErrors.ResourceLimit, error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void Unresolved_top_n_accepts_exact_bounds(int topN)
    {
        Assert.True(ClassifyOperatorErgonomicsContracts.IsValidTopN(topN));
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyUnresolvedReportRequest("1.0", "eval", topN, 2), out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(501)]
    [InlineData(0)]
    public void Unresolved_minimum_count_rejects_one_under_and_one_over(int minimumCount)
    {
        Assert.False(ClassifyOperatorErgonomicsContracts.IsValidMinimumCount(minimumCount));
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyUnresolvedReportRequest("1.0", "eval", 10, minimumCount), out var error));
        Assert.Equal(ClassifyErrors.ResourceLimit, error);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(500)]
    public void Unresolved_minimum_count_accepts_exact_bounds(int minimumCount)
    {
        Assert.True(ClassifyOperatorErgonomicsContracts.IsValidMinimumCount(minimumCount));
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(
            new ClassifyUnresolvedReportRequest("1.0", "eval", 10, minimumCount), out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Corpus_label_count_rejects_one_under_and_one_over(int labelCount)
    {
        Assert.False(ClassifyOperatorErgonomicsContracts.IsValidLabelCount(labelCount));
        var labels = Enumerable.Range(0, labelCount)
            .Select(i => new ClassifyCorpusBuildLabel("tx-" + i, ClassifyOutcomeKind.NoSuggestion))
            .ToArray();
        var request = SampleCorpusBuildRequest() with { Labels = labels };
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(request, out var error));
        Assert.Equal(ClassifyErrors.ResourceLimit, error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    public void Corpus_label_count_accepts_exact_bounds(int labelCount)
    {
        Assert.True(ClassifyOperatorErgonomicsContracts.IsValidLabelCount(labelCount));
        var labels = Enumerable.Range(0, labelCount)
            .Select(i => new ClassifyCorpusBuildLabel("tx-" + i, ClassifyOutcomeKind.NoSuggestion))
            .ToArray();
        // Projection items need not match labels for boundary validation of counts alone;
        // label uniqueness + count are enforced here.
        var request = SampleCorpusBuildRequest() with { Labels = labels };
        Assert.True(ClassifyOperatorErgonomicsContracts.TryValidate(request, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Published_bounds_match_data_models_and_fr_not_test_locals_only()
    {
        Assert.Equal(1, ClassifyOperatorErgonomicsContracts.MinPageSize);
        Assert.Equal(500, ClassifyOperatorErgonomicsContracts.MaxPageSize);
        Assert.Equal(1, ClassifyOperatorErgonomicsContracts.MinTopN);
        Assert.Equal(500, ClassifyOperatorErgonomicsContracts.MaxTopN);
        Assert.Equal(2, ClassifyOperatorErgonomicsContracts.MinMinimumCount);
        Assert.Equal(500, ClassifyOperatorErgonomicsContracts.MaxMinimumCount);
        Assert.Equal(1, ClassifyOperatorErgonomicsContracts.MinLabelCount);
        Assert.Equal(10_000, ClassifyOperatorErgonomicsContracts.MaxLabelCount);
        Assert.Equal(200, ClassifyOperatorErgonomicsContracts.SelectedOutcomesMax);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, "classification_v1");
    }

    [Fact]
    public void Corpus_rejects_non_classification_v1_projection_version()
    {
        var request = SampleCorpusBuildRequest() with
        {
            Projection = SampleCorpusBuildRequest().Projection with { ProjectionVersion = "other_v1" }
        };
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(request, out var error));
        Assert.Equal(ClassifyErrors.LedgerIncompatible, error);
    }

    [Fact]
    public void Corpus_rejects_duplicate_or_incomplete_suggestion_labels()
    {
        var baseRequest = SampleCorpusBuildRequest();
        var dup = baseRequest with
        {
            Labels =
            [
                new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-1"),
                new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)
            ]
        };
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(dup, out var e1));
        Assert.Equal(ClassifyErrors.LabelInvalid, e1);

        var missingCat = baseRequest with
        {
            Labels = [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.Suggestion, null)]
        };
        Assert.False(ClassifyOperatorErgonomicsContracts.TryValidate(missingCat, out var e2));
        Assert.Equal(ClassifyErrors.LabelInvalid, e2);
    }

    // ── Unknown fields / source generation ───────────────────────────────────

    [Theory]
    [MemberData(nameof(AdditiveRequestTypeInfos))]
    public void Additive_requests_reject_unknown_fields(JsonTypeInfo typeInfo)
    {
        const string json = """{"contractVersion":"1.0","extra":"nope","pageSize":10,"evaluationId":"e","topN":1,"minimumCount":2,"idempotencyKey":"k","outputPath":"/x","projection":{"ledgerContractVersion":"1.0","projectionVersion":"classification_v1","storeGenerationFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","snapshotId":"s","snapshotExpiresAt":"t","catalogueFingerprint":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","normalizationVersion":"normalization_v1","items":[]},"labels":[]}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, typeInfo));
    }

    [Fact]
    public void Closed_enums_serialize_as_canonical_snake_names()
    {
        Assert.Equal(
            "\"fresh\"",
            JsonSerializer.Serialize(ClassifyOutcomeStaleFilter.Fresh, ClassifyJsonContext.Default.ClassifyOutcomeStaleFilter));
        Assert.Equal(
            "\"owner_authored\"",
            JsonSerializer.Serialize(ClassifyRuleProvenanceKind.OwnerAuthored, ClassifyJsonContext.Default.ClassifyRuleProvenanceKind));
        Assert.Equal(
            "\"archived\"",
            JsonSerializer.Serialize(ClassifyCategoryLifecycleState.Archived, ClassifyJsonContext.Default.ClassifyCategoryLifecycleState));
        Assert.Equal(
            "\"completed\"",
            JsonSerializer.Serialize(ClassifyCorpusBuildTerminalState.Completed, ClassifyJsonContext.Default.ClassifyCorpusBuildTerminalState));
        Assert.Equal(
            "\"active\"",
            JsonSerializer.Serialize(ClassifyActiveRuleSetLifecycleStatus.Active, ClassifyJsonContext.Default.ClassifyActiveRuleSetLifecycleStatus));
        Assert.Equal(
            "\"expense\"",
            JsonSerializer.Serialize(ClassificationAmountDirection.Expense, ClassifyJsonContext.Default.ClassificationAmountDirection));
    }

    [Fact]
    public void Json_context_exposes_type_info_for_every_additive_payload()
    {
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleListRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleListResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleSetActiveGetRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildProjectionEnvelope);
        Assert.NotNull(ClassifyJsonContext.Default.ClassificationProjectionItem);
        Assert.NotNull(ClassifyJsonContext.Default.ClassificationAmountDirection);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyUnresolvedPatternGroup);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyActiveRuleSetLifecycleStatus);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildTerminalState);
    }

    [Fact]
    public void Outcome_list_and_rule_list_expose_required_page_fields()
    {
        var outcome = PropertyNames(ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.Contains("overallCount", outcome);
        Assert.Contains("filteredCount", outcome);
        Assert.Contains("returnedCount", outcome);
        Assert.Contains("continuation", outcome);
        Assert.Contains("evaluationFingerprint", outcome);
        Assert.Contains("ledgerGeneration", outcome);

        var item = PropertyNames(ClassifyJsonContext.Default.ClassifyOutcomeListItem);
        Assert.Contains("outcomeId", item);
        Assert.Contains("safeReason", item);
        Assert.DoesNotContain("sourceDescription", item);
        Assert.DoesNotContain("amountAbsoluteMinor", item);

        var corpus = PropertyNames(ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.Contains("catalogueFingerprint", corpus);
        Assert.Contains("terminalState", corpus);
        Assert.DoesNotContain("outputPath", corpus);
        Assert.DoesNotContain("labels", corpus);
        Assert.DoesNotContain("categoryLifecycleFingerprint", corpus);
    }

    [Fact]
    public void Additive_error_codes_remain_stable_and_metadata_only()
    {
        Assert.Equal("CLASSIFY-CURSOR-INVALID", ClassifyErrors.CursorInvalid);
        Assert.Equal("CLASSIFY-CURSOR-STALE", ClassifyErrors.CursorStale);
        Assert.Equal("CLASSIFY-ACTIVE-RULE-SET-NOT-FOUND", ClassifyErrors.ActiveRuleSetNotFound);
        Assert.Equal("CLASSIFY-PRIVACY-REJECTED", ClassifyErrors.PrivacyRejected);
        Assert.Equal("CLASSIFY-DESTINATION-EXISTS", ClassifyErrors.DestinationExists);
        Assert.Equal("CLASSIFY-LABEL-INVALID", ClassifyErrors.LabelInvalid);
        Assert.Equal("CLASSIFY-INPUT-INVALID", ClassifyErrors.InvalidInput);
        foreach (var code in new[]
                 {
                     ClassifyErrors.CursorInvalid, ClassifyErrors.PrivacyRejected,
                     ClassifyErrors.LabelInvalid, ClassifyErrors.ResourceLimit
                 })
        {
            Assert.DoesNotContain('/', code);
            Assert.DoesNotContain("description", code, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Frozen 0.3.3 released fingerprints / published-phase inventory ───────

    [Fact]
    public void Published_inventory_is_one_hundred_five_global_and_seventeen_classify()
    {
        // Phase transition (bd-rly1): five additive ergonomics IDs are published.
        // Released C12 IDs remain the stable prefix; total inventory is 105 / 17.
        Assert.Equal(17, ClassifyOperationIds.All.Count);
        Assert.Equal(12, ClassifyOperationIds.ReleasedC12.Count);
        Assert.Equal(
            [
                "classify.evaluate",
                "classify.outcome.get",
                "classify.apply.preview",
                "classify.apply.run",
                "classify.rule.save",
                "classify.rule.validate",
                "classify.rule.activate",
                "classify.rule.retire",
                "classify.feedback.record",
                "classify.status",
                "classify.abandon",
                "classify.cleanup"
            ],
            ClassifyOperationIds.ReleasedC12);
        Assert.Equal(ClassifyOperationIds.ReleasedC12, ClassifyOperationIds.All.Take(12));
        Assert.Equal(
            [
                "classify.evaluate",
                "classify.outcome.get",
                "classify.apply.preview",
                "classify.apply.run",
                "classify.rule.save",
                "classify.rule.validate",
                "classify.rule.activate",
                "classify.rule.retire",
                "classify.feedback.record",
                "classify.status",
                "classify.abandon",
                "classify.cleanup",
                "classify.outcome.list",
                "classify.rule.list",
                "classify.rule-set.active.get",
                "classify.corpus.build",
                "classify.unresolved.report"
            ],
            ClassifyOperationIds.All);
        Assert.Contains("classify.outcome.list", ClassifyOperationIds.All);
        Assert.Contains("classify.rule.list", ClassifyOperationIds.All);
        Assert.Contains("classify.rule-set.active.get", ClassifyOperationIds.All);
        Assert.Contains("classify.corpus.build", ClassifyOperationIds.All);
        Assert.Contains("classify.unresolved.report", ClassifyOperationIds.All);

        var registry = OperationRegistry.Create().Descriptors;
        Assert.Equal(105, registry.Count);
        Assert.Equal(
            17,
            registry.Count(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal)));
    }

    [Fact]
    public void Published_module_publishes_exactly_seventeen_descriptors_including_five_additive()
    {
        var module = new ClassifyOperationModule();
        Assert.Equal(17, module.Descriptors.Count);
        Assert.Equal(ClassifyOperationIds.All, module.Descriptors.Select(d => d.OperationId));
        Assert.Contains(
            module.Descriptors,
            d => d.OperationId == ClassifyOperationIds.OutcomeList);
        Assert.Contains(
            module.Descriptors,
            d => d.OperationId == ClassifyOperationIds.RuleList);
        Assert.Contains(
            module.Descriptors,
            d => d.OperationId == ClassifyOperationIds.RuleSetActiveGet);
        Assert.Contains(
            module.Descriptors,
            d => d.OperationId == ClassifyOperationIds.CorpusBuild);
        Assert.Contains(
            module.Descriptors,
            d => d.OperationId == ClassifyOperationIds.UnresolvedReport);
    }

    [Theory]
    [MemberData(nameof(ReleasedOperationFingerprints))]
    public void Released_c12_descriptor_fingerprints_are_frozen(
        string operationId,
        bool requiresIdempotency,
        string kind,
        string fingerprint)
    {
        var module = new ClassifyOperationModule();
        var descriptor = module.Descriptors.Single(d => d.OperationId == operationId);
        Assert.Equal(requiresIdempotency, descriptor.RequiresIdempotencyKey);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
        Assert.Equal(fingerprint, ComputeDescriptorFingerprint(descriptor));
    }

    [Fact]
    public void Apply_preview_selection_mode_selected_outcomes_wire_name_is_unchanged()
    {
        Assert.Equal(
            "\"selected_outcomes\"",
            JsonSerializer.Serialize(
                ClassifyApplySelectionMode.SelectedOutcomes,
                ClassifyJsonContext.Default.ClassifyApplySelectionMode));
        Assert.Equal(200, ClassifyOperatorErgonomicsContracts.SelectedOutcomesMax);
    }

    [Fact]
    public void Released_request_and_result_type_infos_remain_source_generated()
    {
        var module = new ClassifyOperationModule();
        foreach (var descriptor in module.Descriptors)
        {
            Assert.NotNull(descriptor.RequestTypeInfo);
            Assert.NotNull(descriptor.ResultTypeInfo);
        }

        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"contractVersion":"1.0","extra":true}""",
                ClassifyJsonContext.Default.ClassifyEvaluateRequest));
    }

    [Fact]
    public void Released_domain_error_exit_codes_remain_in_three_to_ten()
    {
        var module = new ClassifyOperationModule();
        foreach (var descriptor in module.Descriptors)
        {
            Assert.NotEmpty(descriptor.DomainErrors!);
            Assert.All(descriptor.DomainErrors!, error =>
            {
                Assert.StartsWith("CLASSIFY-", error.Code, StringComparison.Ordinal);
                Assert.InRange(error.ExitCode, 3, 10);
            });
        }
    }

    [Fact]
    public void Additive_types_do_not_replace_released_type_info_identity()
    {
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyEvaluateRequest,
            ClassifyJsonContext.Default.ClassifyEvaluateRequest);
        Assert.NotSame(
            ClassifyJsonContext.Default.ClassifyEvaluateRequest,
            ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
    }

    public static TheoryData<JsonTypeInfo> AdditiveRequestTypeInfos() =>
        new()
        {
            ClassifyJsonContext.Default.ClassifyOutcomeListRequest,
            ClassifyJsonContext.Default.ClassifyRuleListRequest,
            ClassifyJsonContext.Default.ClassifyRuleSetActiveGetRequest,
            ClassifyJsonContext.Default.ClassifyCorpusBuildRequest,
            ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest
        };

    /// <summary>
    /// Golden SHA-256 fingerprints for the twelve 0.3.3 descriptors.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (bool Idempotency, string Kind, string Fingerprint)> FrozenC12 =
        new Dictionary<string, (bool, string, string)>(StringComparer.Ordinal)
        {
            ["classify.evaluate"] = (true, "mutation", "bf871fb01329a59bc467468b7bb822ebc4fbe6758678b6d0e3c5b9c7891a0105"),
            ["classify.outcome.get"] = (false, "query", "5745abfccd7962a153d9da7c880efc29c8df70a01f2a590fee870e1235c50ecb"),
            ["classify.apply.preview"] = (true, "mutation", "02172a00efa755391179db79043e9575b8e1f6e42975d5254334f2f2c314a28e"),
            ["classify.apply.run"] = (true, "mutation", "1b959f38d885005b1c61df1b9895a71b07c5e8db9f82d4b3b4c719beaa47a224"),
            ["classify.rule.save"] = (true, "mutation", "5e126c9de1ab6936b9329b08cf6aa80630712e65b681c3e6ee0e70174bfc7b74"),
            ["classify.rule.validate"] = (true, "mutation", "e549f94b5238aa7e506a4b33efc1ab39ee1457fa43a865ee98a4b0e203f1e7cc"),
            ["classify.rule.activate"] = (true, "mutation", "c28507462e2d527ef0547f794000a90d5f498287deabf0beca6a029d66d523fc"),
            ["classify.rule.retire"] = (true, "mutation", "b95326c04ad1d001c9604eb6166b3025e49f7977bde309a9b65a0106e2336a8c"),
            ["classify.feedback.record"] = (true, "mutation", "e7f3d210482a87dfeba9fdeb3a23490d2979259a22fff9ba4f2540c29e2c1e0c"),
            ["classify.status"] = (false, "query", "3f4bd3631585df4b8887e35a05d7e734036d76da1ec4f9c11ab7cb4f5cdea387"),
            ["classify.abandon"] = (true, "mutation", "2ff6d927ea5e70277213642c42c8f4096d6f5cfa5dc09714a53b80bff673410b"),
            ["classify.cleanup"] = (true, "mutation", "af64117a200118433666eda11c41fbca4a0daf018924137ba07c4c7edf8f925e")
        };

    public static TheoryData<string, bool, string, string> ReleasedOperationFingerprints()
    {
        var data = new TheoryData<string, bool, string, string>();
        foreach (var (operationId, frozen) in FrozenC12)
        {
            data.Add(operationId, frozen.Idempotency, frozen.Kind, frozen.Fingerprint);
        }

        return data;
    }

    private static string ComputeDescriptorFingerprint(OperationDescriptor descriptor)
    {
        var requestProps = PropertyNames(descriptor.RequestTypeInfo!).Order(StringComparer.Ordinal);
        var resultProps = PropertyNames(descriptor.ResultTypeInfo!).Order(StringComparer.Ordinal);
        var errors = (descriptor.DomainErrors ?? [])
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .Select(e => $"{e.Code}:{e.Category}:{e.ExitCode}");
        var payload = string.Join(
            "\n",
            [
                descriptor.OperationId,
                descriptor.Kind,
                descriptor.RequiresIdempotencyKey ? "idempotent" : "no-idempotency",
                descriptor.MinimumContractVersion,
                descriptor.MaximumContractVersion,
                "REQ:" + string.Join(",", requestProps),
                "RES:" + string.Join(",", resultProps),
                "ERR:" + string.Join(",", errors)
            ]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static HashSet<string> PropertyNames(JsonTypeInfo typeInfo) =>
        typeInfo.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    private static ClassifyRuleSetActiveGetResult SampleActiveRuleSetResult() =>
        new(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            "rsv-1",
            BroadApplyAllowed: false,
            ActivationId: "act-1",
            ValidationId: "val-1",
            TrustedGateReceiptId: "rcpt-1",
            TrustedGateReceiptFingerprint: new string('1', 64),
            NormalizationVersion: "normalization_v1",
            ActivationEpoch: "2026-08-02T00:00:00.0000000Z",
            LifecycleStatus: ClassifyActiveRuleSetLifecycleStatus.Active,
            ActivatedAt: "2026-08-02T00:00:00.0000000Z",
            RetiredAt: null,
            RuleVersionIds: ["rv-1"],
            Categories: [new ClassifyActiveRuleSetCategory("cat-1", "Groceries", ClassifyCategoryLifecycleState.Active)]);

    private static ClassifyCorpusBuildRequest SampleCorpusBuildRequest() =>
        new(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            IdempotencyKey: "idem-1",
            OutputPath: "/owner/private/corpus.jsonl",
            Projection: new ClassifyCorpusBuildProjectionEnvelope(
                ActualsContractVersions.Current,
                ClassificationProjectionVersions.ClassificationV1,
                new string('a', 64),
                "snap-1",
                "2026-08-02T12:00:00.0000000Z",
                new string('b', 64),
                "normalization_v1",
                [
                    new ClassificationProjectionItem(
                        Ordinal: 0,
                        TransactionId: "tx-1",
                        AccountId: "acct-1",
                        EffectiveDate: "2026-07-15",
                        SignedAmount: "-12.34",
                        SourceDescription: "COFFEE SHOP",
                        AmountDirection: ClassificationAmountDirection.Expense,
                        CategoryMutationState: CategoryMutationState.Assignable,
                        CurrentCategoryId: null,
                        CurrentAllocationId: null,
                        TransactionRevision: "tr-1",
                        RelationshipRevision: "rr-1",
                        AllocationRevision: "ar-1")
                ]),
            Labels: [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-1")]);

    private static ClassifyCorpusBuildResult SampleCorpusBuildResult() =>
        new(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            "build-1",
            IdempotencyFingerprint: new string('d', 64),
            ProjectionFingerprint: new string('e', 64),
            StoreGenerationFingerprint: new string('f', 64),
            CatalogueFingerprint: new string('1', 64),
            NormalizationVersion: "normalization_v1",
            LabelCount: 1,
            WrittenRowCount: 1,
            WrittenByteCount: 128,
            CorpusFingerprint: new string('2', 64),
            TerminalState: ClassifyCorpusBuildTerminalState.Completed,
            Replayed: false);

    private static ClassifyUnresolvedReportResult SampleUnresolvedReportResult() =>
        new(
            ClassifyOperatorErgonomicsContracts.ContractVersion,
            "eval-1",
            EvaluationFingerprint: new string('a', 64),
            ProjectionFingerprint: new string('b', 64),
            CategoryLifecycleFingerprint: new string('c', 64),
            RuleSetFingerprint: new string('d', 64),
            NormalizationVersion: "normalization_v1",
            NoSuggestionOutcomeCount: 10,
            JoinedRowCount: 10,
            CandidateRowCount: 8,
            BelowMinimumRowCount: 2,
            DistinctGroupCount: 3,
            ReturnedGroupCount: 1,
            OmittedGroupCount: 2,
            BoundedRequestTopN: 25,
            BoundedRequestMinimumCount: 2,
            ReportFingerprint: new string('e', 64),
            Groups:
            [
                new ClassifyUnresolvedPatternGroup(
                    1,
                    "coffee shop",
                    "acct-1",
                    ClassificationAmountDirection.Expense,
                    TransactionCount: 5,
                    CheckedSignedAmountMinorTotal: -5000,
                    CheckedAbsoluteAmountMinorTotal: 5000,
                    GroupFingerprint: new string('f', 64))
            ]);
}
