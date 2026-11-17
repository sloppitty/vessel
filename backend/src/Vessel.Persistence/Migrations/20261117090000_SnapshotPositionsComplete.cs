using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <summary>
    /// Issue #89: whether a snapshot's position read was complete. Existing snapshots were stored as the whole picture and
    /// stay complete; only later partial reads are marked, so their positions stay unknown rather than zero.
    /// </summary>
    public partial class SnapshotPositionsComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PositionsComplete",
                table: "account_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PositionsComplete",
                table: "account_snapshots");
        }
    }
}
