using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaperTradingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_accounts_portfolios_OwnerId_PortfolioId",
                table: "accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_plays_accounts_OwnerId_AccountId",
                table: "plays");

            migrationBuilder.DropIndex(
                name: "IX_accounts_OwnerId_PortfolioId",
                table: "accounts");

            migrationBuilder.AddColumn<string>(
                name: "TradingMode",
                table: "portfolios",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "real");

            migrationBuilder.AddColumn<string>(
                name: "TradingMode",
                table: "plays",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "real");

            migrationBuilder.AddColumn<decimal>(
                name: "StartingCapitalUsd",
                table: "accounts",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradingMode",
                table: "accounts",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "real");

            // The three non-null column defaults backfill every retained row as real without emitting
            // artificial account updates (which would advance activity-facts revision triggers).
            migrationBuilder.AddUniqueConstraint(
                name: "AK_portfolios_OwnerId_Id_TradingMode",
                table: "portfolios",
                columns: new[] { "OwnerId", "Id", "TradingMode" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_plays_OwnerId_Id_TradingMode",
                table: "plays",
                columns: new[] { "OwnerId", "Id", "TradingMode" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_accounts_OwnerId_Id_TradingMode",
                table: "accounts",
                columns: new[] { "OwnerId", "Id", "TradingMode" });

            migrationBuilder.CreateTable(
                name: "paper_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingMode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LevelId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Role = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    PnlUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    MarginUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    PlanVersion = table.Column<int>(type: "integer", nullable: false),
                    ModelVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CandleOpenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CandleCloseAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_executions", x => x.Id);
                    table.UniqueConstraint("AK_paper_executions_OwnerId_Id_AccountId_PlayId", x => new { x.OwnerId, x.Id, x.AccountId, x.PlayId });
                    table.CheckConstraint("CK_paper_execution_mode", "\"TradingMode\"='paper'");
                    table.CheckConstraint("CK_paper_execution_model", "\"ModelVersion\"=1");
                    table.CheckConstraint("CK_paper_execution_role", "(\"Role\"='entry' AND \"LevelId\" IS NULL AND \"PnlUsd\"=0 AND \"MarginUsd\">0) OR (\"Role\" IN ('stop','target','exit') AND \"MarginUsd\"<0)");
                    table.CheckConstraint("CK_paper_execution_time", "\"CandleCloseAtUtc\"=\"CandleOpenAtUtc\"+interval '1 minute' AND \"OccurredAtUtc\"=\"CandleCloseAtUtc\"");
                    table.CheckConstraint("CK_paper_execution_values", "\"Price\">0 AND \"Quantity\">0 AND \"PlanVersion\">=1");
                    table.ForeignKey(
                        name: "FK_paper_executions_accounts_OwnerId_AccountId_TradingMode",
                        columns: x => new { x.OwnerId, x.AccountId, x.TradingMode },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_paper_executions_plays_OwnerId_PlayId_TradingMode",
                        columns: x => new { x.OwnerId, x.PlayId, x.TradingMode },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paper_plan_activations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingMode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanVersion = table.Column<int>(type: "integer", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    EffectiveAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_plan_activations", x => x.Id);
                    table.CheckConstraint("CK_paper_activation_mode", "\"TradingMode\"='paper'");
                    table.ForeignKey(
                        name: "FK_paper_plan_activations_accounts_OwnerId_AccountId_TradingMo~",
                        columns: x => new { x.OwnerId, x.AccountId, x.TradingMode },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_paper_plan_activations_plays_OwnerId_PlayId_TradingMode",
                        columns: x => new { x.OwnerId, x.PlayId, x.TradingMode },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paper_reservations",
                columns: table => new
                {
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingMode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_reservations", x => x.PlayId);
                    table.CheckConstraint("CK_paper_reservation_amount", "\"AmountUsd\">=0");
                    table.CheckConstraint("CK_paper_reservation_mode", "\"TradingMode\"='paper'");
                    table.ForeignKey(
                        name: "FK_paper_reservations_accounts_OwnerId_AccountId_TradingMode",
                        columns: x => new { x.OwnerId, x.AccountId, x.TradingMode },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_paper_reservations_plays_OwnerId_PlayId_TradingMode",
                        columns: x => new { x.OwnerId, x.PlayId, x.TradingMode },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paper_settings",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CheckIntervalMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_settings", x => x.OwnerId);
                    table.CheckConstraint("CK_paper_settings_interval", "\"CheckIntervalMinutes\" BETWEEN 1 AND 1440");
                });

            migrationBuilder.CreateTable(
                name: "paper_simulations",
                columns: table => new
                {
                    PlayId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingMode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CheckpointUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PreviousClose = table.Column<decimal>(type: "numeric", nullable: true),
                    IssuesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_simulations", x => x.PlayId);
                    table.CheckConstraint("CK_paper_simulation_mode", "\"TradingMode\"='paper'");
                    table.CheckConstraint("CK_paper_simulation_state", "\"State\" IN ('waiting','ready','ambiguous','gap','disabled')");
                    table.ForeignKey(
                        name: "FK_paper_simulations_accounts_OwnerId_AccountId_TradingMode",
                        columns: x => new { x.OwnerId, x.AccountId, x.TradingMode },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_paper_simulations_plays_OwnerId_PlayId_TradingMode",
                        columns: x => new { x.OwnerId, x.PlayId, x.TradingMode },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paper_ledger_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    TradingMode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_ledger_entries", x => x.Id);
                    table.CheckConstraint("CK_paper_ledger_kind", "(\"Kind\"='seed' AND \"PlayId\" IS NULL AND \"ExecutionId\" IS NULL AND \"AmountUsd\">=0) OR (\"Kind\"='realized-pnl' AND \"PlayId\" IS NOT NULL AND \"ExecutionId\" IS NOT NULL)");
                    table.CheckConstraint("CK_paper_ledger_mode", "\"TradingMode\" = 'paper'");
                    table.ForeignKey(
                        name: "FK_paper_ledger_entries_accounts_OwnerId_AccountId_TradingMode",
                        columns: x => new { x.OwnerId, x.AccountId, x.TradingMode },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_paper_ledger_entries_paper_executions_OwnerId_ExecutionId_A~",
                        columns: x => new { x.OwnerId, x.ExecutionId, x.AccountId, x.PlayId },
                        principalTable: "paper_executions",
                        principalColumns: new[] { "OwnerId", "Id", "AccountId", "PlayId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_paper_ledger_entries_plays_OwnerId_PlayId_TradingMode",
                        columns: x => new { x.OwnerId, x.PlayId, x.TradingMode },
                        principalTable: "plays",
                        principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_portfolios_trading_mode",
                table: "portfolios",
                sql: "\"TradingMode\" IN ('real','paper')");

            migrationBuilder.CreateIndex(
                name: "IX_plays_OwnerId_AccountId_TradingMode",
                table: "plays",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_plays_trading_mode",
                table: "plays",
                sql: "\"TradingMode\" IN ('real', 'paper')");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_OwnerId_PortfolioId_TradingMode",
                table: "accounts",
                columns: new[] { "OwnerId", "PortfolioId", "TradingMode" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_accounts_paper",
                table: "accounts",
                sql: "(\"TradingMode\"='real' AND \"StartingCapitalUsd\" IS NULL) OR (\"TradingMode\"='paper' AND \"PortfolioId\" IS NOT NULL AND \"VenueId\"='hyperliquid' AND \"Address\" IS NULL AND \"SourceId\" IS NULL AND \"ManualAccountValueUsd\" IS NULL AND \"StartingCapitalUsd\" IS NOT NULL AND \"StartingCapitalUsd\">=0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_accounts_trading_mode",
                table: "accounts",
                sql: "\"TradingMode\" IN ('real','paper')");

            migrationBuilder.CreateIndex(
                name: "IX_paper_executions_OwnerId_AccountId_OccurredAtUtc",
                table: "paper_executions",
                columns: new[] { "OwnerId", "AccountId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_executions_OwnerId_AccountId_TradingMode",
                table: "paper_executions",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_executions_OwnerId_PlayId_TradingMode",
                table: "paper_executions",
                columns: new[] { "OwnerId", "PlayId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_executions_OwnerId_PlayId_EntryId",
                table: "paper_executions",
                columns: new[] { "OwnerId", "PlayId", "EntryId" },
                unique: true,
                filter: "\"Role\"='entry'");

            migrationBuilder.CreateIndex(
                name: "IX_paper_executions_OwnerId_PlayId_EntryId_LevelId_Role",
                table: "paper_executions",
                columns: new[] { "OwnerId", "PlayId", "EntryId", "LevelId", "Role" },
                unique: true,
                filter: "\"Role\"<>'entry'");

            migrationBuilder.CreateIndex(
                name: "IX_paper_ledger_entries_OwnerId_AccountId",
                table: "paper_ledger_entries",
                columns: new[] { "OwnerId", "AccountId" },
                unique: true,
                filter: "\"Kind\"='seed'");

            migrationBuilder.CreateIndex(
                name: "IX_paper_ledger_entries_OwnerId_AccountId_TradingMode",
                table: "paper_ledger_entries",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_ledger_entries_OwnerId_ExecutionId",
                table: "paper_ledger_entries",
                columns: new[] { "OwnerId", "ExecutionId" },
                unique: true,
                filter: "\"ExecutionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_paper_ledger_entries_OwnerId_ExecutionId_AccountId_PlayId",
                table: "paper_ledger_entries",
                columns: new[] { "OwnerId", "ExecutionId", "AccountId", "PlayId" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_ledger_entries_OwnerId_PlayId_TradingMode",
                table: "paper_ledger_entries",
                columns: new[] { "OwnerId", "PlayId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_plan_activations_OwnerId_AccountId_TradingMode",
                table: "paper_plan_activations",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_plan_activations_OwnerId_PlayId_TradingMode",
                table: "paper_plan_activations",
                columns: new[] { "OwnerId", "PlayId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_plan_activations_OwnerId_PlayId_EffectiveAtUtc_PlanVe~",
                table: "paper_plan_activations",
                columns: new[] { "OwnerId", "PlayId", "EffectiveAtUtc", "PlanVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_paper_reservations_OwnerId_AccountId_TradingMode",
                table: "paper_reservations",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_reservations_OwnerId_PlayId_TradingMode",
                table: "paper_reservations",
                columns: new[] { "OwnerId", "PlayId", "TradingMode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_paper_simulations_OwnerId_AccountId_TradingMode",
                table: "paper_simulations",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_simulations_OwnerId_NextCheckAtUtc_PlayId",
                table: "paper_simulations",
                columns: new[] { "OwnerId", "NextCheckAtUtc", "PlayId" });

            migrationBuilder.CreateIndex(
                name: "IX_paper_simulations_OwnerId_PlayId_TradingMode",
                table: "paper_simulations",
                columns: new[] { "OwnerId", "PlayId", "TradingMode" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_accounts_portfolios_OwnerId_PortfolioId_TradingMode",
                table: "accounts",
                columns: new[] { "OwnerId", "PortfolioId", "TradingMode" },
                principalTable: "portfolios",
                principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_plays_accounts_OwnerId_AccountId_TradingMode",
                table: "plays",
                columns: new[] { "OwnerId", "AccountId", "TradingMode" },
                principalTable: "accounts",
                principalColumns: new[] { "OwnerId", "Id", "TradingMode" },
                onDelete: ReferentialAction.Restrict);
            InstallGuards(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RemoveGuards(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_accounts_portfolios_OwnerId_PortfolioId_TradingMode",
                table: "accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_plays_accounts_OwnerId_AccountId_TradingMode",
                table: "plays");

            migrationBuilder.DropTable(
                name: "paper_ledger_entries");

            migrationBuilder.DropTable(
                name: "paper_plan_activations");

            migrationBuilder.DropTable(
                name: "paper_reservations");

            migrationBuilder.DropTable(
                name: "paper_settings");

            migrationBuilder.DropTable(
                name: "paper_simulations");

            migrationBuilder.DropTable(
                name: "paper_executions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_portfolios_OwnerId_Id_TradingMode",
                table: "portfolios");

            migrationBuilder.DropCheckConstraint(
                name: "CK_portfolios_trading_mode",
                table: "portfolios");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_plays_OwnerId_Id_TradingMode",
                table: "plays");

            migrationBuilder.DropIndex(
                name: "IX_plays_OwnerId_AccountId_TradingMode",
                table: "plays");

            migrationBuilder.DropCheckConstraint(
                name: "CK_plays_trading_mode",
                table: "plays");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_accounts_OwnerId_Id_TradingMode",
                table: "accounts");

            migrationBuilder.DropIndex(
                name: "IX_accounts_OwnerId_PortfolioId_TradingMode",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_accounts_paper",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_accounts_trading_mode",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "portfolios");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "plays");

            migrationBuilder.DropColumn(
                name: "StartingCapitalUsd",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "TradingMode",
                table: "accounts");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_OwnerId_PortfolioId",
                table: "accounts",
                columns: new[] { "OwnerId", "PortfolioId" });

            migrationBuilder.AddForeignKey(
                name: "FK_accounts_portfolios_OwnerId_PortfolioId",
                table: "accounts",
                columns: new[] { "OwnerId", "PortfolioId" },
                principalTable: "portfolios",
                principalColumns: new[] { "OwnerId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_plays_accounts_OwnerId_AccountId",
                table: "plays",
                columns: new[] { "OwnerId", "AccountId" },
                principalTable: "accounts",
                principalColumns: new[] { "OwnerId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
