using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnresolvedStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "play_status_changes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnresolvedReason",
                table: "play_status_changes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PlayVersion",
                table: "play_status_changes",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older code cannot read the Unresolved status (phase 1, 1.3a). Rolling back with any Unresolved Play or history
            // row would leave rows it cannot load, so it is refused; restore the pre-upgrade backup instead.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM plays WHERE "Status" = 'Unresolved')
                        OR EXISTS (SELECT 1 FROM play_status_changes WHERE "From" = 'Unresolved' OR "To" = 'Unresolved') THEN
                        RAISE EXCEPTION 'Unresolved Plays or their history exist. Restore the pre-upgrade backup instead of rolling back UnresolvedStatus.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "play_status_changes");

            migrationBuilder.DropColumn(
                name: "UnresolvedReason",
                table: "play_status_changes");

            migrationBuilder.DropColumn(
                name: "PlayVersion",
                table: "play_status_changes");
        }
    }
}
