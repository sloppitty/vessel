using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vessel.Application.MarketData;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Worker;

namespace Vessel.Tests;

public sealed class PaperWorkerPostgresTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 20, 12, 1, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task Replicas_share_one_bounded_instrument_read_and_never_duplicate_private_decisions()
    {
        await using var f = await Fixture.CreateAsync();
        await Task.WhenAll(f.Scheduler.TickAsync(default), f.Scheduler.TickAsync(default));
        Assert.Equal(1, f.Reader.Reads);
        Assert.Equal(("BTC", "1m", Open.ToUnixTimeMilliseconds(), Open.AddMinutes(2).ToUnixTimeMilliseconds() - 1),
            f.Reader.Last);
        foreach (var owner in f.Owners)
        {
            await using var db = f.Database.Context(owner);
            Assert.Equal(2, await db.PaperExecutions.CountAsync());
            Assert.Equal(1, await db.PaperLedgerEntries.CountAsync());
            Assert.All(await db.Plays.ToListAsync(), p => Assert.Equal(PlayStatus.Open, p.Status));
        }
        await f.Scheduler.TickAsync(default);
        Assert.Equal(1, f.Reader.Reads);
    }

    [PostgresFact]
    public async Task Suspended_owner_is_rechecked_after_shared_public_read_before_private_execution()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.BeforeReturn = () => f.Directory.Suspended = f.Owners[0];
        await f.Scheduler.TickAsync(default);
        Assert.Equal(1, f.Reader.Reads);
        await using var suspended = f.Database.Context(f.Owners[0]);
        await using var active = f.Database.Context(f.Owners[1]);
        Assert.Empty(await suspended.PaperExecutions.ToListAsync());
        Assert.Equal(2, await active.PaperExecutions.CountAsync());
        Assert.All(await suspended.PaperSimulations.ToListAsync(), s => Assert.Equal(Open, s.CheckpointUtc));
    }

    [PostgresFact]
    public async Task Tick_budget_never_splits_or_strands_an_accounts_active_siblings()
    {
        await using var f = await Fixture.CreateAsync(maxPlaysPerTick: 1);
        await f.Scheduler.TickAsync(default);
        await using (var first = f.Database.Context(f.Owners[0]))
        {
            Assert.Equal(2, await first.PaperExecutions.CountAsync());
            Assert.All(await first.PaperSimulations.ToListAsync(), s => Assert.Equal(Open.AddMinutes(2), s.CheckpointUtc));
        }
        await f.Scheduler.TickAsync(default);
        await using var second = f.Database.Context(f.Owners[1]);
        Assert.Equal(2, await second.PaperExecutions.CountAsync());
        Assert.All(await second.PaperSimulations.ToListAsync(), s => Assert.Equal(Open.AddMinutes(2), s.CheckpointUtc));
    }

    [PostgresFact]
    public async Task Public_instrument_groups_are_read_first_then_apply_one_chronological_account_timeline()
    {
        await using var f = await Fixture.CreateAsync(crossInstrumentLoss: true);
        await f.Scheduler.TickAsync(default);
        Assert.Equal(2, f.Reader.Reads);
        foreach (var owner in f.Owners)
        {
            await using var db = f.Database.Context(owner);
            Assert.Equal(1000, await db.PaperLedgerEntries.SumAsync(x => x.AmountUsd));
            var other = await db.Plays.SingleAsync(p => p.ContractId == "ETH");
            Assert.Equal(29, await db.PaperExecutions.Where(e => e.PlayId == other.Id && e.Role == "entry").SumAsync(e => e.Quantity));
            Assert.Equal("ready", (await db.PaperSimulations.FindAsync(other.Id))!.State);
        }
    }

    [PostgresFact]
    public async Task Sibling_revision_during_public_reads_invalidates_all_prepared_account_decisions()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reader.BeforeReturn = () =>
        {
            using var db = f.Database.Context(f.Owners[0]);
            var play = db.Plays.First();
            // A plan revision on a sibling is enough: the fetched account set no longer proves a common frontier.
            play.ChangePlan(PlayDocuments.Serialize(PlayDocuments.Read(play.Plan) with { Size = "101" }), "During read", Open);
            db.SaveChanges();
        };
        await f.Scheduler.TickAsync(default);
        await using var first = f.Database.Context(f.Owners[0]);
        Assert.Empty(await first.PaperExecutions.ToListAsync());
        Assert.All(await first.PaperSimulations.ToListAsync(), s => Assert.Equal(Open, s.CheckpointUtc));
        await using var other = f.Database.Context(f.Owners[1]);
        Assert.Equal(2, await other.PaperExecutions.CountAsync());
    }

    [PostgresFact]
    public async Task Full_real_venue_capacity_defers_paper_without_advancing_private_frontiers()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var db = f.Database.Context(f.Owners[0]))
        {
            for (var i = 0; i < 4; i++)
            {
                var account = new Account(Guid.NewGuid(), f.Owners[0], "hyperliquid", "Running real monitor");
                db.AddRange(account, new MonitorAccount
                {
                    OwnerId = account.OwnerId, AccountId = account.Id, ManualState = "running",
                    RequestId = Guid.NewGuid(), LeaseHolder = "real-worker", LeaseUntilUtc = f.Clock.GetUtcNow().AddMinutes(5), Generation = 1
                });
            }
            await db.SaveChangesAsync();
        }
        await f.Scheduler.TickAsync(default);
        Assert.Equal(0, f.Reader.Reads);
        foreach (var owner in f.Owners)
        {
            await using var db = f.Database.Context(owner);
            Assert.Empty(await db.PaperExecutions.ToListAsync());
            Assert.All(await db.PaperSimulations.ToListAsync(), s => Assert.Equal(Open, s.CheckpointUtc));
        }
    }

    [PostgresFact]
    public async Task Healthy_empty_scan_writes_heartbeat_but_failed_schema_scan_does_not()
    {
        await using var f = await Fixture.CreateAsync();
        f.Directory.Empty = true;
        await f.Scheduler.StartAsync(default);
        try
        {
            await EventuallyAsync(() => File.Exists(f.Heartbeat));
            var fields = (await File.ReadAllTextAsync(f.Heartbeat)).Split(' ');
            Assert.Equal([f.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(), "120"], fields);
        }
        finally { await f.Scheduler.StopAsync(default); }
        Assert.Equal(0, f.Reader.Reads);

        var missing = f.Heartbeat + ".unmigrated";
        await using var data = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(f.Database.ConnectionString)
            { SearchPath = "paper_deliberately_unmigrated" }.ConnectionString);
        using var broken = new PaperScheduler(f.Services.GetRequiredService<IOwnerScopeFactory>(), f.Directory, data,
            f.Services.GetRequiredService<IServerStateStore>(), new() { HeartbeatFile = missing }, f.Clock,
            NullLogger<PaperScheduler>.Instance);
        await broken.StartAsync(default);
        try
        {
            await Task.Delay(100);
            Assert.False(File.Exists(missing));
            await Assert.ThrowsAsync<PostgresException>(() => broken.TickAsync(default));
        }
        finally { await broken.StopAsync(default); }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, limit.Token);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Open.AddMinutes(2);
    }
    private sealed class Directory(Guid[] ids) : IJournalOwnerDirectory
    {
        public Guid? Suspended;
        public bool Empty;
        public Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(!ids.Contains(ownerId) ? JournalOwnerStatus.Unknown :
                Suspended == ownerId ? JournalOwnerStatus.Suspended : JournalOwnerStatus.Active);
        public Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(Empty ? [] : ids.Where(x => x != Suspended &&
                (after is null || x.CompareTo(after.Value) > 0)).Order().Take(limit).ToArray());
    }
    private sealed class Reader : ICandleReader
    {
        public string VenueId => "hyperliquid";
        public int Reads;
        public Action? BeforeReturn;
        public bool CrossInstrumentLoss;
        public (string Contract, string Interval, long From, long To)? Last;
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            Last = (contractId, interval, fromMs, toMs);
            BeforeReturn?.Invoke();
            return Task.FromResult<IReadOnlyList<VenueCandle>>(Enumerable.Range(0, 2).Select(i =>
            {
                var loss = CrossInstrumentLoss && contractId == "BTC" && i == 1;
                return new VenueCandle(Open.AddMinutes(i).ToUnixTimeMilliseconds(), Open.AddMinutes(i + 1).ToUnixTimeMilliseconds() - 1,
                    loss ? "70" : "100", loss ? "71" : "101", loss ? "69" : "99", loss ? "70" : "100", "1", 1);
            }).ToArray());
        }
    }
    private sealed class Fixture(CoreDatabase database, ServiceProvider services, Guid[] owners, Reader reader,
        Directory directory, Clock clock, string heartbeat, PaperScheduler scheduler) : IAsyncDisposable
    {
        public CoreDatabase Database { get; } = database;
        public ServiceProvider Services { get; } = services;
        public Guid[] Owners { get; } = owners;
        public Reader Reader { get; } = reader;
        public Directory Directory { get; } = directory;
        public Clock Clock { get; } = clock;
        public string Heartbeat { get; } = heartbeat;
        public PaperScheduler Scheduler { get; } = scheduler;

        public static async Task<Fixture> CreateAsync(int maxPlaysPerTick = 200, bool crossInstrumentLoss = false)
        {
            var database = await CoreDatabase.CreateAsync();
            var owners = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
            foreach (var owner in owners)
            {
                await using var db = database.Context(owner);
                var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
                var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", 4000);
                account.Configure(portfolio.Id, null, null);
                db.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, 4000, Open.AddHours(-1)),
                    new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = 1 });
                for (var i = 0; i < 2; i++)
                {
                    var size = crossInstrumentLoss ? i == 0 ? "1000" : "2900" : "100";
                    var plan = new PlayPlanDocument("long", "margin", size, crossInstrumentLoss && i == 0 ? "10" : "1", null,
                        [new("e", "Entry", "#ffffff", "100", "100",
                            crossInstrumentLoss ? i == 0 ? [new("s", "price", "70", "100")] : [] : [new("s", "price", "90", "100")],
                            crossInstrumentLoss ? [] : [new("t", "price", "110", "100")])],
                        new("", "", "", ""));
                    var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", crossInstrumentLoss && i == 1 ? "ETH" : "BTC"),
                        InstrumentSource.Venue, PlayDocuments.Serialize(plan), Open.AddMinutes(-1));
                    var planned = play.MarkPlanned(Open.AddMinutes(-1));
                    db.AddRange(play, planned.Change, planned.Revision,
                        new PaperSimulation { OwnerId = owner, AccountId = account.Id, PlayId = play.Id,
                            CheckpointUtc = Open, NextCheckAtUtc = Open.AddMinutes(1) },
                        new PaperPlanActivation { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id,
                            PlayId = play.Id, PlanVersion = 1, PlanJson = play.Plan, EffectiveAtUtc = Open },
                        new PaperReservation { OwnerId = owner, AccountId = account.Id, PlayId = play.Id, AmountUsd = decimal.Parse(size) });
                }
                await db.SaveChangesAsync();
            }
            var directory = new Directory(owners);
            var reader = new Reader { CrossInstrumentLoss = crossInstrumentLoss };
            var clock = new Clock();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Vessel"] = database.ConnectionString }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IJournalOwnerDirectory>(directory);
            services.AddScoped<IJournalOwnerContext, AssignedOwnerContext>();
            services.AddScoped<IVenueRegistry>(_ => TestVenues.With(reader));
            services.AddVesselPersistence(config);
            services.AddPaperTrading();
            var provider = services.BuildServiceProvider();
            var heartbeat = Path.Combine(Path.GetTempPath(), "vessel-paper-test-" + Guid.NewGuid().ToString("N"));
            var scheduler = new PaperScheduler(provider.GetRequiredService<IOwnerScopeFactory>(), directory,
                provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<IServerStateStore>(),
                new() { HeartbeatFile = heartbeat, MaxPlaysPerTick = maxPlaysPerTick }, clock, NullLogger<PaperScheduler>.Instance);
            return new(database, provider, owners, reader, directory, clock, heartbeat, scheduler);
        }
        public async ValueTask DisposeAsync()
        {
            Scheduler.Dispose();
            await Services.DisposeAsync();
            await Database.DisposeAsync();
            if (File.Exists(Heartbeat)) File.Delete(Heartbeat);
        }
    }
}
