using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IfThenRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewRule",
                table: "plays",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleIf",
                table: "plays",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleThen",
                table: "plays",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RuleVersion",
                table: "plays",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rule",
                table: "play_plan_revisions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "play_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    If = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Then = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourcePlayId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_rules", x => x.Id);
                    table.CheckConstraint("CK_play_rules_trading_mode", "\"TradingMode\" IN ('real', 'paper')");
                    table.CheckConstraint("CK_play_rules_version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "UX_play_rules_owner_mode_active",
                table: "play_rules",
                columns: new[] { "OwnerId", "TradingMode" },
                unique: true,
                filter: "\"ArchivedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_play_rules_owner_mode_version",
                table: "play_rules",
                columns: new[] { "OwnerId", "TradingMode", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "play_rules");

            migrationBuilder.DropColumn(
                name: "ReviewRule",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "RuleIf",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "RuleThen",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "RuleVersion",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "Rule",
                table: "play_plan_revisions");
        }
    }
}
