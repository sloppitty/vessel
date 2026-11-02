using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Identity.Migrations
{
    /// <inheritdoc />
    public partial class DevicePairing(IdentitySchema schema) : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: schema.Name,
                table: "sessions",
                type: "text",
                nullable: false,
                defaultValue: "wallet");

            migrationBuilder.CreateTable(
                name: "device_pairings",
                schema: schema.Name,
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signer_wallet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    typed_code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    binding_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    code_attempts = table.Column<int>(type: "integer", nullable: false),
                    device_summary = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_pairings", x => x.id);
                    table.CheckConstraint("ck_device_pairings_live_wallet", "signer_wallet_id IS NOT NULL OR status NOT IN ('pending', 'claimed', 'approved')");
                    table.CheckConstraint("ck_device_pairings_status", "status IN ('pending', 'claimed', 'approved', 'completed', 'cancelled', 'contested', 'expired')");
                    table.ForeignKey(
                        name: "FK_device_pairings_sessions_created_session_id",
                        column: x => x.created_session_id,
                        principalSchema: schema.Name,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_device_pairings_user_wallets_signer_wallet_id",
                        column: x => x.signer_wallet_id,
                        principalSchema: schema.Name,
                        principalTable: "user_wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_device_pairings_users_user_id",
                        column: x => x.user_id,
                        principalSchema: schema.Name,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_source",
                schema: schema.Name,
                table: "sessions",
                sql: "source IN ('wallet', 'pairing')");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairings_created_session_id",
                schema: schema.Name,
                table: "device_pairings",
                column: "created_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairings_expires_at",
                schema: schema.Name,
                table: "device_pairings",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairings_secret_hash",
                schema: schema.Name,
                table: "device_pairings",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_pairings_signer_wallet_id",
                schema: schema.Name,
                table: "device_pairings",
                column: "signer_wallet_id");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairings_typed_code_hash",
                schema: schema.Name,
                table: "device_pairings",
                column: "typed_code_hash",
                unique: true,
                filter: "status IN ('pending', 'claimed', 'approved')");

            migrationBuilder.CreateIndex(
                name: "IX_device_pairings_user_id_status",
                schema: schema.Name,
                table: "device_pairings",
                columns: new[] { "user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_pairings",
                schema: schema.Name);

            migrationBuilder.DropCheckConstraint(
                name: "ck_sessions_source",
                schema: schema.Name,
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "source",
                schema: schema.Name,
                table: "sessions");
        }
    }
}
