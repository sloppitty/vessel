using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 7: omission markers are durable and deduplicated, and an order they name has known missing executions
/// whatever its status, whichever of the two arrived first.
/// </summary>
public sealed class OmissionMarkerPostgresTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static PerpetualVenueReadResult Read(DateTimeOffset at, params VenueOmission[] omissions) =>
        new(new(at, "synthetic", 100, 100, 0, []), [], [], "Synthetic", PositionsComplete: true, FillHistoryTruncated: false)
        { Omissions = omissions };

    private static VenueOrder VenueOrder(string id) => new(id, "BTC", "sell", "Limit", 100, null, false, false, 2, 1, At.AddHours(-2),
        VenueOrderStatus.Canceled, "canceled", At.AddHours(-1));

    [PostgresFact]
    public async Task Markers_survive_reads_that_no_longer_return_the_row_and_flag_their_orders_in_either_order()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        account.Configure(null, "0x" + new string('c', 40), null);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db);
        // Recorded five seconds after the snapshot it came with.
        var workspace = new WorkspaceStore(db, work, new ManualTime(At.AddSeconds(5)));
        var executions = new ExecutionStore(db, work);
        async Task Unit(Func<Task> step) => await work.ExecuteAsync(async () => { await step(); return true; }, default);

        // The order is retained first; the marker naming it arrives with a later read.
        await Unit(() => executions.UpsertOrdersAsync(account, [VenueOrder("before")], At, default));
        var unknownTime = new VenueOmission("t2", ExecutionOmission.MissingPnl, "no reported realized P&L", "BTC", null, "later", null);
        await Unit(() => workspace.SaveRefreshAsync(account, Read(At, new VenueOmission("t1", ExecutionOmission.Fee, "fee-bearing or unverified fee", "BTC", null, "before", At.AddMinutes(-5)),
            unknownTime) with { OldestRawFillAtUtc = At.AddHours(-3) }, default));
        // A read that no longer returns the rows erases nothing; one that returns them again adds nothing.
        await Unit(() => workspace.SaveRefreshAsync(account, Read(At.AddMinutes(10)), default));
        await Unit(() => workspace.SaveRefreshAsync(account, Read(At.AddMinutes(20), new VenueOmission("t1", ExecutionOmission.Fee, "fee-bearing or unverified fee", "BTC", null, "before", At.AddMinutes(-5))), default));
        // The other marker's order arrives after it.
        await Unit(() => executions.UpsertOrdersAsync(account, [VenueOrder("later"), VenueOrder("unnamed")], At.AddMinutes(30), default));
        db.ChangeTracker.Clear();
        var markers = await db.ExecutionOmissions.AsNoTracking().OrderBy(m => m.SourceId).ToListAsync();
        Assert.Equal(["t1", "t2"], markers.Select(m => m.SourceId));
        Assert.Equal((At, At.AddMinutes(20)), (markers[0].FirstSeenAtUtc, markers[0].LastSeenAtUtc));
        // An unreported time is bounded by its read's window, which ends when the read was recorded, after its snapshot
        // (Sol round 1: Lighter reads its trades after the snapshot).
        Assert.Equal(((DateTimeOffset?)null, At.AddHours(-3), At.AddSeconds(5)), (markers[1].OccurredAtUtc, markers[1].UncertainFromUtc!.Value, markers[1].UncertainToUtc!.Value));
        var flagged = (await db.Orders.AsNoTracking().ToListAsync()).ToDictionary(o => o.ExternalOrderId, o => o.ExecutionsOmitted);
        Assert.Equal((true, true, false), (flagged["before"], flagged["later"], flagged["unnamed"]));
    }

    private static ImportedOrder On(string contract, string? native, string id = "o") => new()
    {
        Id = Guid.NewGuid(), ContractId = contract, VenueContractId = native, ExternalOrderId = id, Side = "sell", OrderType = "Limit",
        OriginalSize = 2, RemainingSize = 1, Status = "canceled", VenueStatus = "canceled"
    };

    [PostgresFact]
    public async Task Unrecognized_market_markers_flag_their_orders_and_a_marker_on_another_native_market_fails_closed_either_way()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var account = new Account(Guid.NewGuid(), owner, "risex", "Synthetic");
        account.Configure(null, "0x" + new string('d', 40), null);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db);
        var workspace = new WorkspaceStore(db, work, new ManualTime(At));
        var executions = new ExecutionStore(db, work);
        async Task Unit(Func<Task> step) => await work.ExecuteAsync(async () => { await step(); return true; }, default);
        VenueOrder Native(string id, string contract, string native) => new(id, contract, "sell", "Limit", 100, null, false, false, 2, 1,
            At.AddHours(-2), VenueOrderStatus.Canceled, "canceled", At.AddHours(-1), VenueContractId: native);

        // An order retained while its market was listed, then an omission once the catalogue no longer lists it.
        await Unit(() => executions.UpsertOrdersAsync(account, [Native("gone", "XYZ", "7"), Native("kept", "BTC", "1")], At, default));
        await Unit(() => workspace.SaveRefreshAsync(account, Read(At, new VenueOmission("t1", ExecutionOmission.UnrecognizedMarket,
            "unrecognized market", null, "7", "gone", At.AddMinutes(-5))), default));
        // A marker first, then its order on the same unrecognized native market.
        await Unit(() => workspace.SaveRefreshAsync(account, Read(At.AddMinutes(1), new VenueOmission("t2", ExecutionOmission.MissingPnl,
            "no reported realized P&L", null, "8", "late", At.AddMinutes(-4))), default));
        await Unit(() => executions.UpsertOrdersAsync(account, [Native("late", "ABC", "8")], At.AddMinutes(2), default));
        db.ChangeTracker.Clear();
        var flagged = (await db.Orders.AsNoTracking().ToListAsync()).ToDictionary(o => o.ExternalOrderId, o => o.ExecutionsOmitted);
        Assert.Equal((true, true, false), (flagged["gone"], flagged["late"], flagged["kept"]));

        // The same canonical order reported on another native market fails closed, saving nothing, in either order.
        await Assert.ThrowsAsync<VenueReadException>(() => Unit(() => workspace.SaveRefreshAsync(account, Read(At.AddMinutes(3),
            new VenueOmission("t3", ExecutionOmission.MissingPnl, "no reported realized P&L", "BTC", "2", "kept", At.AddMinutes(-3))), default)));
        db.ChangeTracker.Clear();
        Assert.False((await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == "kept")).ExecutionsOmitted);
        Assert.Null(await db.ExecutionOmissions.AsNoTracking().SingleOrDefaultAsync(m => m.SourceId == "t3"));
        await Unit(() => workspace.SaveRefreshAsync(account, Read(At.AddMinutes(4), new VenueOmission("t4", ExecutionOmission.MissingPnl,
            "no reported realized P&L", "ETH", "5", "pending", At.AddMinutes(-2))), default));
        await Assert.ThrowsAsync<VenueReadException>(() => Unit(() =>
            executions.UpsertOrdersAsync(account, [Native("pending", "ETH", "6")], At.AddMinutes(5), default)));
        db.ChangeTracker.Clear();
        Assert.Null(await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.ExternalOrderId == "pending"));
    }
}
