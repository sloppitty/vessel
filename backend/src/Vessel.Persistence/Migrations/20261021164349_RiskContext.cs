using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RiskContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LiquidationPrice",
                table: "account_positions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarginMode",
                table: "account_positions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_risk_observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VenueContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SignedQuantity = table.Column<decimal>(type: "numeric", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    LiquidationPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    MarginMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_risk_observations", x => x.Id);
                    table.CheckConstraint("CK_account_risk_observations_liquidation", "\"LiquidationPrice\" IS NULL OR \"LiquidationPrice\" > 0");
                    table.CheckConstraint("CK_account_risk_observations_margin_mode", "\"MarginMode\" IS NULL OR \"MarginMode\" IN ('cross', 'isolated')");
                    table.CheckConstraint("CK_account_risk_observations_reported", "\"LiquidationPrice\" IS NOT NULL OR \"MarginMode\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_account_risk_observations_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_account_positions_margin_mode",
                table: "account_positions",
                sql: "\"MarginMode\" IS NULL OR \"MarginMode\" IN ('cross', 'isolated')");

            migrationBuilder.CreateIndex(
                name: "IX_account_risk_observations_OwnerId_AccountId_ContractId_Obse~",
                table: "account_risk_observations",
                columns: new[] { "OwnerId", "AccountId", "ContractId", "ObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_risk_observations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_account_positions_margin_mode",
                table: "account_positions");

            migrationBuilder.DropColumn(
                name: "LiquidationPrice",
                table: "account_positions");

            migrationBuilder.DropColumn(
                name: "MarginMode",
                table: "account_positions");
        }
    }
}
