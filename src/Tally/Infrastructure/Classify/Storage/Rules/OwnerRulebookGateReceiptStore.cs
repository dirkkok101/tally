using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Classify.Evidence;

namespace Tally.Infrastructure.Classify.Storage.Rules;

/// <summary>
/// Immutable aggregate owner-rulebook gate receipt persistence
/// (DD-CLASSIFY-RULE-AUTHORITY-PROVENANCE / TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE).
/// Never stores private corpus path, candidate IDs, description, amount, expected outcome, or token.
/// Never trusts a caller-supplied authority bool — only derived, persisted rows are authoritative.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class OwnerRulebookGateReceiptStore
{
    public async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OwnerRulebookGateReceiptRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        RequireHex64(row.ReceiptFingerprint, nameof(row.ReceiptFingerprint));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO owner_rulebook_gate_receipt (
                receipt_id, receipt_fingerprint, schema_version, receipt_kind,
                authority_granted, safety_passed, benefit_sufficient,
                requires_explicit_owner_benefit_decision, block_code,
                eligible_rows, suggested_rows, correction_rows, no_suggestion_rows,
                conflict_rows, excluded_rows, stale_rows,
                incorrect_application_canaries, unexplained_conflict_count, drift_canary_count,
                unauthorized_mutation_count, description_inferred_relationship_count,
                coverage_basis_points, owner_decision_count_before, owner_decision_count_after,
                elapsed_owner_minutes_before, elapsed_owner_minutes_after,
                candidate_fingerprint, corpus_fingerprint, hold_out_fingerprint,
                report_fingerprint, outcomes_canonical_hash,
                deterministic_replay_passed, disclosure_passed, locality_passed,
                projection_version, snapshot_id, store_generation_fingerprint,
                category_lifecycle_fingerprint, normalization_version,
                representative_validation_run_id, independent_replay_validation_run_id,
                hold_out_validation_run_id, explicit_benefit_decision, actor, created_at
            ) VALUES (
                $receipt_id, $receipt_fingerprint, $schema_version, $receipt_kind,
                $authority_granted, $safety_passed, $benefit_sufficient,
                $requires_explicit_owner_benefit_decision, $block_code,
                $eligible_rows, $suggested_rows, $correction_rows, $no_suggestion_rows,
                $conflict_rows, $excluded_rows, $stale_rows,
                $incorrect_application_canaries, $unexplained_conflict_count, $drift_canary_count,
                $unauthorized_mutation_count, $description_inferred_relationship_count,
                $coverage_basis_points, $owner_decision_count_before, $owner_decision_count_after,
                $elapsed_owner_minutes_before, $elapsed_owner_minutes_after,
                $candidate_fingerprint, $corpus_fingerprint, $hold_out_fingerprint,
                $report_fingerprint, $outcomes_canonical_hash,
                $deterministic_replay_passed, $disclosure_passed, $locality_passed,
                $projection_version, $snapshot_id, $store_generation_fingerprint,
                $category_lifecycle_fingerprint, $normalization_version,
                $representative_validation_run_id, $independent_replay_validation_run_id,
                $hold_out_validation_run_id, $explicit_benefit_decision, $actor, $created_at
            );
            """;
        Bind(command, row);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OwnerRulebookGateReceiptRow?> GetAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string receiptId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt_id, receipt_fingerprint, schema_version, receipt_kind,
                   authority_granted, safety_passed, benefit_sufficient,
                   requires_explicit_owner_benefit_decision, block_code,
                   eligible_rows, suggested_rows, correction_rows, no_suggestion_rows,
                   conflict_rows, excluded_rows, stale_rows,
                   incorrect_application_canaries, unexplained_conflict_count, drift_canary_count,
                   unauthorized_mutation_count, description_inferred_relationship_count,
                   coverage_basis_points, owner_decision_count_before, owner_decision_count_after,
                   elapsed_owner_minutes_before, elapsed_owner_minutes_after,
                   candidate_fingerprint, corpus_fingerprint, hold_out_fingerprint,
                   report_fingerprint, outcomes_canonical_hash,
                   deterministic_replay_passed, disclosure_passed, locality_passed,
                   projection_version, snapshot_id, store_generation_fingerprint,
                   category_lifecycle_fingerprint, normalization_version,
                   representative_validation_run_id, independent_replay_validation_run_id,
                   hold_out_validation_run_id, explicit_benefit_decision, actor, created_at
            FROM owner_rulebook_gate_receipt
            WHERE receipt_id = $id;
            """;
        command.Parameters.AddWithValue("$id", receiptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    /// <summary>
    /// Build an immutable row from a derived aggregate receipt plus binding identity.
    /// Receipt fingerprint is computed over durable authority fields (not caller-supplied).
    /// </summary>
    public static OwnerRulebookGateReceiptRow FromDerived(
        VerifiedOwnerRulebookGateReceipt derived,
        string receiptId,
        string representativeValidationRunId,
        string independentReplayValidationRunId,
        string holdOutValidationRunId,
        string? categoryLifecycleFingerprint,
        string? normalizationVersion,
        string? explicitBenefitDecision,
        string actor,
        string createdAt)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(representativeValidationRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(independentReplayValidationRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(holdOutValidationRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdAt);

        var fingerprint = ComputeFingerprint(
            derived,
            representativeValidationRunId,
            independentReplayValidationRunId,
            holdOutValidationRunId,
            categoryLifecycleFingerprint,
            normalizationVersion,
            explicitBenefitDecision,
            actor,
            createdAt);

        return new OwnerRulebookGateReceiptRow(
            ReceiptId: receiptId,
            ReceiptFingerprint: fingerprint,
            SchemaVersion: derived.SchemaVersion,
            ReceiptKind: derived.ReceiptKind,
            AuthorityGranted: derived.AuthorityGranted,
            SafetyPassed: derived.SafetyPassed,
            BenefitSufficient: derived.BenefitSufficient,
            RequiresExplicitOwnerBenefitDecision: derived.RequiresExplicitOwnerBenefitDecision,
            BlockCode: derived.BlockCode,
            EligibleRows: derived.EligibleRows,
            SuggestedRows: derived.SuggestedRows,
            CorrectionRows: derived.CorrectionRows,
            NoSuggestionRows: derived.NoSuggestionRows,
            ConflictRows: derived.ConflictRows,
            ExcludedRows: derived.ExcludedRows,
            StaleRows: derived.StaleRows,
            IncorrectApplicationCanaries: derived.IncorrectApplicationCanaries,
            UnexplainedConflictCount: derived.UnexplainedConflictCount,
            DriftCanaryCount: derived.DriftCanaryCount,
            UnauthorizedMutationCount: derived.UnauthorizedMutationCount,
            DescriptionInferredRelationshipCount: derived.DescriptionInferredRelationshipCount,
            CoverageBasisPoints: derived.CoverageBasisPoints,
            OwnerDecisionCountBefore: derived.OwnerDecisionCountBefore,
            OwnerDecisionCountAfter: derived.OwnerDecisionCountAfter,
            ElapsedOwnerMinutesBefore: derived.ElapsedOwnerMinutesBefore,
            ElapsedOwnerMinutesAfter: derived.ElapsedOwnerMinutesAfter,
            CandidateFingerprint: derived.CandidateFingerprint,
            CorpusFingerprint: derived.CorpusFingerprint,
            HoldOutFingerprint: derived.HoldOutFingerprint,
            ReportFingerprint: derived.ReportFingerprint,
            OutcomesCanonicalHash: derived.OutcomesCanonicalHash,
            DeterministicReplayPassed: derived.DeterministicReplayPassed,
            DisclosurePassed: derived.DisclosurePassed,
            LocalityPassed: derived.LocalityPassed,
            ProjectionVersion: derived.ProjectionVersion,
            SnapshotId: derived.SnapshotId,
            StoreGenerationFingerprint: derived.StoreGenerationFingerprint,
            CategoryLifecycleFingerprint: categoryLifecycleFingerprint,
            NormalizationVersion: normalizationVersion,
            RepresentativeValidationRunId: representativeValidationRunId,
            IndependentReplayValidationRunId: independentReplayValidationRunId,
            HoldOutValidationRunId: holdOutValidationRunId,
            ExplicitBenefitDecision: string.IsNullOrWhiteSpace(explicitBenefitDecision)
                ? null
                : explicitBenefitDecision.Trim(),
            Actor: actor,
            CreatedAt: createdAt);
    }

    public static string ComputeFingerprint(
        VerifiedOwnerRulebookGateReceipt derived,
        string representativeValidationRunId,
        string independentReplayValidationRunId,
        string holdOutValidationRunId,
        string? categoryLifecycleFingerprint,
        string? normalizationVersion,
        string? explicitBenefitDecision,
        string actor,
        string createdAt)
    {
        ArgumentNullException.ThrowIfNull(derived);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("authorityGranted", derived.AuthorityGranted);
            writer.WriteNumber("benefitSufficient", derived.BenefitSufficient ? 1 : 0);
            writer.WriteString("blockCode", derived.BlockCode);
            writer.WriteString("candidateFingerprint", derived.CandidateFingerprint);
            writer.WriteString("categoryLifecycleFingerprint", categoryLifecycleFingerprint);
            writer.WriteString("corpusFingerprint", derived.CorpusFingerprint);
            writer.WriteNumber("coverageBasisPoints", derived.CoverageBasisPoints);
            writer.WriteString("createdAt", createdAt);
            writer.WriteNumber("deterministicReplayPassed", derived.DeterministicReplayPassed ? 1 : 0);
            writer.WriteNumber("driftCanaryCount", derived.DriftCanaryCount);
            writer.WriteNumber("eligibleRows", derived.EligibleRows);
            writer.WriteString(
                "explicitBenefitDecision",
                string.IsNullOrWhiteSpace(explicitBenefitDecision) ? null : explicitBenefitDecision.Trim());
            writer.WriteString("holdOutFingerprint", derived.HoldOutFingerprint);
            writer.WriteString("holdOutValidationRunId", holdOutValidationRunId);
            writer.WriteNumber("incorrectApplicationCanaries", derived.IncorrectApplicationCanaries);
            writer.WriteString("independentReplayValidationRunId", independentReplayValidationRunId);
            writer.WriteString("normalizationVersion", normalizationVersion);
            writer.WriteString("outcomesCanonicalHash", derived.OutcomesCanonicalHash);
            writer.WriteNumber("ownerDecisionCountAfter", derived.OwnerDecisionCountAfter);
            writer.WriteNumber("ownerDecisionCountBefore", derived.OwnerDecisionCountBefore);
            writer.WriteString("projectionVersion", derived.ProjectionVersion);
            writer.WriteString("reportFingerprint", derived.ReportFingerprint);
            writer.WriteString("representativeValidationRunId", representativeValidationRunId);
            writer.WriteNumber("requiresExplicitOwnerBenefitDecision", derived.RequiresExplicitOwnerBenefitDecision ? 1 : 0);
            writer.WriteNumber("safetyPassed", derived.SafetyPassed ? 1 : 0);
            writer.WriteNumber("schemaVersion", derived.SchemaVersion);
            writer.WriteString("snapshotId", derived.SnapshotId);
            writer.WriteString("storeGenerationFingerprint", derived.StoreGenerationFingerprint);
            writer.WriteNumber("suggestedRows", derived.SuggestedRows);
            writer.WriteNumber("unexplainedConflictCount", derived.UnexplainedConflictCount);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static VerifiedOwnerRulebookGateReceipt ToContract(OwnerRulebookGateReceiptRow row) =>
        new(
            SchemaVersion: row.SchemaVersion,
            ReceiptKind: row.ReceiptKind,
            AuthorityGranted: row.AuthorityGranted,
            SafetyPassed: row.SafetyPassed,
            BenefitSufficient: row.BenefitSufficient,
            RequiresExplicitOwnerBenefitDecision: row.RequiresExplicitOwnerBenefitDecision,
            BlockCode: row.BlockCode,
            EligibleRows: row.EligibleRows,
            SuggestedRows: row.SuggestedRows,
            CorrectionRows: row.CorrectionRows,
            NoSuggestionRows: row.NoSuggestionRows,
            ConflictRows: row.ConflictRows,
            ExcludedRows: row.ExcludedRows,
            StaleRows: row.StaleRows,
            IncorrectApplicationCanaries: row.IncorrectApplicationCanaries,
            UnexplainedConflictCount: row.UnexplainedConflictCount,
            DriftCanaryCount: row.DriftCanaryCount,
            UnauthorizedMutationCount: row.UnauthorizedMutationCount,
            DescriptionInferredRelationshipCount: row.DescriptionInferredRelationshipCount,
            CoverageBasisPoints: row.CoverageBasisPoints,
            OwnerDecisionCountBefore: row.OwnerDecisionCountBefore,
            OwnerDecisionCountAfter: row.OwnerDecisionCountAfter,
            ElapsedOwnerMinutesBefore: row.ElapsedOwnerMinutesBefore,
            ElapsedOwnerMinutesAfter: row.ElapsedOwnerMinutesAfter,
            CandidateFingerprint: row.CandidateFingerprint,
            CorpusFingerprint: row.CorpusFingerprint,
            HoldOutFingerprint: row.HoldOutFingerprint,
            ReportFingerprint: row.ReportFingerprint,
            OutcomesCanonicalHash: row.OutcomesCanonicalHash,
            DeterministicReplayPassed: row.DeterministicReplayPassed,
            DisclosurePassed: row.DisclosurePassed,
            LocalityPassed: row.LocalityPassed,
            ProjectionVersion: row.ProjectionVersion,
            SnapshotId: row.SnapshotId,
            StoreGenerationFingerprint: row.StoreGenerationFingerprint,
            ReceiptId: row.ReceiptId,
            ReceiptFingerprint: row.ReceiptFingerprint,
            RepresentativeValidationRunId: row.RepresentativeValidationRunId,
            IndependentReplayValidationRunId: row.IndependentReplayValidationRunId,
            HoldOutValidationRunId: row.HoldOutValidationRunId,
            ExplicitBenefitDecision: row.ExplicitBenefitDecision,
            Actor: row.Actor,
            CreatedAt: row.CreatedAt);

    private static void Bind(SqliteCommand command, OwnerRulebookGateReceiptRow row)
    {
        command.Parameters.AddWithValue("$receipt_id", row.ReceiptId);
        command.Parameters.AddWithValue("$receipt_fingerprint", row.ReceiptFingerprint);
        command.Parameters.AddWithValue("$schema_version", row.SchemaVersion);
        command.Parameters.AddWithValue("$receipt_kind", row.ReceiptKind);
        command.Parameters.AddWithValue("$authority_granted", row.AuthorityGranted ? 1 : 0);
        command.Parameters.AddWithValue("$safety_passed", row.SafetyPassed ? 1 : 0);
        command.Parameters.AddWithValue("$benefit_sufficient", row.BenefitSufficient ? 1 : 0);
        command.Parameters.AddWithValue(
            "$requires_explicit_owner_benefit_decision",
            row.RequiresExplicitOwnerBenefitDecision ? 1 : 0);
        command.Parameters.AddWithValue("$block_code", (object?)row.BlockCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$eligible_rows", row.EligibleRows);
        command.Parameters.AddWithValue("$suggested_rows", row.SuggestedRows);
        command.Parameters.AddWithValue("$correction_rows", row.CorrectionRows);
        command.Parameters.AddWithValue("$no_suggestion_rows", row.NoSuggestionRows);
        command.Parameters.AddWithValue("$conflict_rows", row.ConflictRows);
        command.Parameters.AddWithValue("$excluded_rows", row.ExcludedRows);
        command.Parameters.AddWithValue("$stale_rows", row.StaleRows);
        command.Parameters.AddWithValue("$incorrect_application_canaries", row.IncorrectApplicationCanaries);
        command.Parameters.AddWithValue("$unexplained_conflict_count", row.UnexplainedConflictCount);
        command.Parameters.AddWithValue("$drift_canary_count", row.DriftCanaryCount);
        command.Parameters.AddWithValue("$unauthorized_mutation_count", row.UnauthorizedMutationCount);
        command.Parameters.AddWithValue(
            "$description_inferred_relationship_count",
            row.DescriptionInferredRelationshipCount);
        command.Parameters.AddWithValue("$coverage_basis_points", row.CoverageBasisPoints);
        command.Parameters.AddWithValue("$owner_decision_count_before", row.OwnerDecisionCountBefore);
        command.Parameters.AddWithValue("$owner_decision_count_after", row.OwnerDecisionCountAfter);
        command.Parameters.AddWithValue(
            "$elapsed_owner_minutes_before",
            (object?)row.ElapsedOwnerMinutesBefore ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$elapsed_owner_minutes_after",
            (object?)row.ElapsedOwnerMinutesAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("$candidate_fingerprint", (object?)row.CandidateFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$corpus_fingerprint", (object?)row.CorpusFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$hold_out_fingerprint", (object?)row.HoldOutFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$report_fingerprint", (object?)row.ReportFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$outcomes_canonical_hash",
            (object?)row.OutcomesCanonicalHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$deterministic_replay_passed", row.DeterministicReplayPassed ? 1 : 0);
        command.Parameters.AddWithValue("$disclosure_passed", row.DisclosurePassed ? 1 : 0);
        command.Parameters.AddWithValue("$locality_passed", row.LocalityPassed ? 1 : 0);
        command.Parameters.AddWithValue("$projection_version", row.ProjectionVersion);
        command.Parameters.AddWithValue("$snapshot_id", (object?)row.SnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$store_generation_fingerprint",
            (object?)row.StoreGenerationFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$category_lifecycle_fingerprint",
            (object?)row.CategoryLifecycleFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalization_version", (object?)row.NormalizationVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$representative_validation_run_id", row.RepresentativeValidationRunId);
        command.Parameters.AddWithValue(
            "$independent_replay_validation_run_id",
            row.IndependentReplayValidationRunId);
        command.Parameters.AddWithValue("$hold_out_validation_run_id", row.HoldOutValidationRunId);
        command.Parameters.AddWithValue(
            "$explicit_benefit_decision",
            (object?)row.ExplicitBenefitDecision ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", row.Actor);
        command.Parameters.AddWithValue("$created_at", row.CreatedAt);
    }

    private static OwnerRulebookGateReceiptRow Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetInt32(4) != 0,
        reader.GetInt32(5) != 0,
        reader.GetInt32(6) != 0,
        reader.GetInt32(7) != 0,
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetInt32(12),
        reader.GetInt32(13),
        reader.GetInt32(14),
        reader.GetInt32(15),
        reader.GetInt32(16),
        reader.GetInt32(17),
        reader.GetInt32(18),
        reader.GetInt32(19),
        reader.GetInt32(20),
        reader.GetInt32(21),
        reader.GetInt32(22),
        reader.GetInt32(23),
        reader.IsDBNull(24) ? null : reader.GetDouble(24),
        reader.IsDBNull(25) ? null : reader.GetDouble(25),
        reader.IsDBNull(26) ? null : reader.GetString(26),
        reader.IsDBNull(27) ? null : reader.GetString(27),
        reader.IsDBNull(28) ? null : reader.GetString(28),
        reader.IsDBNull(29) ? null : reader.GetString(29),
        reader.IsDBNull(30) ? null : reader.GetString(30),
        reader.GetInt32(31) != 0,
        reader.GetInt32(32) != 0,
        reader.GetInt32(33) != 0,
        reader.GetString(34),
        reader.IsDBNull(35) ? null : reader.GetString(35),
        reader.IsDBNull(36) ? null : reader.GetString(36),
        reader.IsDBNull(37) ? null : reader.GetString(37),
        reader.IsDBNull(38) ? null : reader.GetString(38),
        reader.GetString(39),
        reader.GetString(40),
        reader.GetString(41),
        reader.IsDBNull(42) ? null : reader.GetString(42),
        reader.GetString(43),
        reader.GetString(44));

    private static void RequireHex64(string value, string name)
    {
        if (value.Length != 64)
        {
            throw new ArgumentException($"{name} must be a 64-character hex SHA-256 digest.", name);
        }
    }
}

public sealed record OwnerRulebookGateReceiptRow(
    string ReceiptId,
    string ReceiptFingerprint,
    int SchemaVersion,
    string ReceiptKind,
    bool AuthorityGranted,
    bool SafetyPassed,
    bool BenefitSufficient,
    bool RequiresExplicitOwnerBenefitDecision,
    string? BlockCode,
    int EligibleRows,
    int SuggestedRows,
    int CorrectionRows,
    int NoSuggestionRows,
    int ConflictRows,
    int ExcludedRows,
    int StaleRows,
    int IncorrectApplicationCanaries,
    int UnexplainedConflictCount,
    int DriftCanaryCount,
    int UnauthorizedMutationCount,
    int DescriptionInferredRelationshipCount,
    int CoverageBasisPoints,
    int OwnerDecisionCountBefore,
    int OwnerDecisionCountAfter,
    double? ElapsedOwnerMinutesBefore,
    double? ElapsedOwnerMinutesAfter,
    string? CandidateFingerprint,
    string? CorpusFingerprint,
    string? HoldOutFingerprint,
    string? ReportFingerprint,
    string? OutcomesCanonicalHash,
    bool DeterministicReplayPassed,
    bool DisclosurePassed,
    bool LocalityPassed,
    string ProjectionVersion,
    string? SnapshotId,
    string? StoreGenerationFingerprint,
    string? CategoryLifecycleFingerprint,
    string? NormalizationVersion,
    string RepresentativeValidationRunId,
    string IndependentReplayValidationRunId,
    string HoldOutValidationRunId,
    string? ExplicitBenefitDecision,
    string Actor,
    string CreatedAt);
