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

public sealed class MonitorDiscrepancyRound4PostgresTests
{
    [PostgresFact] // Finding 1: continuously failing reads still age pending-only work under the account fence.
    public async Task Failing_reads_expire_pending_only_work_and_release_the_account()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 1 };
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
        await database.Seed(f);
        f.VenueOrders.Add(new("late-entry", "BTC", "buy", "Limit", 100, null, false, false, 1, 0, f.Clock.Now.AddMinutes(-25),
            VenueOrderStatus.Filled, "filled", f.Clock.Now.AddMinutes(-2)));
        await using var scope = database.Scope(f);
        var reader = new SwitchableReader(f);
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(monitor, f.Clock), new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(reader, new FixedOrders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, scope.Service);
        var request = await coordinator.RequestAsync(f.Account.Id, default);
        Assert.True(await coordinator.RunAsync((await monitor.ClaimAsync(f.Account.Id, request.RequestId, "worker", f.Clock.Now, default))!, default));
        Assert.Equal("candidate", (await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).State);

        // From now on every venue read fails; only the pending candidate keeps the account eligible.
        reader.Failing = true;
        f.Advance(61);
        Assert.False(await coordinator.RunAsync(Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default)), default));
        Assert.NotNull((await monitor.FindAsync(f.Account.Id, default))!.NextRunAtUtc);
        Assert.Null((await scope.Db.Set<PlayDiscrepancy>().SingleAsync()).UnverifiableAtUtc);

        f.Advance((int)TimeSpan.FromDays(7).TotalSeconds);
        Assert.False(await coordinator.RunAsync(Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default)), default));
        var row = await scope.Db.Set<PlayDiscrepancy>().SingleAsync();
        Assert.Equal("candidate", row.State);
        Assert.NotNull(row.UnverifiableAtUtc);
        Assert.Null(row.ResolvedAtUtc);
        var state = (await monitor.FindAsync(f.Account.Id, default))!;
        Assert.Null(state.NextRunAtUtc);
        Assert.False(await monitor.EligibleAsync(f.Account.Id, f.Clock.Now, default));
        var events = (await EventReads.ReadAsync(scope.Db, 0, 100, default)).Events.Where(x => x.Type.StartsWith("discrepancy.")).ToArray();
        var unverifiable = Assert.Single(events);
        Assert.Equal(("discrepancy.unverifiable", "warning", $"discrepancy:{row.Id}:{row.Revision}:unverifiable"),
            (unverifiable.Type, unverifiable.Severity, unverifiable.DedupKey));
        Assert.Equal("unverifiable", Assert.Single(await scope.Service.ReadAsync(f.Account.Id, null, default)).Outcome);
    }

    private sealed class SwitchableReader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public bool Failing;
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) => Failing
            ? throw new HttpRequestException("synthetic venue outage")
            : Task.FromResult(f.Observation().Read.Sync with { FillHistoryComplete = true });
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
