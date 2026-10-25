using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RiskSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "risk_settings",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenRiskLimitPercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_settings", x => x.OwnerId);
                    table.CheckConstraint("CK_risk_settings_open_risk_limit", "\"OpenRiskLimitPercent\" IS NULL OR (\"OpenRiskLimitPercent\" > 0 AND \"OpenRiskLimitPercent\" <= 100)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "risk_settings");
        }
    }
}
