using System.Text.Json;
using Tally.Application;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify.Contract;

/// <summary>
/// TC-CLASSIFY-CONTRACT-DISCOVERY-CONTRACT / TC-CLASSIFY-STRUCTURED-INVOCATION-CONTRACT
/// Contract foundation proofs — no classify.db, corpus, or Ledger reads.
/// </summary>
public sealed class ClassifyOperationContractTests
{
    [Fact]
    public void Inventory_contains_exactly_twelve_c12_operations_in_canonical_order()
    {
        var descriptors = Module().Descriptors;
        Assert.Equal(12, descriptors.Count);
        Assert.Equal(ClassifyOperationIds.All, descriptors.Select(d => d.OperationId));
        Assert.Equal(12, Module().Operations.Count);
    }

    [Fact]
    public void All_cli_paths_are_unique_and_classify_prefixed()
    {
        var paths = Module().Descriptors.Select(d => d.CliPath).ToArray();
        Assert.All(paths, path => Assert.StartsWith("tally classify ", path, StringComparison.Ordinal));
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(ClassifyOperationIds.Evaluate, true, "mutation")]
    [InlineData(ClassifyOperationIds.OutcomeGet, false, "query")]
    [InlineData(ClassifyOperationIds.ApplyPreview, true, "mutation")]
    [InlineData(ClassifyOperationIds.ApplyRun, true, "mutation")]
    [InlineData(ClassifyOperationIds.RuleSave, true, "mutation")]
    [InlineData(ClassifyOperationIds.RuleValidate, true, "mutation")]
    [InlineData(ClassifyOperationIds.RuleActivate, true, "mutation")]
    [InlineData(ClassifyOperationIds.RuleRetire, true, "mutation")]
    [InlineData(ClassifyOperationIds.FeedbackRecord, true, "mutation")]
    [InlineData(ClassifyOperationIds.Status, false, "query")]
    [InlineData(ClassifyOperationIds.Abandon, true, "mutation")]
    [InlineData(ClassifyOperationIds.Cleanup, true, "mutation")]
    public void Mutability_and_idempotency_metadata_match_operation_kind(
        string operationId,
        bool requiresIdempotency,
        string kind)
    {
        var descriptor = Module().Descriptors.Single(d => d.OperationId == operationId);
        Assert.Equal(requiresIdempotency, descriptor.RequiresIdempotencyKey);
        Assert.Equal(kind, descriptor.Kind);
        Assert.Equal("1.0", descriptor.MinimumContractVersion);
        Assert.Equal("1.0", descriptor.MaximumContractVersion);
    }

