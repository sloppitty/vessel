using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vessel.Application.MarketData;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Worker;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

[CollectionDefinition("Paper corrective worker", DisableParallelization = true)]
public sealed class PaperCorrectiveWorkerCollection;

[Collection("Paper corrective worker")]
public sealed class PaperCorrectiveWorkerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstOpen = new(2026, 10, 20, 12, 1, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PlanEntry Entry(string id, string share, string price, PlanExit[] stops, PlanExit[] targets) =>
        new(id, id, "#ffffff", share, price, stops, targets);
    private static PlayPlanDocument Plan(string sizingMode, string size, string leverage, params PlanEntry[] entries) =>
        new("long", sizingMode, size, leverage, null, entries, new("", "", "", ""));
    private sealed class Directory(Guid[] ids) : IJournalOwnerDirectory
    {
        public Task<JournalOwnerStatus> StatusAsync(Guid ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(ids.Contains(ownerId) ? JournalOwnerStatus.Active : JournalOwnerStatus.Unknown);
        public Task<IReadOnlyList<Guid>> ActiveOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(ids.Where(x => after is null || x.CompareTo(after.Value) > 0).Order().Take(limit).ToArray());
    }

    /// <summary>Returns stored candles inside the requested window; optionally fails one contract or hides recent minutes.</summary>
    private sealed class Reader : ICandleReader
    {
        public string VenueId => "hyperliquid";
        public readonly Dictionary<string, Func<int, (decimal Value, decimal Low, decimal High)>> Prices = new(StringComparer.Ordinal);
        public string? Failing;
        public DateTimeOffset? PublishedBefore { get; set; }
        public (string Contract, int Minute)? Poison { get; set; }
        public (string Contract, int Minute)? Missing { get; set; }
        public readonly List<(string Contract, long From, long To)> Reads = [];
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs,
            CancellationToken cancellationToken)
        {
            lock (Reads) Reads.Add((contractId, fromMs, toMs));
            if (contractId == Failing) return Task.FromException<IReadOnlyList<VenueCandle>>(new VenueReadException("Hyperliquid is unavailable. Try again later."));
            var price = Prices[contractId];
            var rows = new List<VenueCandle>();
            for (var open = fromMs; open <= toMs; open += 60_000)
            {
                if (PublishedBefore is { } published && open + 60_000 > published.ToUnixTimeMilliseconds()) continue;
                var minute = (int)((open - FirstOpen.ToUnixTimeMilliseconds()) / 60_000);
                if (Missing == (contractId, minute)) continue;
                var (value, low, high) = price(minute);
                string Text(decimal x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);
                rows.Add(Poison == (contractId, minute)
                    ? new(open, open + 59_999, Text(value), Text(value), Text(value), Text(value), "1.0", 0) // n=0 but nonzero volume
                    : new(open, open + 59_999, Text(value), Text(high), Text(low), Text(value), "1", 1));
            }
            return Task.FromResult<IReadOnlyList<VenueCandle>>(rows);
        }
    }

    private sealed class Worker(CoreDatabase database, ServiceProvider services, Clock clock, ICandleReader reader,
        PaperScheduler scheduler, string heartbeat) : IAsyncDisposable
    {
        public CoreDatabase Database { get; } = database;
        public ServiceProvider Services { get; } = services;
        public Clock Clock { get; } = clock;
        public ICandleReader Reader { get; } = reader;
        public PaperScheduler Scheduler { get; } = scheduler;
        public string Heartbeat { get; } = heartbeat;

        public static async Task<Worker> CreateAsync(CoreDatabase database, Guid[] owners, ICandleReader reader, Clock clock, int maxCandlesPerRead)
        {
            var directory = new Directory(owners);
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
            var heartbeat = Path.Combine(Path.GetTempPath(), "vessel-opus-r3-" + Guid.NewGuid().ToString("N"));
            var scheduler = new PaperScheduler(provider.GetRequiredService<IOwnerScopeFactory>(), directory,
                provider.GetRequiredService<NpgsqlDataSource>(), provider.GetRequiredService<IServerStateStore>(),
                new() { HeartbeatFile = heartbeat, MaxCandlesPerRead = maxCandlesPerRead }, clock, NullLogger<PaperScheduler>.Instance);
            await Task.CompletedTask;
            return new(database, provider, clock, reader, scheduler, heartbeat);
        }

        public async ValueTask DisposeAsync()
        {
            Scheduler.Dispose();
            await Services.DisposeAsync();
            await Database.DisposeAsync();
            if (File.Exists(Heartbeat)) File.Delete(Heartbeat);
        }
    }

    /// <summary>Seeds an armed paper Play with the same rows ArmAsync writes (activation, frontier, reservation).</summary>
    private static async Task<(Guid Account, Guid[] Plays)> SeedAccountAsync(CoreDatabase database, Guid owner,
        params (string Contract, PlayPlanDocument Plan, decimal Reserved)[] plays)
    {
        await using var db = database.Context(owner);
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper " + owner.ToString("N")[..6], "paper", 4000);
        account.Configure(portfolio.Id, null, null);
        db.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, 4000, FirstOpen.AddHours(-1)));
        if (!await db.PaperSettings.AnyAsync())
            db.Add(new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = 1 });
        var ids = new List<Guid>();
        foreach (var (contract, plan, reserved) in plays)
        {
            var play = new Play(Guid.NewGuid(), account, new Vessel.Domain.Markets.PerpetualInstrument("hyperliquid", contract),
                InstrumentSource.Venue, PlayDocuments.Serialize(plan), FirstOpen.AddMinutes(-1));
            var planned = play.MarkPlanned(FirstOpen.AddMinutes(-1));
            db.AddRange(play, planned.Change, planned.Revision,
                new PaperSimulation { OwnerId = owner, AccountId = account.Id, PlayId = play.Id, CheckpointUtc = FirstOpen, NextCheckAtUtc = FirstOpen.AddMinutes(1) },
                new PaperPlanActivation { Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id,
                    PlanVersion = 1, PlanJson = play.Plan, EffectiveAtUtc = FirstOpen },
                new PaperReservation { OwnerId = owner, AccountId = account.Id, PlayId = play.Id, AmountUsd = reserved });
            ids.Add(play.Id);
        }
        await db.SaveChangesAsync();
        return (account.Id, ids.ToArray());
    }

    private static readonly PlayPlanDocument Never = Plan("margin", "100", "1",
        Entry("e", "100", "50", [new("s", "price", "40", "100")], [new("t", "price", "60", "100")]));
    private static readonly PlayPlanDocument Dip = Plan("margin", "100", "1",
        Entry("e", "100", "90", [new("s", "price", "80", "100")], [new("t", "price", "120", "100")]));

    private static async Task<(PaperSimulation State, int Executions)> ProgressAsync(CoreDatabase database, Guid owner, Guid play)
    {
        await using var db = database.Context(owner);
        return ((await db.PaperSimulations.AsNoTracking().SingleAsync(s => s.PlayId == play)),
            await db.PaperExecutions.CountAsync(e => e.PlayId == play));
    }

    private static async Task<(PaperSimulation State, int Executions)> RunSharedInstrumentScenarioAsync(bool withFailingCoin)
    {
        var database = await CoreDatabase.CreateAsync();
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        var reader = new Reader { Failing = "FOO" };
        // BTC trades at 100 until minute 6, which dips to 90 (owner 1's entry), then recovers to 95.
        reader.Prices["BTC"] = m => m < 6 ? (100, 99, 101) : m == 6 ? (90, 89, 91) : (95, 94, 96);
        var first = withFailingCoin
            ? await SeedAccountAsync(database, owners[0], ("FOO", Never, 100), ("BTC", Never, 100))
            : await SeedAccountAsync(database, owners[0], ("BTC", Never, 100));
        var victim = await SeedAccountAsync(database, owners[1], ("BTC", Dip, 100));
        var secondHealthy = await SeedAccountAsync(database, owners[2], ("BTC", Dip, 100));
        var clock = new Clock();
        // Production default is 1,440 candles per read; three makes the same lag threshold observable in minutes.
        await using var worker = await Worker.CreateAsync(database, owners, reader, clock, maxCandlesPerRead: 3);
        for (var tick = 1; tick <= 6; tick++)
        {
            clock.Now = FirstOpen.AddMinutes(2 * tick).AddSeconds(15);
            await worker.Scheduler.TickAsync(default);
        }
        _ = first;
        var result = await ProgressAsync(database, owners[1], victim.Plays[0]);
        var other = await ProgressAsync(database, owners[2], secondHealthy.Plays[0]);
        Assert.Equal(FirstOpen.AddMinutes(12), other.State.CheckpointUtc);
        Assert.Equal(1, other.Executions);
        return result;
    }

    [PostgresFact]
    public async Task Control_shared_instrument_progresses_and_executes_the_dip_without_a_failing_coin()
    {
        var (state, executions) = await RunSharedInstrumentScenarioAsync(withFailingCoin: false);
        Assert.Equal(FirstOpen.AddMinutes(12), state.CheckpointUtc);
        Assert.Equal(1, executions);
    }

    [PostgresFact]
    public async Task A_lagged_account_cannot_constrain_another_owners_frontier_on_a_shared_instrument()
    {
        var (state, executions) = await RunSharedInstrumentScenarioAsync(withFailingCoin: true);
        // Both healthy owners progress beyond the stalled account's read bound.
        Assert.Equal(FirstOpen.AddMinutes(12), state.CheckpointUtc);
        Assert.Equal("ready", state.State);
        Assert.Equal("[]", state.IssuesJson);
        Assert.Equal(1, executions); // the healthy owner executes its minute-6 touch once
    }


    [PostgresFact]
    public async Task Repeated_malformed_rows_expire_into_visible_gaps_instead_of_endless_transient_ready()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var reader = new Reader { Poison = ("BTC", 1) };
        reader.Prices["BTC"] = _ => (100, 99, 101);
        var seed = await SeedAccountAsync(database, owner, ("BTC", Never, 100));
        var clock = new Clock { Now = FirstOpen.AddMinutes(2).AddSeconds(15) };
        await using var worker = await Worker.CreateAsync(database, [owner], reader, clock, 3);
        await worker.Scheduler.TickAsync(default);
        clock.Now = clock.Now.AddMinutes(4);
        await worker.Scheduler.TickAsync(default);
        var (state, _) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Contains(state.State, new[] { "waiting", "ready" });
        Assert.Equal(FirstOpen, state.CheckpointUtc);
        clock.Now = clock.Now.AddMinutes(1);
        await worker.Scheduler.TickAsync(default);
        (state, _) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Equal("gap", state.State);
        Assert.Null(state.NextCheckAtUtc);
        Assert.Contains("source", state.IssuesJson, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresTheory]
    [InlineData(200, false, true, true)]   // malformed JSON
    [InlineData(200, false, false, true)]  // invalid native shape
    [InlineData(400, false, false, true)]
    [InlineData(404, false, false, true)]
    [InlineData(422, false, false, true)]
    [InlineData(503, false, false, false)]
    [InlineData(429, false, false, false)]
    [InlineData(200, true, false, false)]  // adapter timeout mapping
    public async Task Real_Hyperliquid_adapter_classifies_only_proven_source_failures_as_finite_recovery(
        int status, bool timeout, bool malformed, bool deterministic)
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var seed = await SeedAccountAsync(database, owner, ("BTC", Never, 100));
        var clock = new Clock { Now = FirstOpen.AddMinutes(2).AddSeconds(15) };
        using var client = new HttpClient(new SourceHandler(status, timeout, malformed)) { BaseAddress = new("https://example.invalid/") };
        var reader = new HyperliquidPerpetualReader(client, clock);
        await using var worker = await Worker.CreateAsync(database, [owner], reader, clock, 3);
        await worker.Scheduler.TickAsync(default);
        clock.Now = clock.Now.AddMinutes(5);
        await worker.Scheduler.TickAsync(default);
        var (state, executions) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Equal(deterministic ? "gap" : "waiting", state.State);
        Assert.Equal(FirstOpen, state.CheckpointUtc);
        Assert.Equal(0, executions);
        var server = worker.Services.GetRequiredService<IServerStateStore>();
        if (status == 429) Assert.True(await server.VenueRetryAtAsync("hyperliquid", default) > clock.Now);
        else Assert.Null(await server.VenueRetryAtAsync("hyperliquid", default));
    }

    private sealed class SourceHandler(int status, bool timeout, bool malformed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            timeout ? Task.FromException<HttpResponseMessage>(new OperationCanceledException()) :
                Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(malformed ? "not-json" : "{}") });
    }

    [PostgresFact]
    public async Task Late_publication_commits_only_the_proven_account_prefix_and_later_executes_exactly_once()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var plan = Plan("quantity", "1", "1", Entry("e", "100", "100", [new("s", "price", "90", "100")],
            [new("t", "price", "110", "100")]));
        var seed = await SeedAccountAsync(database, owner, ("BTC", plan, 100));
        var reader = new Reader { PublishedBefore = FirstOpen.AddMinutes(1) };
        reader.Prices["BTC"] = m => m == 0 ? (100, 99, 101) : (110, 109, 111);
        var clock = new Clock { Now = FirstOpen.AddMinutes(2).AddSeconds(15) };
        await using var worker = await Worker.CreateAsync(database, [owner], reader, clock, 3);
        await worker.Scheduler.TickAsync(default);
        var (state, executions) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Equal("ready", state.State);
        Assert.Equal(FirstOpen.AddMinutes(1), state.CheckpointUtc);
        Assert.Equal(clock.Now.AddMinutes(1), state.NextCheckAtUtc);
        Assert.Contains("publication", state.IssuesJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, executions);
        reader.PublishedBefore = null;
        clock.Now = clock.Now.AddMinutes(1);
        await worker.Scheduler.TickAsync(default);
        await worker.Scheduler.TickAsync(default);
        (state, executions) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Equal("ready", state.State);
        Assert.Equal(2, executions);
        Assert.Null(state.NextCheckAtUtc);
        Assert.Equal("[]", state.IssuesJson);
        await using var db = database.Context(owner);
        Assert.Equal(PlayStatus.Closed, (await db.Plays.FindAsync(seed.Plays[0]))!.Status);
        Assert.Equal(10m, await db.PaperExecutions.SumAsync(e => e.PnlUsd));
    }

    [PostgresTheory]
    [InlineData(299999, false)]
    [InlineData(300000, true)]
    [InlineData(3600000, true)]
    public async Task Publication_grace_expires_from_the_missing_candles_close_not_first_observation(int ageMs, bool gap)
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var seed = await SeedAccountAsync(database, owner, ("BTC", Never, 100));
        var reader = new Reader { PublishedBefore = FirstOpen.AddMinutes(1) };
        reader.Prices["BTC"] = _ => (100, 99, 101);
        var clock = new Clock { Now = FirstOpen.AddMinutes(2).AddMilliseconds(ageMs) };
        await using var worker = await Worker.CreateAsync(database, [owner], reader, clock, 100);
        await worker.Scheduler.TickAsync(default);
        var (state, executions) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Equal(gap ? "gap" : "ready", state.State);
        Assert.Equal(FirstOpen.AddMinutes(1), state.CheckpointUtc);
        Assert.Equal(0, executions);
        if (gap) Assert.Null(state.NextCheckAtUtc);
        else Assert.Equal(clock.Now.AddMinutes(1), state.NextCheckAtUtc);
    }

    [PostgresFact]
    public async Task An_interior_missing_minute_remains_a_gap_even_inside_the_publication_grace()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var seed = await SeedAccountAsync(database, owner, ("BTC", Never, 100));
        var reader = new Reader { Missing = ("BTC", 1) };
        reader.Prices["BTC"] = _ => (100, 99, 101);
        var clock = new Clock { Now = FirstOpen.AddMinutes(3).AddSeconds(15) };
        await using var worker = await Worker.CreateAsync(database, [owner], reader, clock, 100);
        await worker.Scheduler.TickAsync(default);
        var (state, _) = await ProgressAsync(database, owner, seed.Plays[0]);
        Assert.Equal("gap", state.State);
        Assert.Equal(FirstOpen.AddMinutes(1), state.CheckpointUtc);
        Assert.Null(state.NextCheckAtUtc);
    }

    [PostgresFact]
    public async Task Only_the_failed_symbol_terminalizes_and_known_sibling_protection_still_executes()
    {
        var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var plan = Plan("quantity", "1", "1", Entry("e", "100", "100", [new("s", "price", "90", "100")], []));
        var seed = await SeedAccountAsync(database, owner, ("BTC", plan, 100), ("FOO", Never, 100));
        var reader = new Reader { Poison = ("FOO", 1) };
        reader.Prices["BTC"] = m => m < 2 ? (100, 99, 101) : (85, 84, 86);
        reader.Prices["FOO"] = _ => (200, 199, 201);
        var clock = new Clock { Now = FirstOpen.AddMinutes(1).AddSeconds(15) };
        await using var worker = await Worker.CreateAsync(database, [owner], reader, clock, 100);
        await worker.Scheduler.TickAsync(default); // entry before the bad source minute
        clock.Now = clock.Now.AddMinutes(1);
        await worker.Scheduler.TickAsync(default); // deterministic failure first seen
        clock.Now = clock.Now.AddMinutes(5);
        await worker.Scheduler.TickAsync(default); // only FOO becomes a terminal gap
        Assert.Equal("gap", (await ProgressAsync(database, owner, seed.Plays[1])).State.State);
        Assert.NotEqual("gap", (await ProgressAsync(database, owner, seed.Plays[0])).State.State);
        clock.Now = clock.Now.AddMinutes(1);
        await worker.Scheduler.TickAsync(default);
        await using var db = database.Context(owner);
        var stop = Assert.Single(await db.PaperExecutions.Where(e => e.Role == "stop").ToListAsync());
        Assert.Equal((seed.Plays[0], 90m, 1m, -10m), (stop.PlayId, stop.Price, stop.Quantity, stop.PnlUsd));
        Assert.Equal(PlayStatus.Closed, (await db.Plays.FindAsync(seed.Plays[0]))!.Status);
        Assert.Empty(await db.PaperExecutions.Where(e => e.PlayId == seed.Plays[1]).ToListAsync());
    }
}
