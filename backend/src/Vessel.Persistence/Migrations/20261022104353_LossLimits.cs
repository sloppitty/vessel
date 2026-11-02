using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LossLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DailyLossLimitR",
                table: "risk_settings",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeeklyLossLimitR",
                table: "risk_settings",
                type: "numeric(7,4)",
                precision: 7,
                scale: 4,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_risk_settings_daily_loss_limit",
                table: "risk_settings",
                sql: "\"DailyLossLimitR\" IS NULL OR (\"DailyLossLimitR\" > 0 AND \"DailyLossLimitR\" <= 100)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_risk_settings_weekly_loss_limit",
                table: "risk_settings",
                sql: "\"WeeklyLossLimitR\" IS NULL OR (\"WeeklyLossLimitR\" > 0 AND \"WeeklyLossLimitR\" <= 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_risk_settings_daily_loss_limit",
                table: "risk_settings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_risk_settings_weekly_loss_limit",
                table: "risk_settings");

            migrationBuilder.DropColumn(
                name: "DailyLossLimitR",
                table: "risk_settings");

            migrationBuilder.DropColumn(
                name: "WeeklyLossLimitR",
                table: "risk_settings");
        }
    }
}
