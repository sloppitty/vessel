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
}
