using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tally.Contracts.Common;
using Tally.Contracts.Ingest;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Features.Ingest.Contract;
using Xunit;

namespace Tally.Tests.Ingest.Contract;

public sealed class IngestContractModelTests
{
    // FR-INGEST-CONTRACT-DISCOVERY
    [Fact]
    public void IngestOperationIds_contains_exactly_the_eight_ingest_operations()
    {
        var expected = new[]
        {
            "ingest.preview", "ingest.inspect", "ingest.approve", "ingest.commit",
            "ingest.resume", "ingest.status", "ingest.abandon", "ingest.cleanup"
        };

        Assert.Equal(8, IngestOperationIds.All.Count);
        Assert.Equal(expected.Order(StringComparer.Ordinal), IngestOperationIds.All.Order(StringComparer.Ordinal));
        Assert.Equal(8, IngestOperationIds.All.Distinct(StringComparer.Ordinal).Count());
    }

    // Failure criteria: no single "action" enum operation hides transitions behind a generic discriminator.
    [Fact]
    public void IngestOperationIds_has_no_generic_action_discriminator_operation()
    {
        Assert.All(IngestOperationIds.All, id => Assert.DoesNotContain("action", id, StringComparison.OrdinalIgnoreCase));
        Assert.All(IngestOperationIds.All, id => Assert.DoesNotContain("execute", id, StringComparison.OrdinalIgnoreCase));
        Assert.All(IngestOperationIds.All, id => Assert.StartsWith("ingest.", id, StringComparison.Ordinal));
    }

    // DM-INGEST-OPERATION-CONTRACTS: sourcePath is forbidden as a named argument outside preview.
    [Fact]
    public void SourcePath_appears_only_inside_the_preview_request()
    {
        var ingestTypes = typeof(PreviewImportInput).Assembly.GetTypes()
            .Where(t => t.Namespace == "Tally.Contracts.Ingest" && t.IsClass && t.IsPublic && !t.IsNested && t != typeof(IngestJsonContext))
            .ToArray();

        Assert.NotEmpty(ingestTypes);
        foreach (var type in ingestTypes)
        {
            var typeInfo = IngestJsonContext.Default.GetTypeInfo(type);
            Assert.NotNull(typeInfo);
            var hasSourcePath = typeInfo!.Properties.Any(p => p.Name == "sourcePath");
            Assert.True(hasSourcePath == (type == typeof(PreviewImportInput)), $"unexpected sourcePath presence on {type.Name}");
        }
    }

    public static TheoryData<Type, string[]> ClosedEnumVocabularies => new()
    {
        { typeof(BatchStatus), ["previewed", "approved", "committing", "interrupted", "completed", "abandoned", "cleaned"] },
        { typeof(SourceRecordDisposition), ["accepted_candidate", "exact_duplicate", "excluded_non_transaction", "blocked"] },
        { typeof(IngestErrorCategory), ["usage", "validation", "unsupported", "unsafe_source", "compatibility", "permission", "resource", "reconciliation", "overlap", "ledger", "interrupted", "conflict", "unexpected"] },
        { typeof(MutationPossibility), ["none", "possible", "confirmed"] },
        { typeof(IngestRetryAction), ["none", "retry", "repreview", "resume", "abandon", "correct_source"] },
        { typeof(ImportReceiptStatus), ["approved", "committing", "interrupted", "completed", "abandoned"] },
        { typeof(CandidateReceiptState), ["pending", "attempting", "accepted", "exact_duplicate", "conflicted", "rejected", "unresolved"] },
        { typeof(ArtifactKind), ["manifest", "candidates", "receipt", "metadata"] },
        { typeof(ImportProvenanceKind), ["statement_import"] }
    };

    // DM-INGEST-ERROR-STATUS-CONTRACTS / DM-INGEST-IMPORT-MANIFEST / DM-INGEST-IMPORT-RECEIPT: closed enum vocabularies.
    [Theory]
    [MemberData(nameof(ClosedEnumVocabularies))]
    public void Enum_vocabulary_is_closed_and_matches_the_data_model(Type enumType, string[] expectedValues)
    {
        var values = Enum.GetValues(enumType).Cast<object>()
            .Select(value => JsonSerializer.Serialize(value, enumType, new JsonSerializerOptions()).Trim('"'))
            .ToArray();

        Assert.Equal(expectedValues.Order(StringComparer.Ordinal), values.Order(StringComparer.Ordinal));
    }

