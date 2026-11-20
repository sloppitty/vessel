using Microsoft.EntityFrameworkCore.Migrations;

namespace Vessel.Persistence.Migrations;

/// <summary>
/// Issue #108: the Activity read model. <c>activity_account_revisions</c> is a per-account history revision, moved at commit
/// by the same changes that move the owner's execution revision (issue #107), so a write to one account leaves the other
/// accounts' stored parts current. <c>activity_projection_accounts</c> holds one part per enabled real account (what it was
/// built from, its counts, coverage and unattributed funding) and <c>activity_operations</c> its operations, which Activity
/// pages, filters and counts in SQL. Nothing is backfilled: an account without a revision row is at revision 0, and parts are
/// built by the first read or by the offline <c>activity-rebuild --apply</c> command.
/// </summary>
public partial class ActivityReadModelTables : Migration
{
    private static readonly string[] EveryChange = ["imported_fills", "funding_payments", "account_leverage_observations",
        "execution_omissions", "imported_forced_events"];

    // Updates of these tables matter only when a column the aggregate's read depends on changes (issue #107's list).
    private static readonly (string Table, string[] Columns)[] Identity =
    [
        ("accounts", ["OwnerId", "IsEnabled", "TradingMode"]),
        ("imported_orders", ["OwnerId", "AccountId", "ContractId", "ExternalOrderId"]),
        ("play_order_links", ["OwnerId", "AccountId", "PlayId", "ImportedOrderId", "State"]),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE activity_account_revisions (
                account_id uuid PRIMARY KEY REFERENCES accounts ("Id") ON DELETE CASCADE,
                revision bigint NOT NULL
            );

            -- Collects the accounts a transaction touched and moves each one's revision once, at commit, in sorted order. The
            -- trigger names sort after activity_execution_*, so the owner's revision row is always locked first, as #107 does.
            CREATE FUNCTION activity_account_bump() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            DECLARE
                account uuid;
                touched uuid[] := ARRAY[]::uuid[];
                pending text := nullif(current_setting('vessel.activity_accounts', true), '');
                bumped text := coalesce(current_setting('vessel.activity_accounts_bumped', true), '');
            BEGIN
                IF TG_TABLE_NAME = 'accounts' THEN
                    IF TG_OP <> 'INSERT' THEN touched := touched || OLD."Id"; END IF;
                    IF TG_OP <> 'DELETE' THEN touched := touched || NEW."Id"; END IF;
                ELSE
                    IF TG_OP <> 'INSERT' THEN touched := touched || OLD."AccountId"; END IF;
                    IF TG_OP <> 'DELETE' THEN touched := touched || NEW."AccountId"; END IF;
                END IF;
                FOREACH account IN ARRAY touched LOOP
                    IF strpos(',' || coalesce(pending, '') || ',', ',' || account::text || ',') = 0 THEN
                        pending := concat_ws(',', pending, account::text);
                        PERFORM set_config('vessel.activity_accounts', pending, true);
                    END IF;
                END LOOP;
                IF pending IS NULL OR pending = bumped THEN RETURN NULL; END IF;
                FOR account IN SELECT DISTINCT value::uuid FROM unnest(string_to_array(pending, ',')) value
                    WHERE strpos(',' || bumped || ',', ',' || value || ',') = 0 ORDER BY 1 LOOP
                    -- A deleted account has no row to move; its revision row went with it.
                    INSERT INTO activity_account_revisions (account_id, revision)
                    SELECT a."Id", 1 FROM accounts a WHERE a."Id" = account
                    ON CONFLICT (account_id) DO UPDATE SET revision = activity_account_revisions.revision + 1;
                END LOOP;
                PERFORM set_config('vessel.activity_accounts_bumped', pending, true);
                RETURN NULL;
            END $$;

            CREATE SEQUENCE activity_projection_builds;

            CREATE TABLE activity_projection_accounts (
                account_id uuid PRIMARY KEY REFERENCES accounts ("Id") ON DELETE CASCADE,
                owner_id uuid NOT NULL,
                build_id bigint NOT NULL,
                revision bigint NOT NULL,
                observation_key text NOT NULL,
                snapshot_at timestamptz,
                snapshot_after timestamptz,
                snapshot_before timestamptz,
                snapshot_pinned boolean NOT NULL,
                fill_count integer NOT NULL,
                operation_count integer NOT NULL,
                contracts text[] NOT NULL,
                coverage text NOT NULL,
                unassigned_funding text NOT NULL,
                built_at timestamptz NOT NULL
            );
            CREATE INDEX ix_activity_projection_accounts_owner ON activity_projection_accounts (owner_id);

            CREATE TABLE activity_operations (
                account_id uuid NOT NULL REFERENCES activity_projection_accounts (account_id) ON DELETE CASCADE,
                operation_id text COLLATE "C" NOT NULL,
                owner_id uuid NOT NULL,
                last_occurred_at timestamptz NOT NULL,
                contract_id text NOT NULL,
                direction text,
                status text NOT NULL,
                partial boolean NOT NULL,
                contract_identity text NOT NULL,
                contract_native boolean NOT NULL,
                basis_notional text,
                basis_quantity text,
                summary text NOT NULL,
                PRIMARY KEY (account_id, operation_id)
            );
            -- Activity's page order (newest first, then account and operation ID, both ordinal), whole scope or one account.
            CREATE INDEX ix_activity_operations_page ON activity_operations (last_occurred_at DESC, account_id, operation_id);
            CREATE INDEX ix_activity_operations_account_page ON activity_operations (account_id, last_occurred_at DESC, operation_id);
            """);
        foreach (var table in EveryChange)
            migrationBuilder.Sql($"""
                CREATE CONSTRAINT TRIGGER activity_projection_commit AFTER INSERT OR UPDATE OR DELETE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION activity_account_bump();
                """);
        foreach (var (table, columns) in Identity)
        {
            var changed = string.Join(" OR ", columns.Select(c => $"OLD.\"{c}\" IS DISTINCT FROM NEW.\"{c}\""));
            migrationBuilder.Sql($"""
                CREATE CONSTRAINT TRIGGER activity_projection_commit AFTER INSERT OR DELETE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION activity_account_bump();
                CREATE CONSTRAINT TRIGGER activity_projection_update AFTER UPDATE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ({changed}) EXECUTE FUNCTION activity_account_bump();
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in EveryChange)
            migrationBuilder.Sql($"DROP TRIGGER activity_projection_commit ON {table};");
        foreach (var (table, _) in Identity)
            migrationBuilder.Sql($"DROP TRIGGER activity_projection_commit ON {table}; DROP TRIGGER activity_projection_update ON {table};");
        migrationBuilder.Sql("""
            DROP TABLE activity_operations;
            DROP TABLE activity_projection_accounts;
            DROP SEQUENCE activity_projection_builds;
            DROP FUNCTION activity_account_bump();
            DROP TABLE activity_account_revisions;
            """);
    }
}
