using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Application.Venues;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class SizingPostgresTests
{
    private static VenueOrder Order(string id, string side, decimal price, string status, bool reduceOnly = false) => new(id, "BTC", side, "Limit",
        price, null, reduceOnly, false, 1m, status == "open" ? 1m : 0m, DateTimeOffset.UtcNow, HyperliquidPerpetualReader.Normalize(status), status,
        DateTimeOffset.UtcNow);

    private static VenueFill Fill(string id, string orderId, string dir, decimal price, decimal pnl) =>
        new(id, "BTC", dir.StartsWith("Open") ? ExecutionFacts.Buy : ExecutionFacts.Sell, dir, price, 1m, 0.01m, "USDC", pnl, DateTimeOffset.UtcNow, orderId, "hash-" + id, "{}",
            HyperliquidPerpetualReader.EffectOf(dir));

    [PostgresFact]
    public async Task Record_comes_from_linked_fills_of_closed_plays_on_enabled_accounts_of_the_owner()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var reader = new FixtureReader();
        reader.Result = reader.Result with { Fills = [] };
        var orders = new FakeOrderReader();
        var workspace = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TestVenues.With(reader), TestUnitOfWork.For(db));
        var plays = new PlayService(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), TimeProvider.System, TestUnitOfWork.For(db));
        var sizing = new SizingService(new SizingStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.For(db));
        var account = await workspace.CreateAccountAsync(new(null, "Venue", "hyperliquid", "0x" + Guid.NewGuid().ToString("N") + "00000000"), default);

        var play = await plays.CreateAsync(new CreatePlayRequest(account.Id, "BTC", "venue", "Breakout", TestPlays.Plan()), default);
        play = await plays.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default);
        orders.Orders.AddRange([Order("21", "buy", 100m, "filled"), Order("22", "sell", 110m, "filled", reduceOnly: true)]);
        reader.Result = reader.Result with { Fills = [Fill("g1", "21", "Open Long", 100m, 0), Fill("g2", "22", "Close Long", 110m, 10m)] };
        Assert.Equal("closed", (await MonitorTestPaths.CheckAsync(db, owner, TestVenues.With(reader, orders), play.Summary.Id)).Status);

        var result = await sizing.GetAsync(default);
        var outcome = Assert.Single(result.Record.Recent);
        Assert.Equal(("win", "9.98", "5", "1.996"), (outcome.Outcome, outcome.NetResultUsd, outcome.RiskUsd, outcome.RMultiple));
        Assert.Equal("1", result.Record.BattingAverage);

        Assert.Equal("2.5", (await sizing.UpdateSettingsAsync(new("2.5"), default)).Settings.RiskPercent);
        await using (var again = database.Context(owner))
            Assert.Equal("2.5", (await new SizingService(new SizingStore(again, TestUnitOfWork.For(again)), new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.For(again)).GetAsync(default)).Settings.RiskPercent);

        await using (var other = database.Context(Guid.NewGuid()))
        {
            var foreign = await new SizingService(new SizingStore(other, TestUnitOfWork.For(other)), new CoreOwner(Guid.NewGuid()), TimeProvider.System, TestUnitOfWork.For(other)).GetAsync(default);
            Assert.Equal((0, "1.25"), (foreign.Record.ClosedPlays, foreign.Settings.RiskPercent));
        }

        var stored = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        await workspace.UpdateAccountAsync(account.Id, new(stored.Name, stored.PortfolioId, false, stored.SettingsRevision), default);
        Assert.Equal(0, (await sizing.GetAsync(default)).Record.ClosedPlays);
    }
}