    // DM-INGEST-OPERATION-CONTRACTS: unknown fields are rejected.
    [Fact]
    public void PreviewImportInput_rejects_unknown_fields() =>
        AssertRejectsUnknownField(SamplePreviewInput(), IngestJsonContext.Default.PreviewImportInput);

    [Fact]
    public void IngestError_rejects_unknown_fields() =>
        AssertRejectsUnknownField(SampleError(), IngestJsonContext.Default.IngestError);

    [Fact]
    public void ImportReceipt_rejects_unknown_fields() =>
        AssertRejectsUnknownField(SampleReceipt(), IngestJsonContext.Default.ImportReceipt);

    [Fact]
    public void IngestStatusInput_rejects_unknown_fields() =>
        AssertRejectsUnknownField(new IngestStatusInput("batch-1", 25, "cursor-1"), IngestJsonContext.Default.IngestStatusInput);

    // DM-INGEST-OPERATION-CONTRACTS: status limit is 1..100 with a default of 50.
    [Fact]
    public void IngestStatusInput_declares_the_contract_limit_range()
    {
        var limit = typeof(IngestStatusInput).GetProperty(nameof(IngestStatusInput.Limit))!;
        var range = limit.GetCustomAttribute<RangeAttribute>();

        Assert.NotNull(range);
        Assert.Equal(1, range.Minimum);
        Assert.Equal(100, range.Maximum);
        Assert.Equal(50, new IngestStatusInput().Limit);
    }

    [Fact]
    public void CleanupBatchInput_rejects_unknown_fields() =>
        AssertRejectsUnknownField(new CleanupBatchInput("batch-1", BatchStatus.Completed), IngestJsonContext.Default.CleanupBatchInput);

