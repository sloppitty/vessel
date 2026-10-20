using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <summary>
    /// Phase 0 item 0.4c: the last stop-coverage outcome per Open Play, with its check time and reason. It starts empty;
    /// each account's next check records it. Apply explicitly; no backfill and no data change elsewhere.
    /// </summary>
    public partial class MonitorCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monitor_coverage",
                columns: table => new
                {
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Covered = table.Column<bool>(type: "boolean", nullable: true),
                    Reason = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Stops = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitor_coverage", x => x.PlayId);
                    table.CheckConstraint("CK_monitor_coverage_covered", "(\"State\" = 'known') = (\"Covered\" IS NOT NULL)");
                    table.CheckConstraint("CK_monitor_coverage_state", "\"State\" IN ('known','shared','unknown','unsupported')");
                    table.ForeignKey(
                        name: "FK_monitor_coverage_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_monitor_coverage_plays_OwnerId_PlayId",
                        columns: x => new { x.OwnerId, x.PlayId },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monitor_coverage_OwnerId_AccountId",
                table: "monitor_coverage",
                columns: new[] { "OwnerId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_monitor_coverage_OwnerId_PlayId",
                table: "monitor_coverage",
                columns: new[] { "OwnerId", "PlayId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitor_coverage");
        }
    }
}
