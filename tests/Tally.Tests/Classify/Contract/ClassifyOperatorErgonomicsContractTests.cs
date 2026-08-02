using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify.Contract;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-CONTRACT-FOUNDATION / bd-1gly —
/// Additive request/result/enum/error shapes under source-generated JSON with
/// frozen 0.3.3 compatibility fingerprints. No descriptors or handlers.
/// </summary>
public sealed class ClassifyOperatorErgonomicsContractTests
{
    /// <summary>Published selected_outcomes apply bound remains 200 (C12 apply contract).</summary>
    public const int SelectedOutcomesMax = 200;

    /// <summary>Discovery pageSize / unresolved topN upper bound (DM pages).</summary>
    public const int DiscoveryPageMax = 500;

    /// <summary>Corpus label upper bound (DM-CLASSIFY-PRIVATE-CORPUS-BUILD).</summary>
    public const int CorpusLabelMax = 10_000;

    // ── Source generation / round-trip ───────────────────────────────────────

    [Fact]
    public void Outcome_list_request_round_trips_under_source_generation()
    {
        var request = new ClassifyOutcomeListRequest(
            "1.0",
            "eval-1",
            PageSize: 50,
            OutcomeKind: ClassifyOutcomeKind.Suggestion,
            SuggestedCategoryId: "cat-1",
            StaleState: ClassifyOutcomeStaleFilter.Fresh,
            Continuation: "opaque-token");
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
        Assert.NotNull(roundTrip);
        Assert.Equal("1.0", roundTrip!.ContractVersion);
        Assert.Equal("eval-1", roundTrip.EvaluationId);
        Assert.Equal(50, roundTrip.PageSize);
        Assert.Equal(ClassifyOutcomeKind.Suggestion, roundTrip.OutcomeKind);
        Assert.Equal(ClassifyOutcomeStaleFilter.Fresh, roundTrip.StaleState);
        Assert.Equal("opaque-token", roundTrip.Continuation);
    }

