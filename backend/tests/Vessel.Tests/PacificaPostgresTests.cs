using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Infrastructure.Venues.Pacifica;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PacificaPostgresTests
{
    [PostgresFact]
    public async Task Migration_preserves_legacy_sources_and_enforces_decoded_case_sensitive_owner_venue_identity()
    {
        await using var database = await CoreDatabase.CreateAsync("20261020155750_PaperTradingFoundation");
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var evm = "0x" + new string('a', 40);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id","OwnerId","Name","VenueId","Address","SourceId","SyncStatus","IsEnabled")
            VALUES ({Guid.NewGuid()},{owner},'EVM','hyperliquid',{evm},{evm},'not-synced',true),
                   ({Guid.NewGuid()},{owner},'Index','lighter',NULL,'9223372036854775807','not-synced',true);
            """);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(2, await db.Accounts.CountAsync());
        Assert.Equal(evm, (await db.Accounts.SingleAsync(a => a.VenueId == "hyperliquid")).SourceId);
        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)),
            new CoreOwner(owner), PacificaIdentityTests.Registry(), TestUnitOfWork.For(db));
        var created = await service.CreateAccountAsync(new(null, "Solana", PacificaReader.Id, SourceId: PacificaAdapterTests.Source), default);
        Assert.Equal(PacificaAdapterTests.Source, created.SourceId); Assert.Null(created.Address);
        var duplicate = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(
            new(null, "Again", PacificaReader.Id, SourceId: PacificaAdapterTests.Source), default));
        Assert.Equal(409, duplicate.StatusCode);

        // Both decode to 32 bytes but are DIFFERENT public keys, not case variants of one identity.
        var otherCase = PacificaAdapterTests.Source.ToLowerInvariant();
        Assert.True(AccountSource.IsSolanaAddress(otherCase));
        await Insert(owner, otherCase);
        await Insert(Guid.NewGuid(), PacificaAdapterTests.Source);
        Assert.Equal(2, await db.Accounts.CountAsync(a => a.VenueId == PacificaReader.Id));
        Assert.Equal(5, await db.Accounts.IgnoreQueryFilters().CountAsync());
        var unique = await Assert.ThrowsAsync<PostgresException>(() => Insert(owner, PacificaAdapterTests.Source));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);
        var address = await Assert.ThrowsAsync<PostgresException>(() => Insert(owner, PacificaAdapterTests.Source, evm));
        Assert.Equal(PostgresErrorCodes.CheckViolation, address.SqlState);
        foreach (var source in new[] { new string('z', 32), new string('z', 44), new string('1', 31), new string('1', 33),
                     "0x" + new string('a', 40), "O" + new string('1', 42), PacificaAdapterTests.Source + "\n" })
        {
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => Insert(owner, source));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE accounts SET "Name"='Retained case' WHERE "OwnerId"={owner} AND "SourceId"={PacificaAdapterTests.Source};
            """);
        db.ChangeTracker.Clear();
        Assert.Equal("Retained case", (await db.Accounts.SingleAsync(a => a.Id == created.Id)).Name);
        Assert.Equal(PacificaAdapterTests.Source, (await db.Accounts.SingleAsync(a => a.Id == created.Id)).SourceId);

        Task Insert(Guid user, string source, string? addressValue = null) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id","OwnerId","Name","VenueId","Address","SourceId","SyncStatus","IsEnabled")
            VALUES ({Guid.NewGuid()},{user},'Synthetic','pacifica',{addressValue},{source},'not-synced',true)
            """);
    }

    [PostgresFact]
    public async Task Real_refresh_path_is_owner_locked_idempotent_records_continuity_and_never_infers_leverage()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, PacificaReader.Id, "Synthetic");
        account.Configure(null, null, null, PacificaAdapterTests.Source);
        db.Accounts.Add(account); await db.SaveChangesAsync();
        var (reader, routes) = PacificaAdapterTests.Reader();
        var registry = new VenueRegistry([VenueDescriptor.Manual, PacificaReader.Descriptor], [reader], [reader], [reader], [], [], [], []);
        for (var pass = 0; pass < 2; pass++)
        {
            var refreshed = await MonitorTestPaths.RefreshAsync(db, owner, registry, account.Id, time: new PacificaAdapterTests.Clock());
            Assert.Equal("synced", refreshed.SyncStatus);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(4, await db.Fills.CountAsync());
        Assert.All(await db.Fills.ToListAsync(), fill =>
        {
            Assert.Equal(owner, fill.OwnerId); Assert.Equal("BTC", fill.VenueContractId); Assert.Null(fill.Leverage);
        });
        var monitor = await db.Set<MonitorAccount>().SingleAsync();
        Assert.NotNull(monitor.NewestFillAtUtc); Assert.False(monitor.HasHistoryGap);
        Assert.Empty(await db.LeverageObservations.ToListAsync());
        var page = 0;
        routes.Override = request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/v1/trades/history") return null;
            var row = PacificaAdapterTests.Fixture()["trades"]!["data"]![0]!.DeepClone();
            row["history_id"] = 1000 + ++page;
            row["created_at"] = PacificaAdapterTests.Now.AddSeconds(-1).ToUnixTimeMilliseconds();
            return new System.Text.Json.Nodes.JsonObject { ["success"] = true, ["data"] = new System.Text.Json.Nodes.JsonArray(row),
                ["has_more"] = true, ["next_cursor"] = "page-" + page }.ToJsonString();
        };
        await MonitorTestPaths.RefreshAsync(db, owner, registry, account.Id, time: new PacificaAdapterTests.Clock());
        db.ChangeTracker.Clear();
        Assert.True((await db.Set<MonitorAccount>().SingleAsync()).HasHistoryGap);
        routes.Override = null;
        await MonitorTestPaths.RefreshAsync(db, owner, registry, account.Id, time: new PacificaAdapterTests.Clock());
        db.ChangeTracker.Clear();
        Assert.True((await db.Set<MonitorAccount>().SingleAsync()).HasHistoryGap); // Refresh never clears it.
        Assert.Contains(routes.Requests, r => r.Contains("account=" + PacificaAdapterTests.Source, StringComparison.Ordinal));
        await using var foreign = database.Context(Guid.NewGuid());
        await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(
            foreign, foreign.CurrentOwnerId, registry, account.Id, time: new PacificaAdapterTests.Clock()));
        Assert.Empty(await foreign.Fills.ToListAsync());

        account = await db.Accounts.SingleAsync();
        account.UpdateSettings(account.Name, null, false); await db.SaveChangesAsync();
        var calls = routes.Requests.Count;
        await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(
            db, owner, registry, account.Id, time: new PacificaAdapterTests.Clock()));
        Assert.Equal(calls, routes.Requests.Count);
    }
}
