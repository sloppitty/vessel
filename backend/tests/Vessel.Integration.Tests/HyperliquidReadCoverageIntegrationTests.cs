using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidAdapterIntegrationTests : HyperliquidAdapterTestsSupport
{

    [PostgresFact]
    public async Task Coverage_a_complete_untruncated_check_that_dropped_an_unknown_primary_fill_is_degraded()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Vessel.Domain.Accounts.Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        account.Configure(null, Address, null);
        var play = new Vessel.Domain.Plays.Play(Guid.NewGuid(), account, new Vessel.Domain.Markets.PerpetualInstrument("hyperliquid", "BTC"),
            Vessel.Domain.Plays.InstrumentSource.Venue, Vessel.Application.Plays.PlayDocuments.Serialize(TestPlays.Plan()), DateTimeOffset.UtcNow.AddMinutes(-2));
        play.MarkPlanned(DateTimeOffset.UtcNow.AddMinutes(-1));
        db.AddRange(account, play);
        await db.SaveChangesAsync();
        var unknown = Mutate(Fill, node => { node["coin"] = "SOL"; node["tid"] = "42"; });
        using var handler = Fixtures(Meta, Mutate(State, node => node["assetPositions"] = new JsonArray()), $"[{Fill},{unknown}]");
        using var client = Client(handler);
        var venues = TestVenues.With(new HyperliquidPerpetualReader(client, new TestClock()), new CompleteOrders());
        await MonitorTestPaths.CheckAsync(db, owner, venues, play.Id);
        db.ChangeTracker.Clear();
        var monitor = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.Set<Vessel.Domain.Monitoring.MonitorAccount>());
        // Positions and open orders are complete and the fill read was not truncated: only the omission degrades it.
        Assert.Equal("degraded", monitor.State);
        Assert.Contains("could not verify", monitor.Error);
    }

    private sealed class CompleteOrders : IVenueOrderReader
    {
        public string VenueId => "hyperliquid";
        public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) =>
            Task.FromResult(new VenueOrderReadResult([], DateTimeOffset.UtcNow, "Complete synthetic orders", OpenOrdersComplete: true));
    }
}
