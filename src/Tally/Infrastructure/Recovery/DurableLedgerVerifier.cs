using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Application.Ports;
using Tally.Domain.Ledger.Actuals;
using Tally.Domain.Ledger.Reconciliation;
using Tally.Domain.Ledger.Recovery;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Actuals;

namespace Tally.Infrastructure.Recovery;

[SupportedOSPlatform("linux")]
public sealed class DurableLedgerVerifier(IHostArtifactProtection artifactProtection)
{
    public const string StorageContractVersion = "2";

    private static readonly string[] DurableTables =
    [
        "account",
        "artifact_manifest",
        "cardholder",
        "catalogue_lifecycle_event",
        "category_allocation_event",
        "category_parent_event",
        "coverage_entry",
        "evidence_link_event",
        "evidence_observation",
        "evidence_record",
        "financial_relationship",
        "idempotency_record",
        "logical_effect",
        "migration_metadata",
        "payment_instrument",
        "pool_assignment_event",
        "reconciliation_decision",
        "reconciliation_decision_authority",
        "reconciliation_exception",
        "relationship_lifecycle_event",
        "spend_category",
        "spend_pool",
        "statement_correction",
        "statement_correction_relationship_event",
        "statement_scope",
        "statement_scope_evidence",
        "statement_unknown_attribution_authority",
        "store_generation",
        "transaction_attribution_event",
        "transaction_fact",
        "transaction_lifecycle_event"
    ];

    private static readonly string[] EphemeralTables = ["query_snapshot", "query_snapshot_group", "query_snapshot_item", "query_snapshot_payload"];

    private static readonly string[] ForbiddenSchemaTerms =
    [
        "agentmail", "mailbox", "mime", "whatsapp", "recipient", "delivery", "raw_payload", "providercursor", "provider_cursor", "credential"
    ];

    private static readonly HashSet<string> MutationOperationsWithoutLogicalEffects =
    [
        "ledger.account.archive", "ledger.account.create", "ledger.account.rename",
        "ledger.cardholder.archive", "ledger.cardholder.create", "ledger.cardholder.reactivate", "ledger.cardholder.rename",
        "ledger.category.archive", "ledger.category.create", "ledger.category.reactivate", "ledger.category.rename", "ledger.category.reparent",
        "ledger.instrument.archive", "ledger.instrument.create", "ledger.instrument.reactivate", "ledger.instrument.rename",
        "ledger.pool.archive", "ledger.pool.create", "ledger.pool.reactivate", "ledger.pool.rename",
        "ledger.transaction.attribution.assign", "ledger.transaction.attribution.correct",
        "ledger.transaction.category.assign", "ledger.transaction.category.correct",
        "ledger.transaction.pool.assign", "ledger.transaction.pool.correct"
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> LogicalEffectOperations =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["evidence"] = ["ledger.evidence.register"],
            ["evidence_supporting_link"] = ["ledger.evidence.link-supporting"],
            ["transaction_record"] = ["ledger.transaction.record"],
            ["transaction_correction"] = ["ledger.transaction.void", "ledger.transaction.supersede"],
            ["transfer_confirmation"] = ["ledger.transfer.confirm"],
            ["refund_confirmation"] = ["ledger.refund.confirm"],
            ["relationship_revoke"] = ["ledger.transfer.revoke", "ledger.refund.revoke"],
            ["relationship_replacement"] = ["ledger.transfer.replace", "ledger.refund.replace"],
            ["reconciliation_apply"] = ["ledger.reconciliation.apply"],
            ["reconciliation_decision_transition"] =
            [
                "ledger.reconciliation.decision.confirm", "ledger.reconciliation.decision.reject",
                "ledger.reconciliation.decision.revoke", "ledger.reconciliation.decision.replace"
            ],
            ["statement_coverage_completion"] = ["ledger.reconciliation.coverage.complete"],
            ["backup_artifact"] = ["ledger.backup.create"],
            ["restore_prepare"] = ["ledger.restore.prepare"],
            ["restore_activation"] = ["ledger.restore.activate"]
        };

    private static readonly (string Table, string Id, string Previous, string SafeType)[] HistoryChains =
    [
        ("catalogue_lifecycle_event", "lifecycle_event_id", "previous_event_id", "catalogue_lifecycle"),
        ("category_parent_event", "parent_event_id", "previous_parent_event_id", "category_hierarchy"),
        ("category_allocation_event", "allocation_event_id", "previous_event_id", "category_allocation"),
        ("transaction_attribution_event", "attribution_event_id", "previous_event_id", "payment_attribution"),
        ("pool_assignment_event", "pool_assignment_event_id", "previous_event_id", "pool_assignment"),
        ("reconciliation_decision", "decision_id", "previous_decision_id", "reconciliation_decision"),
        ("evidence_link_event", "link_event_id", "previous_link_event_id", "evidence_link")
    ];

    public Task<DurableLedgerVerificationResult> VerifyAsync(LedgerDb database, CancellationToken cancellationToken) =>
        VerifyAsync(database, expectedDatabaseChecksum: null, cancellationToken);

