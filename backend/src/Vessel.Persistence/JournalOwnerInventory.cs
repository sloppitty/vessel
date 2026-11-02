using Npgsql;
using Vessel.Application.Ownership;

namespace Vessel.Persistence;

/// <summary>Offline reconciliation sees all journal owners, even those without an event cursor.</summary>
public sealed class JournalOwnerInventory(NpgsqlDataSource data) : IJournalOwnerInventory
{
    public async Task<IReadOnlyList<Guid>> DistinctOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        await using var command = data.CreateCommand("""
            SELECT owner_id FROM (
                SELECT "OwnerId" AS owner_id FROM accounts
                UNION SELECT "OwnerId" FROM portfolios
                UNION SELECT "OwnerId" FROM account_credentials
                UNION SELECT "OwnerId" FROM account_snapshots
                UNION SELECT "OwnerId" FROM account_positions
                UNION SELECT "OwnerId" FROM account_stablecoins
                UNION SELECT "OwnerId" FROM imported_fills
                UNION SELECT "OwnerId" FROM imported_orders
                UNION SELECT "OwnerId" FROM funding_payments
                UNION SELECT "OwnerId" FROM account_funding_states
                UNION SELECT "OwnerId" FROM plays
                UNION SELECT "OwnerId" FROM play_evidence
                UNION SELECT "OwnerId" FROM play_plan_revisions
                UNION SELECT "OwnerId" FROM play_status_changes
                UNION SELECT "OwnerId" FROM play_order_links
                UNION SELECT "OwnerId" FROM play_review_revisions
                UNION SELECT "OwnerId" FROM manual_executions
                UNION SELECT "OwnerId" FROM manual_execution_history
                UNION SELECT "OwnerId" FROM diary_requests
                UNION SELECT "OwnerId" FROM sizing_settings
                UNION SELECT "OwnerId" FROM risk_settings
                UNION SELECT "OwnerId" FROM monitor_accounts
                UNION SELECT "OwnerId" FROM monitor_exit_watches
                UNION SELECT "OwnerId" FROM play_discrepancies
                UNION SELECT owner_id FROM event_cursors
                UNION SELECT owner_id FROM events
                UNION SELECT owner_id FROM event_dedup
                UNION SELECT owner_id FROM event_baselines
                UNION SELECT owner_id FROM event_fills
                UNION SELECT owner_id FROM event_subscriptions
                UNION SELECT owner_id FROM event_receipts
                UNION SELECT owner_id FROM notification_preferences
                UNION SELECT owner_id FROM notifications
                UNION SELECT owner_id FROM notification_sources
                UNION SELECT owner_id FROM notification_deliveries
                UNION SELECT owner_id FROM notification_cursors
            ) owners WHERE (@after IS NULL OR owner_id>@after) ORDER BY owner_id LIMIT @limit
            """);
        command.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var owners = new List<Guid>();
        while (await rows.ReadAsync(cancellationToken)) owners.Add(rows.GetGuid(0));
        return owners;
    }
}
