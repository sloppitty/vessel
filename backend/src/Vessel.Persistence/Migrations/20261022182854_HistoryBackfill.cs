using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoryBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_account_fill_coverage_source",
                table: "account_fill_coverage");

            migrationBuilder.AddColumn<bool>(
                name: "Backfilled",
                table: "imported_fills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BeyondVenueReach",
                table: "account_history_gaps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenedByBackfillId",
                table: "account_history_gaps",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_history_backfills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    GapId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartKnown = table.Column<bool>(type: "boolean", nullable: false),
                    CursorUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Witness = table.Column<string>(type: "text", nullable: false),
                    Pages = table.Column<int>(type: "integer", nullable: false),
                    RawRows = table.Column<int>(type: "integer", nullable: false),
                    AddedFills = table.Column<int>(type: "integer", nullable: false),
                    OmittedExecutions = table.Column<int>(type: "integer", nullable: false),
                    FirstRawAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartServed = table.Column<bool>(type: "boolean", nullable: true),
                    ContinuityLost = table.Column<bool>(type: "boolean", nullable: false),
                    Exhausted = table.Column<bool>(type: "boolean", nullable: false),
                    NextPageAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_history_backfills", x => x.Id);
                    table.CheckConstraint("CK_account_history_backfills_state", "\"State\" IN ('queued', 'completed', 'narrowed', 'failed')");
                    table.CheckConstraint("CK_account_history_backfills_window", "\"FromUtc\" <= \"ToUtc\" AND \"CursorUtc\" <= \"ToUtc\" AND \"Pages\" >= 0 AND \"RawRows\" >= 0 AND\n\"AddedFills\" >= 0 AND \"OmittedExecutions\" >= 0 AND \"Failures\" >= 0 AND (\"State\" = 'queued') = (\"FinishedAtUtc\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_account_history_backfills_account_history_gaps_GapId",
                        column: x => x.GapId,
                        principalTable: "account_history_gaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_account_history_backfills_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_account_fill_coverage_source",
                table: "account_fill_coverage",
                sql: "\"Source\" IN ('refresh', 'check', 'backfill')");

            migrationBuilder.CreateIndex(
                name: "IX_account_history_backfills_GapId",
                table: "account_history_backfills",
                column: "GapId");

            migrationBuilder.CreateIndex(
                name: "IX_account_history_backfills_OwnerId_AccountId_CreatedAtUtc",
                table: "account_history_backfills",
                columns: new[] { "OwnerId", "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_account_history_backfills_active",
                table: "account_history_backfills",
                columns: new[] { "OwnerId", "AccountId" },
                unique: true,
                filter: "\"State\" = 'queued'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_history_backfills");

            migrationBuilder.DropCheckConstraint(
                name: "CK_account_fill_coverage_source",
                table: "account_fill_coverage");

            migrationBuilder.DropColumn(
                name: "Backfilled",
                table: "imported_fills");

            migrationBuilder.DropColumn(
                name: "BeyondVenueReach",
                table: "account_history_gaps");

            migrationBuilder.DropColumn(
                name: "OpenedByBackfillId",
                table: "account_history_gaps");

            migrationBuilder.AddCheckConstraint(
                name: "CK_account_fill_coverage_source",
                table: "account_fill_coverage",
                sql: "\"Source\" IN ('refresh', 'check')");
        }
    }
}