    // Success criterion 5: byte-identical logical JSON across repeated serialization.
    [Fact]
    public void ImportReceipt_serializes_byte_identically_across_repeated_calls()
    {
        var receipt = SampleReceipt();

        var first = JsonSerializer.Serialize(receipt, IngestJsonContext.Default.ImportReceipt);
        var second = JsonSerializer.Serialize(receipt, IngestJsonContext.Default.ImportReceipt);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CompletedMetadataReceipt_serializes_byte_identically_across_repeated_calls()
    {
        var receipt = SampleCompletedMetadataReceipt();

        var first = JsonSerializer.Serialize(receipt, IngestJsonContext.Default.CompletedMetadataReceipt);
        var second = JsonSerializer.Serialize(receipt, IngestJsonContext.Default.CompletedMetadataReceipt);

        Assert.Equal(first, second);
    }

    // Success criterion 3: IngestError cannot carry unsafe payload facts.
    [Fact]
    public void IngestError_carries_no_forbidden_payload_fields()
    {
        var properties = IngestJsonContext.Default.GetTypeInfo(typeof(IngestError))!.Properties.Select(p => p.Name).ToArray();

        foreach (var forbidden in new[] { "sourcePath", "rows", "amount", "amounts", "balance", "balances", "bankIdentifier", "rawRequest", "request", "manifest", "stackTrace", "parserException" })
        {
            Assert.DoesNotContain(forbidden, properties, StringComparer.OrdinalIgnoreCase);
        }
    }

    // Success criterion 4: CompletedMetadataReceipt excludes descriptions, amounts, balances, evidence, controls, and frozen requests.
    [Fact]
    public void CompletedMetadataReceipt_excludes_forbidden_fields()
    {
        var properties = IngestJsonContext.Default.GetTypeInfo(typeof(CompletedMetadataReceipt))!.Properties.Select(p => p.Name).ToArray();

        foreach (var forbidden in new[] { "description", "originalDescription", "amount", "signedAmount", "signedAmountMinor", "balance", "evidence", "controls", "frozenLedgerRequest", "frozenRequest" })
        {
            Assert.DoesNotContain(forbidden, properties, StringComparer.OrdinalIgnoreCase);
        }
    }

    // DM-INGEST-ERROR-STATUS-CONTRACTS: status contracts never expose raw statement facts.
    [Fact]
    public void BatchStatus_contracts_carry_no_forbidden_raw_fields()
    {
        var summaryProperties = IngestJsonContext.Default.GetTypeInfo(typeof(BatchStatusSummary))!.Properties.Select(p => p.Name).ToArray();
        var detailProperties = IngestJsonContext.Default.GetTypeInfo(typeof(BatchStatusDetail))!.Properties.Select(p => p.Name).ToArray();

        foreach (var forbidden in new[] { "sourcePath", "statementRow", "statementRows", "description", "amount", "balance", "bankIdentifier", "request", "manifest", "stackTrace", "parserException" })
        {
            Assert.DoesNotContain(forbidden, summaryProperties, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, detailProperties, StringComparer.OrdinalIgnoreCase);
        }
    }

    // DD-INGEST-LEDGER-PUBLIC-INTEGRATION / DM-INGEST-LEDGER-COMMIT-CONTRACT
    [Fact]
    public void FrozenLedgerRecordRequest_uses_the_released_record_transaction_input_directly()
    {
        var inputProperties = LedgerJsonContext.Default.RecordTransactionInput.Properties.Select(p => p.Name).ToArray();

        Assert.Equal(typeof(RecordTransactionInput), typeof(FrozenLedgerRecordRequest).GetProperty("Input")!.PropertyType);
        Assert.Null(typeof(FrozenLedgerRecordRequest).Assembly.GetType("Tally.Contracts.Ingest.FrozenLedgerRecordInput"));
        Assert.Equal(
            ["accountId", "signedAmount", "currencyCode", "transactionDate", "postingDate", "originalDescription", "instrumentId", "cardholderId", "initialEvidence"],
            inputProperties);
        Assert.Equal(typeof(RegisterEvidenceInput), typeof(RecordTransactionInput).GetProperty("InitialEvidence")!.PropertyType);
        Assert.DoesNotContain("sourceReference", inputProperties);
        Assert.DoesNotContain("provenance", inputProperties);
    }

    // DD-INGEST-LEDGER-PUBLIC-INTEGRATION: terminal equality is restricted to immutable request/evidence facts.
    [Fact]
    public void LedgerImmutableVerification_excludes_mutable_ledger_projections()
    {
        var verificationType = typeof(FrozenLedgerRecordRequest).Assembly.GetType("Tally.Contracts.Ingest.LedgerImmutableVerification");

        Assert.NotNull(verificationType);
        var properties = IngestJsonContext.Default.GetTypeInfo(verificationType!)!.Properties.Select(p => p.Name).ToArray();
        Assert.Equal(
            ["transactionId", "accountId", "signedAmount", "currencyCode", "transactionDate", "postingDate", "originalDescription", "instrumentId", "cardholderId", "initialEvidence"],
            properties);
        foreach (var forbidden in new[] { "history", "lifecycle", "category", "pool", "reconciliation", "actor", "recordedAt" })
        {
            Assert.DoesNotContain(forbidden, properties, StringComparer.OrdinalIgnoreCase);
        }
    }

    // DM-INGEST-LEDGER-COMMIT-CONTRACT: the exact frozen request is byte-stable through source-generated JSON.
    [Fact]
    public void FrozenLedgerRecordRequest_serializes_byte_identically_across_repeated_calls()
    {
        var request = SampleFrozenLedgerRecordRequest();

        var first = JsonSerializer.Serialize(request, IngestJsonContext.Default.FrozenLedgerRecordRequest);
        var second = JsonSerializer.Serialize(request, IngestJsonContext.Default.FrozenLedgerRecordRequest);

        Assert.Equal(first, second);
        Assert.Contains("\"initialEvidence\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceReference", first, StringComparison.Ordinal);
        Assert.DoesNotContain("provenance", first, StringComparison.Ordinal);
    }

    // Success criterion 5: source-generated metadata exists for every contract type.
    [Fact]
    public void IngestJsonContext_has_source_generated_metadata_for_every_registered_contract_type()
    {
        var registeredTypes = typeof(IngestJsonContext).GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(JsonSerializableAttribute))
            .Select(attribute => (Type)attribute.ConstructorArguments[0].Value!)
            .ToArray();

        Assert.True(registeredTypes.Length >= 30, $"expected at least 30 registered contract types, found {registeredTypes.Length}");
        foreach (var type in registeredTypes)
        {
            Assert.NotNull(IngestJsonContext.Default.GetTypeInfo(type));
        }
    }

