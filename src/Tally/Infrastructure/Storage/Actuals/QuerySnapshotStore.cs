using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Actuals;

namespace Tally.Infrastructure.Storage.Actuals;

public sealed class QuerySnapshotStore(LedgerDb database, LedgerConnectionFactory connectionFactory)
{
    public const string ContractVersion = "1.0";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    internal async Task<SnapshotPage> CreateAsync(
        ActualsFilter filter,
        string filterHash,
        int pageSize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var createdAt = Utc(now);
        var expiresAt = Utc(now.Add(Lifetime));
        await DeleteExpiredAsync(connection, transaction, createdAt, cancellationToken);

        var membership = await ActualsProjectionStore.ProjectAsync(connection, transaction, filter, cancellationToken);
        var calculation = ActualsCalculator.Calculate(membership, filter.GroupBy);
        var hierarchyFingerprint = await HierarchyFingerprintAsync(connection, transaction, cancellationToken);
        var generationFingerprint = GenerationFingerprint(database);
        var snapshotId = LedgerId.New().ToString();

        await InsertHeaderAsync(
            connection,
            transaction,
            snapshotId,
            filterHash,
            generationFingerprint,
            hierarchyFingerprint,
            createdAt,
            expiresAt,
            calculation.Totals,
            cancellationToken);
        await InsertItemsAsync(connection, transaction, snapshotId, calculation.Items, cancellationToken);
        await InsertGroupsAsync(connection, transaction, snapshotId, calculation.Groups, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return PageFromCalculation(
            snapshotId,
            expiresAt,
            filterHash,
            generationFingerprint,
            hierarchyFingerprint,
            pageSize,
            calculation);
    }

    internal async Task<SnapshotReadResult> ReadAsync(
        ActualsCursorPayload cursor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Ledger storage requires Linux host protections.");
        if (!string.Equals(cursor.GenerationFingerprint, GenerationFingerprint(database), StringComparison.Ordinal))
        {
            return SnapshotReadResult.Failure(ActualsErrors.GenerationMismatch);
        }

        if (!TryUtc(cursor.ExpiresAt, out var cursorExpiry) || cursorExpiry <= now.ToUniversalTime())
        {
            return SnapshotReadResult.Failure(ActualsErrors.SnapshotExpired);
        }

        await using var connection = await connectionFactory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, cancellationToken);
        var header = await ReadHeaderAsync(connection, cursor.SnapshotId, cancellationToken);
        if (header is null) return SnapshotReadResult.Failure(ActualsErrors.SnapshotNotFound);
        if (!string.Equals(cursor.ContractVersion, header.ContractVersion, StringComparison.Ordinal))
        {
            return SnapshotReadResult.Failure(ActualsErrors.ContractMismatch);
        }
        if (!string.Equals(cursor.FilterHash, header.FilterHash, StringComparison.Ordinal))
        {
            return SnapshotReadResult.Failure(ActualsErrors.CursorFilterMismatch);
        }
        if (!string.Equals(cursor.GenerationFingerprint, header.GenerationFingerprint, StringComparison.Ordinal))
        {
            return SnapshotReadResult.Failure(ActualsErrors.GenerationMismatch);
        }
        if (!string.Equals(cursor.CategoryHierarchyFingerprint, header.HierarchyFingerprint, StringComparison.Ordinal))
        {
            return SnapshotReadResult.Failure(ActualsErrors.HierarchyMismatch);
        }
        if (!string.Equals(cursor.ExpiresAt, header.ExpiresAt, StringComparison.Ordinal)
            || !TryUtc(header.ExpiresAt, out var storedExpiry)
            || storedExpiry <= now.ToUniversalTime())
        {
            return SnapshotReadResult.Failure(ActualsErrors.SnapshotExpired);
        }
        if (cursor.NextOrdinal < 0 || cursor.NextOrdinal >= header.TotalCount || cursor.PageSize is < 1 or > 500)
        {
            return SnapshotReadResult.Failure(ActualsErrors.CursorInvalid);
        }

        var items = await ReadItemsAsync(connection, cursor.SnapshotId, cursor.NextOrdinal, cursor.PageSize, cancellationToken);
        if (items.Count == 0) return SnapshotReadResult.Failure(ActualsErrors.CursorInvalid);
        var groups = await ReadGroupsAsync(connection, cursor.SnapshotId, cancellationToken);
        var nextOrdinal = cursor.NextOrdinal + items.Count;
        var result = new ActualsQueryResult(
            cursor.SnapshotId,
            header.ExpiresAt,
            header.TotalCount,
            items,
            Totals(header.NetMinor, header.SpendMinor, header.BudgetMinor),
            groups,
            null);
        return SnapshotReadResult.Success(new(
            result,
            nextOrdinal < header.TotalCount ? nextOrdinal : null,
            cursor.PageSize,
            header.FilterHash,
            header.GenerationFingerprint,
            header.HierarchyFingerprint));
    }

