using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Domain.Monitoring;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6: an account that keeps its execution history is read on the monitor's schedule without an eligible
/// Play, behind protection checks, and every import records its coverage by 0.4b's continuity rule.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    private sealed class CountingEvaluator : IMonitorEvaluator
    {
        public int Evaluations;
        public Task<MonitorEvaluationResult> EvaluateAsync(MonitorEvaluation observation, CancellationToken ct)
        { Evaluations++; return Task.FromResult(new MonitorEvaluationResult(null)); }
        public Task<IReadOnlyList<MonitorDiscrepancyDto>> ReadAsync(Guid accountId, Guid? playId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MonitorDiscrepancyDto>>([]);
        public Task<MonitorDiscrepancyList> ReadListAsync(Guid accountId, Guid? playId, CancellationToken ct) =>
            Task.FromResult(new MonitorDiscrepancyList([], false));
        public Task SettleAsync(Vessel.Domain.Accounts.Account account, Guid runId, string trigger, bool interrupted, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task KeepHistoryAsync(VesselDbContext db, Guid id, bool keep)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == id);
        account.UpdateSettings(account.Name, account.PortfolioId, account.IsEnabled, keep);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [PostgresFact]
    public async Task Phase2_an_account_that_keeps_its_history_is_checked_without_a_Play_and_records_its_coverage_without_comparing()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var start = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        await using var db = database.Context(owner);
        var venue = new VenueHistory { Cap = 3 };
        venue.Add("BTC", start.AddHours(-3));
        var reader = new Reader { Shape = read => venue.Shape(read, clock.Now) };
        var work = TestUnitOfWork.For(db);
        var options = new MonitorOptions();
        var store = MonitorTestPaths.Store(db, work, options);
        var evaluator = new CountingEvaluator();
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(store, clock), new WorkspaceStore(db, work), new Importer(),
            TestVenues.With(reader, new Orders()), new Credentials(), work, new EventSink(), new Commands(), new CoreOwner(owner), options,
            clock, evaluator);

        // Without a Play or the opt-in there is nothing to check.
        Assert.False(await store.EligibleAsync(id, clock.Now, default));
        await KeepHistoryAsync(db, id, true);
        Assert.True(await store.EligibleAsync(id, clock.Now, default));
        Assert.False(await store.ProtectionEligibleAsync(id, clock.Now, default));

        // The scheduled run imports and records coverage, and compares nothing: no Play, no protection work.
        await ScheduledCheckAsync(coordinator, store, clock);
        Assert.Equal(0, evaluator.Evaluations);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Fills.CountAsync());
        var first = Assert.Single(await db.FillCoverage.AsNoTracking().ToListAsync());
        Assert.Equal((FillCoverageRecord.Check, start.AddHours(-3), start, 1), (first.Source, first.CoveredFromUtc!.Value, first.CoveredToUtc!.Value, first.ImportCount));
        Assert.Equal(("live", false), ((await MonitorRowAsync(db)).State, (await MonitorRowAsync(db)).HasHistoryGap));

        // The next continuous check extends the same record.
        venue.Add("BTC", start.AddMinutes(5));
        clock.Now = (await MonitorRowAsync(db)).NextRunAtUtc!.Value.AddSeconds(1);
        await ScheduledCheckAsync(coordinator, store, clock);
        db.ChangeTracker.Clear();
        var extended = Assert.Single(await db.FillCoverage.AsNoTracking().ToListAsync());
        Assert.Equal((2, start.AddHours(-3), clock.Now), (extended.ImportCount, extended.CoveredFromUtc!.Value, extended.CoveredToUtc!.Value));

        // After a pause the capped read starts after the last known fill: a gap from it, and a new record.
        var paused = clock.Now;
        for (var i = 0; i < 4; i++) venue.Add("ETH", paused.AddDays(1).AddMinutes(i));
        clock.Now = paused.AddDays(1).AddHours(1);
        await ScheduledCheckAsync(coordinator, store, clock);
        db.ChangeTracker.Clear();
        var records = await db.FillCoverage.AsNoTracking().OrderBy(r => r.LastObservedAtUtc).ToListAsync();
        Assert.Equal(2, records.Count);
        var gap = Assert.Single(await db.HistoryGaps.AsNoTracking().ToListAsync());
        Assert.Equal((start.AddMinutes(5), paused.AddDays(1).AddMinutes(1), records[1].Id, true), (gap.FromUtc!.Value, gap.ToUtc!.Value,
            gap.OpenedByCoverageId!.Value, gap.IsOpen));
        Assert.Equal((gap.FromUtc, gap.ToUtc), (records[1].GapFromUtc, records[1].GapToUtc));
        Assert.True((await MonitorRowAsync(db)).HasHistoryGap);

        // A Refresh is its own record, beside the checks (after the check's manual cooldown).
        clock.Now = clock.Now.AddMinutes(2);
        await MonitorTestPaths.RefreshAsync(db, owner, TestVenues.With(reader, new Orders()), id, time: clock);
        db.ChangeTracker.Clear();
        Assert.Equal(FillCoverageRecord.Refresh, (await db.FillCoverage.AsNoTracking().OrderByDescending(r => r.LastObservedAtUtc)
            .ThenByDescending(r => r.Id).FirstAsync()).Source);

        // A Play brings back the comparison; without the opt-in and a Play, the account goes idle.
        await SeedPlannedAsync(db, clock.Now);
        Assert.True(await store.ProtectionEligibleAsync(id, clock.Now, default));
        await db.Plays.ExecuteDeleteAsync();
        await KeepHistoryAsync(db, id, false);
        Assert.False(await store.EligibleAsync(id, clock.Now, default));
    }

    [PostgresFact]
    public async Task Phase2_a_kept_history_read_leaves_an_owner_slot_and_venue_headroom_for_protection_checks()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var ids = await AccountsAsync(database, owner, 3);
        var clock = new Clock(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        await using var db = database.Context(owner);
        // Two accounts keep their history only; the third has a Play.
        foreach (var kept in ids.Take(2)) await KeepHistoryAsync(db, kept, true);
        var play = new Vessel.Domain.Plays.Play(Guid.NewGuid(), await db.Accounts.SingleAsync(a => a.Id == ids[2]),
            new Vessel.Domain.Markets.PerpetualInstrument("hyperliquid", "BTC"), Vessel.Domain.Plays.InstrumentSource.Venue, "{}", clock.Now.AddMinutes(-2));
        play.MarkPlanned(clock.Now.AddMinutes(-1));
        db.Add(play);
        await db.SaveChangesAsync();
        var options = new MonitorOptions { VenueReadConcurrency = 2, ProtectionHeadroom = 1 };
        var work = TestUnitOfWork.For(db);
        var store = MonitorTestPaths.Store(db, work, options);
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(store, clock), new WorkspaceStore(db, work), new Importer(),
            TestVenues.With(new Reader(), new Orders()), new Credentials(), work, new EventSink(), new Commands(), new CoreOwner(owner), options, clock);
        await coordinator.DiscoverAsync(["hyperliquid"], clock.Now, default);

        // One kept-history read runs; the second would take the owner's last slot, so it waits.
        Assert.NotNull(await store.ClaimAsync(ids[0], null, "worker", clock.Now, default));
        Assert.Null(await store.ClaimAsync(ids[1], null, "worker", clock.Now, default));
        // The protection check still gets the slot kept for it.
        Assert.NotNull(await store.ClaimAsync(ids[2], null, "worker", clock.Now, default));

        // Without headroom the kept-history reads share the budget like any other check.
        db.ChangeTracker.Clear();
        await db.Set<MonitorAccount>().ExecuteUpdateAsync(s => s.SetProperty(m => m.ManualState, "idle").SetProperty(m => m.LeaseUntilUtc, (DateTimeOffset?)null)
            .SetProperty(m => m.LeaseHolder, (string?)null).SetProperty(m => m.NextRunAtUtc, clock.Now));
        var shared = MonitorTestPaths.Store(db, work, new MonitorOptions { VenueReadConcurrency = 2, ProtectionHeadroom = 0 });
        Assert.NotNull(await shared.ClaimAsync(ids[0], null, "worker", clock.Now, default));
        Assert.NotNull(await shared.ClaimAsync(ids[1], null, "worker", clock.Now, default));
        Assert.Null(await shared.ClaimAsync(ids[2], null, "worker", clock.Now, default));
    }
}
