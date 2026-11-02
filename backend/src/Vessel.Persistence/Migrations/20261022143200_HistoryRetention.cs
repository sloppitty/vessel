using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoryRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepExecutionHistory",
                table: "accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "account_fill_coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FirstObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ImportCount = table.Column<int>(type: "integer", nullable: false),
                    RequestedFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequestedToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: true),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    CoveredFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CoveredToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GapFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GapToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_fill_coverage", x => x.Id);
                    table.CheckConstraint("CK_account_fill_coverage_ranges", "\"FirstObservedAtUtc\" <= \"LastObservedAtUtc\" AND \"ImportCount\" >= 1 AND\n(\"RequestedFromUtc\" IS NULL OR \"RequestedFromUtc\" <= \"RequestedToUtc\") AND\n(\"CoveredFromUtc\" IS NULL OR \"CoveredToUtc\" IS NOT NULL AND \"CoveredFromUtc\" <= \"CoveredToUtc\") AND\n(\"GapToUtc\" IS NULL OR \"GapFromUtc\" IS NOT NULL AND \"GapFromUtc\" <= \"GapToUtc\") AND\n(\"Truncated\" IS NOT NULL OR \"CoveredToUtc\" IS NULL AND \"GapFromUtc\" IS NULL AND NOT \"Complete\")");
                    table.CheckConstraint("CK_account_fill_coverage_source", "\"Source\" IN ('refresh', 'check')");
                    table.ForeignKey(
                        name: "FK_account_fill_coverage_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_history_gaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpenedByCoverageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedByCoverageId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_history_gaps", x => x.Id);
                    table.CheckConstraint("CK_account_history_gaps_range", "(\"FromUtc\" IS NULL OR \"ToUtc\" IS NULL OR \"FromUtc\" <= \"ToUtc\") AND (\"ClosedAtUtc\" IS NULL OR \"ClosedAtUtc\" >= \"OpenedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_account_history_gaps_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_accounts_keep_history",
                table: "accounts",
                sql: "NOT \"KeepExecutionHistory\" OR \"TradingMode\"='real' AND \"SourceId\" IS NOT NULL AND \"VenueId\" <> 'manual'");

            migrationBuilder.CreateIndex(
                name: "IX_account_fill_coverage_OwnerId_AccountId_LastObservedAtUtc",
                table: "account_fill_coverage",
                columns: new[] { "OwnerId", "AccountId", "LastObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_account_history_gaps_OwnerId_AccountId_OpenedAtUtc",
                table: "account_history_gaps",
                columns: new[] { "OwnerId", "AccountId", "OpenedAtUtc" });

            // A gap the monitor flagged before the ledger existed has no recorded range; it stays open, with unknown ends,
            // until a read proves it covered.
            migrationBuilder.Sql("""
                INSERT INTO account_history_gaps ("Id", "OwnerId", "AccountId", "FromUtc", "ToUtc", "OpenedAtUtc")
                SELECT gen_random_uuid(), m."OwnerId", m."AccountId", NULL, NULL, now()
                FROM monitor_accounts m WHERE m."HasHistoryGap";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_fill_coverage");

            migrationBuilder.DropTable(
                name: "account_history_gaps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_accounts_keep_history",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "KeepExecutionHistory",
                table: "accounts");
        }
    }
}
