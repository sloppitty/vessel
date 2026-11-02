using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FillAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_play_search_state",
                table: "play_search");

            migrationBuilder.CreateTable(
                name: "fill_allocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    FeeRemainder = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fill_allocations", x => x.Id);
                    table.CheckConstraint("CK_fill_allocations_reason", "\"Reason\" IN ('SharedClose', 'Flip', 'Other')");
                    table.CheckConstraint("CK_fill_allocations_revision", "\"Revision\" >= 1");
                    table.ForeignKey(
                        name: "FK_fill_allocations_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fill_allocations_imported_fills_FillId",
                        column: x => x.FillId,
                        principalTable: "imported_fills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fill_allocation_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    EntryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    Fee = table.Column<decimal>(type: "numeric", nullable: true),
                    TookRemainder = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fill_allocation_lines", x => x.Id);
                    table.CheckConstraint("CK_fill_allocation_lines_entry", "(\"Role\" = 'Entry') = (\"EntryId\" IS NOT NULL)");
                    table.CheckConstraint("CK_fill_allocation_lines_quantity", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_fill_allocation_lines_role", "\"Role\" IN ('Exit', 'Entry')");
                    table.ForeignKey(
                        name: "FK_fill_allocation_lines_fill_allocations_AllocationId",
                        column: x => x.AllocationId,
                        principalTable: "fill_allocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fill_allocation_lines_plays_OwnerId_PlayId",
                        columns: x => new { x.OwnerId, x.PlayId },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fill_allocation_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FeeRemainder = table.Column<decimal>(type: "numeric", nullable: true),
                    Lines = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fill_allocation_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fill_allocation_revisions_fill_allocations_AllocationId",
                        column: x => x.AllocationId,
                        principalTable: "fill_allocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_play_search_state",
                table: "play_search",
                sql: "\"State\" IN ('active', 'scored', 'executions', 'fees', 'unresolved', 'cancelled', 'not-allocated')");

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocation_lines_AllocationId_PlayId",
                table: "fill_allocation_lines",
                columns: new[] { "AllocationId", "PlayId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocation_lines_OwnerId_PlayId",
                table: "fill_allocation_lines",
                columns: new[] { "OwnerId", "PlayId" });

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocation_revisions_AllocationId_Revision",
                table: "fill_allocation_revisions",
                columns: new[] { "AllocationId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocation_revisions_OwnerId_AllocationId",
                table: "fill_allocation_revisions",
                columns: new[] { "OwnerId", "AllocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocations_FillId",
                table: "fill_allocations",
                column: "FillId");

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocations_OwnerId_AccountId",
                table: "fill_allocations",
                columns: new[] { "OwnerId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_fill_allocations_OwnerId_FillId",
                table: "fill_allocations",
                columns: new[] { "OwnerId", "FillId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An allocation decided Plays' quantities, statuses and results; dropping it would leave them unexplained.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM fill_allocations) THEN
                        RAISE EXCEPTION 'Fill allocations exist. Restore the pre-upgrade backup instead of rolling back FillAllocations.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "fill_allocation_lines");

            migrationBuilder.DropTable(
                name: "fill_allocation_revisions");

            migrationBuilder.DropTable(
                name: "fill_allocations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_play_search_state",
                table: "play_search");

            migrationBuilder.AddCheckConstraint(
                name: "CK_play_search_state",
                table: "play_search",
                sql: "\"State\" IN ('active', 'scored', 'executions', 'fees', 'unresolved', 'cancelled')");
        }
    }
}