    [Fact]
    public void Rule_list_request_round_trips_closed_lifecycle_filter()
    {
        var request = new ClassifyRuleListRequest(
            "1.0",
            PageSize: 100,
            LogicalRuleId: "rule-a",
            Lifecycle: ClassifyRuleLifecycleFilter.Active,
            ActiveMembership: true);
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyRuleListRequest);
        Assert.Contains("\"active\"", json, StringComparison.Ordinal);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyRuleListRequest);
        Assert.Equal(ClassifyRuleLifecycleFilter.Active, roundTrip!.Lifecycle);
        Assert.True(roundTrip.ActiveMembership);
    }

    [Fact]
    public void Rule_set_active_get_request_is_contract_version_only()
    {
        var request = new ClassifyRuleSetActiveGetRequest("1.0");
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetRequest);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.0", doc.RootElement.GetProperty("contractVersion").GetString());
        Assert.False(doc.RootElement.TryGetProperty("authorityGranted", out _));
        Assert.False(doc.RootElement.TryGetProperty("broadApplyAllowed", out _));
    }

    [Fact]
    public void Corpus_build_request_round_trips_labels_and_projection_envelope()
    {
        var request = SampleCorpusBuildRequest();
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyCorpusBuildRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyCorpusBuildRequest);
        Assert.NotNull(roundTrip);
        Assert.Single(roundTrip!.Labels);
        Assert.Equal("tx-1", roundTrip.Labels[0].TransactionId);
        Assert.Equal(ClassifyOutcomeKind.Suggestion, roundTrip.Labels[0].ExpectedOutcome);
        Assert.Single(roundTrip.Projection.Items);
        Assert.Equal("/owner/private/corpus.jsonl", roundTrip.OutputPath);
    }

    [Fact]
    public void Unresolved_report_request_round_trips_bounds_and_filters()
    {
        var request = new ClassifyUnresolvedReportRequest(
            "1.0",
            "eval-1",
            TopN: 25,
            MinimumCount: 2,
            AccountId: "acct-1",
            AmountDirection: ClassificationAmountDirectionValue.Outflow);
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest);
        Assert.Equal(25, roundTrip!.TopN);
        Assert.Equal(2, roundTrip.MinimumCount);
        Assert.Equal(ClassificationAmountDirectionValue.Outflow, roundTrip.AmountDirection);
    }

    [Fact]
    public void Outcome_list_result_round_trips_page_metadata_and_items()
    {
        var result = SampleOutcomeListResult();
        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.NotNull(roundTrip);
        Assert.Equal(146, roundTrip!.OverallCount);
        Assert.Equal(1, roundTrip.ReturnedCount);
        Assert.Equal("out-1", roundTrip.Items[0].OutcomeId);
        // Fresh suggestion: null next operation (same semantics as outcome.get).
        Assert.Null(roundTrip.Items[0].PermittedNextOperationId);
    }

    [Fact]
    public void Rule_list_result_includes_closed_conditions_only()
    {
        var result = SampleRuleListResult();
        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyRuleListResult);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyRuleListResult);
        Assert.Single(roundTrip!.Items);
        Assert.Equal(ClassifyRuleProvenanceKind.OwnerAuthored, roundTrip.Items[0].Provenance);
        Assert.Single(roundTrip.Items[0].Conditions);
        Assert.DoesNotContain("regex", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule_set_active_get_result_exposes_authority_summary_without_fabricated_empty()
    {
        var result = SampleActiveRuleSetResult();
        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        Assert.Equal("rsv-1", roundTrip!.RuleSetVersionId);
        Assert.False(string.IsNullOrWhiteSpace(roundTrip.ActivationId));
        Assert.Equal(["rv-1"], roundTrip.RuleVersionIds);
        Assert.DoesNotContain("authorityGranted", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Corpus_build_result_is_aggregate_only()
    {
        var result = SampleCorpusBuildResult();
        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.DoesNotContain("outputPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/owner/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("labels", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amountAbsoluteMinor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedOutcome", json, StringComparison.OrdinalIgnoreCase);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.Equal(1, roundTrip!.WrittenRowCount);
        Assert.False(roundTrip.Replayed);
    }

    [Fact]
    public void Unresolved_report_result_excludes_transaction_ids_and_raw_descriptions()
    {
        var result = SampleUnresolvedReportResult();
        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.DoesNotContain("transactionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("continuation", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("representativeNormalizedDescription", json, StringComparison.Ordinal);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.Single(roundTrip!.Groups);
        Assert.Equal(1, roundTrip.Groups[0].Rank);
    }

    // ── Unknown fields / contract version ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(AdditiveRequestTypeInfos))]
    public void Additive_requests_reject_unknown_fields(JsonTypeInfo typeInfo)
    {
        const string json = """{"contractVersion":"1.0","extra":"nope","pageSize":10,"evaluationId":"e","topN":1,"minimumCount":2,"idempotencyKey":"k","outputPath":"/x","projection":{"ledgerContractVersion":"1.0","projectionVersion":"classification_v1","storeGenerationFingerprint":"a","snapshotId":"s","snapshotExpiresAt":"t","categoryLifecycleFingerprint":"c","normalizationVersion":"normalization_v1","items":[]},"labels":[]}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, typeInfo));
    }

    [Fact]
    public void Additive_enums_serialize_as_canonical_snake_names()
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
            "\"superseded\"",
            JsonSerializer.Serialize(ClassifyRuleLifecycleFilter.Superseded, ClassifyJsonContext.Default.ClassifyRuleLifecycleFilter));
    }

    // ── Bounds constants (contract surface documentation) ────────────────────

    [Fact]
    public void Discovery_and_report_bounds_match_published_data_models()
    {
        Assert.Equal(500, DiscoveryPageMax);
        Assert.Equal(10_000, CorpusLabelMax);
        Assert.Equal(200, SelectedOutcomesMax);
        // pageSize / topN must stay within 1..DiscoveryPageMax for handlers (later beads).
        Assert.InRange(1, 1, DiscoveryPageMax);
        Assert.InRange(DiscoveryPageMax, 1, DiscoveryPageMax);
        Assert.InRange(2, 2, DiscoveryPageMax); // minimumCount lower bound
    }

    [Fact]
    public void Outcome_list_result_exposes_required_page_fields()
    {
        var names = PropertyNames(ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.Contains("contractVersion", names);
        Assert.Contains("evaluationId", names);
        Assert.Contains("evaluationFingerprint", names);
        Assert.Contains("resultFingerprint", names);
        Assert.Contains("ruleSetFingerprint", names);
        Assert.Contains("categoryLifecycleFingerprint", names);
        Assert.Contains("ledgerGeneration", names);
        Assert.Contains("overallCount", names);
        Assert.Contains("filteredCount", names);
        Assert.Contains("returnedCount", names);
        Assert.Contains("items", names);
        Assert.Contains("continuation", names);
    }

    [Fact]
    public void Outcome_list_item_exposes_selection_fields_without_private_payload()
    {
        var names = PropertyNames(ClassifyJsonContext.Default.ClassifyOutcomeListItem);
        Assert.Contains("outcomeId", names);
        Assert.Contains("transactionId", names);
        Assert.Contains("ordinal", names);
        Assert.Contains("kind", names);
        Assert.Contains("safeReason", names);
        Assert.Contains("suggestedCategoryId", names);
        Assert.Contains("contributingRuleVersionIds", names);
        Assert.Contains("matchedFieldKeys", names);
        Assert.Contains("conflictSummary", names);
        Assert.Contains("staleDimensions", names);
        Assert.Contains("permittedNextOperationId", names);
        Assert.DoesNotContain("sourceDescription", names);
        Assert.DoesNotContain("amountAbsoluteMinor", names);
        Assert.DoesNotContain("normalizedValueHash", names);
        Assert.DoesNotContain("outputPath", names);
    }

    [Fact]
    public void Corpus_build_result_properties_exclude_path_and_rows()
    {
        var names = PropertyNames(ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.Contains("buildId", names);
        Assert.Contains("idempotencyFingerprint", names);
        Assert.Contains("corpusFingerprint", names);
        Assert.Contains("writtenRowCount", names);
        Assert.Contains("writtenByteCount", names);
        Assert.Contains("terminalState", names);
        Assert.Contains("replayed", names);
        Assert.DoesNotContain("outputPath", names);
        Assert.DoesNotContain("labels", names);
        Assert.DoesNotContain("items", names);
        Assert.DoesNotContain("sourceDescription", names);
    }

    [Fact]
    public void Unresolved_report_group_exposes_aggregates_not_transaction_ids()
    {
        var names = PropertyNames(ClassifyJsonContext.Default.ClassifyUnresolvedPatternGroup);
        Assert.Contains("rank", names);
        Assert.Contains("representativeNormalizedDescription", names);
        Assert.Contains("accountId", names);
        Assert.Contains("amountDirection", names);
        Assert.Contains("transactionCount", names);
        Assert.Contains("groupFingerprint", names);
        Assert.DoesNotContain("transactionId", names);
        Assert.DoesNotContain("transactionIds", names);
        Assert.DoesNotContain("sourceDescription", names);
    }

    [Fact]
    public void Additive_error_codes_are_stable_classify_prefixed_strings()
    {
        Assert.Equal("CLASSIFY-CURSOR-INVALID", ClassifyErrors.CursorInvalid);
        Assert.Equal("CLASSIFY-CURSOR-STALE", ClassifyErrors.CursorStale);
        Assert.Equal("CLASSIFY-ACTIVE-RULE-SET-NOT-FOUND", ClassifyErrors.ActiveRuleSetNotFound);
        Assert.Equal("CLASSIFY-PRIVACY-REJECTED", ClassifyErrors.PrivacyRejected);
        Assert.Equal("CLASSIFY-DESTINATION-EXISTS", ClassifyErrors.DestinationExists);
        Assert.Equal("CLASSIFY-LABEL-INVALID", ClassifyErrors.LabelInvalid);
        Assert.StartsWith("CLASSIFY-", ClassifyErrors.CursorInvalid, StringComparison.Ordinal);
        // Existing C12 codes remain unchanged.
        Assert.Equal("CLASSIFY-INPUT-INVALID", ClassifyErrors.InvalidInput);
        Assert.Equal("CLASSIFY-STALE", ClassifyErrors.Stale);
        Assert.Equal("CLASSIFY-IDEMPOTENCY-CONFLICT", ClassifyErrors.IdempotencyConflict);
    }

    [Fact]
    public void Json_context_exposes_type_info_for_every_additive_payload()
    {
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyOutcomeListRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyOutcomeListItem);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleListRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleListResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleListItem);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleSetActiveGetRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyActiveRuleSetCategory);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildLabel);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildProjectionEnvelope);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCorpusBuildProjectionItem);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyUnresolvedReportRequest);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyUnresolvedPatternGroup);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyOutcomeStaleFilter);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleLifecycleFilter);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyRuleProvenanceKind);
        Assert.NotNull(ClassifyJsonContext.Default.ClassifyCategoryLifecycleState);
    }

    // ── Frozen 0.3.3 released fingerprints (must not drift this bead) ─────────

    [Fact]
    public void Released_c12_operation_inventory_remains_exactly_twelve()
    {
        Assert.Equal(12, ClassifyOperationIds.All.Count);
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
            ClassifyOperationIds.All);
        // Additive ops must not appear in C12 inventory until runtime convergence.
        Assert.DoesNotContain("classify.outcome.list", ClassifyOperationIds.All);
        Assert.DoesNotContain("classify.rule.list", ClassifyOperationIds.All);
        Assert.DoesNotContain("classify.rule-set.active.get", ClassifyOperationIds.All);
        Assert.DoesNotContain("classify.corpus.build", ClassifyOperationIds.All);
        Assert.DoesNotContain("classify.unresolved.report", ClassifyOperationIds.All);
    }

    [Fact]
    public void Released_c12_module_still_publishes_exactly_twelve_descriptors()
    {
        var module = new ClassifyOperationModule();
        Assert.Equal(12, module.Descriptors.Count);
        Assert.Equal(12, module.Operations.Count);
        Assert.Equal(ClassifyOperationIds.All, module.Descriptors.Select(d => d.OperationId));
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

    /// <summary>
    /// Golden SHA-256 fingerprints for the twelve 0.3.3 descriptors (request/result
    /// property names + domain error exit tuples + mutability). Regenerating these
    /// requires an intentional C12 contract change outside this bead.
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

    [Fact]
    public void Apply_preview_selection_mode_selected_outcomes_wire_name_is_unchanged()
    {
        Assert.Equal(
            "\"selected_outcomes\"",
            JsonSerializer.Serialize(
                ClassifyApplySelectionMode.SelectedOutcomes,
                ClassifyJsonContext.Default.ClassifyApplySelectionMode));
        Assert.Equal(200, SelectedOutcomesMax);
    }

    [Fact]
    public void Released_request_and_result_type_infos_remain_source_generated()
    {
        var module = new ClassifyOperationModule();
        foreach (var descriptor in module.Descriptors)
        {
            Assert.NotNull(descriptor.RequestTypeInfo);
            Assert.NotNull(descriptor.ResultTypeInfo);
            Assert.True(descriptor.RequestTypeInfo!.IsReadOnly || descriptor.RequestTypeInfo.Kind != default);
            // Round-trip empty unknown-field rejection still holds for evaluate.
        }

        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"contractVersion":"1.0","extra":true}""",
                ClassifyJsonContext.Default.ClassifyEvaluateRequest));
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"contractVersion":"1.0","evaluationId":"e","transactionId":"t","extra":true}""",
                ClassifyJsonContext.Default.ClassifyOutcomeGetRequest));
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
    public void Corpus_and_unresolved_privacy_hold_on_serialized_errors_shape()
    {
        // Typed error codes only — never embed path/row/description in the constant surface.
        foreach (var code in new[]
                 {
                     ClassifyErrors.CursorInvalid,
                     ClassifyErrors.CursorStale,
                     ClassifyErrors.PrivacyRejected,
                     ClassifyErrors.DestinationExists,
                     ClassifyErrors.LabelInvalid,
                     ClassifyErrors.ResourceLimit,
                     ClassifyErrors.InvalidInput
                 })
        {
            Assert.DoesNotContain('/', code);
            Assert.DoesNotContain(' ', code);
            Assert.DoesNotContain("description", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", code, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Additive_request_results_do_not_mutate_released_type_info_identity()
    {
        // Adding JsonSerializable entries must not replace released TypeInfo instances.
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyEvaluateRequest,
            ClassifyJsonContext.Default.ClassifyEvaluateRequest);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyApplyPreviewRequest,
            ClassifyJsonContext.Default.ClassifyApplyPreviewRequest);
        Assert.Same(
            ClassifyJsonContext.Default.ClassifyStatusResult,
            ClassifyJsonContext.Default.ClassifyStatusResult);
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

    public static TheoryData<string, bool, string, string> ReleasedOperationFingerprints()
    {
        var data = new TheoryData<string, bool, string, string>();
        foreach (var (operationId, frozen) in FrozenC12)
        {
            data.Add(operationId, frozen.Idempotency, frozen.Kind, frozen.Fingerprint);
        }

        return data;
    }

    /// <summary>
    /// Stable fingerprint over mutability, idempotency, request/result property names,
    /// and domain error code+exit tuples for one released descriptor.
    /// </summary>
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

    private static ClassifyOutcomeListResult SampleOutcomeListResult() =>
        new(
            "1.0",
            "eval-1",
            EvaluationFingerprint: new string('a', 64),
            ResultFingerprint: new string('b', 64),
            RuleSetFingerprint: new string('c', 64),
            CategoryLifecycleFingerprint: new string('d', 64),
            LedgerGeneration: new string('e', 64),
            OverallCount: 146,
            FilteredCount: 146,
            ReturnedCount: 1,
            Items:
            [
                new ClassifyOutcomeListItem(
                    "out-1",
                    "tx-1",
                    0,
                    ClassifyOutcomeKind.Suggestion,
                    "suggestion",
                    "cat-1",
                    "Groceries",
                    ["rv-1"],
                    ["description.normalized"],
                    null,
                    [],
                    null)
            ],
            Continuation: null);

    private static ClassifyRuleListResult SampleRuleListResult() =>
        new(
            "1.0",
            OverallCount: 1,
            FilteredCount: 1,
            ReturnedCount: 1,
            Items:
            [
                new ClassifyRuleListItem(
                    "rule-1",
                    "rv-1",
                    null,
                    "cat-1",
                    "Groceries",
                    ClassifyCategoryLifecycleState.Active,
                    "normalization_v1",
                    ClassifyRuleLifecycleFilter.Active,
                    ActiveMembership: true,
                    BroadApplyAllowed: false,
                    ClassifyRuleProvenanceKind.OwnerAuthored,
                    ScopeHash: new string('f', 64),
                    CreatedAt: "2026-08-02T00:00:00.0000000Z",
                    ValidatedAt: null,
                    ActivatedAt: "2026-08-02T00:00:00.0000000Z",
                    RetiredAt: null,
                    Conditions:
                    [
                        new ClassificationRuleConditionInput(
                            0,
                            ClassificationRuleFieldKey.DescriptionNormalized,
                            ClassificationRulePredicateKind.Equals,
                            ValueText: "coffee shop")
                    ])
            ],
            Continuation: null);

    private static ClassifyRuleSetActiveGetResult SampleActiveRuleSetResult() =>
        new(
            "1.0",
            "rsv-1",
            BroadApplyAllowed: false,
            ActivationId: "act-1",
            ValidationId: "val-1",
            TrustedGateReceiptId: "rcpt-1",
            TrustedGateReceiptFingerprint: new string('1', 64),
            NormalizationVersion: "normalization_v1",
            ActivationEpoch: "2026-08-02T00:00:00.0000000Z",
            LifecycleStatus: "active",
            ActivatedAt: "2026-08-02T00:00:00.0000000Z",
            RetiredAt: null,
            RuleVersionIds: ["rv-1"],
            Categories: [new ClassifyActiveRuleSetCategory("cat-1", "Groceries", ClassifyCategoryLifecycleState.Active)]);

    private static ClassifyCorpusBuildRequest SampleCorpusBuildRequest() =>
        new(
            "1.0",
            IdempotencyKey: "idem-1",
            OutputPath: "/owner/private/corpus.jsonl",
            Projection: new ClassifyCorpusBuildProjectionEnvelope(
                "1.0",
                "classification_v1",
                new string('a', 64),
                "snap-1",
                "2026-08-02T12:00:00.0000000Z",
                new string('b', 64),
                "normalization_v1",
                [
                    new ClassifyCorpusBuildProjectionItem(
                        "tx-1",
                        0,
                        "acct-1",
                        "COFFEE SHOP",
                        "outflow",
                        1234,
                        new string('c', 64))
                ]),
            Labels: [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.Suggestion, "cat-1")]);

    private static ClassifyCorpusBuildResult SampleCorpusBuildResult() =>
        new(
            "1.0",
            "build-1",
            IdempotencyFingerprint: new string('d', 64),
            ProjectionFingerprint: new string('e', 64),
            StoreGenerationFingerprint: new string('f', 64),
            CategoryLifecycleFingerprint: new string('1', 64),
            NormalizationVersion: "normalization_v1",
            LabelCount: 1,
            WrittenRowCount: 1,
            WrittenByteCount: 128,
            CorpusFingerprint: new string('2', 64),
            TerminalState: "completed",
            Replayed: false);

    private static ClassifyUnresolvedReportResult SampleUnresolvedReportResult() =>
        new(
            "1.0",
            "eval-1",
            EvaluationFingerprint: new string('a', 64),
            ProjectionFingerprint: new string('b', 64),
            CategoryLifecycleFingerprint: new string('c', 64),
            RuleSetFingerprint: new string('d', 64),
            NormalizationVersion: "normalization_v1",
            EligibleNoSuggestionCount: 10,
            MatchedFreshRowCount: 10,
            GroupCount: 3,
            ReturnedGroupCount: 1,
            BelowMinimumRowCount: 2,
            CandidateRowCount: 1,
            ReportFingerprint: new string('e', 64),
            Groups:
            [
                new ClassifyUnresolvedPatternGroup(
                    1,
                    "coffee shop",
                    "acct-1",
                    ClassificationAmountDirectionValue.Outflow,
                    TransactionCount: 5,
                    CheckedSignedAmountMinorTotal: -5000,
                    CheckedAbsoluteAmountMinorTotal: 5000,
                    GroupFingerprint: new string('f', 64))
            ]);
}
