using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeverageObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Leverage",
                table: "imported_fills",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_leverage_observations",
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
                    Leverage = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_leverage_observations", x => x.Id);
                    table.CheckConstraint("CK_account_leverage_observations_leverage", "\"Leverage\" BETWEEN 1 AND 1000");
                    table.ForeignKey(
                        name: "FK_account_leverage_observations_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_imported_fills_leverage",
                table: "imported_fills",
                sql: "\"Leverage\" IS NULL OR \"Leverage\" BETWEEN 1 AND 1000");

            migrationBuilder.CreateIndex(
                name: "IX_account_leverage_observations_OwnerId_AccountId_ContractId_~",
                table: "account_leverage_observations",
                columns: new[] { "OwnerId", "AccountId", "ContractId", "ObservedAtUtc" });

            // Keep the leverage of currently open positions, so it survives their closing before the next refresh.
            // Existing fills stay null: no venue payload is retained to backfill them from.
            migrationBuilder.Sql("""
                INSERT INTO account_leverage_observations
                    ("Id", "OwnerId", "AccountId", "ContractId", "VenueContractId", "ObservedAtUtc", "SignedQuantity", "EntryPrice", "Leverage")
                SELECT gen_random_uuid(), p."OwnerId", p."AccountId", p."ContractId", p."VenueContractId", s."ObservedAtUtc",
                       p."SignedQuantity", p."EntryPrice", p."Leverage"
                FROM account_positions p
                JOIN account_snapshots s ON s."OwnerId" = p."OwnerId" AND s."AccountId" = p."AccountId"
                WHERE p."Leverage" BETWEEN 1 AND 1000 AND p."SignedQuantity" <> 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_leverage_observations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_imported_fills_leverage",
                table: "imported_fills");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "imported_fills");
        }
    }
}
