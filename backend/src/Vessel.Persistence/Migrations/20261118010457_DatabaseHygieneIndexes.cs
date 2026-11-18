using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vessel.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseHygieneIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Issue #106, additive only. pg_trgm is a trusted extension (PostgreSQL 13+): the database owner, or any role
            // with CREATE on the database, may create it; otherwise a superuser creates it in public before migrate runs.
            // It always lives in public and the index names its operator class there, whatever the search path. The
            // transaction-scoped lock lets concurrent migrators of one database (test schemas) wait instead of racing.
            migrationBuilder.Sql("""
                SELECT pg_advisory_xact_lock(hashtextextended('vessel-extension:pg_trgm', 0));
                CREATE EXTENSION IF NOT EXISTS pg_trgm SCHEMA public;
                """);

            // Plain CREATE INDEX: migrations run with every host stopped.

            migrationBuilder.CreateIndex(
                name: "IX_play_search_texts_Folded",
                table: "play_search_texts",
                column: "Folded")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "public.gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_imported_fills_OwnerId_AccountId_OccurredAtUtc",
                table: "imported_fills",
                columns: new[] { "OwnerId", "AccountId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_play_search_texts_Folded",
                table: "play_search_texts");

            migrationBuilder.DropIndex(
                name: "IX_imported_fills_OwnerId_AccountId_OccurredAtUtc",
                table: "imported_fills");

            // The extension stays: an operator may have created it, and other databases' objects may use it.
        }
    }
}
