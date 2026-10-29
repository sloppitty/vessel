using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyRound3PostgresTests
{
    [PostgresFact] // Finding 1 (Sol r2)
    public Task Reconfirm_resolved_excess_after_last_watch_ends_keeps_schedule() => Reconfirm(false);

    [PostgresFact] // Finding 1 (Sol r2)
    public Task Reconfirm_changed_dismissed_excess_after_last_watch_ends_keeps_schedule() => Reconfirm(true);

    [PostgresFact] // Findings 1 and 3: outstanding work holds eligibility across interruptions; unverifiable does not.
    public async Task Pending_reconfirmation_holds_eligibility_until_unverifiable()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear();
        await database.Seed(f);
        var row = new PlayDiscrepancy { OwnerId = f.Account.OwnerId, AccountId = f.Account.Id, Kind = "position.excess", SubjectKey = "x",
            ContractId = "BTC", NativeContractId = "BTC", Severity = "warning", State = "resolved", PendingFingerprint = "AB",
            PendingSinceUtc = null, LastObservedAtUtc = f.Clock.Now };
        await using (var seed = database.Scope(f)) { seed.Db.Add(row); await seed.Db.SaveChangesAsync(); }
        await using (var scope = database.Scope(f))
            Assert.True(await MonitorTestPaths.Store(scope.Db, scope.Work).EligibleAsync(f.Account.Id, f.Clock.Now, default));
        await using (var scope = database.Scope(f))
        {
            (await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).UnverifiableAtUtc = f.Clock.Now;
            (await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).State = "open";
            await scope.Db.SaveChangesAsync();
        }
        await using (var scope = database.Scope(f))
            Assert.False(await MonitorTestPaths.Store(scope.Db, scope.Work).EligibleAsync(f.Account.Id, f.Clock.Now, default));
    }

    [PostgresFact] // Finding 3: the outcome is persisted.
    public async Task Unverifiable_outcome_persists()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 1 };
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        await database.Seed(f);
        async Task Run() { await using var scope = database.Scope(f); await scope.Evaluate(f.Observation()); }
        await Run(); f.Advance(60); await Run();
        f.Retired = true; f.Advance((int)TimeSpan.FromDays(7).TotalSeconds + 60); await Run();
        await using var read = database.Scope(f);
        var dto = Assert.Single(await read.Service.ReadAsync(f.Account.Id, null, default));
        Assert.Equal(("open", "unverifiable"), (dto.State, dto.Outcome));
    }

    private static async Task Reconfirm(bool dismissed)
    {
        await using var database = await DiscrepancyDatabase.Create();
        // A real previous position discrepancy from an earlier active Play on this account/contract.
        var f = new DiscrepancyFixture { Position = 2 };
        await f.Confirm();
        var historical = f.Store.Rows.Single(x => x.Kind == "position.excess");
        if (dismissed) await f.Service.DismissAsync(f.Account.Id, null, historical.Id, null, default);
        else { f.Position = 1; f.Advance(61); await f.Evaluate(); }
        f.Position = 1;
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        var plan = new PlayPlanDocument("long", "quantity", "1", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "90", "100")], [new("t", "price", "120", "100")])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), f.Account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), f.Clock.Now.AddHours(-1));
        play.MarkPlanned(f.Clock.Now.AddMinutes(-30));
        f.Store.Plays.Add(play);
        f.Store.Links.Add(f.Link(play, "late-entry", OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-25)));
        play.Cancel(CancelReason.ChangedMind, null, f.Clock.Now.AddMinutes(-10));
        f.VenueOrders.Add(new("late-entry", "BTC", "buy", "Limit", 100, null, false, false, 1, 0, f.Clock.Now.AddMinutes(-25),
            VenueOrderStatus.Filled, "filled", f.Clock.Now.AddMinutes(-2)));
        await database.Seed(f);
        await using (var history = database.Scope(f))
        {
            history.Db.Add(historical);
            await history.Db.SaveChangesAsync();
        }
        f.Advance(61);
        await using var scope = database.Scope(f);
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(monitor, f.Clock), new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(new FixedReader(f), new FixedOrders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, scope.Service);
        var request = await coordinator.RequestAsync(f.Account.Id, default);
        var lease = (await monitor.ClaimAsync(f.Account.Id, request.RequestId, "worker", f.Clock.Now, default))!;
        Assert.True(await coordinator.RunAsync(lease, default));
        var row = await scope.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "position.excess");
        Assert.NotNull(row.PendingFingerprint);
        Assert.Equal(dismissed ? "dismissed" : "resolved", row.State);
        Assert.NotNull(await scope.Db.Set<MonitorExitWatch>().SingleAsync(x => x.EndedAtUtc != null));
        Assert.NotNull((await monitor.FindAsync(f.Account.Id, default))!.NextRunAtUtc);
        // The re-confirmation run reopens it.
        f.Advance(61);
        var confirmation = Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        Assert.True(await coordinator.RunAsync(confirmation, default));
        Assert.Equal("open", (await scope.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "position.excess")).State);
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
