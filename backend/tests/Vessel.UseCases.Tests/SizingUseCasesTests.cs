using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Sizing;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class SizingServiceUseCasesTests : SizingServiceTestsValues
{
    private readonly MemorySizingStore store = new();
    private SizingService Service => new(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None);

    [Fact]
    public async Task Get_uses_the_default_risk_and_reports_the_record_newest_first()
    {
        // A long's stop and target sell. The outcomes close at their exits (an hour apart), not at detection.
        store.Closed.Add(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0)),
            new(OrderLinkRole.Stop, Fill(95, 1, pnl: -5, fee: 0, minutes: 60, side: ExecutionFacts.Sell))], closedDay: 2));
        store.Closed.Add(Closed(Plan(), [new(OrderLinkRole.Entry, Fill(100, 1, fee: 0)),
            new(OrderLinkRole.Target, Fill(110, 1, pnl: 10, fee: 0, minutes: 120, side: ExecutionFacts.Sell))], closedDay: 3));
        var sizing = await Service.GetAsync(default);
        Assert.Equal("1.25", sizing.Settings.RiskPercent);
        Assert.Equal(("full", "1", "1.25", "10", "2"), (sizing.Exposure.Level, sizing.Exposure.Multiplier, sizing.Limits.EffectiveRiskPercent,
            sizing.Limits.MaxStopPercent, sizing.Limits.MinRewardRisk));
        Assert.Equal(("0.5", "2", "1"), (sizing.Record.BattingAverage, sizing.Record.WinLossRatio, sizing.Record.BreakEvenRewardRisk));
        Assert.Equal(["win", "loss"], sizing.Record.Recent.Select(r => r.Outcome));
        Assert.Equal(("10", "10", "5", "2"), (sizing.Record.Recent[0].NetResultUsd, sizing.Record.Recent[0].ReturnPercent,
            sizing.Record.Recent[0].RiskUsd, sizing.Record.Recent[0].RMultiple));
    }

    [Fact]
    public async Task Closed_records_their_fills_cannot_settle_are_not_scored()
    {
        LinkedFill In(int minutes = 0) => new(OrderLinkRole.Entry, Fill(100, 1, fee: 0, minutes: minutes, effect: ExecutionFacts.Open));
        LinkedFill Out(int minutes = 5, decimal quantity = 1, string side = ExecutionFacts.Sell, string effect = ExecutionFacts.Close) =>
            new(OrderLinkRole.Target, Fill(110, quantity, pnl: 10, fee: 0, minutes: minutes, side: side, effect: effect));
        store.Closed.Add(Closed(Plan(), [In(), Out()]));
        store.Closed.Add(Closed(Plan(), [In(minutes: 10), Out()]));
        store.Closed.Add(Closed(Plan(), [In(), Out(side: ExecutionFacts.Buy, effect: ExecutionFacts.Open)]));
        store.Closed.Add(Closed(Plan(), [In(), Out(effect: ExecutionFacts.Flip)]));
        store.Closed.Add(Closed(Plan(), [In(), Out(quantity: 2)]));
        // The entry order filled 2 at the venue, but only this one execution is retained.
        store.Closed.Add(Closed(Plan(), [In(), Out()]) with { IncompleteOrders = ["o"] });
        var sizing = await Service.GetAsync(default);
        Assert.Equal((1, 1), (sizing.Record.ClosedPlays, sizing.Record.Wins));
    }

    [Fact]
    public async Task Update_saves_the_owners_risk_and_rejects_bad_values()
    {
        var sizing = await Service.UpdateSettingsAsync(new("2"), default);
        Assert.Equal(("2", owner, 1), (sizing.Settings.RiskPercent, store.Settings!.OwnerId, store.Saves));
        Assert.Equal("1.5", (await Service.UpdateSettingsAsync(new("1.50"), default)).Settings.RiskPercent);
        foreach (var bad in new[] { "", "abc", "1e2", "-1", " 1" })
            Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => Service.UpdateSettingsAsync(new(bad), default))).StatusCode);
        foreach (var outside in new[] { "0.05", "6", "1.333" })
            await Assert.ThrowsAsync<DomainValidationException>(() => Service.UpdateSettingsAsync(new(outside), default));
        Assert.Equal(1.5m, store.Settings.RiskPercent);
    }
}
