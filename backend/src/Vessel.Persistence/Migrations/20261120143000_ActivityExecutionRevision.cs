using Microsoft.EntityFrameworkCore.Migrations;

namespace Vessel.Persistence.Migrations;

/// <summary>
/// Issue #107: a second counter beside the Activity facts revision, moved only by the facts the expensive aggregate reads
/// from history (fills, funding payments, leverage observations, omission markers, forced events, and the account, order
/// and link columns that decide which of them are read and which Play they belong to). Snapshots, positions, wallet
/// balances, funding coverage and sync state still move the facts revision, so the funding proof's fence is unchanged,
/// but a monitor check that only saves a newer observation leaves this counter alone and the cached aggregate is rebased
/// onto the new observations instead of being read and walked again.
/// </summary>
public partial class ActivityExecutionRevision : Migration
{
    private static readonly string[] EveryChange = ["imported_fills", "funding_payments", "account_leverage_observations",
        "execution_omissions", "imported_forced_events"];

    // Updates of these tables matter only when a column the aggregate's read depends on changes.
    private static readonly (string Table, string[] Columns)[] Identity =
    [
        ("accounts", ["OwnerId", "IsEnabled", "TradingMode"]),
        ("imported_orders", ["OwnerId", "AccountId", "ContractId", "ExternalOrderId"]),
        ("play_order_links", ["OwnerId", "AccountId", "PlayId", "ImportedOrderId", "State"]),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE activity_facts_revisions ADD COLUMN execution_revision bigint NOT NULL DEFAULT 1;

            -- The same owner collection and sorted, once-per-transaction commit-time locking as activity_facts_bump, with its
            -- own transaction-local set. A new owner's row starts at revision 0 because activity_revision_commit, which every
            -- table here also carries, makes it 1 in the same commit.
            CREATE FUNCTION activity_execution_bump() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            DECLARE
                owner uuid;
                owners text := nullif(current_setting('vessel.activity_owners', true), '');
                bumped text := coalesce(current_setting('vessel.activity_execution_owners', true), '');
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    IF strpos(',' || coalesce(owners, '') || ',', ',' || OLD."OwnerId"::text || ',') = 0 THEN
                        owners := concat_ws(',', owners, OLD."OwnerId"::text);
                        PERFORM set_config('vessel.activity_owners', owners, true);
                    END IF;
                END IF;
                IF TG_OP <> 'DELETE' THEN
                    IF strpos(',' || coalesce(owners, '') || ',', ',' || NEW."OwnerId"::text || ',') = 0 THEN
                        owners := concat_ws(',', owners, NEW."OwnerId"::text);
                        PERFORM set_config('vessel.activity_owners', owners, true);
                    END IF;
                END IF;
                IF owners IS NULL OR owners = bumped THEN RETURN NULL; END IF;
                FOR owner IN SELECT DISTINCT value::uuid FROM unnest(string_to_array(owners, ',')) value
                    WHERE strpos(',' || bumped || ',', ',' || value || ',') = 0 ORDER BY 1 LOOP
                    INSERT INTO activity_facts_revisions (owner_id, revision, execution_revision) VALUES (owner, 0, 1)
                    ON CONFLICT (owner_id) DO UPDATE SET execution_revision = activity_facts_revisions.execution_revision + 1;
                END LOOP;
                PERFORM set_config('vessel.activity_execution_owners', owners, true);
                RETURN NULL;
            END $$;
            """);
        foreach (var table in EveryChange)
            migrationBuilder.Sql($"""
                CREATE CONSTRAINT TRIGGER activity_execution_commit AFTER INSERT OR UPDATE OR DELETE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION activity_execution_bump();
                """);
        foreach (var (table, columns) in Identity)
        {
            var changed = string.Join(" OR ", columns.Select(c => $"OLD.\"{c}\" IS DISTINCT FROM NEW.\"{c}\""));
            migrationBuilder.Sql($"""
                CREATE CONSTRAINT TRIGGER activity_execution_commit AFTER INSERT OR DELETE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION activity_execution_bump();
                CREATE CONSTRAINT TRIGGER activity_execution_update AFTER UPDATE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ({changed}) EXECUTE FUNCTION activity_execution_bump();
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in EveryChange)
            migrationBuilder.Sql($"DROP TRIGGER activity_execution_commit ON {table};");
        foreach (var (table, _) in Identity)
            migrationBuilder.Sql($"DROP TRIGGER activity_execution_commit ON {table}; DROP TRIGGER activity_execution_update ON {table};");
        migrationBuilder.Sql("DROP FUNCTION activity_execution_bump(); ALTER TABLE activity_facts_revisions DROP COLUMN execution_revision;");
    }
}
