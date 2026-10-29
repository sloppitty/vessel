using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Application.Plays;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Fifth review round for PR #46: Sol r4 recovery repros and scheduling fairness.</summary>
public sealed class MonitorDiscrepancyRound5IntegrationTests : MonitorDiscrepancyRound5TestsSupport
{
    [PostgresFact]
    public async Task Terminal_unverifiable_entry_should_recover_from_positive_partial_order_read()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked();
        var plan = new PlayPlanDocument("long", "quantity", "1", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "90", "100")], [new("t", "price", "120", "100")])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), f.Account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), f.Clock.Now.AddHours(-1));
        play.MarkPlanned(f.Clock.Now.AddMinutes(-30));
        f.Store.Plays.Add(play);
        f.Store.Links.Add(f.Link(play, "entry", OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-25)));
        play.Cancel(CancelReason.ChangedMind, null, f.Clock.Now.AddMinutes(-10));
        var resting = new Vessel.Application.Venues.VenueOrder("entry", "BTC", "buy", "Limit", 100, null, false, false,
            1, 1, f.Clock.Now.AddMinutes(-25), Vessel.Application.Venues.VenueOrderStatus.Open, "open", f.Clock.Now);
        f.VenueOrders.Add(resting);
        await database.Seed(f);
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Retired = true; f.VenueOrders.Clear(); f.Advance(60);
        await using (var second = database.Scope(f))
        {
            await second.Work.ExecuteAsync(async () =>
            {
                await second.Store.LockAsync(f.Account.Id, default);
                var monitor = MonitorTestPaths.Store(second.Db, second.Work);
                await monitor.UpdateWatchesAsync(f.Account.Id, f.Observation().Read.Orders!, f.Clock.Now, default);
                await second.Work.SaveChangesAsync(default);
                return await second.Service.EvaluateAsync(f.Observation(), default);
            }, default);
        }
        f.Advance(Lifetime + 60);
        await using (var third = database.Scope(f))
        {
            await third.Evaluate(f.Observation());
            var row = await third.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "entry.orphaned");
            Assert.NotNull(row.UnverifiableAtUtc);
        }
        // A different active Play makes the account monitored again. It must not hide this old Play's live entry.
        f.Retired = false; f.OrdersComplete = false; f.VenueOrders.Add(resting); f.Advance(60);
        var active = f.AddPlay("long", 1, "new-entry");
        await using (var fourth = database.Scope(f))
        {
            fourth.Db.Add(active);
            fourth.Db.AddRange(f.Store.Links.Where(x => x.PlayId == active.Id));
            fourth.Db.AddRange(f.Store.Orders.Where(x => x.ExternalOrderId == "new-entry"));
            fourth.Db.AddRange(f.Store.Fills.Where(x => x.ExternalOrderId == "new-entry"));
            await fourth.Db.SaveChangesAsync();
            Assert.True(await MonitorTestPaths.Store(fourth.Db, fourth.Work).EligibleAsync(f.Account.Id, f.Clock.Now, default));
            await fourth.Work.ExecuteAsync(async () =>
            {
                await fourth.Store.LockAsync(f.Account.Id, default);
                var monitor = MonitorTestPaths.Store(fourth.Db, fourth.Work);
                await monitor.UpdateWatchesAsync(f.Account.Id, f.Observation().Read.Orders!, f.Clock.Now, default);
                await fourth.Work.SaveChangesAsync(default);
                return await fourth.Service.EvaluateAsync(f.Observation(), default);
            }, default);
        }
        f.Advance(60);
        await using var fifth = database.Scope(f);
        await fifth.Evaluate(f.Observation());
        var recovered = await fifth.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "entry.orphaned");
        Assert.Equal("open", recovered.State);
        Assert.Null(recovered.UnverifiableAtUtc);
    }
    [PostgresFact] // P2: successful refresh completions run expiry housekeeping under the fence.
    public async Task Successful_refreshes_should_not_bypass_expiry_forever()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var options = new MonitorOptions { UnobservableLifetime = TimeSpan.FromHours(1) };
        var (evaluator, monitor, coordinator) = Monitor(scope, f, options);
        await scope.Work.ExecuteAsync(async () =>
        {
            await scope.Store.LockAsync(f.Account.Id, default);
            await evaluator.EvaluateAsync(f.Observation(), default);
            return true;
        }, default);
        f.Retired = true;
        // Only owner refreshes, never a scheduled check.
        for (var i = 0; i < 62; i++)
        {
            Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
            f.Advance(60);
        }
        var row = await scope.Db.Set<PlayDiscrepancy>().SingleAsync();
        Assert.NotNull(row.UnverifiableAtUtc);
        Assert.Equal("unverifiable", row.PublishedTransition);
        Assert.False(await monitor.EligibleAsync(f.Account.Id, f.Clock.Now, default));
    }

    [PostgresFact] // P2: an owner refreshing at every cooldown end cannot starve an overdue scheduled check.
    public async Task Repeated_refreshes_do_not_starve_an_overdue_scheduled_check()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = Untracked();
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var (evaluator, monitor, coordinator) = Monitor(scope, f, new MonitorOptions());
        await scope.Work.ExecuteAsync(async () =>
        {
            await scope.Store.LockAsync(f.Account.Id, default);
            await evaluator.EvaluateAsync(f.Observation(), default);
            return true;
        }, default);
        // Discovery schedules the check for the candidate alone; refreshes never move its due time.
        await coordinator.DiscoverAsync(["hyperliquid"], f.Clock.Now, default);
        // The check is overdue and past the confirmation delay before the refreshes start.
        f.Advance(60);
        var scheduled = 0;
        for (var i = 0; i < 20 && scheduled == 0; i++)
        {
            // Owner refresh wins admission exactly when the cooldown ends, before this scheduler tick.
            Assert.True(await coordinator.RunAsync(await coordinator.AdmitRefreshAsync(f.Account.Id, "owner", default), default));
            f.Advance(30);
            foreach (var lease in await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default))
            {
                Assert.NotEqual("refresh", lease.Trigger);
                Assert.True(await coordinator.RunAsync(lease, default));
                scheduled++;
            }
            f.Advance(30);
        }
        Assert.Equal(1, scheduled);
        // The scheduled check confirmed the candidate the refreshes never evaluated.
        Assert.Equal("open", (await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).State);
        Assert.True((await monitor.FindAsync(f.Account.Id, default))!.NextRunAtUtc > f.Clock.Now);
    }

    private static (MonitorDiscrepancyService, MonitorStore, MonitorCoordinator) Monitor(DiscrepancyDatabaseScope scope, DiscrepancyFixture f, MonitorOptions options)
    {
        var evaluator = new MonitorDiscrepancyService(scope.Store, scope.Work, new CoreOwner(f.Account.OwnerId), scope.Events, options, f.Clock);
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        return (evaluator, monitor, new MonitorCoordinator(monitor, new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(new FixedReader(f), new FixedOrders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), options, f.Clock, evaluator));
    }

    private sealed class FixedReader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync with { FillHistoryComplete = true });
    }
    private sealed class FixedOrders(DiscrepancyFixture f) : IVenueOrderReader
    {
        public string VenueId => "hyperliquid";
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) => Task.FromResult(f.Observation().Read.Orders!);
    }
    private sealed class NoImporter : IMonitorImporter
    {
        public Task RetainAsync(Account account, VenueOrderReadResult orders, CancellationToken ct) => Task.CompletedTask;
        public Task ReconcileAsync(Account account, VenueOrderReadResult orders, IReadOnlyList<VenueInstrument> instruments, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NoCredentials : IAccountCredentialReader
    {
        public Task<string?> ReadAsync(Guid accountId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<CredentialAttempt?> ReadAttemptAsync(Guid accountId, CancellationToken ct) => Task.FromResult<CredentialAttempt?>(null);
        public Task MarkRefusedAsync(Guid accountId, string attemptedRevision, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class NoCommands : IMonitorCommandSink
    {
        public Task EnqueueAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
    }

}
