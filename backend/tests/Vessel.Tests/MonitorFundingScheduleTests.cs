using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 9c: the monitor worker reads the settled funding of accounts with an Open venue Play or kept execution
/// history on a slow schedule, in runs of their own behind protection checks, with 9a's windows and cursor. A run never
/// moves the check schedule, attempt time, state or cooldown, and announces nothing.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    private const string FailedFundingNotice =
        "Funding history could not be refreshed. Retained payments are unchanged; missing funding is not zero.";

    /// <summary>
    /// A venue whose page budget holds windows up to <see cref="Budget"/>: a longer one comes back truncated. Each complete
    /// window settles one payment at its start.
    /// </summary>
    private sealed class ScheduledFundingVenue(Clock clock) : IPerpetualVenueReader, IVenueFundingReader
    {
        public string VenueId => "hyperliquid";
        public List<(DateTimeOffset From, DateTimeOffset To)> Windows { get; } = [];
        public TimeSpan Budget { get; set; } = TimeSpan.FromDays(31);
        public Exception? Failure { get; set; }
        public int AccountReads;
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct)
        {
            AccountReads++;
            return Task.FromResult(new PerpetualVenueReadResult(new(clock.Now, "synthetic", 100, 100, 0, []), [], [], "Synthetic isolated read",
                PositionsComplete: true, FillHistoryComplete: true));
        }
        public Task<VenueFundingReadResult> ReadFundingAsync(Account account, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            Windows.Add((from, to));
            if (Failure is { } failure) throw failure;
            return Task.FromResult(to - from > Budget
                ? new VenueFundingReadResult([], from, to, false, "Incomplete synthetic funding: the page budget was reached.") { Truncated = true }
                : new VenueFundingReadResult([new($"funding-{from.ToUnixTimeMilliseconds()}", "BTC", "BTC", -1m, "USDC", from, "{}")], from, to,
                    true, "Synthetic settled funding."));
        }
    }

    private sealed class RecordingRunLog : IMonitorRunLog
    {
        public List<(string Trigger, string Error)> Failures { get; } = [];
        public void Failed(MonitorLease lease, string errorType) => Failures.Add((lease.Trigger, errorType));
    }

    private sealed record FundingSetup(VesselDbContext Db, MonitorStore Store, MonitorCoordinator Coordinator, ScheduledFundingVenue Venue,
        EventSink Sink, RecordingRunLog Log, Clock Clock, Guid AccountId);

    private static readonly DateTimeOffset FundingStart = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static MonitorCoordinator FundingCoordinator(VesselDbContext db, Guid owner, Clock clock, IVenueRegistry venues,
        EventSink? sink = null, RecordingRunLog? log = null)
    {
        var work = TestUnitOfWork.For(db);
        var options = new MonitorOptions();
        return new(new MonitorWorkerOnline(MonitorTestPaths.Store(db, work, options), clock), new WorkspaceStore(db, work), new Importer(),
            venues, new Credentials(), work, sink ?? new EventSink(), new Commands(), new CoreOwner(owner), options, clock, log: log,
            funding: new FundingOptions());
    }

    /// <summary>An account that keeps its history, checked once so it has a check schedule the funding runs must leave alone.</summary>
    private static async Task<FundingSetup> FundingScheduleAsync(CoreDatabase database, Guid owner)
    {
        var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(FundingStart);
        var db = database.Context(owner);
        await KeepHistoryAsync(db, id, true);
        var venue = new ScheduledFundingVenue(clock);
        var sink = new EventSink();
        var log = new RecordingRunLog();
        var coordinator = FundingCoordinator(db, owner, clock, TestVenues.With(venue, new Orders()), sink, log);
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db), new MonitorOptions());
        await ScheduledCheckAsync(coordinator, store, clock);
        return new(db, store, coordinator, venue, sink, log, clock, id);
    }

    /// <summary>Everything about the row that only a check may move.</summary>
    private static object CheckSchedule(MonitorAccount row) => (row.NextRunAtUtc, row.LastAttemptAtUtc, row.LastSuccessAtUtc, row.State,
        row.StateRevision, row.NextManualAllowedAtUtc, row.ConsecutiveFailures, row.Error, row.LastObservationAtUtc);

    private static async Task<IReadOnlyList<DueMonitorCandidate>> DueFundingAsync(CoreDatabase database, TimeProvider clock,
        params string[] venues)
    {
        await using var data = NpgsqlDataSource.Create(database.ConnectionString);
        return await new SystemMonitorStore(data, clock).DueFundingAsync(venues, 100, default);
    }

    private static Task<MonitorLease?> ClaimFundingAsync(FundingSetup s) =>
        s.Store.ClaimFundingAsync(s.AccountId, "funding-worker", s.Clock.Now, default);

    [PostgresFact]
    public async Task Phase2_a_scheduled_funding_read_resumes_from_the_cursor_each_interval_and_leaves_the_check_schedule_alone()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await FundingScheduleAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        // A page budget that holds at most five days of this account's funding, as in 9a's Refresh resume test.
        s.Venue.Budget = TimeSpan.FromDays(5);
        var checkedRow = await MonitorRowAsync(db);
        Assert.Null(checkedRow.NextFundingAtUtc);
        // Never read: due at once.
        Assert.Equal(s.AccountId, Assert.Single(await DueFundingAsync(database, s.Clock, "hyperliquid")).AccountId);
        var drafts = s.Sink.Drafts.Count;

        var lease = await ClaimFundingAsync(s);
        Assert.Equal("funding", lease!.Trigger);
        // Never beside another run of the account.
        Assert.Null(await ClaimFundingAsync(s));
        Assert.Empty(await DueFundingAsync(database, s.Clock, "hyperliquid"));
        Assert.True(await s.Coordinator.RunAsync(lease, default));

        db.ChangeTracker.Clear();
        // Three windows too long for the budget, then five 3.75-day windows before the eight-read limit, as a Refresh reads.
        Assert.Equal(8, s.Venue.Windows.Count);
        var state = await db.FundingStates.AsNoTracking().SingleAsync();
        Assert.Equal((FundingStart.AddDays(-18.75), FundingStart, true), (state.CoveredFromUtc, state.CoveredToUtc, state.LastReadComplete));
        Assert.Equal(5, await db.FundingPayments.CountAsync());
        var after = await MonitorRowAsync(db);
        Assert.Equal(("idle", FundingStart.AddHours(6)), (after.ManualState, after.NextFundingAtUtc));
        // Not a check: the schedule, attempt, success, state, cooldown and observation are as the check left them. No account
        // read and nothing announced.
        Assert.Equal(CheckSchedule(checkedRow), CheckSchedule(after));
        Assert.Equal(1, s.Venue.AccountReads);
        Assert.Equal(drafts, s.Sink.Drafts.Count);
        Assert.Empty(s.Log.Failures);

        // Not again before the interval.
        s.Clock.Now = FundingStart.AddHours(1);
        Assert.Null(await ClaimFundingAsync(s));
        Assert.Empty(await DueFundingAsync(database, s.Clock, "hyperliquid"));

        // At the interval the account's own check is due too, and goes first.
        s.Clock.Now = FundingStart.AddHours(6);
        Assert.Null(await ClaimFundingAsync(s));
        Assert.Empty(await DueFundingAsync(database, s.Clock, "hyperliquid"));
        await ScheduledCheckAsync(s.Coordinator, s.Store, s.Clock);
        var rechecked = await MonitorRowAsync(db);
        Assert.Equal(FundingStart.AddHours(6), rechecked.NextFundingAtUtc);
        lease = await ClaimFundingAsync(s);
        Assert.True(await s.Coordinator.RunAsync(lease!, default));

        db.ChangeTracker.Clear();
        // Resumed from the cursor: the new six hours from the stored end first, then the older span from the stored start at
        // the learned 3.75 days, never relearned from 30 days, until the 30 days are covered.
        var second = s.Venue.Windows.Skip(8).ToList();
        Assert.Equal((FundingStart, FundingStart.AddHours(6)), second[0]);
        Assert.Equal((FundingStart.AddDays(-22.5), FundingStart.AddDays(-18.75)), second[1]);
        Assert.All(second, w => Assert.True(w.To - w.From <= TimeSpan.FromDays(7.5)));
        state = await db.FundingStates.AsNoTracking().SingleAsync();
        var to = FundingStart.AddHours(6);
        Assert.Equal((to.AddDays(-30), to, true, "Synthetic settled funding."),
            (state.CoveredFromUtc, state.CoveredToUtc, state.LastReadComplete, state.Notice));
        after = await MonitorRowAsync(db);
        Assert.Equal(FundingStart.AddHours(12), after.NextFundingAtUtc);
        Assert.Equal(CheckSchedule(rechecked), CheckSchedule(after));
    }

    [PostgresFact]
    public async Task Phase2_a_rate_limited_scheduled_funding_read_defers_the_venue_and_stays_due_until_the_venue_allows()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await FundingScheduleAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        var checkedRow = await MonitorRowAsync(db);
        s.Venue.Failure = new VenueRateLimitException(FundingStart.AddMinutes(2));

        Assert.False(await s.Coordinator.RunAsync((await ClaimFundingAsync(s))!, default));
        // The venue waits, for every account on it.
        Assert.Equal(FundingStart.AddMinutes(2), await s.Store.VenueRetryAtAsync("hyperliquid", default));
        var row = await MonitorRowAsync(db);
        Assert.Equal(("idle", (DateTimeOffset?)null), (row.ManualState, row.NextFundingAtUtc));
        Assert.Equal(CheckSchedule(checkedRow), CheckSchedule(row));
        // The Refresh's failed read, quietly: its fixed notice and the exception type, never its text.
        var state = await db.FundingStates.AsNoTracking().SingleAsync();
        Assert.Equal((false, FailedFundingNotice), (state.LastReadComplete, state.Notice));
        Assert.Equal(("funding", nameof(VenueRateLimitException)), Assert.Single(s.Log.Failures));

        // Still due, but not while the venue waits.
        s.Clock.Now = FundingStart.AddMinutes(1);
        Assert.Null(await ClaimFundingAsync(s));
        Assert.Empty(await DueFundingAsync(database, s.Clock, "hyperliquid"));
        s.Clock.Now = FundingStart.AddMinutes(3);
        s.Venue.Failure = null;
        Assert.Equal(s.AccountId, Assert.Single(await DueFundingAsync(database, s.Clock, "hyperliquid")).AccountId);
        Assert.True(await s.Coordinator.RunAsync((await ClaimFundingAsync(s))!, default));
        row = await MonitorRowAsync(db);
        Assert.Equal(FundingStart.AddMinutes(3).AddHours(6), row.NextFundingAtUtc);
        Assert.Equal(CheckSchedule(checkedRow), CheckSchedule(row));
    }

    [PostgresFact]
    public async Task Phase2_a_failed_scheduled_funding_read_keeps_the_last_window_quietly_and_waits_an_interval()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var s = await FundingScheduleAsync(database, Guid.NewGuid());
        await using var db = s.Db;
        Assert.True(await s.Coordinator.RunAsync((await ClaimFundingAsync(s))!, default));
        db.ChangeTracker.Clear();
        var read = await db.FundingStates.AsNoTracking().SingleAsync();
        Assert.Equal((FundingStart.AddDays(-30), FundingStart, true), (read.CoveredFromUtc, read.CoveredToUtc, read.LastReadComplete));

        s.Clock.Now = FundingStart.AddHours(6);
        await ScheduledCheckAsync(s.Coordinator, s.Store, s.Clock);
        var checkedRow = await MonitorRowAsync(db);
        s.Venue.Failure = new VenueReadException("Provider secret must not appear.");
        Assert.False(await s.Coordinator.RunAsync((await ClaimFundingAsync(s))!, default));

        db.ChangeTracker.Clear();
        var failed = await db.FundingStates.AsNoTracking().SingleAsync();
        // Coverage and the last window stay, so the next read starts from half of it; the retained payment stays.
        Assert.Equal((read.CoveredFromUtc, read.CoveredToUtc, read.LastAttemptFromUtc, read.LastAttemptToUtc, false, FailedFundingNotice),
            (failed.CoveredFromUtc, failed.CoveredToUtc, failed.LastAttemptFromUtc, failed.LastAttemptToUtc, failed.LastReadComplete, failed.Notice));
        Assert.Equal(1, await db.FundingPayments.CountAsync());
        Assert.Equal(("funding", nameof(VenueReadException)), Assert.Single(s.Log.Failures));
        var row = await MonitorRowAsync(db);
        Assert.DoesNotContain("secret", row.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal((FundingStart.AddHours(12), "idle"), (row.NextFundingAtUtc!.Value, row.ManualState));
        Assert.Equal(CheckSchedule(checkedRow), CheckSchedule(row));
    }

    [PostgresFact]
    public async Task Phase2_scheduled_funding_reads_only_enabled_real_accounts_with_an_Open_venue_Play_or_kept_history_on_a_funding_venue()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var ids = await AccountsAsync(database, owner, 4);
        var (open, kept, planned, disabled) = (ids[0], ids[1], ids[2], ids[3]);
        var clock = new Clock(FundingStart);
        await using var db = database.Context(owner);
        var opened = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(a => a.Id == open), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddHours(-3));
        opened.MarkPlanned(clock.Now.AddHours(-2));
        opened.MarkOpen("fixture", clock.Now.AddHours(-1));
        // A Planned Play is protection work, but nothing is held yet: no funding to read.
        var waiting = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(a => a.Id == planned), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddHours(-3));
        waiting.MarkPlanned(clock.Now.AddHours(-2));
        // A paper account whose Play is open in the simulation.
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var paper = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", 4000);
        paper.Configure(portfolio.Id, null, null);
        var simulated = new Play(Guid.NewGuid(), paper, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue, "{}",
            clock.Now.AddHours(-3));
        simulated.MarkPlanned(clock.Now.AddHours(-2));
        db.AddRange(opened, waiting, portfolio, paper, simulated);
        await db.SaveChangesAsync();
        await db.Plays.Where(p => p.Id == simulated.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, PlayStatus.Open));
        db.ChangeTracker.Clear();
        await KeepHistoryAsync(db, kept, true);
        await KeepHistoryAsync(db, disabled, true);
        var off = await db.Accounts.SingleAsync(a => a.Id == disabled);
        off.UpdateSettings(off.Name, null, false);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(new[] { open, kept }.Order(), (await DueFundingAsync(database, clock, "hyperliquid")).Select(c => c.AccountId).Order());
        // Only the venues the scheduler offers, those with a funding reader.
        Assert.Empty(await DueFundingAsync(database, clock, "lighter"));
        Assert.Empty(await DueFundingAsync(database, clock));
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        foreach (var id in new[] { planned, disabled, paper.Id })
            Assert.Null(await store.ClaimFundingAsync(id, "funding-worker", clock.Now, default));
        var venue = new ScheduledFundingVenue(clock);
        var coordinator = FundingCoordinator(db, owner, clock, TestVenues.With(venue, new Orders()));
        foreach (var id in new[] { open, kept })
            Assert.True(await coordinator.RunAsync((await store.ClaimFundingAsync(id, "funding-worker", clock.Now, default))!, default));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { open, kept }.Order(), (await db.FundingStates.AsNoTracking().Select(f => f.AccountId).ToListAsync()).Order());

        // A venue without a funding reader is never asked: the run reads nothing and waits an interval.
        clock.Now = FundingStart.AddHours(6);
        var stored = await db.FundingStates.AsNoTracking().SingleAsync(f => f.AccountId == kept);
        var plain = FundingCoordinator(db, owner, clock, TestVenues.With(new Reader(), new Orders()));
        Assert.False(await plain.RunAsync((await store.ClaimFundingAsync(kept, "funding-worker", clock.Now, default))!, default));
        db.ChangeTracker.Clear();
        Assert.Equal(2, venue.Windows.Count);
        Assert.Equal(stored.LastAttemptToUtc, (await db.FundingStates.AsNoTracking().SingleAsync(f => f.AccountId == kept)).LastAttemptToUtc);
        var row = await db.Set<MonitorAccount>().AsNoTracking().SingleAsync(m => m.AccountId == kept);
        Assert.Equal(("idle", FundingStart.AddHours(12)), (row.ManualState, row.NextFundingAtUtc!.Value));

        // The Play closing and the opt-in going off end it.
        var play = await db.Plays.SingleAsync(p => p.Id == opened.Id);
        play.MarkClosed("fixture", clock.Now);
        await db.SaveChangesAsync();
        await KeepHistoryAsync(db, kept, false);
        clock.Now = FundingStart.AddDays(1);
        Assert.Empty(await DueFundingAsync(database, clock, "hyperliquid"));
        foreach (var id in new[] { open, kept })
            Assert.Null(await store.ClaimFundingAsync(id, "funding-worker", clock.Now, default));
    }
}
