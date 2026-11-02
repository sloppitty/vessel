using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.4a: the persistent incident indicators (Plays navigation, list and header badges, the Overview row) read
/// GET /api/monitor. An outstanding announcement must keep its account in that summary after scheduling has no work left.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    /// <summary>
    /// The account's last Play closed, its watches ended and its unresolved discrepancy became unverifiable. Nobody
    /// dismissed it, so the summary keeps the account and the record although nothing is left to check; a dismissal,
    /// disabling the account or another owner's view drop it.
    /// </summary>
    [PostgresTheory]
    [InlineData(DiscrepancyLifecycle.Open)]
    [InlineData(DiscrepancyLifecycle.Candidate)]
    public async Task Phase0_an_undismissed_unverifiable_incident_keeps_its_account_in_the_summary_without_scheduling_work(string state)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        // The last Play ended eight days ago with no linked orders left to watch.
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddDays(-9));
        play.MarkPlanned(clock.Now.AddDays(-9)); play.MarkOpen("fixture", clock.Now.AddDays(-9)); play.MarkClosed("fixture", clock.Now.AddDays(-8));
        db.Add(play);
        db.Add(Row(owner, id, "idle", clock.Now));
        var record = new PlayDiscrepancy
        {
            OwnerId = owner, AccountId = id, PlayId = play.Id, InvolvedPlayIds = [play.Id], ContractId = "BTC", NativeContractId = "BTC",
            SubjectKey = "play:" + play.Id, Kind = "protection.orphaned", Severity = "warning", State = state, Fingerprint = "fingerprint",
            OpenedAtUtc = clock.Now.AddDays(-8), ConfirmedAtUtc = state == DiscrepancyLifecycle.Open ? clock.Now.AddDays(-8) : null,
            LastObservedAtUtc = clock.Now.AddDays(-8), UnverifiableAtUtc = clock.Now.AddDays(-1), Revision = 2,
            PublishedTransition = DiscrepancyLifecycle.Unverifiable, PublishedRevision = 2
        };
        db.Add(record);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db);
        var store = MonitorTestPaths.Store(db, work);
        var evaluator = new MonitorDiscrepancyService(new MonitorDiscrepancyStore(db, work), work, new CoreOwner(owner), new EventSink(),
            new MonitorOptions(), clock);
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(store, clock), new WorkspaceStore(db, work), new Importer(),
            TestVenues.With(new Reader(), new Orders()), new Credentials(), work, new EventSink(), new Commands(), new CoreOwner(owner),
            new MonitorOptions(), clock, evaluator);

        // Nothing is left to schedule, yet the announcement still awaits the owner.
        Assert.False(await store.EligibleAsync(id, clock.Now, default));
        var account = Assert.Single((await coordinator.SummaryAsync(default)).Accounts);
        Assert.Equal(id, account.AccountId);
        var listed = Assert.Single(account.Discrepancies);
        Assert.Equal((record.Id, "unverifiable", (DateTimeOffset?)null), (listed.Id, listed.Outcome, listed.DismissedAtUtc));

        // Another owner never sees it.
        await using (var foreign = database.Context(Guid.NewGuid()))
            Assert.Empty(await MonitorTestPaths.Store(foreign, TestUnitOfWork.For(foreign)).SummaryAsync(["hyperliquid"], clock.Now, default));

        // A disabled account stays out of the summary, incident or not.
        db.ChangeTracker.Clear();
        (await db.Accounts.SingleAsync()).UpdateSettings("Disabled", null, false);
        await db.SaveChangesAsync();
        Assert.Empty(await store.SummaryAsync(["hyperliquid"], clock.Now, default));
        db.ChangeTracker.Clear();
        (await db.Accounts.SingleAsync()).UpdateSettings("Enabled", null, true);
        await db.SaveChangesAsync();
        Assert.Single(await store.SummaryAsync(["hyperliquid"], clock.Now, default));

        // Once the owner dismisses it, nothing outstanding keeps the account listed.
        db.ChangeTracker.Clear();
        await evaluator.DismissAsync(id, null, record.Id, "checked at the venue", default);
        db.ChangeTracker.Clear();
        Assert.Equal(DiscrepancyLifecycle.Dismissed, (await db.Set<PlayDiscrepancy>().AsNoTracking().SingleAsync()).PublishedTransition);
        Assert.Empty(await store.SummaryAsync(["hyperliquid"], clock.Now, default));
    }

    /// <summary>
    /// Review round 2: the 500-record cap used to sort resolved history (newest confirmation first) ahead of an outstanding
    /// candidate without a confirmation time, so 500 resolved records hid the one undismissed unverifiable incident and
    /// the persistent indicators counted none. Incidents now come first, and the truncation is reported.
    /// </summary>
    [PostgresFact]
    public async Task Phase0_five_hundred_resolved_records_never_hide_an_outstanding_incident_and_truncation_is_reported()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var db = database.Context(owner);
        var play = new Play(Guid.NewGuid(), await db.Accounts.SingleAsync(), new PerpetualInstrument("hyperliquid", "BTC"),
            InstrumentSource.Venue, "{}", clock.Now.AddDays(-9));
        play.MarkPlanned(clock.Now.AddDays(-9)); play.MarkOpen("fixture", clock.Now.AddDays(-9)); play.MarkClosed("fixture", clock.Now.AddDays(-8));
        db.Add(play);
        db.Add(Row(owner, id, "idle", clock.Now));
        PlayDiscrepancy Record(string kind, string subject) => new()
        {
            OwnerId = owner, AccountId = id, PlayId = play.Id, InvolvedPlayIds = [play.Id], ContractId = "BTC", NativeContractId = "BTC",
            SubjectKey = subject, Kind = kind, Severity = "warning", Fingerprint = "fingerprint", OpenedAtUtc = clock.Now.AddDays(-8),
            LastObservedAtUtc = clock.Now.AddDays(-8), Revision = 2, PublishedRevision = 2
        };
        // 500 resolved records with confirmation times, newer than anything the incident has.
        for (var i = 0; i < MonitorDiscrepancyList.MaxRecords; i++)
        {
            var resolved = Record("level.drift", $"history:{i:D3}");
            resolved.State = DiscrepancyLifecycle.Resolved;
            resolved.ConfirmedAtUtc = clock.Now.AddHours(-i - 1);
            resolved.ResolvedAtUtc = clock.Now.AddMinutes(-i);
            resolved.PublishedTransition = DiscrepancyLifecycle.Resolved;
            db.Add(resolved);
        }
        // One undismissed unverifiable candidate, never confirmed.
        var incident = Record("protection.orphaned", "play:" + play.Id);
        incident.State = DiscrepancyLifecycle.Candidate;
        incident.UnverifiableAtUtc = clock.Now.AddDays(-1);
        incident.PublishedTransition = DiscrepancyLifecycle.Unverifiable;
        db.Add(incident);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db);
        var evaluator = new MonitorDiscrepancyService(new MonitorDiscrepancyStore(db, work), work, new CoreOwner(owner), new EventSink(),
            new MonitorOptions(), clock);
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(MonitorTestPaths.Store(db, work), clock), new WorkspaceStore(db, work),
            new Importer(), TestVenues.With(new Reader(), new Orders()), new Credentials(), work, new EventSink(), new Commands(),
            new CoreOwner(owner), new MonitorOptions(), clock, evaluator);

        foreach (var monitor in new[] { Assert.Single((await coordinator.SummaryAsync(default)).Accounts), await coordinator.AccountAsync(id, default) })
        {
            Assert.Equal(MonitorDiscrepancyList.MaxRecords, monitor.Discrepancies.Count);
            Assert.True(monitor.DiscrepanciesTruncated);
            // The incident leads; the newest resolved records follow in their previous order, and the oldest one is left out.
            Assert.Equal((incident.Id, "unverifiable"), (monitor.Discrepancies[0].Id, monitor.Discrepancies[0].Outcome));
            Assert.Equal(Enumerable.Range(0, MonitorDiscrepancyList.MaxRecords - 1).Select(i => $"history:{i:D3}"),
                monitor.Discrepancies.Skip(1).Select(x => db.Set<PlayDiscrepancy>().Local.Single(r => r.Id == x.Id).SubjectKey));
        }

        // Within the cap nothing is reported truncated.
        await db.Set<PlayDiscrepancy>().Where(x => x.SubjectKey == "history:499").ExecuteDeleteAsync();
        var whole = await coordinator.AccountAsync(id, default);
        Assert.Equal((MonitorDiscrepancyList.MaxRecords, false), (whole.Discrepancies.Count, whole.DiscrepanciesTruncated));
    }
}
