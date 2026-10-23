using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FundingSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextFundingAtUtc",
                table: "monitor_accounts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextFundingAtUtc",
                table: "monitor_accounts");
        }
    }
}
