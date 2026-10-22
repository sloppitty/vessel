using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6b: an owner-started backfill of a history gap, read in runs of its own behind protection checks. It
/// proves what its witnesses vouch for, closes that part of the gap, and leaves the rest open: beyond the venue's reach
/// when the venue no longer serves it, or open to another backfill when its continuity broke.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    /// <summary>A venue that serves only its latest <see cref="Reach"/> fills: the latest <see cref="Cap"/> per account read, and time pages.</summary>
    private sealed class BackfillVenue(Clock clock) : IPerpetualVenueReader, IVenueFillHistoryReader
    {
        public List<VenueFill> Fills { get; } = [];
        public int Cap { get; set; } = 3;
        public int PageLimit { get; set; } = 2;
        public int Reach { get; set; } = 100;
        public int Pages;
        /// <summary>Runs after each page, as the venue's reach can move between pages.</summary>
        public Action<int>? AfterPage { get; set; }
        public string VenueId => "hyperliquid";
        public VenueFill Add(DateTimeOffset at)
        {
            var n = Fills.Count;
            var fill = new VenueFill($"fill-{n}", "BTC", "buy", "Open Long", 100, 1, 0, "USDC", 0, at, $"order-{n}", $"tx-{n}", "{}",
                PositionEffect: ExecutionFacts.Open);
            Fills.Add(fill);
            return fill;
        }
        private IEnumerable<VenueFill> Served => Fills.OrderByDescending(f => f.OccurredAtUtc).Take(Reach);
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct)
        {
            var latest = Served.Take(Cap).ToArray();
            return Task.FromResult(new PerpetualVenueReadResult(new(clock.Now, "synthetic", 100, 100, 0, []), [], latest, "Synthetic",
                PositionsComplete: true, FillHistoryTruncated: latest.Length >= Cap,
                OldestRawFillAtUtc: latest.Length == 0 ? null : latest.Min(f => f.OccurredAtUtc)));
        }
        public Task<VenueFillPage> ReadFillPageAsync(Account account, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            Pages++;
            var page = Served.Where(f => f.OccurredAtUtc >= fromUtc && f.OccurredAtUtc <= toUtc).OrderBy(f => f.OccurredAtUtc).Take(PageLimit).ToArray();
            AfterPage?.Invoke(Pages);
            return Task.FromResult(new VenueFillPage(page, page.Select(f => new VenueRawRow(f.SourceFillId, f.OccurredAtUtc)).ToList(), 0));
        }
    }

    private sealed record BackfillSetup(VesselDbContext Db, MonitorStore Store, MonitorCoordinator Coordinator, BackfillVenue Venue,
        EventSink Sink, Clock Clock, Guid AccountId, VenueFill Boundary, VenueFill OldestRead);

    /// <summary>
    /// An account that keeps its history, checked once, then again after a pause whose capped read starts after the last
    /// known fill: a gap from that fill to the read's oldest, with three fills missing between them.
    /// </summary>
    private static async Task<BackfillSetup> GapAsync(CoreDatabase database, Guid owner)
    {
        var id = (await AccountsAsync(database, owner, 1)).Single();
        var start = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var db = database.Context(owner);
        await KeepHistoryAsync(db, id, true);
        var venue = new BackfillVenue(clock);
        var boundary = venue.Add(start.AddHours(-3));
        var work = TestUnitOfWork.For(db);
        var options = new MonitorOptions();
        var store = MonitorTestPaths.Store(db, work, options);
        var sink = new EventSink();
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(store, clock), new WorkspaceStore(db, work), new Importer(),
            TestVenues.With(venue, new Orders()), new Credentials(), work, sink, new Commands(), new CoreOwner(owner), options, clock);
        await ScheduledCheckAsync(coordinator, store, clock);
        VenueFill? oldest = null;
        for (var i = 1; i <= 6; i++) { var fill = venue.Add(start.AddMinutes(i)); if (i == 4) oldest = fill; }
        clock.Now = (await MonitorRowAsync(db)).NextRunAtUtc!.Value.AddSeconds(1);
        await ScheduledCheckAsync(coordinator, store, clock);
        return new(db, store, coordinator, venue, sink, clock, id, boundary, oldest!);
    }

    /// <summary>Claims and runs backfill runs as the scheduler would, each when its next page is due.</summary>
    private static async Task<int> RunBackfillsAsync(BackfillSetup s)
    {
        var runs = 0;
        for (; runs < 6 && await s.Store.ActiveBackfillAsync(s.AccountId, default) is { } active; runs++)
        {
            // Not before its pacing.
            Assert.Null(await s.Store.ClaimBackfillAsync(s.AccountId, "backfill-worker", active.NextPageAtUtc.AddSeconds(-1), default));
            s.Clock.Now = (active.NextPageAtUtc > s.Clock.Now ? active.NextPageAtUtc : s.Clock.Now).AddSeconds(1);
            var lease = await s.Store.ClaimBackfillAsync(s.AccountId, "backfill-worker", s.Clock.Now, default);
            Assert.Equal("backfill", lease!.Trigger);
            await s.Coordinator.RunAsync(lease, default);
            // The run releases its own lease.
            Assert.Equal("idle", (await MonitorRowAsync(s.Db)).ManualState);
        }
        Assert.Null(await s.Store.ActiveBackfillAsync(s.AccountId, default));
        return runs;
    }

    [PostgresFact]
    public async Task Phase2_a_backfill_that_finds_the_gaps_starting_fill_closes_the_gap_in_its_own_runs_and_announces_nothing()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await GapAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        var gap = Assert.Single(await db.HistoryGaps.AsNoTracking().ToListAsync());
        Assert.Equal((s.Boundary.OccurredAtUtc, s.OldestRead.OccurredAtUtc), (gap.FromUtc!.Value, gap.ToUtc!.Value));
        var before = await MonitorRowAsync(db);
        Assert.True(before.HasHistoryGap);
        Assert.Equal(4, await db.Fills.CountAsync());

        var started = await s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default);
        Assert.Equal((HistoryBackfill.Queued, s.Boundary.OccurredAtUtc, HistoryBackfill.Floor(s.Clock.Now)), (started.State, started.FromUtc, started.ToUtc));
        // One backfill at a time.
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default))).StatusCode);
        // The scheduler finds it due; checks never read it.
        await using (var data = NpgsqlDataSource.Create(database.ConnectionString))
            Assert.Equal(s.AccountId, Assert.Single(await new SystemMonitorStore(data, s.Clock).DueBackfillsAsync(10, default)).AccountId);
        // Never beside another run of the account.
        var check = await s.Store.ClaimAsync(s.AccountId, null, "checker", before.NextRunAtUtc!.Value.AddSeconds(1), default);
        Assert.NotNull(check);
        Assert.Null(await s.Store.ClaimBackfillAsync(s.AccountId, "backfill-worker", before.NextRunAtUtc!.Value.AddSeconds(1), default));
        s.Clock.Now = before.NextRunAtUtc!.Value.AddSeconds(1);
        await s.Coordinator.RunAsync(check!, default);
        Assert.Equal(0, s.Venue.Pages);
        var scheduled = await MonitorRowAsync(db);

        // Seven pages of two: more than one run of five.
        var mark = s.Sink.RunTriggers.Count;
        Assert.Equal(2, await RunBackfillsAsync(s));
        db.ChangeTracker.Clear();
        Assert.Equal(7, await db.Fills.CountAsync());
        Assert.Equal(3, await db.Fills.CountAsync(f => f.Backfilled));
        var backfill = Assert.Single(await db.HistoryBackfills.AsNoTracking().ToListAsync());
        Assert.Equal((HistoryBackfill.Completed, 3, 7, (bool?)true, s.Boundary.OccurredAtUtc),
            (backfill.State, backfill.AddedFills, backfill.Pages, backfill.StartServed, backfill.FirstRawAtUtc!.Value));
        // Every added fill was claimed as history, under the owner's own trigger.
        Assert.Equal(3, s.Sink.HistoricalFills);
        Assert.Equal(7, s.Sink.RunTriggers.Count - mark);
        Assert.All(s.Sink.RunTriggers.Skip(mark), trigger => Assert.Equal("owner", trigger));
        var record = await db.FillCoverage.AsNoTracking().SingleAsync(r => r.Source == FillCoverageRecord.Backfill);
        Assert.Equal((s.Boundary.OccurredAtUtc, backfill.ToUtc), (record.CoveredFromUtc!.Value, record.CoveredToUtc!.Value));
        var closed = await db.HistoryGaps.AsNoTracking().SingleAsync();
        Assert.Equal(record.Id, closed.ClosedByCoverageId);
        var after = await MonitorRowAsync(db);
        Assert.False(after.HasHistoryGap);
        // Backfill runs leave the check schedule, attempt time and state alone.
        Assert.Equal((scheduled.NextRunAtUtc, scheduled.LastAttemptAtUtc, scheduled.State, scheduled.NextManualAllowedAtUtc),
            (after.NextRunAtUtc, after.LastAttemptAtUtc, after.State, after.NextManualAllowedAtUtc));
        // A closed gap cannot be backfilled again.
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => s.Coordinator.StartBackfillAsync(s.AccountId, Guid.NewGuid(), default))).StatusCode);
    }

    [PostgresFact]
    public async Task Phase2_a_backfill_whose_start_is_no_longer_served_proves_only_what_it_read_and_leaves_the_rest_beyond_reach()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await GapAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        var gap = Assert.Single(await db.HistoryGaps.AsNoTracking().ToListAsync());
        // The venue now serves only its latest six fills: the gap's starting fill is gone.
        s.Venue.Reach = 6;
        await s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default);
        await RunBackfillsAsync(s);

        db.ChangeTracker.Clear();
        var backfill = Assert.Single(await db.HistoryBackfills.AsNoTracking().ToListAsync());
        var firstServed = s.Venue.Fills[1].OccurredAtUtc;
        Assert.Equal((HistoryBackfill.Narrowed, (bool?)false, firstServed), (backfill.State, backfill.StartServed, backfill.FirstRawAtUtc!.Value));
        var gaps = await db.HistoryGaps.AsNoTracking().OrderBy(g => g.OpenedAtUtc).ToListAsync();
        Assert.Equal(2, gaps.Count);
        Assert.False(gaps[0].IsOpen);
        // What remains open is the unserved span before the first fill read, beyond the venue's reach.
        Assert.Equal((true, true, s.Boundary.OccurredAtUtc, firstServed, backfill.Id), (gaps[1].IsOpen, gaps[1].BeyondVenueReach,
            gaps[1].FromUtc!.Value, gaps[1].ToUtc!.Value, gaps[1].OpenedByBackfillId!.Value));
        Assert.True((await MonitorRowAsync(db)).HasHistoryGap);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => s.Coordinator.StartBackfillAsync(s.AccountId, gaps[1].Id, default))).StatusCode);
    }

    [PostgresFact]
    public async Task Phase2_a_backfill_whose_reach_moves_between_pages_proves_only_up_to_its_last_witness_and_reopens_the_rest()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await GapAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        var gap = Assert.Single(await db.HistoryGaps.AsNoTracking().ToListAsync());
        // After two pages the venue drops its oldest fills, the second page's last one included.
        s.Venue.AfterPage = page => { if (page == 2) s.Venue.Reach = 4; };
        await s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default);
        await RunBackfillsAsync(s);

        db.ChangeTracker.Clear();
        var backfill = Assert.Single(await db.HistoryBackfills.AsNoTracking().ToListAsync());
        var lastWitness = s.Venue.Fills[2].OccurredAtUtc;
        Assert.Equal((HistoryBackfill.Narrowed, true, 3), (backfill.State, backfill.ContinuityLost, backfill.Pages));
        var record = await db.FillCoverage.AsNoTracking().SingleAsync(r => r.Source == FillCoverageRecord.Backfill);
        Assert.Equal((s.Boundary.OccurredAtUtc, lastWitness), (record.CoveredFromUtc!.Value, record.CoveredToUtc!.Value));
        // The rest of the gap stays open, and the venue may still serve it: another backfill can try.
        var rest = await db.HistoryGaps.AsNoTracking().SingleAsync(g => g.ClosedAtUtc == null);
        Assert.Equal((lastWitness, s.OldestRead.OccurredAtUtc, false), (rest.FromUtc!.Value, rest.ToUtc!.Value, rest.BeyondVenueReach));
        s.Venue.AfterPage = null;
        await s.Coordinator.StartBackfillAsync(s.AccountId, rest.Id, default);
        await RunBackfillsAsync(s);
        db.ChangeTracker.Clear();
        // Its start (the last witness) is gone, so what precedes the venue's oldest fill is beyond its reach.
        var left = await db.HistoryGaps.AsNoTracking().SingleAsync(g => g.ClosedAtUtc == null);
        Assert.Equal((lastWitness, s.Venue.Fills[3].OccurredAtUtc, true), (left.FromUtc!.Value, left.ToUtc!.Value, left.BeyondVenueReach));
    }

    [PostgresFact]
    public async Task Phase2_a_whole_history_that_fits_the_reach_closes_every_gap_including_one_from_before_the_ledger()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var s = await GapAsync(database, owner);
        await using var db = s.Db;
        db.HistoryGaps.Add(new AccountHistoryGap { Id = Guid.CreateVersion7(), OwnerId = owner, AccountId = s.AccountId, OpenedAtUtc = s.Clock.Now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var legacy = await db.HistoryGaps.AsNoTracking().SingleAsync(g => g.FromUtc == null);
        await s.Coordinator.StartBackfillAsync(s.AccountId, legacy.Id, default);
        await RunBackfillsAsync(s);

        db.ChangeTracker.Clear();
        var record = await db.FillCoverage.AsNoTracking().SingleAsync(r => r.Source == FillCoverageRecord.Backfill);
        Assert.True(record.Complete);
        Assert.Equal(HistoryBackfill.Completed, (await db.HistoryBackfills.AsNoTracking().SingleAsync()).State);
        Assert.All(await db.HistoryGaps.AsNoTracking().ToListAsync(), g => Assert.Equal(record.Id, g.ClosedByCoverageId));
        Assert.False((await MonitorRowAsync(db)).HasHistoryGap);
    }
}