    public async Task<DurableLedgerVerificationResult> VerifyAsync(
        LedgerDb database,
        string? expectedDatabaseChecksum,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger verification requires Linux host protections.");

        if (await IsLiveAsync(database, cancellationToken))
        {
            return Failure(DurableLedgerErrors.LiveStore, "generation");
        }

        if (!TryRequireProtection(database))
        {
            return Failure(DurableLedgerErrors.HostProtection, "artifact");
        }

        IReadOnlyList<DurableArtifactReport> artifacts;
        try
        {
            artifacts = await ArtifactReportsAsync(database, cancellationToken);
        }
        catch (IOException)
        {
            return Failure(DurableLedgerErrors.IntegrityFailure, "artifact");
        }

        var databaseChecksum = artifacts.Single(report => report.Name == "ledger.db").Checksum;
        if (expectedDatabaseChecksum is not null
            && !string.Equals(expectedDatabaseChecksum, databaseChecksum, StringComparison.Ordinal))
        {
            return Failure(DurableLedgerErrors.ChecksumMismatch, "artifact");
        }

        DurableLedgerVerificationResult result;
        try
        {
            await using var connection = await OpenReadOnlyAsync(database, cancellationToken);
            result = await VerifyConnectionAsync(connection, artifacts, cancellationToken);
        }
        catch (SqliteException)
        {
            return Failure(DurableLedgerErrors.IntegrityFailure, "sqlite");
        }
        catch (InvalidOperationException)
        {
            return Failure(DurableLedgerErrors.InvariantViolation, "actuals");
        }
        catch (OverflowException)
        {
            return Failure(DurableLedgerErrors.InvariantViolation, "exact_totals");
        }

        if (!result.IsVerified) return result;
        var afterDatabaseChecksum = await FileChecksumAsync(database.DatabasePath, cancellationToken);
        return string.Equals(databaseChecksum, afterDatabaseChecksum, StringComparison.Ordinal)
            ? result
            : Failure(DurableLedgerErrors.StateChanged, "artifact");
    }

    private static async Task<DurableLedgerVerificationResult> VerifyConnectionAsync(
        SqliteConnection connection,
        IReadOnlyList<DurableArtifactReport> artifacts,
        CancellationToken cancellationToken)
    {
        var integrity = await ScalarTextAsync(connection, "PRAGMA integrity_check;", cancellationToken);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(DurableLedgerErrors.IntegrityFailure, "sqlite");
        }
        var foreignKeyViolations = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", cancellationToken);
        if (foreignKeyViolations != 0)
        {
            return Failure(DurableLedgerErrors.IntegrityFailure, "foreign_key", foreignKeyViolations);
        }

        var schema = await ValidateSchemaAsync(connection, cancellationToken);
        if (schema is not null) return schema;

        var policies = await ValidatePoliciesAsync(connection, cancellationToken);
        if (policies.Result is not null) return policies.Result;

        var invariant = await ValidateInvariantsAsync(connection, cancellationToken);
        if (invariant is not null) return invariant;

        var history = await ValidateHistoryChainsAsync(connection, cancellationToken);
        if (history is not null) return history;

        var idempotency = await ValidateIdempotencyAsync(connection, cancellationToken);
        if (idempotency is not null) return idempotency;

        var types = new List<DurableTypeReport>(DurableTables.Length);
        foreach (var table in DurableTables)
        {
            types.Add(await TypeReportAsync(connection, table, cancellationToken));
        }

        var actuals = await ActualsReportsAsync(connection, cancellationToken);
        var hierarchyFingerprint = await QueryFingerprintAsync(
            connection,
            "SELECT category_id, parent_category_id, depth, ancestry_ids, status FROM current_category_projection ORDER BY category_id;",
            cancellationToken);
        var replacementFingerprint = await QueryFingerprintAsync(
            connection,
            "SELECT transaction_id, action, replacement_transaction_id, reconciliation_decision_id FROM transaction_lifecycle_event ORDER BY transaction_id;",
            cancellationToken);
        var relationshipFingerprint = await QueryFingerprintAsync(
            connection,
            "SELECT relationship_id, relationship_type, source_transaction_id, target_transaction_id, amount_minor, state, lifecycle_event_id, event_type, replacement_relationship_id FROM financial_relationship_current ORDER BY relationship_id;",
            cancellationToken);
        var reconciliationFingerprint = await QueryFingerprintAsync(
            connection,
            "SELECT decision_id, evidence_id, prior_transaction_id, active_transaction_id, disposition, policy_id, policy_version, authority_kind, statement_authority_basis, previous_decision_id FROM reconciliation_decision_v2 ORDER BY evidence_id, decided_at, decision_id;",
            cancellationToken);
        var idempotencyFingerprint = await QueryFingerprintAsync(
            connection,
            "SELECT record.idempotency_key, record.operation_id, record.canonical_request_hash, record.actor, record.state, record.stable_result, effect.logical_identity, effect.effect_type FROM idempotency_record AS record LEFT JOIN logical_effect AS effect ON effect.idempotency_key = record.idempotency_key ORDER BY record.idempotency_key, effect.logical_identity;",
            cancellationToken);

