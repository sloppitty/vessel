using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <summary>
    /// Phase 1 item 1.5: the pre-flight checklist recorded with plan revision 1 at Plan it, as JSON. Nullable: older plans
    /// and later revisions have none, and nothing is backfilled. Apply explicitly.
    /// </summary>
    public partial class PlanPreflight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Preflight",
                table: "play_plan_revisions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Preflight",
                table: "play_plan_revisions");
        }
    }
}
