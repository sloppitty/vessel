using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.4b, Refresh continuity (review defect D-02, owner decision 10): every import, Refresh included,
/// checks fill-window continuity against the newest fill known before it. Refresh can set a gap and advances the
/// continuity watermark, but never clears a gap and never moves check state, backoff, last success or due time.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    private const string GapError = "The venue fill window may have a gap. Missing executions cannot be ruled out.";

    /// <summary>
    /// Ported from the review probe <c>verify-new-dotnet/NewVerify.cs</c> <c>Watermark</c>: 3,501 of 3,501 fills
    /// retained through Refresh used to make the next check report a gap.
    /// </summary>
    [PostgresFact]
    public async Task Phase0_refreshes_that_retain_every_fill_leave_no_false_gap_for_the_next_check()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var october1 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new Clock(october1.AddHours(2));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        // Hyperliquid's userFills: the latest 2,000 fills, capped when 2,000 come back.
        var venue = new VenueHistory { Cap = 2000 };
        venue.Add("BTC", october1.AddHours(1));
        var reader = new Reader { Shape = read => venue.Shape(read, clock.Now) };
        var coordinator = Coordinator(db, owner, reader, clock: clock);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));

        // One scheduled check sets the continuity watermark at the October 1 fill.
        await ScheduledCheckAsync(coordinator, store, clock);
        var checkedRow = await MonitorRowAsync(db);
        Assert.Equal(("live", false), (checkedRow.State, checkedRow.HasHistoryGap));
        Assert.Equal(october1.AddHours(1), checkedRow.NewestFillAtUtc);

        // Then 14 days of 250 fills a day, imported only by Refresh while the latest-2,000 window moves on.
        var venues = TestVenues.With(reader, new Orders());
        for (var day = 1; day <= 14; day++)
        {
            for (var i = 0; i < 250; i++) venue.Add("BTC", october1.AddDays(day).AddMinutes(5 * i));
            clock.Now = october1.AddDays(day).AddHours(22);
            await MonitorTestPaths.RefreshAsync(db, owner, venues, id, time: clock);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(3501, await db.Fills.CountAsync());
        var refreshed = await MonitorRowAsync(db);
        // Refresh left the check's state, last success, failures and due time alone.
        Assert.Equal(("live", checkedRow.LastSuccessAtUtc, checkedRow.NextRunAtUtc, 0, checkedRow.StateRevision),
            (refreshed.State, refreshed.LastSuccessAtUtc, refreshed.NextRunAtUtc, refreshed.ConsecutiveFailures, refreshed.StateRevision));

        // The next scheduled check reads the latest 2,000 (capped). Every fill before them is retained.
        clock.Now = october1.AddDays(15);
        await ScheduledCheckAsync(coordinator, store, clock);
        var row = await MonitorRowAsync(db);
        Assert.Equal((false, "live", (string?)null), (row.HasHistoryGap, row.State, row.Error));
        Assert.Equal(venue.Fills[^1].OccurredAtUtc, row.NewestFillAtUtc);
    }

    /// <summary>
    /// Negative control for the probe: after a long pause, a capped Refresh whose oldest raw fill is newer than everything
    /// retained records a real gap. The next checks surface it and suppress missing and short protection; later
    /// uncapped reads and Refreshes never clear it. The uncapped variant shows the same Play would be flagged.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Phase0_a_capped_refresh_after_a_pause_records_a_sticky_gap_that_suppresses_protection(bool capped)
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        await database.Seed(f);
        await using var scope = database.Scope(f);
        // A venue whose read returns at most 50 fills; the fixture's entry fill is the only one retained so far.
        var venue = new VenueHistory { Cap = 50 };
        var entry = Assert.Single(f.Store.Fills);
        venue.Add(entry.ContractId, entry.OccurredAtUtc, entry.SourceFillId, entry.ExternalOrderId);
        var options = new MonitorOptions();
        var store = MonitorTestPaths.Store(scope.Db, scope.Work, options);
        var evaluator = new MonitorDiscrepancyService(scope.Store, scope.Work, new CoreOwner(f.Account.OwnerId), scope.Events, options, f.Clock);
        var coordinator = new MonitorCoordinator(store, new WorkspaceStore(scope.Db, scope.Work), new Importer(),
            TestVenues.With(new Reader { Shape = _ => venue.Shape(f.Observation().Read.Sync, f.Clock.Now) }, new FixtureOrders(f)),
            new Credentials(), scope.Work, scope.Events, new Commands(), new CoreOwner(f.Account.OwnerId), options, f.Clock, evaluator);
        async Task<MonitorAccount> Row()
        {
            scope.Db.ChangeTracker.Clear();
            return await scope.Db.Set<MonitorAccount>().AsNoTracking().SingleAsync();
        }
        async Task Refresh() =>
            Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "api", default), default));
        async Task CheckWhenDue()
        {
            // An overdue check runs now (a Refresh never moves its due time); otherwise wait until it is due.
            var due = (await Row()).NextRunAtUtc!.Value;
            f.Clock.Now = (due > f.Clock.Now ? due : f.Clock.Now).AddSeconds(1);
            await ScheduledCheckAsync(coordinator, store, f.Clock);
        }
        async Task<IReadOnlyList<PlayDiscrepancy>> Protection()
        {
            scope.Db.ChangeTracker.Clear();
            return await scope.Db.Set<PlayDiscrepancy>().AsNoTracking()
                .Where(x => x.Kind == "protection.missing" || x.Kind == "protection.short").ToListAsync();
        }

        // A protected, live account: the check sets the watermark at the retained entry fill.
        var started = f.Clock.Now;
        await ScheduledCheckAsync(coordinator, store, f.Clock);
        var live = await Row();
        Assert.Equal(("live", false, entry.OccurredAtUtc), (live.State, live.HasHistoryGap, live.NewestFillAtUtc));

        // Ten days later the stop no longer rests, and 60 (capped) or 40 (uncapped) fills have happened since.
        f.Advance(10 * 24 * 60 * 60);
        f.VenueOrders.Clear();
        for (var i = 0; i < (capped ? 60 : 40); i++) venue.Add("ETH", started.AddDays(9).AddMinutes(i));
        await Refresh();
        var refreshed = await Row();
        Assert.Equal(capped, refreshed.HasHistoryGap);
        Assert.Equal(venue.Fills[^1].OccurredAtUtc, refreshed.NewestFillAtUtc);
        // The gap is evidence only; the check's state, last success and due time are the check's.
        Assert.Equal(("live", started, live.NextRunAtUtc, 0), (refreshed.State, refreshed.LastSuccessAtUtc,
            refreshed.NextRunAtUtc, refreshed.ConsecutiveFailures));

        // Uncapped reads from now on: the venue returns every fill.
        venue.Cap = 1000;
        await CheckWhenDue();
        await CheckWhenDue();
        var row = await Row();
        if (!capped)
        {
            Assert.Equal(("live", false), (row.State, row.HasHistoryGap));
            Assert.Contains(await Protection(), x => x.Kind == "protection.missing" && x.State == "open");
            return;
        }
        Assert.Equal(("degraded", true, GapError), (row.State, row.HasHistoryGap, row.Error));
        Assert.Empty(await Protection());

        // An uncapped Refresh and another check do not clear it either.
        f.Advance((int)options.ManualCooldown.TotalSeconds + 1);
        await Refresh();
        Assert.True((await Row()).HasHistoryGap);
        await CheckWhenDue();
        row = await Row();
        Assert.Equal(("degraded", true, GapError), (row.State, row.HasHistoryGap, row.Error));
        Assert.Empty(await Protection());
    }

    /// <summary>With nothing retained and no watermark, a capped first import has no earlier fill to be discontinuous with.</summary>
    [PostgresTheory]
    [InlineData("refresh")]
    [InlineData("check")]
    public async Task Phase0_a_first_ever_capped_import_records_no_gap(string trigger)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        await SeedPlannedAsync(db, clock.Now);
        var venue = new VenueHistory { Cap = 20 };
        for (var i = 0; i < 25; i++) venue.Add("BTC", clock.Now.AddHours(-2).AddMinutes(i));
        var reader = new Reader { Shape = read => venue.Shape(read, clock.Now) };
        var coordinator = Coordinator(db, owner, reader, clock: clock);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        if (trigger == "refresh")
            Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(id, "api", default), default));
        else
            await ScheduledCheckAsync(coordinator, store, clock);
        var row = await MonitorRowAsync(db);
        Assert.False(row.HasHistoryGap);
        Assert.Equal(venue.Fills[^1].OccurredAtUtc, row.NewestFillAtUtc);
        Assert.Equal(trigger == "refresh" ? "idle" : "live", row.State);

        // The next check of the same capped window is continuous with what the first import retained.
        clock.Now = clock.Now.AddMinutes(trigger == "refresh" ? 1 : 15);
        await ScheduledCheckAsync(coordinator, store, clock);
        row = await MonitorRowAsync(db);
        Assert.Equal(("live", false), (row.State, row.HasHistoryGap));
    }

    /// <summary>
    /// Refresh records its own continuity (a capped gap, an advanced watermark) and nothing else: state, its revision,
    /// failures, last attempt and success, error, due time and credential revision stay the check's. A read with
    /// unknown truncation records nothing, and a stored gap is never cleared by a Refresh.
    /// </summary>
    [PostgresTheory]
    [InlineData("capped")]
    [InlineData("continuous")]
    [InlineData("unknown")]
    [InlineData("stored-gap")]
    public async Task Phase0_refresh_records_only_continuity_and_leaves_check_state_backoff_and_due_time(string read)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var watermark = now.AddHours(-2);
        await using var db = database.Context(owner);
        var seeded = Row(owner, id, "stale", now);
        seeded.NewestFillAtUtc = watermark;
        seeded.HasHistoryGap = read == "stored-gap";
        seeded.CredentialRevision = "revision-before-refresh";
        db.Add(seeded);
        await db.SaveChangesAsync();
        // Four fills since the watermark; a three-fill cap leaves the oldest of them unread.
        var venue = new VenueHistory { Cap = read == "capped" ? 3 : 10 };
        for (var i = 4; i >= 1; i--) venue.Add("BTC", now.AddMinutes(-10 * i));
        var reader = new Reader
        {
            Shape = observed => read == "unknown"
                ? venue.Shape(observed, now) with { FillHistoryTruncated = null, OldestRawFillAtUtc = null }
                : venue.Shape(observed, now)
        };
        var coordinator = Coordinator(db, owner, reader, clock: new Clock(now));
        Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(id, "api", default), default));
        db.ChangeTracker.Clear();
        var row = await db.Set<MonitorAccount>().SingleAsync();
        AssertUnchanged(row, now);
        Assert.Equal("revision-before-refresh", row.CredentialRevision);
        Assert.Equal(read is "capped" or "stored-gap", row.HasHistoryGap);
        Assert.Equal(read == "unknown" ? watermark : venue.Fills[^1].OccurredAtUtc, row.NewestFillAtUtc);
        Assert.Equal(read == "capped" ? 3 : 4, await db.Fills.CountAsync());
    }

    private static async Task ScheduledCheckAsync(MonitorCoordinator coordinator, MonitorStore store, TimeProvider clock)
    {
        await coordinator.DiscoverAsync(["hyperliquid"], clock.GetUtcNow(), default);
        var lease = Assert.Single(await store.ClaimDueAsync("scheduler", clock.GetUtcNow(), 2, default));
        Assert.NotEqual("refresh", lease.Trigger);
        Assert.True(await coordinator.RunAsync(lease, default));
    }

    private static async Task<MonitorAccount> MonitorRowAsync(VesselDbContext db)
    {
        db.ChangeTracker.Clear();
        return await db.Set<MonitorAccount>().AsNoTracking().SingleAsync();
    }

    /// <summary>
    /// A venue's execution history as an account read sees it: the latest fills up to <see cref="Cap"/>, newest first,
    /// truncated when the cap is reached, with the oldest raw timestamp of what came back.
    /// </summary>
    private sealed class VenueHistory
    {
        public List<VenueFill> Fills { get; } = [];
        public int Cap { get; set; } = 2000;

        public void Add(string contract, DateTimeOffset at, string? id = null, string? order = null)
        {
            var n = Fills.Count;
            Fills.Add(new(id ?? $"fill-{n}", contract, "buy", "Open Long", 100, 1, 0, "USDC", 0, at, order ?? $"order-{n}",
                $"tx-{n}", "{}", PositionEffect: ExecutionFacts.Open));
        }

        public PerpetualVenueReadResult Shape(PerpetualVenueReadResult read, DateTimeOffset observedAt)
        {
            var latest = Fills.OrderByDescending(f => f.OccurredAtUtc).Take(Cap).ToArray();
            return read with
            {
                Snapshot = read.Snapshot with { ObservedAtUtc = observedAt },
                Fills = latest,
                FillHistoryComplete = false,
                FillHistoryTruncated = latest.Length >= Cap,
                OldestRawFillAtUtc = latest.Length == 0 ? null : latest.Min(f => f.OccurredAtUtc)
            };
        }
    }

    private sealed class FixtureOrders(DiscrepancyFixture f) : IVenueOrderReader
    {
        public string VenueId => "hyperliquid";
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) => Task.FromResult(f.Observation().Read.Orders!);
    }
}
