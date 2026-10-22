using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6b: an owner-started backfill of a history gap, read by the monitor behind protection checks. It closes
/// the gap only when the venue still serves the gap's starting execution, and otherwise leaves the unserved span open as
/// a gap beyond the venue's reach.
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
            return Task.FromResult(new VenueFillPage(page, page.Length, page.FirstOrDefault()?.OccurredAtUtc, page.LastOrDefault()?.OccurredAtUtc, 0));
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

    private static async Task RunUntilDoneAsync(BackfillSetup s)
    {
        for (var run = 0; run < 6 && await s.Store.ActiveBackfillAsync(s.AccountId, default) is not null; run++)
        {
            s.Clock.Now = (await MonitorRowAsync(s.Db)).NextRunAtUtc!.Value.AddSeconds(1);
            await ScheduledCheckAsync(s.Coordinator, s.Store, s.Clock);
        }
        Assert.Null(await s.Store.ActiveBackfillAsync(s.AccountId, default));
    }

    [PostgresFact]
    public async Task Phase2_a_backfill_that_finds_the_gaps_starting_fill_closes_the_gap_and_announces_nothing()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await GapAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        var gap = Assert.Single(await db.HistoryGaps.AsNoTracking().ToListAsync());
        Assert.Equal((s.Boundary.OccurredAtUtc, s.OldestRead.OccurredAtUtc), (gap.FromUtc!.Value, gap.ToUtc!.Value));
        Assert.True((await MonitorRowAsync(db)).HasHistoryGap);
        Assert.Equal(4, await db.Fills.CountAsync());

        var started = await s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default);
        Assert.Equal((HistoryBackfill.Queued, s.Boundary.OccurredAtUtc, s.OldestRead.OccurredAtUtc), (started.State, started.FromUtc, started.ToUtc));
        // One backfill at a time.
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => s.Coordinator.StartBackfillAsync(s.AccountId, gap.Id, default))).StatusCode);
        await RunUntilDoneAsync(s);

        db.ChangeTracker.Clear();
        Assert.Equal(7, await db.Fills.CountAsync());
        var backfill = Assert.Single(await db.HistoryBackfills.AsNoTracking().ToListAsync());
        Assert.Equal((HistoryBackfill.Completed, 3, s.Boundary.OccurredAtUtc), (backfill.State, backfill.AddedFills, backfill.FirstRawAtUtc!.Value));
        // Two pages a run: the window took more than one run, and every added fill was claimed as history.
        Assert.True(backfill.Pages > 2);
        Assert.Equal(3, s.Sink.HistoricalFills);
        var record = await db.FillCoverage.AsNoTracking().SingleAsync(r => r.Source == FillCoverageRecord.Backfill);
        Assert.Equal((s.Boundary.OccurredAtUtc, s.OldestRead.OccurredAtUtc), (record.CoveredFromUtc!.Value, record.CoveredToUtc!.Value));
        var closed = await db.HistoryGaps.AsNoTracking().SingleAsync();
        Assert.Equal(record.Id, closed.ClosedByCoverageId);
        Assert.False((await MonitorRowAsync(db)).HasHistoryGap);
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
        await RunUntilDoneAsync(s);

        db.ChangeTracker.Clear();
        var backfill = Assert.Single(await db.HistoryBackfills.AsNoTracking().ToListAsync());
        var firstServed = s.Venue.Fills[1].OccurredAtUtc;
        Assert.Equal((HistoryBackfill.Narrowed, firstServed), (backfill.State, backfill.FirstRawAtUtc!.Value));
        var gaps = await db.HistoryGaps.AsNoTracking().OrderBy(g => g.OpenedAtUtc).ToListAsync();
        Assert.Equal(2, gaps.Count);
        Assert.False(gaps[0].IsOpen);
        // What remains open is the unserved span before the first fill read, beyond the venue's reach.
        Assert.Equal((true, true, s.Boundary.OccurredAtUtc, firstServed, backfill.Id), (gaps[1].IsOpen, gaps[1].BeyondVenueReach,
            gaps[1].FromUtc!.Value, gaps[1].ToUtc!.Value, gaps[1].OpenedByBackfillId!.Value));
        Assert.True((await MonitorRowAsync(db)).HasHistoryGap);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => s.Coordinator.StartBackfillAsync(s.AccountId, gaps[1].Id, default))).StatusCode);
    }
}
