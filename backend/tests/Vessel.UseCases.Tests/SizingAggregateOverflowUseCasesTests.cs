using System.Globalization;
using Vessel.Application.Plays;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Individually representable outcomes whose sums leave decimal range never fail the record or the ladder.</summary>
public sealed class SizingAggregateOverflowUseCasesTests : SizingAggregateOverflowTestsValues
{

    [Fact]
    public async Task Sizing_endpoint_answers_with_an_unknown_ratio_and_a_notice()
    {
        var owner = Guid.NewGuid();
        var store = new MemorySizingStore();
        // A win of 7e26 on a notional of 1 returns 7e28 percent; a loss of 1e-22 returns 1e-20 percent.
        store.Closed.Add(Closed(owner, pnl: 7 * E28 / 100, day: 2));
        store.Closed.Add(Closed(owner, pnl: -decimal.Parse("1e-22", NumberStyles.Float, CultureInfo.InvariantCulture), day: 3));
        var result = await new SizingService(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None).GetAsync(default);
        Assert.Equal((2, (string?)null, "default", "default"),
            (result.Record.DecidedPlays, result.Record.WinLossRatio, result.Limits.MaxStopSource, result.Limits.MinRewardRiskSource));
        Assert.Equal("70000000000000000000000000000", result.Record.AverageGainPercent);
        Assert.EndsWith("The win/loss ratio exceeds the supported decimal range and is unknown.", result.Notice);

        store.Closed.Clear();
        store.Closed.Add(Closed(owner, pnl: 1, day: 2));
        var ordinary = await new SizingService(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None).GetAsync(default);
        Assert.DoesNotContain("win/loss ratio", ordinary.Notice);
    }

    [Fact]
    public async Task Sizing_endpoint_survives_ten_overflowing_wins()
    {
        var owner = Guid.NewGuid();
        var store = new MemorySizingStore();
        for (var day = 2; day < 12; day++) store.Closed.Add(Closed(owner, pnl: E28 / 100, day: day));
        var result = await new SizingService(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None).GetAsync(default);
        Assert.Equal((10, "10000000000000000000000000000"), (result.Record.Wins, result.Record.AverageGainPercent));
    }

    private static ClosedPlayFacts Closed(Guid owner, decimal pnl, int day)
    {
        var planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z", CultureInfo.InvariantCulture);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var plan = new PlayPlanDocument("long", "margin", "250", "1", null,
            [new PlanEntry("entry-1", "entry-1", "#b9c9e4", "100", "1", [new PlanExit("stop-1", "price", "0.5", "100")],
                [new PlanExit("t", "price", "2", "100")])], new PlanNotes("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), planned);
        var revision = play.MarkPlanned(planned).Revision;
        play.MarkOpen("Filled.", planned.AddHours(3));
        play.MarkClosed("Exited.", planned.AddDays(day));
        ImportedFill Fill(string side, decimal closed, int minutes) => new()
        {
            Id = Guid.NewGuid(), Price = 1, Quantity = 1, ClosedPnlUsd = closed, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
            OccurredAtUtc = planned.AddMinutes(minutes), ExternalOrderId = "o" + minutes, ContractId = "BTC", SourceFillId = "f" + minutes, Side = side,
            PositionEffect = ExecutionFacts.Unknown, Direction = "", TransactionHash = "",
        };
        return new ClosedPlayFacts(play, [revision],
            // Each Play exits an hour before its own detection day, so outcomes never share an exit time.
            [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 0, 30)), new(OrderLinkRole.Target, Fill(ExecutionFacts.Sell, pnl, 60 * 24 * day - 60))]);
    }
}