        var normalized = NormalizedFingerprint(
            types,
            actuals,
            hierarchyFingerprint,
            replacementFingerprint,
            relationshipFingerprint,
            reconciliationFingerprint,
            idempotencyFingerprint,
            policies.PolicyVersions!);
        var report = new DurableLedgerReport(
            CompleteLedgerSchema.CurrentVersion,
            StorageContractVersion,
            policies.PolicyVersions!,
            artifacts,
            types,
            actuals,
            hierarchyFingerprint,
            replacementFingerprint,
            relationshipFingerprint,
            reconciliationFingerprint,
            idempotencyFingerprint,
            normalized);
        return DurableLedgerVerificationResult.Verified(report);
    }

    private static async Task<DurableLedgerVerificationResult?> ValidateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var version = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken);
        if (version != CompleteLedgerSchema.CurrentVersion)
        {
            return Failure(DurableLedgerErrors.SchemaIncompatible, "schema_version");
        }

        var actualTables = await NamesAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
            cancellationToken);
        var extras = actualTables.Except(DurableTables, StringComparer.Ordinal).Except(EphemeralTables, StringComparer.Ordinal).ToArray();
        if (extras.Any(name => ForbiddenSchemaTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            return Failure(DurableLedgerErrors.PrivacyViolation, "schema", extras.Length);
        }
        var expected = DurableTables.Concat(EphemeralTables).Order(StringComparer.Ordinal).ToArray();
        if (!actualTables.SequenceEqual(expected, StringComparer.Ordinal))
        {
            return Failure(DurableLedgerErrors.SchemaIncompatible, "schema");
        }

        foreach (var table in actualTables)
        {
            var columns = await NamesAsync(connection, $"SELECT name FROM pragma_table_info('{table.Replace("'", "''", StringComparison.Ordinal)}') ORDER BY cid;", cancellationToken);
            if (ForbiddenSchemaTerms.Any(term => table.Contains(term, StringComparison.OrdinalIgnoreCase)
                || columns.Any(column => column.Contains(term, StringComparison.OrdinalIgnoreCase))))
            {
                return Failure(DurableLedgerErrors.PrivacyViolation, "schema");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, fragment_name FROM migration_metadata ORDER BY version, fragment_name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fragments = new List<(long Version, string Name)>();
        while (await reader.ReadAsync(cancellationToken)) fragments.Add((reader.GetInt64(0), reader.GetString(1)));
        var required = CompleteLedgerSchema.CurrentFragmentNames
            .Select(name => (Version: name switch
            {
                "statement_authority" => 2L,
                "actuals_query_indexes" => 3L,
                _ => 1L
            }, Name: name))
            .OrderBy(value => value.Version)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();
        return fragments.SequenceEqual(required)
            ? null
            : Failure(DurableLedgerErrors.SchemaIncompatible, "migration_metadata");
    }

    private static async Task<(DurableLedgerVerificationResult? Result, IReadOnlyList<string>? PolicyVersions)> ValidatePoliciesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT policy_id, policy_version
            FROM reconciliation_decision
            WHERE policy_id IS NOT NULL OR policy_version IS NOT NULL
            ORDER BY policy_id, policy_version;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var policies = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                return (Failure(DurableLedgerErrors.PolicyIncompatible, "reconciliation_policy"), null);
            }
            var id = reader.GetString(0);
            var version = reader.GetString(1);
            if (!ManualReviewProjectionV1.Supports(id, version)
                && !(id == ReconciliationPolicyV1.PolicyId && version == ReconciliationPolicyV1.PolicyVersion))
            {
                return (Failure(DurableLedgerErrors.PolicyIncompatible, "reconciliation_policy"), null);
            }
            policies.Add(id + ":" + version);
        }
        if (!policies.Contains(ManualReviewProjectionV1.PolicyId + ":" + ManualReviewProjectionV1.PolicyVersion, StringComparer.Ordinal))
        {
            policies.Add(ManualReviewProjectionV1.PolicyId + ":" + ManualReviewProjectionV1.PolicyVersion);
        }
        if (!policies.Contains(ReconciliationPolicyV1.PolicyId + ":" + ReconciliationPolicyV1.PolicyVersion, StringComparer.Ordinal))
        {
            policies.Add(ReconciliationPolicyV1.PolicyId + ":" + ReconciliationPolicyV1.PolicyVersion);
        }
        policies.Sort(StringComparer.Ordinal);
        return (null, policies);
    }

    private static async Task<DurableLedgerVerificationResult?> ValidateInvariantsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var checks = new (string SafeType, string Sql)[]
        {
            ("catalogue_lifecycle", """
                SELECT COUNT(*) FROM (
                    SELECT account_id AS entity_id, 'account' AS kind FROM account
                    UNION ALL SELECT category_id, 'category' FROM spend_category
                    UNION ALL SELECT instrument_id, 'payment_instrument' FROM payment_instrument
                    UNION ALL SELECT cardholder_id, 'cardholder' FROM cardholder
                    UNION ALL SELECT pool_id, 'spend_pool' FROM spend_pool) AS entity
                WHERE (SELECT COUNT(*) FROM catalogue_current AS current WHERE current.catalogue_kind = entity.kind AND current.entity_id = entity.entity_id) <> 1;
                """),
            ("catalogue_lifecycle", """
                SELECT COUNT(*) FROM catalogue_lifecycle_event AS lifecycle
                WHERE (lifecycle.catalogue_kind = 'account' AND NOT EXISTS (SELECT 1 FROM account WHERE account_id = lifecycle.entity_id))
                   OR (lifecycle.catalogue_kind = 'category' AND NOT EXISTS (SELECT 1 FROM spend_category WHERE category_id = lifecycle.entity_id))
                   OR (lifecycle.catalogue_kind = 'payment_instrument' AND NOT EXISTS (SELECT 1 FROM payment_instrument WHERE instrument_id = lifecycle.entity_id))
                   OR (lifecycle.catalogue_kind = 'cardholder' AND NOT EXISTS (SELECT 1 FROM cardholder WHERE cardholder_id = lifecycle.entity_id))
                   OR (lifecycle.catalogue_kind = 'spend_pool' AND NOT EXISTS (SELECT 1 FROM spend_pool WHERE pool_id = lifecycle.entity_id));
                """),
            ("category_hierarchy", """
                SELECT
                    (SELECT COUNT(*) FROM spend_category WHERE (SELECT COUNT(*) FROM category_parent_current AS current WHERE current.category_id = spend_category.category_id) <> 1)
                  + ABS((SELECT COUNT(*) FROM spend_category) - (SELECT COUNT(*) FROM current_category_projection));
                """),
            ("category_hierarchy", """
                WITH RECURSIVE walk(start_id, current_id, path, cycle) AS (
                    SELECT category_id, parent_category_id, '/' || category_id || '/', 0
                    FROM category_parent_current WHERE parent_category_id IS NOT NULL
                    UNION ALL
                    SELECT walk.start_id, parent.parent_category_id,
                           walk.path || parent.category_id || '/',
                           CASE WHEN instr(walk.path, '/' || parent.category_id || '/') > 0 THEN 1 ELSE 0 END
                    FROM walk
                    JOIN category_parent_current AS parent ON parent.category_id = walk.current_id
                    WHERE walk.current_id IS NOT NULL AND walk.cycle = 0)
                SELECT COUNT(*) FROM walk WHERE cycle = 1;
                """),
            ("category_hierarchy", """
                SELECT COUNT(*) FROM current_category_projection AS category
                WHERE depth <> (length(ancestry_ids) - length(replace(ancestry_ids, '/', '')) - 2)
                   OR ancestry_ids NOT LIKE '%/' || category_id || '/';
                """),
            ("pool_assignment", """
                SELECT COUNT(*) FROM transaction_fact AS fact
                WHERE (SELECT COUNT(*) FROM current_pool_assignment AS current WHERE current.transaction_id = fact.transaction_id) <> 1
                   OR (SELECT COUNT(*) FROM pool_assignment_event AS root WHERE root.transaction_id = fact.transaction_id AND root.previous_event_id IS NULL) <> 1;
                """),
            ("payment_attribution", """
                SELECT COUNT(*) FROM transaction_fact AS fact
                WHERE (SELECT COUNT(*) FROM current_transaction_attribution AS current WHERE current.transaction_id = fact.transaction_id) <> 1
                   OR (SELECT COUNT(*) FROM transaction_attribution_event AS root WHERE root.transaction_id = fact.transaction_id AND root.previous_event_id IS NULL) <> 1;
                """),
            ("category_allocation", """
                SELECT COUNT(*) FROM transaction_fact AS fact
                WHERE (SELECT COUNT(*) FROM current_category_allocation AS current WHERE current.transaction_id = fact.transaction_id) > 1
                   OR (SELECT COUNT(*) FROM category_allocation_event AS root WHERE root.transaction_id = fact.transaction_id AND root.previous_event_id IS NULL) > 1;
                """),
            ("transaction_replacement", """
                WITH RECURSIVE replacements(start_id, current_id, path, cycle) AS (
                    SELECT transaction_id, replacement_transaction_id, '/' || transaction_id || '/', 0
                    FROM transaction_lifecycle_event WHERE replacement_transaction_id IS NOT NULL
                    UNION ALL
                    SELECT replacements.start_id, lifecycle.replacement_transaction_id,
                           replacements.path || lifecycle.transaction_id || '/',
                           CASE WHEN instr(replacements.path, '/' || lifecycle.transaction_id || '/') > 0 THEN 1 ELSE 0 END
                    FROM replacements
                    JOIN transaction_lifecycle_event AS lifecycle ON lifecycle.transaction_id = replacements.current_id
                    WHERE replacements.current_id IS NOT NULL AND replacements.cycle = 0)
                SELECT COUNT(*) FROM replacements WHERE cycle = 1;
                """),
            ("transaction_replacement", """
                SELECT COUNT(*) FROM transaction_lifecycle_event AS lifecycle
                WHERE lifecycle.replacement_transaction_id IS NOT NULL
                  AND EXISTS (SELECT 1 FROM transaction_lifecycle_event AS replacement WHERE replacement.transaction_id = lifecycle.replacement_transaction_id);
                """),
            ("financial_relationship", """
                SELECT COUNT(*)
                FROM financial_relationship AS relationship
                JOIN transaction_fact AS source ON source.transaction_id = relationship.source_transaction_id
                JOIN transaction_fact AS target ON target.transaction_id = relationship.target_transaction_id
                WHERE source.currency_code <> 'ZAR' OR target.currency_code <> 'ZAR'
                   OR source.signed_amount_minor >= 0 OR target.signed_amount_minor <= 0
                   OR relationship.amount_minor <> -source.signed_amount_minor
                   OR relationship.amount_minor <> target.signed_amount_minor
                   OR (relationship.relationship_type = 'transfer' AND source.account_id = target.account_id)
                   OR (relationship.relationship_type = 'refund' AND source.account_id <> target.account_id);
                """),
            ("financial_relationship", """
                SELECT COUNT(*) FROM financial_relationship_current AS relationship
                WHERE relationship.state = 'active'
                  AND (EXISTS (SELECT 1 FROM transaction_lifecycle_event WHERE transaction_id = relationship.source_transaction_id)
                    OR EXISTS (SELECT 1 FROM transaction_lifecycle_event WHERE transaction_id = relationship.target_transaction_id));
                """),
            ("relationship_cardinality", """
                SELECT COUNT(*) FROM (
                    SELECT source_transaction_id AS transaction_id FROM financial_relationship_current WHERE state = 'active'
                    UNION ALL
                    SELECT target_transaction_id FROM financial_relationship_current WHERE state = 'active') AS role
                GROUP BY transaction_id HAVING COUNT(*) > 1;
                """),
            ("relationship_replacement", """
                SELECT COUNT(*)
                FROM relationship_lifecycle_event AS lifecycle
                JOIN financial_relationship AS original ON original.relationship_id = lifecycle.relationship_id
                LEFT JOIN financial_relationship AS replacement ON replacement.relationship_id = lifecycle.replacement_relationship_id
                WHERE lifecycle.event_type = 'replaced'
                  AND (replacement.relationship_id IS NULL OR replacement.relationship_type <> original.relationship_type);
                """),
            ("relationship_replacement", """
                WITH RECURSIVE replacements(start_id, current_id, path, cycle) AS (
                    SELECT relationship_id, replacement_relationship_id, '/' || relationship_id || '/', 0
                    FROM relationship_lifecycle_event WHERE replacement_relationship_id IS NOT NULL
                    UNION ALL
                    SELECT replacements.start_id, lifecycle.replacement_relationship_id,
                           replacements.path || lifecycle.relationship_id || '/',
                           CASE WHEN instr(replacements.path, '/' || lifecycle.relationship_id || '/') > 0 THEN 1 ELSE 0 END
                    FROM replacements
                    JOIN relationship_lifecycle_event AS lifecycle ON lifecycle.relationship_id = replacements.current_id
                    WHERE replacements.current_id IS NOT NULL AND replacements.cycle = 0)
                SELECT COUNT(*) FROM replacements WHERE cycle = 1;
                """),
            ("relationship_replacement", """
                SELECT COUNT(*) FROM relationship_lifecycle_event
                WHERE replacement_relationship_id IS NOT NULL
                GROUP BY replacement_relationship_id HAVING COUNT(*) > 1;
                """),
            ("evidence_link", """
                SELECT COUNT(*)
                FROM evidence_link_event AS link
                JOIN evidence_record AS evidence ON evidence.evidence_id = link.evidence_id
                WHERE link.role = 'confirming' AND evidence.kind <> 'statement_row';
                """),
            ("evidence_link", """
                SELECT COUNT(*)
                FROM evidence_active_confirming_target AS link
                JOIN reconciliation_current_v2 AS decision ON decision.decision_id = link.decision_id
                WHERE link.evidence_id <> decision.evidence_id
                   OR link.transaction_id IS NOT decision.active_transaction_id;
                """),
            ("evidence_link", """
                SELECT COUNT(*) FROM evidence_active_confirming_target
                GROUP BY evidence_id HAVING COUNT(*) > 1;
                """),
            ("reconciliation_decision", """
                SELECT ABS((SELECT COUNT(*) FROM reconciliation_decision) - (SELECT COUNT(*) FROM reconciliation_decision_authority))
                     + (SELECT COUNT(*) FROM reconciliation_decision AS decision WHERE (SELECT COUNT(*) FROM reconciliation_decision_authority AS authority WHERE authority.decision_id = decision.decision_id) <> 1);
                """),
            ("reconciliation_decision", """
                SELECT COUNT(*)
                FROM reconciliation_decision AS decision
                JOIN reconciliation_decision_authority AS authority ON authority.decision_id = decision.decision_id
                WHERE (authority.authority_kind = 'deterministic_policy' AND (decision.deterministic <> 1 OR decision.policy_id IS NULL))
                   OR (authority.authority_kind = 'owner' AND decision.deterministic <> 0)
                   OR (decision.transaction_id IS NOT authority.active_transaction_id AND decision.transaction_id IS NOT NULL)
                   OR decision.decided_at <> authority.recorded_at;
                """),
            ("statement_correction", """
                SELECT COUNT(*) FROM reconciliation_decision_authority AS authority
                WHERE authority.disposition_detail = 'corrected_from_statement'
                  AND (SELECT COUNT(*) FROM statement_correction AS correction WHERE correction.decision_id = authority.decision_id) <> 1;
                """),
            ("statement_correction", """
                SELECT COUNT(*)
                FROM statement_correction AS correction
                JOIN reconciliation_decision_authority AS authority ON authority.decision_id = correction.decision_id
                LEFT JOIN transaction_lifecycle_event AS lifecycle ON lifecycle.lifecycle_event_id = correction.supersession_lifecycle_event_id
                LEFT JOIN current_pool_assignment AS pool ON pool.pool_assignment_event_id = correction.pool_assignment_event_id
                LEFT JOIN current_transaction_attribution AS attribution ON attribution.attribution_event_id = correction.attribution_event_id
                LEFT JOIN current_category_allocation AS category ON category.allocation_event_id = correction.category_allocation_event_id
                WHERE authority.disposition_detail <> 'corrected_from_statement'
                   OR authority.prior_transaction_id <> correction.prior_transaction_id
                   OR authority.active_transaction_id <> correction.active_transaction_id
                   OR authority.statement_authority_basis <> correction.authority_basis
                   OR lifecycle.transaction_id IS NOT correction.prior_transaction_id
                   OR lifecycle.replacement_transaction_id IS NOT correction.active_transaction_id
                   OR lifecycle.reconciliation_decision_id IS NOT correction.decision_id
                   OR pool.transaction_id IS NOT correction.active_transaction_id
                   OR attribution.transaction_id IS NOT correction.active_transaction_id
                   OR (correction.category_resolution = 'carry_forward' AND category.transaction_id IS NOT correction.active_transaction_id)
                   OR (correction.category_resolution = 'uncategorized' AND EXISTS (SELECT 1 FROM current_category_allocation WHERE transaction_id = correction.active_transaction_id));
                """),
            ("statement_correction", """
                SELECT COUNT(*)
                FROM statement_correction AS correction
                WHERE correction.payment_resolution = 'unknown_initialization'
                  AND NOT EXISTS (
                      SELECT 1 FROM statement_unknown_attribution_authority AS authority
                      WHERE authority.attribution_event_id = correction.attribution_event_id
                        AND authority.source_transaction_id = correction.prior_transaction_id
                        AND authority.decision_id = correction.decision_id);
                """),
            ("statement_correction", """
                SELECT COUNT(*)
                FROM statement_correction_relationship_event AS member
                JOIN statement_correction AS correction ON correction.correction_id = member.correction_id
                JOIN relationship_lifecycle_event AS lifecycle ON lifecycle.lifecycle_event_id = member.relationship_lifecycle_event_id
                WHERE lifecycle.event_type <> 'replaced'
                   OR lifecycle.reconciliation_decision_id IS NOT correction.decision_id;
                """),
            ("statement_correction", """
                SELECT COUNT(*) FROM (
                    SELECT member.ordinal,
                           ROW_NUMBER() OVER (PARTITION BY member.correction_id ORDER BY member.ordinal) - 1 AS expected_ordinal
                    FROM statement_correction_relationship_event AS member) AS ordered
                WHERE ordinal <> expected_ordinal;
                """),
            ("statement_correction", """
                SELECT COUNT(*)
                FROM relationship_lifecycle_event AS lifecycle
                JOIN statement_correction AS correction ON correction.decision_id = lifecycle.reconciliation_decision_id
                WHERE lifecycle.event_type = 'replaced'
                  AND (SELECT COUNT(*) FROM statement_correction_relationship_event AS member
                       WHERE member.correction_id = correction.correction_id
                         AND member.relationship_lifecycle_event_id = lifecycle.lifecycle_event_id) <> 1;
                """),
            ("coverage", """
                SELECT COUNT(*) FROM statement_scope_evidence AS member
                JOIN evidence_record AS evidence ON evidence.evidence_id = member.evidence_id
                WHERE evidence.kind <> 'statement_row';
                """),
            ("coverage", """
                SELECT COUNT(*) FROM coverage_entry AS entry
                WHERE entry.active_decision_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM reconciliation_current_v2 AS decision
                      WHERE decision.decision_id = entry.active_decision_id
                        AND decision.evidence_id = entry.evidence_id);
                """),
            ("coverage", """
                SELECT COUNT(*) FROM coverage_entry
                WHERE transaction_id IS NOT NULL
                GROUP BY scope_id, transaction_id HAVING COUNT(*) > 1;
                """),
            ("reconciliation_decision", """
                SELECT COUNT(*) FROM (
                    SELECT decision.evidence_id
                    FROM reconciliation_decision AS decision
                    GROUP BY decision.evidence_id
                    HAVING SUM(CASE WHEN decision.previous_decision_id IS NULL THEN 1 ELSE 0 END) <> 1
                       OR (SELECT COUNT(*) FROM reconciliation_current_v2 AS current
                           WHERE current.evidence_id = decision.evidence_id) <> 1);
                """)
        };

        foreach (var check in checks)
        {
            var violations = await ScalarLongAsync(connection, check.Sql, cancellationToken);
            if (violations != 0)
            {
                return Failure(DurableLedgerErrors.InvariantViolation, check.SafeType, violations);
            }
        }
        return null;
    }

    private static async Task<DurableLedgerVerificationResult?> ValidateHistoryChainsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var history in HistoryChains)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"{history.Id}\", \"{history.Previous}\" FROM \"{history.Table}\";";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var predecessors = new Dictionary<string, string?>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                predecessors.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            }

            var complete = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in predecessors.Keys)
            {
                if (complete.Contains(start)) continue;
                var path = new HashSet<string>(StringComparer.Ordinal);
                var current = start;
                while (predecessors.TryGetValue(current, out var previous))
                {
                    if (complete.Contains(current)) break;
                    if (!path.Add(current)) return Failure(DurableLedgerErrors.InvariantViolation, history.SafeType);
                    if (previous is null) break;
                    current = previous;
                }
                complete.UnionWith(path);
            }
        }
        return null;
    }

    private static async Task<DurableLedgerVerificationResult?> ValidateIdempotencyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT record.idempotency_key, record.operation_id, record.canonical_request_hash,
                   record.actor, record.stable_result, effect.logical_identity, effect.effect_type
            FROM idempotency_record AS record
            LEFT JOIN logical_effect AS effect ON effect.idempotency_key = record.idempotency_key
            ORDER BY record.idempotency_key, effect.logical_identity;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var effectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var storedOperation = reader.GetString(1);
            var requestHash = reader.GetString(2);
            var actor = reader.GetString(3);
            var stableResult = reader.GetString(4);
            if (!TryOperation(storedOperation, out var operation)
                || requestHash.Length != 64
                || requestHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || string.IsNullOrWhiteSpace(actor)
                || !IsStableResult(stableResult))
            {
                return Failure(DurableLedgerErrors.InvariantViolation, "idempotency");
            }

            if (reader.IsDBNull(5))
            {
                if (!MutationOperationsWithoutLogicalEffects.Contains(operation))
                    return Failure(DurableLedgerErrors.InvariantViolation, "idempotency");
                continue;
            }

            var logicalIdentity = reader.GetString(5);
            var effectType = reader.GetString(6);
            effectCounts[key] = effectCounts.GetValueOrDefault(key) + 1;
            if (effectCounts[key] != 1
                || string.IsNullOrWhiteSpace(logicalIdentity)
                || !LogicalEffectOperations.TryGetValue(effectType, out var operations)
                || !operations.Contains(operation))
            {
                return Failure(DurableLedgerErrors.InvariantViolation, "idempotency");
            }
        }
        return null;
    }

    private static bool TryOperation(string storedOperation, out string operation)
    {
        const string prefix = "1.0\n";
        var parsedOperation = storedOperation.StartsWith(prefix, StringComparison.Ordinal)
            ? storedOperation[prefix.Length..]
            : string.Empty;
        operation = parsedOperation;
        return MutationOperationsWithoutLogicalEffects.Contains(parsedOperation)
            || LogicalEffectOperations.Values.Any(operations => operations.Contains(parsedOperation));
    }

    private static bool IsStableResult(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(["errorCode", "value"], StringComparer.Ordinal)
                && root.GetProperty("errorCode").ValueKind == JsonValueKind.Null
                && root.GetProperty("value").ValueKind != JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<DurableActualsReport>> ActualsReportsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var membership = await ActualsProjectionStore.ProjectAsync(connection, null, new(), cancellationToken);
        var activeTransactions = await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM transaction_fact AS fact WHERE NOT EXISTS (SELECT 1 FROM transaction_lifecycle_event AS lifecycle WHERE lifecycle.transaction_id = fact.transaction_id);",
            cancellationToken);
        if (membership.Count != activeTransactions)
        {
            throw new InvalidOperationException(ActualsCalculator.InvariantError);
        }

        var reports = new List<DurableActualsReport>();
        foreach (var grouping in Enum.GetValues<ActualsGroupKind>())
        {
            var calculation = ActualsCalculator.Calculate(membership, grouping);
            if (grouping != ActualsGroupKind.CategorySubtree)
            {
                var grouped = calculation.Groups.Aggregate(ActualsTotals.Zero, (total, group) => total.Add(group.Totals));
                if (grouped != calculation.Totals) throw new InvalidOperationException(ActualsCalculator.InvariantError);
            }

            var cells = calculation.Groups.Select(group => string.Join('|',
                (int)group.Kind,
                group.PoolState is null ? "n" : ((int)group.PoolState.Value).ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                group.PoolId ?? "n",
                group.CategoryState is null ? "n" : ((int)group.CategoryState.Value).ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                group.CategoryId ?? "n",
                group.Totals.NetAccountMovement.MinorUnits,
                group.Totals.ExternalSpend.MinorUnits,
                group.Totals.BudgetActual.MinorUnits)).ToArray();
            reports.Add(new(
                Grouping(grouping),
                calculation.Items.Count,
                calculation.Groups.Count,
                calculation.Totals.NetAccountMovement.MinorUnits,
                calculation.Totals.ExternalSpend.MinorUnits,
                calculation.Totals.BudgetActual.MinorUnits,
                Hash(string.Join('\n', cells))));
        }
        return reports;
    }

    private static async Task<DurableTypeReport> TypeReportAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\";";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(CanonicalRow(reader));
        rows.Sort(StringComparer.Ordinal);
        return new(table, rows.Count, Hash(table + "\n" + string.Join('\n', rows)));
    }

    private static async Task<string> QueryFingerprintAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(CanonicalRow(reader));
        return Hash(string.Join('\n', rows));
    }

    private static string CanonicalRow(SqliteDataReader reader)
    {
        var result = new StringBuilder();
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            if (reader.IsDBNull(ordinal))
            {
                result.Append("N|");
                continue;
            }
            var value = reader.GetValue(ordinal);
            switch (value)
            {
                case long integer:
                    result.Append("I:").Append(integer).Append('|');
                    break;
                case string text:
                    result.Append("T:").Append(text.Length).Append(':').Append(text).Append('|');
                    break;
                case byte[] bytes:
                    result.Append("B:").Append(Convert.ToHexStringLower(bytes)).Append('|');
                    break;
                case double real:
                    result.Append("R:").Append(real.ToString("R", global::System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                    break;
                default:
                    throw new InvalidOperationException("LEDGER-VERIFY-UNSUPPORTED-SQLITE-TYPE");
            }
        }
        return result.ToString();
    }

    private static string NormalizedFingerprint(
        IReadOnlyList<DurableTypeReport> types,
        IReadOnlyList<DurableActualsReport> actuals,
        string hierarchy,
        string replacements,
        string relationships,
        string reconciliation,
        string idempotency,
        IReadOnlyList<string> policies)
    {
        var canonical = new StringBuilder();
        canonical.Append("schema:").Append(CompleteLedgerSchema.CurrentVersion).Append('\n');
        canonical.Append("storage:").Append(StorageContractVersion).Append('\n');
        foreach (var policy in policies) canonical.Append("policy:").Append(policy).Append('\n');
        foreach (var type in types.Where(type => type.Name is not "store_generation" and not "artifact_manifest" and not "migration_metadata"))
        {
            canonical.Append("type:").Append(type.Name).Append(':').Append(type.RowCount).Append(':').Append(type.Fingerprint).Append('\n');
        }
        foreach (var actual in actuals)
        {
            canonical.Append("actuals:").Append(actual.Grouping).Append(':').Append(actual.MemberCount).Append(':')
                .Append(actual.CellCount).Append(':').Append(actual.NetAccountMovementMinor).Append(':')
                .Append(actual.ExternalSpendMinor).Append(':').Append(actual.BudgetActualMinor).Append(':')
                .Append(actual.CellFingerprint).Append('\n');
        }
        canonical.Append("hierarchy:").Append(hierarchy).Append('\n');
        canonical.Append("replacements:").Append(replacements).Append('\n');
        canonical.Append("relationships:").Append(relationships).Append('\n');
        canonical.Append("reconciliation:").Append(reconciliation).Append('\n');
        canonical.Append("idempotency:").Append(idempotency).Append('\n');
        return Hash(canonical.ToString());
    }

    private bool TryRequireProtection(LedgerDb database)
    {
        try
        {
            artifactProtection.RequireOwnerOnlyDirectory(database.DataRoot);
            artifactProtection.RequireOwnerOnlyDirectory(Path.GetDirectoryName(database.GenerationDirectory)!);
            artifactProtection.RequireOwnerOnlyDirectory(database.GenerationDirectory);
            artifactProtection.RequireOwnerOnlyArtifact(database.DatabasePath);
            foreach (var sidecar in SidecarPaths(database).Where(File.Exists)) artifactProtection.RequireOwnerOnlyArtifact(sidecar);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<DurableArtifactReport>> ArtifactReportsAsync(
        LedgerDb database,
        CancellationToken cancellationToken)
    {
        var paths = new[] { database.DatabasePath }.Concat(SidecarPaths(database).Where(File.Exists));
        var reports = new List<DurableArtifactReport>();
        foreach (var path in paths)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            reports.Add(new(
                Path.GetFileName(path),
                stream.Length,
                checksum,
                Convert.ToString((int)File.GetUnixFileMode(path), 8) ?? string.Empty));
        }
        return reports.OrderBy(report => report.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> FileChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static IEnumerable<string> SidecarPaths(LedgerDb database)
    {
        yield return database.DatabasePath + "-wal";
        yield return database.DatabasePath + "-shm";
    }

    private static async Task<bool> IsLiveAsync(LedgerDb database, CancellationToken cancellationToken)
    {
        var currentPath = Path.Combine(database.DataRoot, "CURRENT");
        if (!File.Exists(currentPath)) return false;
        var current = (await File.ReadAllTextAsync(currentPath, cancellationToken)).Trim();
        return string.Equals(current, database.GenerationId, StringComparison.Ordinal);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(LedgerDb database, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA query_only = ON;", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), global::System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), global::System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Grouping(ActualsGroupKind value) => value switch
    {
        ActualsGroupKind.None => "none",
        ActualsGroupKind.Pool => "pool",
        ActualsGroupKind.CategoryDirect => "category_direct",
        ActualsGroupKind.CategorySubtree => "category_subtree",
        ActualsGroupKind.PoolCategory => "pool_category",
        _ => throw new InvalidOperationException(ActualsCalculator.InvariantError)
    };

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DurableLedgerVerificationResult Failure(string code, string safeType, long violations = 1) =>
        DurableLedgerVerificationResult.Failure(code, safeType, violations > int.MaxValue ? int.MaxValue : checked((int)violations));
}