    internal static string GenerationFingerprint(LedgerDb value) => Hash($"{value.GenerationId}|{CompleteLedgerSchema.CurrentVersion}");

    private static SnapshotPage PageFromCalculation(
        string snapshotId,
        string expiresAt,
        string filterHash,
        string generationFingerprint,
        string hierarchyFingerprint,
        int pageSize,
        ActualsCalculation calculation)
    {
        var items = calculation.Items.Take(pageSize).Select((item, ordinal) => Item(ordinal, item)).ToArray();
        var result = new ActualsQueryResult(
            snapshotId,
            expiresAt,
            calculation.Items.Count,
            items,
            Totals(calculation.Totals),
            calculation.Groups.Select(Group).ToArray(),
            null);
        return new(
            result,
            items.Length < calculation.Items.Count ? items.Length : null,
            pageSize,
            filterHash,
            generationFingerprint,
            hierarchyFingerprint);
    }

    private static async Task DeleteExpiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM query_snapshot WHERE expires_at <= $now;";
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        string filterHash,
        string generationFingerprint,
        string hierarchyFingerprint,
        string createdAt,
        string expiresAt,
        ActualsTotals totals,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO query_snapshot (
                snapshot_id, contract_version, canonical_filter_hash, generation_fingerprint,
                category_hierarchy_fingerprint, persistence_scope, created_at, expires_at,
                net_account_movement_minor, external_spend_minor, budget_actual_minor)
            VALUES ($snapshotId, $contractVersion, $filterHash, $generationFingerprint,
                    $hierarchyFingerprint, 'ephemeral', $createdAt, $expiresAt,
                    $net, $spend, $budget);
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$contractVersion", ContractVersion);
        command.Parameters.AddWithValue("$filterHash", filterHash);
        command.Parameters.AddWithValue("$generationFingerprint", generationFingerprint);
        command.Parameters.AddWithValue("$hierarchyFingerprint", hierarchyFingerprint);
        command.Parameters.AddWithValue("$createdAt", createdAt);
        command.Parameters.AddWithValue("$expiresAt", expiresAt);
        command.Parameters.AddWithValue("$net", totals.NetAccountMovement.MinorUnits);
        command.Parameters.AddWithValue("$spend", totals.ExternalSpend.MinorUnits);
        command.Parameters.AddWithValue("$budget", totals.BudgetActual.MinorUnits);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        IReadOnlyList<CalculatedActualsItem> items,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < items.Count; ordinal++)
        {
            var item = items[ordinal];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO query_snapshot_item (
                    snapshot_id, ordinal, transaction_id, effective_date, category_state, category_id,
                    frozen_ancestry_ids_json, pool_state, pool_id, instrument_state, instrument_id,
                    cardholder_state, cardholder_id, evidence_kinds_json, reconciliation_state,
                    relationship_state, net_account_movement_minor, external_spend_minor, budget_actual_minor)
                VALUES ($snapshotId, $ordinal, $transactionId, $effectiveDate, $categoryState, $categoryId,
                        $ancestry, $poolState, $poolId, $instrumentState, $instrumentId,
                        $cardholderState, $cardholderId, $evidenceKinds, $reconciliationState,
                        $relationshipState, $net, $spend, $budget);
                """;
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$transactionId", item.Item.TransactionId);
            command.Parameters.AddWithValue("$effectiveDate", item.Item.EffectiveDate.ToString());
            command.Parameters.AddWithValue("$categoryState", CategoryState(item.Item.CategoryState));
            command.Parameters.AddWithValue("$categoryId", Db(item.Item.CategoryId));
            command.Parameters.AddWithValue("$ancestry", JsonSerializer.Serialize(item.Item.CurrentAncestryIds.ToArray(), ActualsJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$poolState", PoolState(item.Item.PoolState));
            command.Parameters.AddWithValue("$poolId", Db(item.Item.PoolId));
            command.Parameters.AddWithValue("$instrumentState", KnowledgeState(item.Item.InstrumentState));
            command.Parameters.AddWithValue("$instrumentId", Db(item.Item.InstrumentId));
            command.Parameters.AddWithValue("$cardholderState", KnowledgeState(item.Item.CardholderState));
            command.Parameters.AddWithValue("$cardholderId", Db(item.Item.CardholderId));
            command.Parameters.AddWithValue("$evidenceKinds", EvidenceJson(item.Item.EvidenceKinds));
            command.Parameters.AddWithValue("$reconciliationState", ReconciliationState(item.Item.ReconciliationState));
            command.Parameters.AddWithValue("$relationshipState", RelationshipState(item.Item.RelationshipState));
            command.Parameters.AddWithValue("$net", item.Contribution.NetAccountMovement.MinorUnits);
            command.Parameters.AddWithValue("$spend", item.Contribution.ExternalSpend.MinorUnits);
            command.Parameters.AddWithValue("$budget", item.Contribution.BudgetActual.MinorUnits);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertGroupsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        IReadOnlyList<ActualsGroup> groups,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < groups.Count; ordinal++)
        {
            var group = groups[ordinal];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO query_snapshot_group (
                    snapshot_id, ordinal, group_kind, pool_bucket, pool_id, category_bucket, category_id,
                    net_account_movement_minor, external_spend_minor, budget_actual_minor)
                VALUES ($snapshotId, $ordinal, $kind, $poolBucket, $poolId, $categoryBucket, $categoryId,
                        $net, $spend, $budget);
                """;
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$kind", GroupKind(group.Kind));
            command.Parameters.AddWithValue("$poolBucket", group.PoolState is null ? "not_applicable" : PoolState(group.PoolState.Value));
            command.Parameters.AddWithValue("$poolId", Db(group.PoolId));
            command.Parameters.AddWithValue("$categoryBucket", group.CategoryState is null ? "not_applicable" : CategoryState(group.CategoryState.Value));
            command.Parameters.AddWithValue("$categoryId", Db(group.CategoryId));
            command.Parameters.AddWithValue("$net", group.Totals.NetAccountMovement.MinorUnits);
            command.Parameters.AddWithValue("$spend", group.Totals.ExternalSpend.MinorUnits);
            command.Parameters.AddWithValue("$budget", group.Totals.BudgetActual.MinorUnits);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<SnapshotHeader?> ReadHeaderAsync(
        SqliteConnection connection,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot.contract_version, snapshot.canonical_filter_hash, snapshot.generation_fingerprint,
                   snapshot.category_hierarchy_fingerprint, snapshot.expires_at,
                   snapshot.net_account_movement_minor, snapshot.external_spend_minor, snapshot.budget_actual_minor,
                   (SELECT COUNT(*) FROM query_snapshot_item AS item WHERE item.snapshot_id = snapshot.snapshot_id)
            FROM query_snapshot AS snapshot
            WHERE snapshot.snapshot_id = $snapshotId;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), checked((int)reader.GetInt64(8)))
            : null;
    }

    private static async Task<IReadOnlyList<ActualsPageItem>> ReadItemsAsync(
        SqliteConnection connection,
        string snapshotId,
        int nextOrdinal,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ordinal, transaction_id, effective_date, category_state, category_id,
                   frozen_ancestry_ids_json, pool_state, pool_id, instrument_state, instrument_id,
                   cardholder_state, cardholder_id, evidence_kinds_json, reconciliation_state,
                   relationship_state, net_account_movement_minor, external_spend_minor, budget_actual_minor
            FROM query_snapshot_item
            WHERE snapshot_id = $snapshotId AND ordinal >= $nextOrdinal
            ORDER BY ordinal
            LIMIT $pageSize;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$nextOrdinal", nextOrdinal);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ActualsPageItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                CategoryState(reader.GetString(3)),
                Optional(reader, 4),
                JsonSerializer.Deserialize(reader.GetString(5), ActualsJsonContext.Default.StringArray)!,
                PoolState(reader.GetString(6)),
                Optional(reader, 7),
                KnowledgeState(reader.GetString(8)),
                Optional(reader, 9),
                KnowledgeState(reader.GetString(10)),
                Optional(reader, 11),
                EvidenceKinds(reader.GetString(12)),
                ReconciliationState(reader.GetString(13)),
                RelationshipRole(reader.GetString(14)),
                Totals(reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17))));
        }
        return items;
    }

    private static async Task<IReadOnlyList<ActualsGroupResult>> ReadGroupsAsync(
        SqliteConnection connection,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_kind, pool_bucket, pool_id, category_bucket, category_id,
                   net_account_movement_minor, external_spend_minor, budget_actual_minor
            FROM query_snapshot_group
            WHERE snapshot_id = $snapshotId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<ActualsGroupResult>();
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new(
                Grouping(reader.GetString(0)),
                reader.GetString(1) == "not_applicable" ? null : PoolState(reader.GetString(1)),
                Optional(reader, 2),
                reader.GetString(3) == "not_applicable" ? null : CategoryState(reader.GetString(3)),
                Optional(reader, 4),
                Totals(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7))));
        }
        return groups;
    }

    private static async Task<string> HierarchyFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT category_id, COALESCE(parent_category_id, ''), ancestry_ids, status
            FROM current_category_projection
            ORDER BY category_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var canonical = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var ordinal = 0; ordinal < 4; ordinal++)
            {
                var value = reader.GetString(ordinal);
                canonical.Append(value.Length).Append(':').Append(value).Append('|');
            }
        }
        return Hash(canonical.ToString());
    }

    private static ActualsPageItem Item(int ordinal, CalculatedActualsItem value) => new(
        ordinal,
        value.Item.TransactionId,
        value.Item.EffectiveDate.ToString(),
        value.Item.CategoryState,
        value.Item.CategoryId,
        value.Item.CurrentAncestryIds,
        value.Item.PoolState,
        value.Item.PoolId,
        value.Item.InstrumentState,
        value.Item.InstrumentId,
        value.Item.CardholderState,
        value.Item.CardholderId,
        value.Item.EvidenceKinds,
        value.Item.ReconciliationState,
        RelationshipRole(value.Item.RelationshipState),
        Totals(value.Contribution));

    private static ActualsGroupResult Group(ActualsGroup value) => new(
        Grouping(value.Kind),
        value.PoolState,
        value.PoolId,
        value.CategoryState,
        value.CategoryId,
        Totals(value.Totals));

    private static ActualsTotalsResult Totals(ActualsTotals value) => Totals(
        value.NetAccountMovement.MinorUnits,
        value.ExternalSpend.MinorUnits,
        value.BudgetActual.MinorUnits);

    private static ActualsTotalsResult Totals(long net, long spend, long budget) => new(
        Money.FromMinorUnits(net).ToString(),
        Money.FromMinorUnits(spend).ToString(),
        Money.FromMinorUnits(budget).ToString());

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryUtc(string value, out DateTimeOffset parsed) => DateTimeOffset.TryParseExact(
        value,
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
        out parsed);
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string? Optional(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string CategoryState(TransactionCategoryState value) => value switch
    {
        TransactionCategoryState.Categorized => "categorized",
        TransactionCategoryState.Uncategorized => "uncategorized",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static TransactionCategoryState CategoryState(string value) => value switch
    {
        "categorized" => TransactionCategoryState.Categorized,
        "uncategorized" => TransactionCategoryState.Uncategorized,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static string PoolState(TransactionPoolState value) => value switch
    {
        TransactionPoolState.Assigned => "assigned",
        TransactionPoolState.Unassigned => "unassigned",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static TransactionPoolState PoolState(string value) => value switch
    {
        "assigned" => TransactionPoolState.Assigned,
        "unassigned" => TransactionPoolState.Unassigned,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static string KnowledgeState(TransactionKnowledgeState value) => value switch
    {
        TransactionKnowledgeState.Known => "known",
        TransactionKnowledgeState.Unknown => "unknown",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static TransactionKnowledgeState KnowledgeState(string value) => value switch
    {
        "known" => TransactionKnowledgeState.Known,
        "unknown" => TransactionKnowledgeState.Unknown,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static string ReconciliationState(TransactionReconciliationState value) => value switch
    {
        TransactionReconciliationState.RecordedUnreconciled => "recorded_unreconciled",
        TransactionReconciliationState.StatementReconciled => "statement_reconciled",
        TransactionReconciliationState.StatementOnly => "statement_only",
        TransactionReconciliationState.RecordedAbsentFromStatement => "recorded_absent_from_statement",
        TransactionReconciliationState.AmbiguousMatch => "ambiguous_match",
        TransactionReconciliationState.OwnerConfirmedMatch => "owner_confirmed_match",
        TransactionReconciliationState.ReconciliationException => "reconciliation_exception",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static TransactionReconciliationState ReconciliationState(string value) => value switch
    {
        "recorded_unreconciled" => TransactionReconciliationState.RecordedUnreconciled,
        "statement_reconciled" => TransactionReconciliationState.StatementReconciled,
        "statement_only" => TransactionReconciliationState.StatementOnly,
        "recorded_absent_from_statement" => TransactionReconciliationState.RecordedAbsentFromStatement,
        "ambiguous_match" => TransactionReconciliationState.AmbiguousMatch,
        "owner_confirmed_match" => TransactionReconciliationState.OwnerConfirmedMatch,
        "reconciliation_exception" => TransactionReconciliationState.ReconciliationException,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static string RelationshipState(ActualsRelationshipState value) => value switch
    {
        ActualsRelationshipState.None => "none",
        ActualsRelationshipState.TransferOutflow => "transfer_outflow",
        ActualsRelationshipState.TransferInflow => "transfer_inflow",
        ActualsRelationshipState.RefundOriginal => "refund_original",
        ActualsRelationshipState.RefundCredit => "refund_credit",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static ActualsRelationshipRole RelationshipRole(ActualsRelationshipState value) => value switch
    {
        ActualsRelationshipState.None => ActualsRelationshipRole.None,
        ActualsRelationshipState.TransferOutflow => ActualsRelationshipRole.TransferOutflow,
        ActualsRelationshipState.TransferInflow => ActualsRelationshipRole.TransferInflow,
        ActualsRelationshipState.RefundOriginal => ActualsRelationshipRole.RefundOriginal,
        ActualsRelationshipState.RefundCredit => ActualsRelationshipRole.RefundCredit,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static ActualsRelationshipRole RelationshipRole(string value) => value switch
    {
        "none" => ActualsRelationshipRole.None,
        "transfer_outflow" => ActualsRelationshipRole.TransferOutflow,
        "transfer_inflow" => ActualsRelationshipRole.TransferInflow,
        "refund_original" => ActualsRelationshipRole.RefundOriginal,
        "refund_credit" => ActualsRelationshipRole.RefundCredit,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static string GroupKind(ActualsGroupKind value) => value switch
    {
        ActualsGroupKind.None => "none",
        ActualsGroupKind.Pool => "pool",
        ActualsGroupKind.CategoryDirect => "category_direct",
        ActualsGroupKind.CategorySubtree => "category_subtree",
        ActualsGroupKind.PoolCategory => "pool_category",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static ActualsGrouping Grouping(ActualsGroupKind value) => value switch
    {
        ActualsGroupKind.None => ActualsGrouping.None,
        ActualsGroupKind.Pool => ActualsGrouping.Pool,
        ActualsGroupKind.CategoryDirect => ActualsGrouping.CategoryDirect,
        ActualsGroupKind.CategorySubtree => ActualsGrouping.CategorySubtree,
        ActualsGroupKind.PoolCategory => ActualsGrouping.PoolCategory,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static ActualsGrouping Grouping(string value) => value switch
    {
        "none" => ActualsGrouping.None,
        "pool" => ActualsGrouping.Pool,
        "category_direct" => ActualsGrouping.CategoryDirect,
        "category_subtree" => ActualsGrouping.CategorySubtree,
        "pool_category" => ActualsGrouping.PoolCategory,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };

    private static string EvidenceJson(IReadOnlyList<EvidenceKind> values) => "[" + string.Join(',', values.Select(value => $"\"{EvidenceKindValue(value)}\"")) + "]";
    private static IReadOnlyList<EvidenceKind> EvidenceKinds(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(value => EvidenceKindValue(value.GetString()!)).ToArray();
    }
    private static string EvidenceKindValue(EvidenceKind value) => value switch
    {
        EvidenceKind.AgentCapture => "agent_capture",
        EvidenceKind.StatementRow => "statement_row",
        EvidenceKind.Receipt => "receipt",
        EvidenceKind.ExternalDocument => "external_document",
        EvidenceKind.OwnerAssertion => "owner_assertion",
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };
    private static EvidenceKind EvidenceKindValue(string value) => value switch
    {
        "agent_capture" => EvidenceKind.AgentCapture,
        "statement_row" => EvidenceKind.StatementRow,
        "receipt" => EvidenceKind.Receipt,
        "external_document" => EvidenceKind.ExternalDocument,
        "owner_assertion" => EvidenceKind.OwnerAssertion,
        _ => throw new InvalidOperationException(ActualsErrors.Invariant)
    };

    private sealed record SnapshotHeader(
        string ContractVersion,
        string FilterHash,
        string GenerationFingerprint,
        string HierarchyFingerprint,
        string ExpiresAt,
        long NetMinor,
        long SpendMinor,
        long BudgetMinor,
        int TotalCount);
}

internal sealed record SnapshotPage(
    ActualsQueryResult Result,
    int? NextOrdinal,
    int PageSize,
    string FilterHash,
    string GenerationFingerprint,
    string HierarchyFingerprint);

internal sealed record SnapshotReadResult(SnapshotPage? Page, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
    public static SnapshotReadResult Success(SnapshotPage page) => new(page, null);
    public static SnapshotReadResult Failure(string errorCode) => new(null, errorCode);
}