    // DM-INGEST-OPERATION-CONTRACTS: finite contract states are part of the Native-AOT metadata graph.
    [Theory]
    [InlineData(typeof(ArtifactKind))]
    [InlineData(typeof(BatchStatus))]
    [InlineData(typeof(SourceRecordDisposition))]
    [InlineData(typeof(ImportProvenanceKind))]
    [InlineData(typeof(IngestErrorCategory))]
    [InlineData(typeof(MutationPossibility))]
    [InlineData(typeof(IngestRetryAction))]
    [InlineData(typeof(ImportReceiptStatus))]
    [InlineData(typeof(CandidateReceiptState))]
    public void IngestJsonContext_has_source_generated_metadata_for_each_contract_enum(Type enumType) =>
        Assert.NotNull(IngestJsonContext.Default.GetTypeInfo(enumType));

    [Fact]
    public void IngestJsonContext_uses_camel_case_naming_and_disallows_unmapped_members()
    {
        Assert.Equal(JsonUnmappedMemberHandling.Disallow, IngestJsonContext.Default.Options.UnmappedMemberHandling);
        Assert.Equal("batchId", IngestJsonContext.Default.Options.PropertyNamingPolicy!.ConvertName("BatchId"));
    }

    private static void AssertRejectsUnknownField<T>(T sample, JsonTypeInfo<T> typeInfo)
    {
        var node = JsonSerializer.SerializeToNode(sample, typeInfo)!.AsObject();
        node["unexpectedField"] = "boom";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(node.ToJsonString(), typeInfo));
    }

    private static SafeActor SampleActor() => new("automation", "ingest-contract-test", "run-01");

    private static PreviewImportInput SamplePreviewInput() =>
        new("1.0", "/tmp/statement.pdf", "acc-1", SampleActor());

    private static IngestError SampleError() =>
        new("INGEST-VALIDATION-001", IngestErrorCategory.Validation, "The statement could not be parsed.", "batch-1", null, MutationPossibility.None, null, IngestRetryAction.Repreview, null);

    private static ImportReceiptCounts SampleReceiptCounts() => new(0, 0, 3, 1, 0, 0, 0);

    private static CandidateReceipt SampleCandidateReceipt() => new(
        "cand-1", CandidateReceiptState.Accepted, 1, "ledger.transaction.record", "1.0", "idem-1",
        "txn-1", null, IngestRetryAction.None, "2026-07-01T00:00:00Z", "2026-07-01T00:00:01Z");

    private static ImportReceipt SampleReceipt() => new(
        "receipt-1", "batch-1", "rev-1", ImportReceiptStatus.Completed, SampleReceiptCounts(),
        [], [SampleCandidateReceipt()], "2026-07-01T00:00:00Z", "2026-07-01T00:00:01Z", "2026-07-01T00:00:02Z");

    private static CompletedMetadataReceipt SampleCompletedMetadataReceipt() => new(
        "receipt-1", "batch-1", new string('a', 64), "acc-1", "za-bank-a-v1",
        new IngestVersions("1.0", "1.0"), new IngestOutcomeCounts(3, 1, 0, 0),
        ["cand-1"], ["txn-1"], "2026-07-01T00:00:02Z");

    private static FrozenLedgerRecordRequest SampleFrozenLedgerRecordRequest() => new(
        "1.0", "ledger.transaction.record", "idem-1", SampleActor(),
        new RecordTransactionInput(
            "acc-1", "-12.34", "ZAR", "2026-07-01", null, "Owner-safe statement line", null, null,
            new RegisterEvidenceInput(
                EvidenceKind.StatementRow,
                new string('a', 64),
                $"ingest:{new string('a', 64)}",
                new string('b', 64),
                new EvidenceObservation("acc-1", -1234, "ZAR", "2026-07-01", null, null, null, new string('c', 64)))));
}
