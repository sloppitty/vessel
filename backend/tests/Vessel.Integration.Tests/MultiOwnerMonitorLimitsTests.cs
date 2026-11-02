using Vessel.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class MultiOwnerMonitorLimitsTests
{
    [PostgresFact]
    public async Task Offline_migration_keeps_maximum_deadline_per_venue_and_starts_deployment_health_empty()
    {
        await using var database = await CoreDatabase.CreateAsync("20261018100000_ServerEventSubscriptions");
        await using var db = database.Context(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var foreign = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO monitor_venue_budgets ("OwnerId", "VenueId", "RetryAtUtc") VALUES
                ({db.CurrentOwnerId}, 'hyperliquid', {now}), ({foreign}, 'hyperliquid', {now.AddMinutes(4)}),
                ({foreign}, 'risex', {now.AddMinutes(2)});
            INSERT INTO monitor_heartbeats ("OwnerId", "WorkerId", "HeartbeatAtUtc") VALUES ({foreign}, 'old-worker', {now});
            """);
        await db.Database.MigrateAsync();
        var state = MonitorServerState.For(db);
        Assert.Equal(now.AddMinutes(4).ToUnixTimeMilliseconds(), (await state.VenueRetryAtAsync("hyperliquid", default))!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(now.AddMinutes(2).ToUnixTimeMilliseconds(), (await state.VenueRetryAtAsync("risex", default))!.Value.ToUnixTimeMilliseconds());
        Assert.Null(await state.HeartbeatAsync(default));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.DoesNotContain(db.Model.GetEntityTypes(), e => e.ClrType.Name is "MonitorHeartbeat" or "MonitorVenueBudget");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var old = new NpgsqlCommand("SELECT to_regclass('monitor_heartbeats') IS NULL AND to_regclass('monitor_venue_budgets') IS NULL", connection);
        Assert.Equal(true, await old.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Candidate_keysets_are_lock_free_bounded_and_hide_no_due_row()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var first = Guid.Parse("11111111-0000-0000-0000-000000000001");
        var second = Guid.Parse("22222222-0000-0000-0000-000000000002");
        var seeded = (await SeedAsync(database, first, "hyperliquid", now, 8)).Concat(await SeedAsync(database, second, "risex", now, 3)).ToArray();
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        var system = new SystemMonitorStore(data, new ManualTime(now));
        var page = await system.DueCandidatesAsync(null, now, 4, default);
        // Pages follow (due, owner, account) order with no per-owner cut, so a continuation never passes a hidden row.
        Assert.Equal(4, page.Count);
        Assert.All(page, candidate => Assert.Equal(first, candidate.OwnerId));
        Assert.All(page, candidate => Assert.Equal(now, candidate.DueAt));
        // A row lock does not block the system discovery query.
        await using var locked = database.Context(first);
        await using var transaction = await locked.Database.BeginTransactionAsync();
        await locked.Database.ExecuteSqlRawAsync("SELECT 1 FROM monitor_accounts FOR UPDATE");
        Assert.Equal(page, await system.DueCandidatesAsync(null, now, 4, default).WaitAsync(TimeSpan.FromSeconds(5)));
        var walked = page.ToList();
        for (var next = page; next.Count > 0; walked.AddRange(next))
            next = await system.DueCandidatesAsync(walked[^1].Key, now, 4, default);
        Assert.Equal(seeded.Order(), walked.Select(c => c.AccountId).Order());
        Assert.Equal(walked.OrderBy(c => c.OwnerId).ThenBy(c => c.AccountId).Select(c => c.AccountId), walked.Select(c => c.AccountId));
        // A scan cycle's bound excludes work that became due after the cycle started.
        Assert.Empty(await system.DueCandidatesAsync(null, now.AddSeconds(-1), 100, default));
    }

    [PostgresFact]
    public async Task Throttled_venue_rows_are_filtered_before_paging_so_another_venue_stays_visible()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var throttled = await SeedAsync(database, owner, "hyperliquid", now.AddMinutes(-2), 2);
        var available = (await SeedAsync(database, owner, "risex", now.AddMinutes(-1), 1)).Single();
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        var state = new ServerStateStore(data, new(["monitor"]));
        await state.DeferVenueAsync("hyperliquid", now.AddHours(1), now, default);
        var clock = new ManualTime(now);
        var system = new SystemMonitorStore(data, clock);
        for (var pass = 0; pass < 3; pass++)
        {
            var candidate = Assert.Single(await system.DueCandidatesAsync(null, now, 100, default));
            Assert.Equal(available, candidate.AccountId);
            Assert.Equal("risex", candidate.VenueId);
        }
        clock.Advance(TimeSpan.FromHours(1));
        // Once the deadline passes, the earlier-due throttled rows come first and the other venue's row stays visible.
        var resumed = (await system.DueCandidatesAsync(null, clock.GetUtcNow(), 100, default)).Select(c => c.AccountId).ToArray();
        Assert.Equal(throttled.Order(), resumed[..2].Order());
        Assert.Equal(available, resumed[2]);
    }

    [PostgresFact]
    public async Task Two_owners_and_two_venues_claim_without_deadlock_and_share_server_slots()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var directory = new MutableOwnerDirectory();
        var candidates = new List<(Guid Owner, Guid Account)>();
        foreach (var owner in owners)
        {
            directory.Set(owner, JournalOwnerStatus.Active);
            foreach (var venue in new[] { "hyperliquid", "risex" })
                candidates.Add((owner, (await SeedAsync(database, owner, venue, now, 1)).Single()));
        }
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        var state = new ServerStateStore(data, new(["monitor"]));
        var leases = await Task.WhenAll(candidates.Select(async candidate =>
        {
            await using var db = database.Context(candidate.Owner);
            var store = new MonitorStore(db, TestUnitOfWork.For(db), state, directory, new() { VenueReadConcurrency = 1 });
            return await store.ClaimAsync(candidate.Account, null, "worker", now, default);
        })).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, leases.Count(l => l is not null));
        foreach (var venue in new[] { "hyperliquid", "risex" })
        {
            var running = 0;
            foreach (var owner in owners)
            {
                await using var db = database.Context(owner);
                running += await (from m in db.Set<MonitorAccount>() join a in db.Accounts on m.AccountId equals a.Id
                    where a.VenueId == venue && m.ManualState == "running" select m).CountAsync();
            }
            Assert.Equal(1, running);
        }
        Assert.All(leases.OfType<MonitorLease>(), l => Assert.Contains(l.OwnerId, owners));
    }

    [PostgresFact]
    public async Task Transactional_venue_count_sees_its_own_uncommitted_lease_and_suspension_only_blocks_new_claims()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var ids = await SeedAsync(database, owner, "hyperliquid", now, 2);
        var directory = new MutableOwnerDirectory(); directory.Set(owner, JournalOwnerStatus.Active);
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        await using var db = database.Context(owner);
        var work = TestUnitOfWork.For(db);
        var store = new MonitorStore(db, work, new ServerStateStore(data, new(["monitor"])), directory, new() { VenueReadConcurrency = 1 });
        MonitorLease? lease = null;
        Assert.False(await work.ExecuteAsync(() => store.LockedAsync(ids[0], async (_, row) =>
        {
            Assert.True(row.Claim(row.RequestId!.Value, "worker", now, MonitorOptions.Lease));
            lease = new(owner, row.AccountId, row.RequestId.Value, row.Generation, "worker", row.Trigger);
            return await store.ReadCapacityAsync("hyperliquid", now, default);
        }, default), default));
        directory.Set(owner, JournalOwnerStatus.Suspended);
        Assert.Null(await store.ClaimAsync(ids[1], null, "worker", now, default));
        Assert.True(await store.RenewAsync(lease!, now.AddMinutes(1), default));
        Assert.False(await store.RenewAsync(lease! with { OwnerId = Guid.NewGuid() }, now, default));
    }

    [PostgresFact]
    public async Task Credential_429_survives_rollback_and_defers_another_owners_verification_and_discovery()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var ids = new List<Guid>();
        foreach (var owner in owners)
        {
            await using var db = database.Context(owner);
            var account = SecurityFixture.Account(owner); db.Add(account); await db.SaveChangesAsync(); ids.Add(account.Id);
        }
        var deadline = SecurityFixture.Now.AddSeconds(90);
        var verifier = new SecurityVerifier { Error = new VenueRateLimitException(deadline) };
        using var vault = SecurityFixture.Vault();
        await using (var db = database.Context(owners[0]))
        {
            var work = TestUnitOfWork.For(db);
            var service = new AccountCredentialService(new WorkspaceStore(db, work), new AccountCredentialStore(db, work), new CoreOwner(owners[0]),
                SecurityFixture.Registry(verifier), vault, SecurityFixture.Clock, work, MonitorServerState.Reads(db, SecurityFixture.Clock));
            var error = await Assert.ThrowsAsync<ProblemException>(() => service.PutAsync(ids[0], new() { Token = SecurityFixture.Token }, default));
            Assert.Equal(429, error.StatusCode); Assert.Equal(90, error.RetryAfterSeconds);
            Assert.Empty(await db.AccountCredentials.ToListAsync());
        }
        await using (var db = database.Context(owners[1]))
        {
            var work = TestUnitOfWork.For(db);
            var untouched = new SecurityVerifier();
            var reads = MonitorServerState.Reads(db, SecurityFixture.Clock);
            var registry = SecurityFixture.Registry(untouched, new SecurityDiscovery([]));
            var service = new AccountCredentialService(new WorkspaceStore(db, work), new AccountCredentialStore(db, work), new CoreOwner(owners[1]),
                registry, vault, SecurityFixture.Clock, work, reads);
            Assert.Equal(429, (await Assert.ThrowsAsync<ProblemException>(() => service.PutAsync(ids[1], new() { Token = SecurityFixture.Token }, default))).StatusCode);
            Assert.Null(untouched.ReceivedToken);
            var discovery = new AccountDiscoveryService(new WorkspaceStore(db, work), new CoreOwner(owners[1]), registry, reads);
            var error = await Assert.ThrowsAsync<ProblemException>(() => discovery.DiscoverAsync(SecurityFixture.Venue.Id, SecurityFixture.Address, default));
            Assert.Equal(429, error.StatusCode); Assert.Equal(90, error.RetryAfterSeconds);
            Assert.Equal(deadline, await MonitorServerState.For(db).VenueRetryAtAsync(SecurityFixture.Venue.Id, default));
        }
    }

    [PostgresFact]
    public async Task Only_configured_monitor_deployments_contribute_to_health()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        var state = new ServerStateStore(data, new(["checks"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.RecordHeartbeatAsync("verification", DateTimeOffset.UtcNow, default));
        var other = new ServerStateStore(data, new(["verification"]));
        await other.RecordHeartbeatAsync("verification", DateTimeOffset.UtcNow, default);
        Assert.Null(await state.HeartbeatAsync(default));
        await state.RecordHeartbeatAsync("checks", DateTimeOffset.UtcNow, default);
        Assert.Equal("checks", (await state.HeartbeatAsync(default))!.Deployment);
    }

    [PostgresFact]
    public async Task Separate_api_processes_share_refresh_venue_capacity_but_not_between_venues()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var firstAccount = (await SeedAsync(database, first, "hyperliquid", DateTimeOffset.UtcNow, 1)).Single();
        var secondAccount = (await SeedAsync(database, second, "hyperliquid", DateTimeOffset.UtcNow, 1)).Single();
        var otherVenue = (await SeedAsync(database, second, "risex", DateTimeOffset.UtcNow, 1)).Single();
        foreach (var owner in new[] { first, second })
        {
            await using var db = database.Context(owner);
            db.RemoveRange(await db.Set<MonitorAccount>().ToListAsync()); await db.SaveChangesAsync();
        }
        var blocked = new GatedReader();
        var other = new FixtureReader { VenueId = "risex" };
        var descriptor = Vessel.Infrastructure.Venues.Hyperliquid.HyperliquidPerpetualReader.Descriptor;
        descriptor = descriptor with { Capabilities = descriptor.Capabilities with { Orders = false } };
        var registry = new VenueRegistry([VenueDescriptor.Manual, descriptor, descriptor with { Id = "risex" }], [blocked, other], [], [], [], [], [], []);
        var settings = new Dictionary<string, string?> { ["Vessel:Monitor:VenueReadConcurrency"] = "1" };
        void Configure(IServiceCollection services)
        {
            services.AddSingleton<IVenueRegistry>(registry);
            // Minimal-host test settings arrive after the eager options binding; replace the captured options.
            services.AddSingleton(new MonitorOptions { VenueReadConcurrency = 1 });
        }
        await using var firstApi = new CoreApiFactory(first, connection: database.ConnectionString, settings: settings, configure: Configure);
        await using var secondApi = new CoreApiFactory(second, connection: database.ConnectionString, settings: settings, configure: Configure);
        using var firstClient = firstApi.AuthorizedClient(); using var secondClient = secondApi.AuthorizedClient();
        Assert.Equal(1, firstApi.Services.GetRequiredService<MonitorOptions>().VenueReadConcurrency);
        Assert.Equal(1, secondApi.Services.GetRequiredService<MonitorOptions>().VenueReadConcurrency);
        var running = firstClient.PostAsync($"/api/accounts/{firstAccount}/sync", null);
        try
        {
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rejected = await secondClient.PostAsync($"/api/accounts/{secondAccount}/sync", null);
            Assert.Equal(429, (int)rejected.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(30), rejected.Headers.RetryAfter?.Delta);
            (await secondClient.PostAsync($"/api/accounts/{otherVenue}/sync", null)).EnsureSuccessStatusCode();
            Assert.Equal(1, other.Reads);
        }
        finally { blocked.Release.TrySetResult(); }
        (await running.WaitAsync(TimeSpan.FromSeconds(10))).EnsureSuccessStatusCode();
    }

    private sealed class GatedReader : IPerpetualVenueReader
    {
        public string VenueId => "hyperliquid";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => new FixtureReader().ReadInstrumentsAsync(ct);
        public async Task<PerpetualVenueReadResult> ReadAsync(string address, CancellationToken ct)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(ct); return new FixtureReader().Result;
        }
    }

    [Fact]
    public void Monitor_health_deployment_ids_come_from_the_messaging_module_map()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Messaging:Deployments:checks:0"] = "monitor",
            ["Vessel:Messaging:Deployments:verifier:0"] = "verification"
        }).Build();
        services.AddVesselMonitoring(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(["checks"], provider.GetRequiredService<MonitorDeployments>().Ids);
    }

    private static async Task<IReadOnlyList<Guid>> SeedAsync(CoreDatabase database, Guid owner, string venue, DateTimeOffset due, int count)
    {
        await using var db = database.Context(owner);
        var ids = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var account = new Account(Guid.NewGuid(), owner, venue, "Synthetic monitor");
            account.Configure(null, "0x" + index.ToString("x40", System.Globalization.CultureInfo.InvariantCulture), null);
            var row = new MonitorAccount { OwnerId = owner, AccountId = account.Id };
            row.Admit(Guid.NewGuid(), "manual", due);
            db.AddRange(account, row); ids.Add(account.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    }
}
