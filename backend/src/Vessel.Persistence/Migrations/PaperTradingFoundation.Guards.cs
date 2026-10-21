using Microsoft.EntityFrameworkCore.Migrations;

namespace Vessel.Persistence.Migrations;

public partial class PaperTradingFoundation
{
    private static readonly string[] ActualAccountTables =
    [
        "imported_fills", "imported_orders", "account_snapshots", "account_positions", "account_stablecoins",
        "account_leverage_observations", "funding_payments", "account_funding_states", "account_credentials"
    ];
    private static readonly string[] ActualPlayTables = ["manual_executions", "manual_execution_history", "play_order_links"];
    private static readonly string[] PaperFactTables = ["paper_ledger_entries", "paper_executions", "paper_plan_activations"];
    private static readonly string[] PaperPlayTables =
        ["paper_ledger_entries", "paper_executions", "paper_plan_activations", "paper_reservations", "paper_simulations"];

    private static void InstallGuards(MigrationBuilder migrationBuilder)
    {
        // Database-only key: putting mutable Play.AccountId in an EF alternate key prevents authorized Draft moves.
        // These additional FKs still bind every paper row to its Play's exact owner/account/mode, without weakening
        // the ordinary EF owner/mode relationships. A ledger seed has no PlayId and naturally skips this FK.
        migrationBuilder.Sql("""
            ALTER TABLE plays ADD CONSTRAINT "AK_plays_OwnerId_Id_AccountId_TradingMode"
              UNIQUE ("OwnerId","Id","AccountId","TradingMode");
            """);
        foreach (var table in PaperPlayTables)
            migrationBuilder.Sql($"""
                ALTER TABLE {table} ADD CONSTRAINT "FK_{table}_play_account_mode"
                  FOREIGN KEY ("OwnerId","PlayId","AccountId","TradingMode")
                  REFERENCES plays ("OwnerId","Id","AccountId","TradingMode") ON DELETE RESTRICT;
                """);
        migrationBuilder.Sql("""
            CREATE FUNCTION vessel_immutable_trading_mode() RETURNS trigger
            LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            BEGIN
              IF NEW."TradingMode" IS DISTINCT FROM OLD."TradingMode" THEN
                RAISE EXCEPTION 'Trading mode is immutable.' USING ERRCODE = '23514';
              END IF;
              IF TG_TABLE_NAME = 'accounts' AND
                 (to_jsonb(NEW)->'StartingCapitalUsd') IS DISTINCT FROM (to_jsonb(OLD)->'StartingCapitalUsd') THEN
                RAISE EXCEPTION 'Paper starting capital is immutable.' USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END $$;

            CREATE FUNCTION vessel_actual_fact_mode() RETURNS trigger
            LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            BEGIN
              IF EXISTS (SELECT 1 FROM accounts WHERE "OwnerId"=NEW."OwnerId"
                AND "Id"=(to_jsonb(NEW)->>'AccountId')::uuid AND "TradingMode"='paper')
              OR EXISTS (SELECT 1 FROM plays WHERE "OwnerId"=NEW."OwnerId"
                AND "Id"=(to_jsonb(NEW)->>'PlayId')::uuid AND "TradingMode"='paper') THEN
                RAISE EXCEPTION 'Actual venue/manual execution facts cannot belong to paper accounts or Plays.'
                  USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END $$;

            CREATE FUNCTION vessel_paper_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
              RAISE EXCEPTION 'Paper execution and capital history is append-only.' USING ERRCODE = '23514';
            END $$;

            CREATE FUNCTION vessel_paper_ledger_fact() RETURNS trigger
            LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
            BEGIN
              IF NEW."Kind"='seed' AND NOT EXISTS
                (SELECT 1 FROM accounts WHERE "OwnerId"=NEW."OwnerId" AND "Id"=NEW."AccountId"
                  AND "TradingMode"='paper' AND "StartingCapitalUsd"=NEW."AmountUsd") THEN
                RAISE EXCEPTION 'Paper seed must equal the account starting capital.' USING ERRCODE = '23514';
              END IF;
              IF NEW."Kind"='realized-pnl' AND NOT EXISTS
                (SELECT 1 FROM paper_executions WHERE "OwnerId"=NEW."OwnerId" AND "Id"=NEW."ExecutionId"
                  AND "AccountId"=NEW."AccountId" AND "PlayId"=NEW."PlayId"
                  AND "Role"<>'entry' AND "PnlUsd"=NEW."AmountUsd") THEN
                RAISE EXCEPTION 'Paper realized cash must match its simulated exit.' USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END $$;
            """);
        foreach (var table in new[] { "accounts", "portfolios", "plays" })
            migrationBuilder.Sql($"""
                CREATE TRIGGER immutable_trading_mode BEFORE UPDATE ON {table}
                FOR EACH ROW EXECUTE FUNCTION vessel_immutable_trading_mode();
                """);
        foreach (var table in ActualAccountTables.Concat(ActualPlayTables))
            migrationBuilder.Sql($"""
                CREATE TRIGGER actual_fact_mode BEFORE INSERT OR UPDATE ON {table}
                FOR EACH ROW EXECUTE FUNCTION vessel_actual_fact_mode();
                """);
        foreach (var table in PaperFactTables)
            migrationBuilder.Sql($"""
                CREATE TRIGGER paper_append_only BEFORE UPDATE OR DELETE ON {table}
                FOR EACH ROW EXECUTE FUNCTION vessel_paper_append_only();
                """);
        migrationBuilder.Sql("""
            CREATE TRIGGER paper_ledger_fact BEFORE INSERT ON paper_ledger_entries
            FOR EACH ROW EXECUTE FUNCTION vessel_paper_ledger_fact();
            """);
    }

    private static void RemoveGuards(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM accounts WHERE "TradingMode"='paper')
                OR EXISTS (SELECT 1 FROM portfolios WHERE "TradingMode"='paper')
                OR EXISTS (SELECT 1 FROM plays WHERE "TradingMode"='paper') THEN
                RAISE EXCEPTION 'Cannot downgrade paper data into real records; restore the pre-upgrade backup.'
                  USING ERRCODE = '23514';
              END IF;
            END $$;
            """);
        foreach (var table in PaperPlayTables)
            migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT \"FK_{table}_play_account_mode\";");
        migrationBuilder.Sql("""ALTER TABLE plays DROP CONSTRAINT "AK_plays_OwnerId_Id_AccountId_TradingMode";""");
        foreach (var table in new[] { "accounts", "portfolios", "plays" })
            migrationBuilder.Sql($"DROP TRIGGER immutable_trading_mode ON {table};");
        foreach (var table in ActualAccountTables.Concat(ActualPlayTables))
            migrationBuilder.Sql($"DROP TRIGGER actual_fact_mode ON {table};");
        foreach (var table in PaperFactTables)
            migrationBuilder.Sql($"DROP TRIGGER paper_append_only ON {table};");
        migrationBuilder.Sql("""
            DROP TRIGGER paper_ledger_fact ON paper_ledger_entries;
            DROP FUNCTION vessel_immutable_trading_mode();
            DROP FUNCTION vessel_actual_fact_mode();
            DROP FUNCTION vessel_paper_append_only();
            DROP FUNCTION vessel_paper_ledger_fact();
            """);
    }
}
