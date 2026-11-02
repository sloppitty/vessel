using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 8a (G-09, V11): the venue's reported liquidation price and margin mode are kept with the snapshot and, as
/// observations, when a position appears or changes, so a review still sees the risk context at the first fill.
/// </summary>
public sealed class RiskObservationPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    private static PerpetualVenueReadResult Read(int minute, IReadOnlyList<VenuePosition> positions, params VenueFill[] fills) =>
        new(new(Start.AddMinutes(minute), "primary-perpetual-dex", 100, 100, 1, positions), [], fills, "Bounded history.");

    private static VenuePosition Position(decimal quantity, decimal? liquidation, string? mode = MarginModes.Cross, decimal entry = 100,
        string? native = null) =>
        new("BTC", quantity, entry, 0, 1, 5, VenueContractId: native, LiquidationPrice: liquidation, MarginMode: mode);

    private static VenueFill Fill(string id, int minute, string side = "buy", string effect = "open", string? native = null) =>
        new(id, "BTC", side, side == "buy" ? "Open Long" : "Close Long", 100, 1, 0, "USDC", 0, Start.AddMinutes(minute), "order-" + id, "", "",
            effect, VenueContractId: native);

    private static async Task SaveAsync(VesselDbContext db, Guid accountId, PerpetualVenueReadResult read)
    {
        var work = TestUnitOfWork.For(db);
        var store = new WorkspaceStore(db, work);
        await work.ExecuteAsync(() => store.WithAccountLockAsync(accountId, async locked =>
        {
            await store.SaveRefreshAsync(locked, read, default);
            return true;
        }, default), default);
    }

    private static async Task<Account> AddAccountAsync(VesselDbContext db, Guid owner)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Account");
        account.Configure(null, "0x" + new string('a', 40), null);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    [PostgresFact]
    public async Task Observations_follow_the_position_not_every_drift_of_a_cross_liquidation_price()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = await AddAccountAsync(db, owner);

        await SaveAsync(db, account.Id, Read(0, [Position(1, 80)]));                          // appears
        await SaveAsync(db, account.Id, Read(1, [Position(1, 81)]));                          // cross drift only: not kept again
        await SaveAsync(db, account.Id, Read(2, [Position(1, 81, MarginModes.Isolated)]));    // mode changed
        await SaveAsync(db, account.Id, Read(3, [Position(2, 85, MarginModes.Isolated, 101)])); // size changed
        await SaveAsync(db, account.Id, Read(4, [Position(2, 86, MarginModes.Isolated, 101)], Fill("f", 4))); // new executions
        await SaveAsync(db, account.Id, Read(5, [Position(2, null, null, 101)]));             // nothing reported: skipped

        var kept = await db.RiskObservations.OrderBy(o => o.ObservedAtUtc).ToListAsync();
        Assert.Equal([(0, 80m, "cross"), (2, 81m, "isolated"), (3, 85m, "isolated"), (4, 86m, "isolated")],
            kept.Select(o => ((int)(o.ObservedAtUtc - Start).TotalMinutes, o.LiquidationPrice!.Value, o.MarginMode!)));
        // The snapshot always holds the latest report, unknown included.
        var position = await db.Snapshots.Where(s => s.AccountId == account.Id).SelectMany(s => s.Positions).SingleAsync();
        Assert.Equal(((decimal?)null, (string?)null), (position.LiquidationPrice, position.MarginMode));
    }

    [PostgresFact]
    public async Task The_execution_read_shows_the_current_and_first_fill_risk_context()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using (var db = database.Context(owner))
        {
            var account = await AddAccountAsync(db, owner);
            var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
                PlayDocuments.Serialize(TestPlays.Plan()), Start);
            var (change, revision) = play.MarkPlanned(Start);
            db.Plays.Add(play); db.Add(change); db.Add(revision);
            var order = new ImportedOrder
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ExternalOrderId = "order-f", ContractId = "BTC", Side = "buy",
                OrderType = "Limit", LimitPrice = 100, OriginalSize = 1, RemainingSize = 0, PlacedAtUtc = Start.AddMinutes(1), Status = "filled",
                VenueStatus = "filled", StatusAtUtc = Start.AddMinutes(2), ObservedAtUtc = Start.AddMinutes(2),
            };
            db.Orders.Add(order);
            db.OrderLinks.Add(new PlayOrderLink(play, order, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Automatic, Start.AddMinutes(2)));
            await db.SaveChangesAsync();
            await SaveAsync(db, account.Id, Read(0, [Position(1, 70)]));                         // before the Play's fill
            await SaveAsync(db, account.Id, Read(3, [Position(2, 80, MarginModes.Cross)], Fill("f", 2)));
            await SaveAsync(db, account.Id, Read(9, [Position(2, 82, MarginModes.Cross)]));     // drift: the snapshot moves on
        }
        await using (var db = database.Context(owner))
        {
            var playId = await db.Plays.Select(p => p.Id).SingleAsync();
            var execution = await new PlayExecutionService(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
                TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db)).GetAsync(playId, default);
            Assert.Equal(("2", "82", "cross", Start.AddMinutes(9)), (execution.VenuePosition!.SignedQuantity, execution.VenuePosition.LiquidationPrice,
                execution.VenuePosition.MarginMode, execution.VenuePosition.ObservedAtUtc));
            // At the first entry fill (minute 2), the first observation kept since then: minute 3, not the earlier one.
            Assert.Equal(("80", Start.AddMinutes(3)), (execution.RiskAtFirstFill!.LiquidationPrice, execution.RiskAtFirstFill.ObservedAtUtc));
        }
    }

    [PostgresFact]
    public async Task An_isolated_price_change_and_a_reopening_seen_in_earlier_history_are_kept()
    {
        // Sol round 1 on #41: an isolated liquidation change was dropped, and so was a reopening whose fills arrived before
        // the snapshot that observed it.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = await AddAccountAsync(db, owner);
        await SaveAsync(db, account.Id, Read(0, [Position(1, 80, MarginModes.Isolated)]));
        await SaveAsync(db, account.Id, Read(1, [Position(1, 90, MarginModes.Isolated)]));
        await SaveAsync(db, account.Id, Read(2, [Position(1, 90, MarginModes.Isolated)], Fill("reopen", 3)));
        await SaveAsync(db, account.Id, Read(4, [Position(1, 90, MarginModes.Isolated)], Fill("reopen", 3)));
        await SaveAsync(db, account.Id, Read(6, [Position(1, 90, MarginModes.Isolated)], Fill("reopen", 3)));
        Assert.Equal([0, 1, 2, 4], (await db.RiskObservations.OrderBy(o => o.ObservedAtUtc).ToListAsync())
            .Select(o => (int)(o.ObservedAtUtc - Start).TotalMinutes));
    }

    [PostgresFact]
    public async Task The_first_fill_context_is_the_plays_own_position_and_a_disabled_account_shows_none()
    {
        // Sol round 1 on #41: the read took an opposite position, another native market, or one opened after the Play closed.
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        Guid playId, accountId;
        await using (var db = database.Context(owner))
        {
            var account = await AddAccountAsync(db, owner);
            accountId = account.Id;
            var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
                PlayDocuments.Serialize(TestPlays.Plan()), Start);
            var (change, revision) = play.MarkPlanned(Start);
            db.Plays.Add(play); db.Add(change); db.Add(revision);
            ImportedOrder Order(string id, string side, int minute) => new()
            {
                Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ExternalOrderId = "order-" + id, ContractId = "BTC", Side = side,
                OrderType = "Limit", LimitPrice = 100, OriginalSize = 1, RemainingSize = 0, PlacedAtUtc = Start.AddMinutes(minute), Status = "filled",
                VenueStatus = "filled", StatusAtUtc = Start.AddMinutes(minute), ObservedAtUtc = Start.AddMinutes(minute),
            };
            var entry = Order("in", "buy", 1);
            var exit = Order("out", "sell", 6);
            db.Orders.AddRange(entry, exit);
            db.OrderLinks.Add(new PlayOrderLink(play, entry, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Automatic, Start.AddMinutes(2)));
            db.OrderLinks.Add(new PlayOrderLink(play, exit, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Automatic, Start.AddMinutes(7)));
            await db.SaveChangesAsync();
            playId = play.Id;
            // Its fills are on native market "0"; an opposite position, another market and a later position must all be ignored.
            await SaveAsync(db, account.Id, Read(2, [Position(-1, 120, native: "0")], Fill("in", 2, native: "0")));
            await SaveAsync(db, account.Id, Read(3, [Position(1, 75, native: "9")]));
            await SaveAsync(db, account.Id, Read(4, [Position(1, 80, native: "0")]));
            await SaveAsync(db, account.Id, Read(6, [], Fill("out", 6, "sell", "close", native: "0")));
            await SaveAsync(db, account.Id, Read(8, [Position(1, 99, native: "0")]));
        }
        await using (var db = database.Context(owner))
        {
            PlayExecutionService Service() => new(new PlayStore(db, TestUnitOfWork.For(db)), new ExecutionStore(db, TestUnitOfWork.For(db)),
                TestVenues.With(), TimeProvider.System, TestUnitOfWork.For(db));
            var execution = await Service().GetAsync(playId, default);
            Assert.Equal(("80", Start.AddMinutes(4)), (execution.RiskAtFirstFill!.LiquidationPrice, execution.RiskAtFirstFill.ObservedAtUtc));
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.UpdateSettings(account.Name, account.PortfolioId, false);
            await db.SaveChangesAsync();
            var hidden = await Service().GetAsync(playId, default);
            Assert.Equal(((VenueRiskDto?)null, (VenueRiskDto?)null), (hidden.VenuePosition, hidden.RiskAtFirstFill));
        }
    }
}