    [Theory]
    [MemberData(nameof(AllOperationIds))]
    public void Descriptor_publishes_request_result_types_and_domain_errors(string operationId)
    {
        var descriptor = Module().Descriptors.Single(d => d.OperationId == operationId);
        Assert.NotNull(descriptor.RequestTypeInfo);
        Assert.NotNull(descriptor.ResultTypeInfo);
        Assert.NotNull(descriptor.DomainErrors);
        Assert.NotEmpty(descriptor.DomainErrors!);
        Assert.All(descriptor.DomainErrors!, error =>
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Code));
            Assert.False(string.IsNullOrWhiteSpace(error.Category));
            Assert.InRange(error.ExitCode, 3, 10);
        });
        var schema = descriptor.ToSchema();
        Assert.Equal(operationId, schema.OperationId);
        Assert.DoesNotContain("ClassifyStateStore", schema.RequestSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("LedgerDb", schema.ResultSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", schema.RequestSchema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", schema.RequestSchema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forbidden_alias_and_generic_operations_are_absent()
    {
        var ids = Module().Descriptors.Select(d => d.OperationId).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("classify.invoke", ids);
        Assert.DoesNotContain("classify.manage", ids);
        Assert.DoesNotContain("classify.run", ids);
        Assert.DoesNotContain("classify.save", ids);
        Assert.DoesNotContain("classify.update", ids);
        Assert.DoesNotContain("classify.list", ids);
        Assert.DoesNotContain("classify.delete", ids);
        Assert.DoesNotContain("classify.execute", ids);
    }

    [Fact]
    public void Closed_enums_serialize_as_canonical_snake_names()
    {
        Assert.Equal(
            "\"description.normalized\"",
            JsonSerializer.Serialize(ClassificationRuleFieldKey.DescriptionNormalized, ClassifyJsonContext.Default.ClassificationRuleFieldKey));
        Assert.Equal(
            "\"contains_token_sequence\"",
            JsonSerializer.Serialize(ClassificationRulePredicateKind.ContainsTokenSequence, ClassifyJsonContext.Default.ClassificationRulePredicateKind));
        Assert.Equal(
            "\"selected_outcomes\"",
            JsonSerializer.Serialize(ClassifyApplySelectionMode.SelectedOutcomes, ClassifyJsonContext.Default.ClassifyApplySelectionMode));
        Assert.Equal(
            "\"no_suggestion\"",
            JsonSerializer.Serialize(ClassifyOutcomeKind.NoSuggestion, ClassifyJsonContext.Default.ClassifyOutcomeKind));
        Assert.Equal(
            "\"already_applied\"",
            JsonSerializer.Serialize(ClassifyApplyItemResultKind.AlreadyApplied, ClassifyJsonContext.Default.ClassifyApplyItemResultKind));
    }

    [Fact]
    public void Unknown_request_fields_are_rejected_by_source_generated_json()
    {
        const string json = """{"contractVersion":"1.0","extra":"nope"}""";
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyEvaluateRequest));
    }

    [Fact]
    public void Rule_save_rejects_unknown_condition_fields()
    {
        const string json = """
            {"contractVersion":"1.0","ruleId":"r1","categoryId":"c1","normalizationVersion":"normalization_v1","conditions":[{"ordinal":0,"fieldKey":"account.id","predicateKind":"equals","valueText":"a","leak":true}],"reason":"x"}
            """;
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyRuleSaveRequest));
    }

    [Fact]
    public void Supported_contract_version_is_exactly_one_dot_zero()
    {
        Assert.True(ClassifyContractMapper.IsSupportedContractVersion("1.0"));
        Assert.False(ClassifyContractMapper.IsSupportedContractVersion("2.0"));
        Assert.False(ClassifyContractMapper.IsSupportedContractVersion(null));
        Assert.False(ClassifyContractMapper.IsSupportedContractVersion(""));
    }

    [Fact]
    public void Pure_mapper_orders_conditions_deterministically()
    {
        var conditions = new[]
        {
            new ClassificationRuleConditionInput(1, ClassificationRuleFieldKey.AmountDirection, ClassificationRulePredicateKind.Equals, EnumValue: ClassificationAmountDirectionValue.Outflow),
            new ClassificationRuleConditionInput(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, ValueText: "acct")
        };
        var ordered = ClassifyContractMapper.OrderConditions(conditions);
        Assert.Equal(0, ordered[0].Ordinal);
        Assert.Equal(ClassificationRuleFieldKey.AccountId, ordered[0].FieldKey);
        Assert.Equal(1, ordered[1].Ordinal);
    }

    [Fact]
    public void Apply_selection_rejects_mixed_modes()
    {
        var mixed = new ClassifyApplySelection(
            ClassifyApplySelectionMode.SelectedOutcomes,
            OutcomeIds: ["o1"],
            RuleVersionId: "rv1");
        Assert.False(ClassifyContractMapper.TryValidateApplySelection(mixed, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);
    }

    [Fact]
    public void Apply_selection_requires_complete_correction_items()
    {
        var incomplete = new ClassifyApplySelection(
            ClassifyApplySelectionMode.ExplicitCorrections,
            CorrectionItems:
            [
                new ClassifyExplicitCorrectionItem("tx", "out", "from", "to", "")
            ]);
        Assert.False(ClassifyContractMapper.TryValidateApplySelection(incomplete, out var error));
        Assert.Equal(ClassifyErrors.SelectionInvalid, error);

        var complete = new ClassifyApplySelection(
            ClassifyApplySelectionMode.ExplicitCorrections,
            CorrectionItems:
            [
                new ClassifyExplicitCorrectionItem("tx", "out", "from", "to", "owner reason")
            ]);
        Assert.True(ClassifyContractMapper.TryValidateApplySelection(complete, out _));
    }

    [Fact]
    public void Every_descriptor_publishes_non_null_deterministic_limits()
    {
        var module = Module();
        Assert.All(module.Operations, operation =>
        {
            Assert.NotNull(operation.Limits);
            Assert.Equal(operation.Limits, module.LimitsFor(operation.Descriptor.OperationId));
        });

        // Evaluation limits match NFR-CLASSIFY-BOUNDED-EVALUATION / C11 exact targets.
        var evaluation = module.LimitsFor(ClassifyOperationIds.Evaluate);
        Assert.Equal(10_000, evaluation.MaxTransactionCount);
        Assert.Equal(500, evaluation.MaxRuleCount);
        Assert.Equal(100_000, evaluation.MaxEvidenceRowCount);
        Assert.Equal(OperationLimits.NotApplicable, evaluation.MaxCorpusRowCount);
        Assert.Equal(256L * 1024 * 1024, evaluation.MaxMemoryBytes);
        Assert.Equal(5_000, evaluation.MaxProcessingTimeMs);
    }

    [Fact]
    public void Inclusive_limit_accepts_max_and_rejects_one_over()
    {
        var limits = ClassifyOperationModule.V1Limits.Evaluation;
        Assert.True(limits.AcceptsTransactionCount(10_000));
        Assert.False(limits.AcceptsTransactionCount(10_001));
        Assert.True(limits.AcceptsRuleCount(500));
        Assert.False(limits.AcceptsRuleCount(501));
        Assert.True(limits.AcceptsMemoryBytes(256L * 1024 * 1024));
        Assert.False(limits.AcceptsMemoryBytes((256L * 1024 * 1024) + 1));
        Assert.True(limits.AcceptsProcessingTimeMs(5_000));
        Assert.False(limits.AcceptsProcessingTimeMs(5_001));
        // Corpus is N/A on evaluation — any non-negative count passes.
        Assert.True(limits.AcceptsCorpusRowCount(0));
        Assert.True(limits.AcceptsCorpusRowCount(99_999));
        Assert.False(ClassifyContractMapper.ExceedsAnyLimit(limits, transactionCount: 10_000, ruleCount: 500));
        Assert.True(ClassifyContractMapper.ExceedsAnyLimit(limits, transactionCount: 10_001));
    }

    [Fact]
    public void Not_applicable_limit_is_explicit_negative_one_not_zero()
    {
        Assert.Equal(-1, OperationLimits.NotApplicable);
        var maintenance = ClassifyOperationModule.V1Limits.Maintenance;
        Assert.Equal(OperationLimits.NotApplicable, maintenance.MaxTransactionCount);
        Assert.Equal(OperationLimits.NotApplicable, maintenance.MaxRuleCount);
        Assert.NotEqual(0, maintenance.MaxTransactionCount);
        Assert.True(maintenance.AcceptsTransactionCount(1_000_000));
    }

    [Fact]
    public void Operation_limits_use_the_published_stable_wire_names()
    {
        var json = JsonSerializer.Serialize(
            ClassifyOperationModule.V1Limits.Evaluation,
            ClassifyJsonContext.Default.OperationLimits);

        Assert.Contains("\"max_transaction_count\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_rule_count\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_evidence_row_count\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_corpus_row_count\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_memory_bytes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_processing_time_ms\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("maxTransactionCount", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stub_handler_requires_actor_without_opening_storage()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.Status)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-1"),
            ClassifyJsonContext.Default.ClassifyStatusRequest);
        var result = await handler.HandleAsync(new OperationRequest(input, null, null), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Mutating_stub_requires_idempotency_key()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.Evaluate)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new ClassifyEvaluateRequest("1.0"),
            ClassifyJsonContext.Default.ClassifyEvaluateRequest);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Unsupported_contract_version_fails_before_storage()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.OutcomeGet)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new ClassifyOutcomeGetRequest("9.9", "eval", "tx"),
            ClassifyJsonContext.Default.ClassifyOutcomeGetRequest);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Malformed_json_object_fails_as_invalid_input()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.Status)
            .HandlerFactory(null!, null!);
        using var document = JsonDocument.Parse("[]");
        var result = await handler.HandleAsync(
            new OperationRequest(document.RootElement.Clone(), new SafeActor("human", "owner"), null),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Valid_read_contract_does_not_return_success_without_store_implementation()
    {
        // Foundation stub fails closed with NotFound — proves no silent empty success / no fabricated payload.
        var handler = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.OutcomeGet)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new ClassifyOutcomeGetRequest("1.0", "01EVAL", "01TX"),
            ClassifyJsonContext.Default.ClassifyOutcomeGetRequest);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), "key-1"),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.NotFound, result.ErrorCode);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public async Task Apply_preview_rejects_invalid_selection_before_storage()
    {
        var handler = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.ApplyPreview)
            .HandlerFactory(null!, null!);
        var input = JsonSerializer.SerializeToElement(
            new ClassifyApplyPreviewRequest(
                "1.0",
                "eval-1",
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes)),
            ClassifyJsonContext.Default.ClassifyApplyPreviewRequest);
        var result = await handler.HandleAsync(
            new OperationRequest(input, new SafeActor("human", "owner"), "idem-1"),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    [Fact]
    public void Descriptor_templates_are_constructible_without_services()
    {
        var templates = ClassifyOperationModule.CreateDescriptorTemplates().Descriptors;
        Assert.Equal(12, templates.Count);
        Assert.All(templates, d => Assert.StartsWith("classify.", d.OperationId, StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_error_codes_are_stable_classify_prefixed()
    {
        var codes = Module().Descriptors.SelectMany(d => d.DomainErrors!).Select(e => e.Code).Distinct().ToArray();
        Assert.All(codes, code => Assert.StartsWith("CLASSIFY-", code, StringComparison.Ordinal));
        Assert.Contains(ClassifyErrors.InvalidInput, codes);
        Assert.Contains(ClassifyErrors.ResourceLimit, codes);
        Assert.Contains(ClassifyErrors.Stale, codes);
        Assert.Contains(ClassifyErrors.LedgerIncompatible, codes);
    }

    [Fact]
    public void Only_status_and_outcome_get_are_non_mutating_queries()
    {
        var readOnly = Module().Descriptors
            .Where(d => !d.RequiresIdempotencyKey)
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { ClassifyOperationIds.OutcomeGet, ClassifyOperationIds.Status }.Order(StringComparer.Ordinal),
            readOnly);
        Assert.All(
            Module().Descriptors.Where(d => !d.RequiresIdempotencyKey),
            d => Assert.Equal("query", d.Kind));
    }

    [Fact]
    public void Cleanup_receipt_schema_includes_identity_and_aggregate_counts()
    {
        var receipt = new ClassifyCleanupResult(
            ClassifyOperationIds.ContractVersion,
            CleanupId: "cleanup-1",
            PolicyVersion: "cleanup_v1",
            RemovedArtifactCount: 3,
            RetainedArtifactCount: 7,
            RemovedTemporaryCount: 2,
            RemovedExpiredPreviewCount: 1,
            RemovedAbandonedPayloadCount: 0);
        var json = JsonSerializer.Serialize(receipt, ClassifyJsonContext.Default.ClassifyCleanupResult);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("cleanupId", out var cleanupId));
        Assert.Equal("cleanup-1", cleanupId.GetString());
        Assert.True(root.TryGetProperty("policyVersion", out _));
        Assert.True(root.TryGetProperty("removedArtifactCount", out var removed));
        Assert.Equal(3, removed.GetInt32());
        Assert.True(root.TryGetProperty("retainedArtifactCount", out var retained));
        Assert.Equal(7, retained.GetInt32());
        Assert.True(root.TryGetProperty("removedTemporaryCount", out _));
        Assert.True(root.TryGetProperty("removedExpiredPreviewCount", out _));
        Assert.True(root.TryGetProperty("removedAbandonedPayloadCount", out _));
        // Disclosure: no path/name/subject fields.
        Assert.False(root.TryGetProperty("path", out _));
        Assert.False(root.TryGetProperty("paths", out _));
        Assert.False(root.TryGetProperty("fileName", out _));
        Assert.False(root.TryGetProperty("subjectId", out _));
        Assert.DoesNotContain("/tmp", json, StringComparison.Ordinal);
        Assert.DoesNotContain("classify/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleanup_request_accepts_only_policy_version_not_path()
    {
        var request = new ClassifyCleanupRequest(ClassifyOperationIds.ContractVersion, "cleanup_v1");
        var json = JsonSerializer.Serialize(request, ClassifyJsonContext.Default.ClassifyCleanupRequest);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("policyVersion", out _));
        Assert.False(document.RootElement.TryGetProperty("path", out _));
        Assert.False(document.RootElement.TryGetProperty("glob", out _));
    }

    [Fact]
    public void Example_invocations_use_stdin_input_boundary()
    {
        Assert.All(Module().Descriptors, d => Assert.Contains("--input -", d.Example, StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_serialization_is_byte_stable_across_repeated_builds()
    {
        var first = Module().Descriptors.Select(d => d.ToSchema()).Select(s =>
            JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = false })).ToArray();
        var second = Module().Descriptors.Select(d => d.ToSchema()).Select(s =>
            JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = false })).ToArray();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Evaluation_and_validate_limits_differ_on_corpus_dimension()
    {
        var evaluation = Module().LimitsFor(ClassifyOperationIds.Evaluate);
        var validate = Module().LimitsFor(ClassifyOperationIds.RuleValidate);
        Assert.Equal(OperationLimits.NotApplicable, evaluation.MaxCorpusRowCount);
        Assert.Equal(10_000, validate.MaxCorpusRowCount);
        Assert.True(validate.AcceptsCorpusRowCount(10_000));
        Assert.False(validate.AcceptsCorpusRowCount(10_001));
    }

    public static TheoryData<string> AllOperationIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in ClassifyOperationIds.All)
        {
            data.Add(id);
        }

        return data;
    }

    [Fact]
    public void Outcome_get_result_publishes_bounded_explanation_fields()
    {
        var result = new ClassifyOutcomeGetResult(
            ContractVersion: "1.0",
            EvaluationId: "eval-1",
            OutcomeId: "out-1",
            TransactionId: "tx-1",
            Ordinal: 0,
            Kind: ClassifyOutcomeKind.Conflict,
            NormalizationVersion: "normalization_v1",
            RuleSetVersionId: "rsv-1",
            SafeReason: "incompatible_category_conflict",
            SuggestedCategoryId: null,
            SuggestedCategoryDisplayName: null,
            ContributingRuleVersionIds: ["rv-a", "rv-b"],
            MatchedFieldKeys: ["description.normalized"],
            ConflictProposals:
            [
                new ClassifyConflictRuleProposal("rv-a", "cat-a"),
                new ClassifyConflictRuleProposal("rv-b", "cat-b")
            ],
            IsStale: false,
            StaleDimensions: null,
            PermittedNextOperationId: ClassifyOperationIds.Evaluate);

        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyOutcomeGetResult);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyOutcomeGetResult);
        Assert.NotNull(roundTrip);
        Assert.Equal("normalization_v1", roundTrip!.NormalizationVersion);
        Assert.Equal("rsv-1", roundTrip.RuleSetVersionId);
        Assert.Equal("incompatible_category_conflict", roundTrip.SafeReason);
        Assert.Equal(2, roundTrip.ContributingRuleVersionIds!.Count);
        Assert.Equal(["description.normalized"], roundTrip.MatchedFieldKeys);
        Assert.Equal(2, roundTrip.ConflictProposals!.Count);
        Assert.Equal("rv-a", roundTrip.ConflictProposals[0].RuleVersionId);
        Assert.Equal("cat-a", roundTrip.ConflictProposals[0].ProposedCategoryId);
        Assert.Equal(ClassifyOperationIds.Evaluate, roundTrip.PermittedNextOperationId);
        Assert.DoesNotContain("normalizedValueHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("predicateKind", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Outcome_get_fresh_suggestion_permits_null_next_operation()
    {
        var result = new ClassifyOutcomeGetResult(
            "1.0", "eval", "out", "tx", 0, ClassifyOutcomeKind.Suggestion,
            "normalization_v1", "rsv", "suggestion", "cat", "Name",
            ["rv"], ["description.normalized"], null, false, null, null);
        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyOutcomeGetResult);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyOutcomeGetResult);
        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip!.PermittedNextOperationId);
        Assert.False(roundTrip.IsStale);
        Assert.Null(roundTrip.ConflictProposals);
    }

    [Fact]
    public void Outcome_get_result_type_is_source_generated_on_descriptor()
    {
        var descriptor = Module().Descriptors.Single(d => d.OperationId == ClassifyOperationIds.OutcomeGet);
        Assert.Same(ClassifyJsonContext.Default.ClassifyOutcomeGetResult, descriptor.ResultTypeInfo);
        Assert.Same(ClassifyJsonContext.Default.ClassifyOutcomeGetRequest, descriptor.RequestTypeInfo);
        var schema = descriptor.ToSchema();
        Assert.Contains("normalizationVersion", schema.ResultSchema, StringComparison.Ordinal);
        Assert.Contains("ruleSetVersionId", schema.ResultSchema, StringComparison.Ordinal);
        Assert.Contains("matchedFieldKeys", schema.ResultSchema, StringComparison.Ordinal);
        Assert.Contains("conflictProposals", schema.ResultSchema, StringComparison.Ordinal);
        Assert.Contains("permittedNextOperationId", schema.ResultSchema, StringComparison.Ordinal);
        Assert.Contains("safeReason", schema.ResultSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("normalizedValueHash", schema.ResultSchema, StringComparison.OrdinalIgnoreCase);
    }

    private static ClassifyOperationModule Module() => new();
}
