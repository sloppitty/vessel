using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Persistence;

namespace Vessel.Tests;

internal sealed class CoreDatabase(string admin, string schema, string connectionString) : IAsyncDisposable
{
    public string ConnectionString => connectionString;
    public VesselDbContext Context(Guid owner) => new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(connectionString).Options, new CoreOwner(owner));
    public static async Task<CoreDatabase> CreateAsync(string? targetMigration = null)
    {
        var admin = Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES")!;
        var schema = "vessel_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
        await command.ExecuteNonQueryAsync();
        var db = new CoreDatabase(admin, schema, new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema, Pooling = false }.ConnectionString);
        try
        {
            await using var context = db.Context(Guid.NewGuid());
            var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync(targetMigration);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal static class ActivityTestSupport
{
    public static Vessel.Application.Activity.ActivityService Activity(VesselDbContext db, TimeProvider? time = null) =>
        new(new ActivityStore(db), new WorkspaceStore(db, TestUnitOfWork.For(db)), time ?? TimeProvider.System);

    /// <summary>The first 100 operations of an account (or all enabled accounts), newest first.</summary>
    public static Task<Vessel.Application.Activity.ActivityPageDto> PageAsync(this Vessel.Application.Activity.ActivityService service,
        Guid? accountId = null) => service.PageAsync(new(AccountId: accountId, PageSize: "100"), default);
}
