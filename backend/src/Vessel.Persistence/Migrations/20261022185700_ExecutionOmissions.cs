using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExecutionOmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExecutionsOmitted",
                table: "imported_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "execution_omissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    VenueContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OrderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UncertainFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UncertainToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_omissions", x => x.Id);
                    table.CheckConstraint("CK_execution_omissions_kind", "\"Kind\" IN ('forced', 'fee', 'missing-pnl', 'unrecognized-market')");
                    table.CheckConstraint("CK_execution_omissions_time", "\"FirstSeenAtUtc\" <= \"LastSeenAtUtc\" AND (\"OccurredAtUtc\" IS NULL OR \"UncertainFromUtc\" IS NULL AND \"UncertainToUtc\" IS NULL) AND\n(\"UncertainFromUtc\" IS NULL OR \"UncertainToUtc\" IS NULL OR \"UncertainFromUtc\" <= \"UncertainToUtc\")");
                    table.ForeignKey(
                        name: "FK_execution_omissions_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "imported_forced_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VenueContractId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Fee = table.Column<decimal>(type: "numeric", nullable: true),
                    RealizedPnl = table.Column<decimal>(type: "numeric", nullable: true),
                    Exclusion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imported_forced_events", x => x.Id);
                    table.CheckConstraint("CK_imported_forced_events_facts", "\"Side\" IN ('buy', 'sell') AND \"Quantity\" > 0 AND (\"Price\" IS NULL OR \"Price\" > 0) AND \"FirstSeenAtUtc\" <= \"LastSeenAtUtc\"");
                    table.CheckConstraint("CK_imported_forced_events_kind", "\"Kind\" IN ('liquidation', 'deleverage', 'settlement')");
                    table.ForeignKey(
                        name: "FK_imported_forced_events_accounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "accounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_omissions_OwnerId_AccountId_Kind_SourceId",
                table: "execution_omissions",
                columns: new[] { "OwnerId", "AccountId", "Kind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_omissions_OwnerId_AccountId_OrderId",
                table: "execution_omissions",
                columns: new[] { "OwnerId", "AccountId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_imported_forced_events_OwnerId_AccountId_OccurredAtUtc",
                table: "imported_forced_events",
                columns: new[] { "OwnerId", "AccountId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_imported_forced_events_OwnerId_AccountId_SourceId",
                table: "imported_forced_events",
                columns: new[] { "OwnerId", "AccountId", "SourceId" },
                unique: true);

            // Phase 2, item 7: Activity reads markers and forced events, so they move its cached facts revision like the
            // other Activity fact tables (ActivityFactsRevision).
            foreach (var table in new[] { "execution_omissions", "imported_forced_events" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER activity_collect_insert AFTER INSERT ON {table}
                        REFERENCING NEW TABLE AS activity_new FOR EACH STATEMENT EXECUTE FUNCTION activity_facts_collect();
                    CREATE TRIGGER activity_collect_update AFTER UPDATE ON {table}
                        REFERENCING OLD TABLE AS activity_old NEW TABLE AS activity_new FOR EACH STATEMENT EXECUTE FUNCTION activity_facts_collect();
                    CREATE TRIGGER activity_collect_delete AFTER DELETE ON {table}
                        REFERENCING OLD TABLE AS activity_old FOR EACH STATEMENT EXECUTE FUNCTION activity_facts_collect();
                    CREATE CONSTRAINT TRIGGER activity_revision_commit AFTER INSERT OR UPDATE OR DELETE ON {table}
                        DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION activity_facts_bump();
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_omissions");

            migrationBuilder.DropTable(
                name: "imported_forced_events");

            migrationBuilder.DropColumn(
                name: "ExecutionsOmitted",
                table: "imported_orders");
        }
    }
}
