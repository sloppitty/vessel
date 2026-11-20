using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <summary>
    /// Issue #110: chart drawings move out of the Play row into the owner-scoped <c>play_drawings</c> table, so list,
    /// Review, Sizing and export reads of Plays no longer load up to a megabyte each. Every Play's drawings are copied
    /// unchanged (jsonb to jsonb, owner kept).
    /// The legacy <c>plays."Drawings"</c> column stays in place: the model no longer maps it, so the application neither
    /// reads nor writes it, and new Plays get its <c>{}</c> default. A later migration drops it, as a separate step.
    /// Until then this migration is reversible: Down writes the current drawings back into the column and drops the table.
    /// </summary>
    public partial class PlayDrawingsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "play_drawings",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_drawings", x => new { x.OwnerId, x.PlayId });
                    table.ForeignKey(
                        name: "FK_play_drawings_plays_OwnerId_PlayId",
                        columns: x => new { x.OwnerId, x.PlayId },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            // One row per Play, empty documents included, so every Play's drawings are carried over exactly.
            migrationBuilder.Sql("""
                INSERT INTO play_drawings ("OwnerId", "PlayId", "Document")
                SELECT "OwnerId", "Id", "Drawings" FROM plays;
                """);

            // Inserts no longer name the retained column; its default (set when it was added) must stay.
            migrationBuilder.Sql("""ALTER TABLE plays ALTER COLUMN "Drawings" SET DEFAULT '{}'::jsonb;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Edits made since Up live only in the table: write them back. A Play without a row has no drawings, and its
            // column already holds the {} default or the value Up copied.
            migrationBuilder.Sql("""
                UPDATE plays AS p SET "Drawings" = d."Document"
                FROM play_drawings AS d
                WHERE d."OwnerId" = p."OwnerId" AND d."PlayId" = p."Id" AND p."Drawings" IS DISTINCT FROM d."Document";
                """);

            migrationBuilder.DropTable(
                name: "play_drawings");
        }
    }
}
