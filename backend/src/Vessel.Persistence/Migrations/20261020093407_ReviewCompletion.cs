using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <summary>
    /// Phase 1 item 1.2 (decision 3): an explicit review completion on plays, an action on each review history row, and the
    /// append-only play_notes log. Plays the Reviewed view lists today (ended, with review text, answers or a review time)
    /// keep that place as a "Legacy" completion at their review time, else their end time; nothing else is reclassified and
    /// no text moves. Apply explicitly.
    /// </summary>
    public partial class ReviewCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewCompletedAtUtc",
                table: "plays",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewCompletion",
                table: "plays",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "play_review_revisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "edited");

            migrationBuilder.CreateTable(
                name: "play_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_notes", x => x.Id);
                    table.CheckConstraint("CK_play_notes_state", "\"State\" IS NULL OR \"State\" IN ('calm','rushed','fomo','tired','after-loss')");
                    table.ForeignKey(
                        name: "FK_play_notes_plays_OwnerId_PlayId",
                        columns: x => new { x.OwnerId, x.PlayId },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_play_notes_OwnerId_PlayId_CreatedAtUtc",
                table: "play_notes",
                columns: new[] { "OwnerId", "PlayId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_play_notes_OwnerId_RequestId",
                table: "play_notes",
                columns: new[] { "OwnerId", "RequestId" },
                unique: true);

            // Today's Reviewed rule, exactly: ended and (HasReview or a review time). HasReview trims every character
            // char.IsWhiteSpace accepts, as the Plays list query does.
            const string space = @"E'\t\n\u000B\f\r \u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000'";
            migrationBuilder.Sql($$"""
                UPDATE plays
                SET "ReviewCompletion" = 'Legacy', "ReviewCompletedAtUtc" = COALESCE("ReviewedAtUtc", "EndedAtUtc", "UpdatedAtUtc")
                WHERE "Status" IN ('Closed', 'Cancelled') AND (
                    "ReviewedAtUtc" IS NOT NULL
                    OR btrim("Review", {{space}}) <> ''
                    OR "ReviewAdherence" IS NOT NULL
                    OR btrim(COALESCE("ReviewMistakeTag", ''), {{space}}) <> ''
                    OR btrim("ReviewLesson", {{space}}) <> ''
                    OR btrim("ReviewNextAction", {{space}}) <> '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "play_notes");

            migrationBuilder.DropColumn(
                name: "ReviewCompletedAtUtc",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "ReviewCompletion",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "play_review_revisions");
        }
    }
}
