using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FillImportConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fill_import_conflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FillId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceFillId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Fields = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reports = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fill_import_conflicts", x => x.Id);
                    table.CheckConstraint("CK_fill_import_conflicts_reports", "\"Reports\" >= 1");
                    table.CheckConstraint("CK_fill_import_conflicts_seen", "\"FirstSeenAtUtc\" <= \"LastSeenAtUtc\"");
                    table.ForeignKey(
                        name: "FK_fill_import_conflicts_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fill_import_conflicts_imported_fills_FillId",
                        column: x => x.FillId,
                        principalTable: "imported_fills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fill_import_conflicts_FillId",
                table: "fill_import_conflicts",
                column: "FillId");

            migrationBuilder.CreateIndex(
                name: "IX_fill_import_conflicts_OwnerId_AccountId_LastSeenAtUtc",
                table: "fill_import_conflicts",
                columns: new[] { "OwnerId", "AccountId", "LastSeenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fill_import_conflicts_OwnerId_FillId",
                table: "fill_import_conflicts",
                columns: new[] { "OwnerId", "FillId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fill_import_conflicts");
        }
    }
}
