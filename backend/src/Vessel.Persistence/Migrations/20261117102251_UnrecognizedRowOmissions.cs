using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnrecognizedRowOmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #118: a well-formed venue row with a value Vessel does not recognize leaves a marker instead of failing
            // the read, and Lighter's one counted omission sentence names it, so the stored notice needs more room.
            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_omissions_kind",
                table: "execution_omissions");

            migrationBuilder.AlterColumn<string>(
                name: "HistoryNotice",
                table: "accounts",
                type: "character varying(1200)",
                maxLength: 1200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_omissions_kind",
                table: "execution_omissions",
                sql: "\"Kind\" IN ('forced', 'fee', 'missing-pnl', 'unrecognized-market', 'unrecognized-row')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Markers of unrecognized rows keep operations incomplete, and a longer notice cannot be cut without losing it.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM execution_omissions WHERE "Kind" = 'unrecognized-row')
                        OR EXISTS (SELECT 1 FROM accounts WHERE length("HistoryNotice") > 1000) THEN
                        RAISE EXCEPTION 'Unrecognized-row omissions or longer history notices exist. Restore the pre-upgrade backup instead of rolling back UnrecognizedRowOmissions.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_omissions_kind",
                table: "execution_omissions");

            migrationBuilder.AlterColumn<string>(
                name: "HistoryNotice",
                table: "accounts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1200)",
                oldMaxLength: 1200,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_omissions_kind",
                table: "execution_omissions",
                sql: "\"Kind\" IN ('forced', 'fee', 'missing-pnl', 'unrecognized-market')");
        }
    }
}
