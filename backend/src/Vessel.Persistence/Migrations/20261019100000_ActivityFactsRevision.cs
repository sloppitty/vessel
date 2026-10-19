using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Vessel.Persistence.Migrations;

[DbContext(typeof(VesselDbContext))]
[Migration("20261019100000_ActivityFactsRevision")]
public sealed class ActivityFactsRevision : Migration
{
    private static readonly string[] Tables = ["accounts", "imported_fills", "imported_orders", "play_order_links",
        "funding_payments", "account_funding_states", "account_snapshots", "account_positions", "account_stablecoins",
        "account_leverage_observations"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE activity_facts_revisions (owner_id uuid PRIMARY KEY, revision bigint NOT NULL);
            INSERT INTO activity_facts_revisions (owner_id, revision) SELECT DISTINCT "OwnerId", 1 FROM accounts;

            -- Statement transition tables collect all owners before any deferred revision lock is taken.
            CREATE FUNCTION activity_facts_collect() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            DECLARE
                recorded text := coalesce(current_setting('vessel.activity_owners', true), '');
                owners text;
            BEGIN
                -- Only aggregate missing owners; repeated single-row statements avoid unnest/set_config entirely.
                IF TG_OP = 'INSERT' THEN
                    SELECT string_agg(DISTINCT "OwnerId"::text, ',') INTO owners FROM activity_new
                    WHERE strpos(',' || recorded || ',', ',' || "OwnerId"::text || ',') = 0;
                ELSIF TG_OP = 'DELETE' THEN
                    SELECT string_agg(DISTINCT "OwnerId"::text, ',') INTO owners FROM activity_old
                    WHERE strpos(',' || recorded || ',', ',' || "OwnerId"::text || ',') = 0;
                ELSE
                    SELECT string_agg(DISTINCT "OwnerId"::text, ',') INTO owners FROM
                        (SELECT "OwnerId" FROM activity_old UNION SELECT "OwnerId" FROM activity_new) affected
                    WHERE strpos(',' || recorded || ',', ',' || "OwnerId"::text || ',') = 0;
                END IF;
                IF owners IS NULL THEN RETURN NULL; END IF;
                PERFORM set_config('vessel.activity_owners', concat_ws(',', nullif(recorded, ''), owners), true);
                RETURN NULL;
            END $$;

            CREATE FUNCTION activity_facts_bump() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            DECLARE
                owner uuid;
                owners text := nullif(current_setting('vessel.activity_owners', true), '');
                bumped text := coalesce(current_setting('vessel.activity_bumped_owners', true), '');
            BEGIN
                -- Immediate constraint events can run before the statement transition-table collector.
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
                -- Sorted within each firing, including offline transactions that touch several owners.
                FOR owner IN SELECT DISTINCT value::uuid FROM unnest(string_to_array(owners, ',')) value
                    WHERE strpos(',' || bumped || ',', ',' || value || ',') = 0 ORDER BY 1 LOOP
                    INSERT INTO activity_facts_revisions (owner_id, revision) VALUES (owner, 1)
                    ON CONFLICT (owner_id) DO UPDATE SET revision = activity_facts_revisions.revision + 1;
                END LOOP;
                PERFORM set_config('vessel.activity_bumped_owners', owners, true);
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
