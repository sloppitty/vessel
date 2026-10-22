using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JournalSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "play_search",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ProjectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExitAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultUsd = table.Column<decimal>(type: "numeric", nullable: true),
                    EstimatedR = table.Column<decimal>(type: "numeric", nullable: true),
                    RReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_search", x => new { x.OwnerId, x.PlayId });
                    table.CheckConstraint("CK_play_search_figures", "(\"State\" = 'scored') = (\"ResultUsd\" IS NOT NULL) AND (\"EstimatedR\" IS NULL OR \"ResultUsd\" IS NOT NULL) AND (\"RReason\" IS NULL OR \"State\" = 'scored' AND \"EstimatedR\" IS NULL)");
                    table.CheckConstraint("CK_play_search_reason", "\"RReason\" IS NULL OR \"RReason\" IN ('retrospective', 'plan-after-fill', 'no-stop')");
                    table.CheckConstraint("CK_play_search_state", "\"State\" IN ('active', 'scored', 'executions', 'fees', 'unresolved', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_play_search_plays_OwnerId_PlayId",
                        columns: x => new { x.OwnerId, x.PlayId },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "play_search_texts",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Field = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Folded = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_search_texts", x => new { x.OwnerId, x.PlayId, x.Position });
                    table.CheckConstraint("CK_play_search_texts_text", "\"Text\" <> '' AND length(\"Folded\") = length(\"Text\") AND \"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_play_search_texts_play_search_OwnerId_PlayId",
                        columns: x => new { x.OwnerId, x.PlayId },
                        principalTable: "play_search",
                        principalColumns: new[] { "OwnerId", "PlayId" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "play_search_texts");

            migrationBuilder.DropTable(
                name: "play_search");
        }
    }
}
