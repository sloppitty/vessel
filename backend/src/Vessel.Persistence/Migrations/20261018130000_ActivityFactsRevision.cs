using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Vessel.Persistence.Migrations;

[DbContext(typeof(VesselDbContext))]
[Migration("20261018130000_ActivityFactsRevision")]
public sealed class ActivityFactsRevision : Migration
{
    private static readonly string[] Tables = ["accounts", "imported_fills", "imported_orders", "play_order_links",
        "funding_payments", "account_funding_states", "account_snapshots", "account_positions", "account_stablecoins"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE activity_facts_revisions (owner_id uuid PRIMARY KEY, revision bigint NOT NULL);
            INSERT INTO activity_facts_revisions (owner_id, revision) SELECT DISTINCT "OwnerId", 1 FROM accounts;

            -- Statement transition tables collect all owners before any deferred revision lock is taken.
            CREATE FUNCTION activity_facts_collect() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE owners text;
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    SELECT string_agg(DISTINCT "OwnerId"::text, ',') INTO owners FROM activity_new;
                ELSIF TG_OP = 'DELETE' THEN
                    SELECT string_agg(DISTINCT "OwnerId"::text, ',') INTO owners FROM activity_old;
                ELSE
                    SELECT string_agg(DISTINCT "OwnerId"::text, ',') INTO owners FROM
                        (SELECT "OwnerId" FROM activity_old UNION SELECT "OwnerId" FROM activity_new) affected;
                END IF;
                IF owners IS NOT NULL THEN
                    SELECT string_agg(DISTINCT value, ',') INTO owners FROM unnest(string_to_array(concat_ws(',',
                        nullif(current_setting('vessel.activity_owners', true), ''), owners), ',')) value;
                    PERFORM set_config('vessel.activity_owners', owners, true);
                END IF;
                RETURN NULL;
            END $$;

            CREATE FUNCTION activity_facts_bump() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE owner uuid;
            BEGIN
                IF current_setting('vessel.activity_bumped', true) = 'true' THEN RETURN NULL; END IF;
                PERFORM set_config('vessel.activity_bumped', 'true', true);
                -- Sorted even for offline transactions that touch several owners.
                FOR owner IN SELECT DISTINCT unnest(string_to_array(current_setting('vessel.activity_owners'), ','))::uuid ORDER BY 1 LOOP
                    INSERT INTO activity_facts_revisions (owner_id, revision) VALUES (owner, 1)
                    ON CONFLICT (owner_id) DO UPDATE SET revision = activity_facts_revisions.revision + 1;
                END LOOP;
                RETURN NULL;
            END $$;
            """);
        foreach (var table in Tables)
        {
            migrationBuilder.Sql($"""
                CREATE TRIGGER activity_collect_insert AFTER INSERT ON {table}
                    REFERENCING NEW TABLE AS activity_new FOR EACH STATEMENT EXECUTE FUNCTION activity_facts_collect();
                CREATE TRIGGER activity_collect_update AFTER UPDATE ON {table}
                    REFERENCING OLD TABLE AS activity_old NEW TABLE AS activity_new FOR EACH STATEMENT EXECUTE FUNCTION activity_facts_collect();
                CREATE TRIGGER activity_collect_delete AFTER DELETE ON {table}
                    REFERENCING OLD TABLE AS activity_old FOR EACH STATEMENT EXECUTE FUNCTION activity_facts_collect();
                CREATE CONSTRAINT TRIGGER activity_revision_commit AFTER INSERT OR UPDATE OR DELETE ON {table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION activity_facts_bump();
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
            migrationBuilder.Sql($"""
                DROP TRIGGER activity_collect_insert ON {table};
                DROP TRIGGER activity_collect_update ON {table};
                DROP TRIGGER activity_collect_delete ON {table};
                DROP TRIGGER activity_revision_commit ON {table};
                """);
        migrationBuilder.Sql("DROP FUNCTION activity_facts_collect(); DROP FUNCTION activity_facts_bump(); DROP TABLE activity_facts_revisions;");
    }
}
