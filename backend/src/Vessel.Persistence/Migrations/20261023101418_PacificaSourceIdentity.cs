using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PacificaSourceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No Solana extension/dependency. Match AccountSource's decoded byte count, including leading zero bytes.
            // Unqualified names deliberately follow the migration's search_path (isolated schemas in tests).
            migrationBuilder.Sql("""
                CREATE FUNCTION vessel_is_solana_address(value text) RETURNS boolean
                LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE AS $$
                DECLARE
                    alphabet constant text := '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
                    number numeric := 0;
                    zero_bytes integer := 0;
                    decoded_bytes integer := 0;
                    digit integer;
                    i integer;
                BEGIN
                    IF length(value) NOT BETWEEN 32 AND 44 THEN RETURN false; END IF;
                    FOR i IN 1..length(value) LOOP
                        digit := strpos(alphabet, substr(value, i, 1)) - 1;
                        IF digit < 0 THEN RETURN false; END IF;
                        number := number * 58 + digit;
                    END LOOP;
                    zero_bytes := length(value) - length(ltrim(value, '1'));
                    WHILE number > 0 LOOP
                        decoded_bytes := decoded_bytes + 1;
                        number := trunc(number / 256);
                    END LOOP;
                    RETURN zero_bytes + decoded_bytes = 32;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_accounts_source_identity",
                table: "accounts");

            migrationBuilder.AlterColumn<string>(
                name: "SourceId", table: "accounts", type: "character varying(128)", maxLength: 128,
                nullable: true, collation: "C", oldClrType: typeof(string), oldType: "character varying(128)",
                oldMaxLength: 128, oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_accounts_source_identity",
                table: "accounts",
                sql: "CASE WHEN \"VenueId\" = 'pacifica' THEN \"Address\" IS NULL AND \"SourceId\" IS NOT NULL AND vessel_is_solana_address(\"SourceId\") ELSE (\"Address\" IS NULL OR \"SourceId\" IS NOT NULL AND \"SourceId\" = \"Address\") AND CASE WHEN \"SourceId\" IS NULL OR \"Address\" IS NOT NULL THEN true WHEN \"SourceId\" ~ '^(0|[1-9][0-9]{0,18})$' THEN \"SourceId\"::numeric <= 9223372036854775807 ELSE false END END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM accounts WHERE "VenueId" = 'pacifica') THEN
                        RAISE EXCEPTION 'Pacifica accounts exist. Restore the pre-upgrade backup instead of rolling back PacificaSourceIdentity.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_accounts_source_identity",
                table: "accounts");

            migrationBuilder.AlterColumn<string>(
                name: "SourceId", table: "accounts", type: "character varying(128)", maxLength: 128,
                nullable: true, oldClrType: typeof(string), oldType: "character varying(128)",
                oldMaxLength: 128, oldNullable: true, oldCollation: "C");

            migrationBuilder.AddCheckConstraint(
                name: "CK_accounts_source_identity",
                table: "accounts",
                sql: "(\"Address\" IS NULL OR \"SourceId\" IS NOT NULL AND \"SourceId\" = \"Address\") AND CASE WHEN \"SourceId\" IS NULL OR \"Address\" IS NOT NULL THEN true WHEN \"SourceId\" ~ '^(0|[1-9][0-9]{0,18})$' THEN \"SourceId\"::numeric <= 9223372036854775807 ELSE false END");
            migrationBuilder.Sql("DROP FUNCTION vessel_is_solana_address(text);");
        }
    }
}
