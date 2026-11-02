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

public sealed class MonitorDiscrepancyRound2PostgresTests
{
    [PostgresFact] // Finding 1
    public async Task Cancelled_plays_entry_filling_after_cancellation_still_gets_its_confirmation_run()
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
        // The entry filled after cancellation: its watch resolves, but the venue now holds untracked exposure.
        f.VenueOrders.Add(new("late-entry", "BTC", "buy", "Limit", 100, null, false, false, 1, 0, f.Clock.Now.AddMinutes(-25),
            VenueOrderStatus.Filled, "filled", f.Clock.Now.AddMinutes(-2)));
        await using var scope = database.Scope(f);
        var monitor = MonitorTestPaths.Store(scope.Db, scope.Work);
        var coordinator = new MonitorCoordinator(new MonitorWorkerOnline(monitor, f.Clock), new WorkspaceStore(scope.Db, scope.Work), new NoImporter(),
            TestVenues.With(new FixedReader(f), new FixedOrders(f)), new NoCredentials(), scope.Work, scope.Events, new NoCommands(),
            new CoreOwner(f.Account.OwnerId), new MonitorOptions(), f.Clock, scope.Service);
        var request = await coordinator.RequestAsync(f.Account.Id, default);
        var lease = (await monitor.ClaimAsync(f.Account.Id, request.RequestId, "worker", f.Clock.Now, default))!;
        Assert.True(await coordinator.RunAsync(lease, default));
        Assert.NotNull(await scope.Db.Set<MonitorExitWatch>().SingleAsync(x => x.EndedAtUtc != null));
        Assert.Equal("candidate", (await scope.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "position.excess")).State);
        Assert.NotNull((await monitor.FindAsync(f.Account.Id, default))!.NextRunAtUtc);

        f.Advance(61);
        var confirmation = Assert.Single(await monitor.ClaimDueAsync("scheduler", f.Clock.Now, 2, default));
        Assert.True(await coordinator.RunAsync(confirmation, default));
        var opened = (await EventReads.ReadAsync(scope.Db, 0, 100, default)).Events.Where(x => x.Type == "discrepancy.opened").ToArray();
        Assert.Equal("position.excess", Assert.Single(opened).Data.GetProperty("kind").GetString());

        // The open warning keeps Check available until fresh evidence clears it.
        f.Advance(61);
        await coordinator.RequestAsync(f.Account.Id, default);
    }

    [PostgresFact] // Event contract after pruning: dedup keys are retained and never reused.
    public async Task Revisioned_dedup_keys_survive_event_pruning()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 0 };
        await database.Seed(f);
        var types = new List<string>();
        async Task Collect()
        {
            await using var read = database.Scope(f);
            types.AddRange((await EventReads.ReadAsync(read.Db, 0, 100, default)).Events
                .Where(x => x.Type.StartsWith("discrepancy.")).Select(x => x.Type));
            // Prune delivered history, as retention does; dedup keys stay.
            await read.Db.Database.ExecuteSqlRawAsync("DELETE FROM events");
        }
        async Task Run() { await using var scope = database.Scope(f); await scope.Evaluate(f.Observation()); }
        await Run(); f.Advance(60); await Run(); await Collect();
        f.Position = 1; f.Advance(60); await Run(); await Collect();
        f.Position = 0; f.Advance(60); await Run(); f.Advance(60); await Run(); await Collect();
        await using (var dismiss = database.Scope(f))
        {
            var id = (await dismiss.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "position.missing")).Id;
            await dismiss.Service.DismissAsync(f.Account.Id, null, id, null, default);
        }
        await Collect();
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved", "discrepancy.opened", "discrepancy.resolved"], types);
        await using var check = database.Scope(f);
        var row = await check.Db.Set<PlayDiscrepancy>().SingleAsync(x => x.Kind == "position.missing");
        var keys = await check.Db.Database.SqlQueryRaw<string>("SELECT dedup_key AS \"Value\" FROM event_dedup WHERE dedup_key LIKE 'discrepancy:%'").ToListAsync();
        Assert.Equal(new[] { $"discrepancy:{row.Id}:1:opened", $"discrepancy:{row.Id}:2:resolved", $"discrepancy:{row.Id}:3:opened",
            $"discrepancy:{row.Id}:4:dismissed" }.Order(), keys.Order());
    }

    private sealed class FixedReader(DiscrepancyFixture f) : IPerpetualVenueReader
    {
        public string VenueId => "hyperliquid";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(f.Observation().Read.Sync.Instruments);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken ct) =>
            Task.FromResult(f.Observation().Read.Sync with { FillHistoryComplete = true });
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
