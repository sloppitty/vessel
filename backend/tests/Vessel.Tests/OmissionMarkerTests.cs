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
public sealed class OmissionMarkerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static ImportedOrder Order(string status, bool omitted = false) => new()
    {
        Id = Guid.NewGuid(), ContractId = "BTC", ExternalOrderId = "o", Side = "sell", OrderType = "Limit", OriginalSize = 2,
        RemainingSize = status == "filled" ? 0 : 1, Status = status, VenueStatus = status, ExecutionsOmitted = omitted
    };

    [Theory]
    [InlineData("filled")]
    [InlineData("canceled")]
    [InlineData("open")]
    public void An_order_with_an_omitted_execution_is_missing_executions_whatever_its_status(string status)
    {
        var fills = new[] { new ImportedFill { Quantity = 2 } };
        Assert.False(ExecutionIntegrity.MissingExecutions(Order(status), fills));
        Assert.True(ExecutionIntegrity.MissingExecutions(Order(status, omitted: true), fills));
    }

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
        var workspace = new WorkspaceStore(db, work);
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
        // An unreported time is bounded by its read's window.
        Assert.Equal(((DateTimeOffset?)null, At.AddHours(-3), At), (markers[1].OccurredAtUtc, markers[1].UncertainFromUtc!.Value, markers[1].UncertainToUtc!.Value));
        var flagged = (await db.Orders.AsNoTracking().ToListAsync()).ToDictionary(o => o.ExternalOrderId, o => o.ExecutionsOmitted);
        Assert.Equal((true, true, false), (flagged["before"], flagged["later"], flagged["unnamed"]));
    }
}
