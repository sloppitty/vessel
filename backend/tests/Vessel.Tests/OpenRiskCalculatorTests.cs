using Vessel.Application.Plays;
using Vessel.Application.Risk;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 1 (G-06): a Play's open risk is the loss if every current stop fills, from the average fill of what is open
/// and the planned price of what is not. Never netted, never negative, and unknown rather than partly summed.
/// </summary>
public sealed class OpenRiskCalculatorTests
{
    private static PlanExit Stop(string value, string share = "100", string unit = "price", string id = "s1") => new(id, unit, value, share);

    private static PlayPlanDocument Plan(string direction = "long", string sizing = "quantity", string size = "1", string leverage = "1",
        params PlanEntry[] entries) => new(direction, sizing, size, leverage, null,
        entries.Length == 0 ? [Entry("e1", "100", "100", Stop("95"))] : entries, new PlanNotes("", "", "", ""));

    private static PlanEntry Entry(string id, string share, string price, params PlanExit[] stops) =>
        new(id, id, "#000000", share, price, stops, []);

    private static RiskExecution Fill(OrderLinkRole role, decimal quantity, decimal price, string? entry = "e1", string? level = null) =>
        new(role, entry, level, quantity, price);

    private static string? Risk(PlayPlanDocument plan, params RiskExecution[] executions) =>
        OpenRiskCalculator.Calculate(plan, executions, usdQuoted: true).Risk?.ToString();

    [Fact]
    public void A_planned_long_risks_its_planned_quantity_to_the_stop()
    {
        // 1.25% of a 1,000 balance: 2.5 units from 100 to 95.
        Assert.Equal("12.5", Risk(Plan(size: "2.5")));
        // A short loses above its entry.
        Assert.Equal("12.5", Risk(Plan("short", size: "2.5", entries: Entry("e1", "100", "100", Stop("105")))));
    }

    [Fact]
    public void Margin_sizing_and_percent_stops_stay_exact()
    {
        // 300 margin at 3× over 100 is 9 units; a 15% return stop at 3× is a 5% move, to 95.
        var plan = Plan(sizing: "margin", size: "300", leverage: "3", entries: Entry("e1", "100", "100", Stop("15", unit: "percent")));
        Assert.Equal("45", Risk(plan));
        // 100 margin over 3 is a repeating quantity: rounded once, and said so.
        var repeating = OpenRiskCalculator.Calculate(Plan(sizing: "margin", size: "100", entries: Entry("e1", "100", "3", Stop("2"))), [], true);
        Assert.False(repeating.Risk!.Value.Terminates);
        Assert.Equal("33.3333333333333333333333333333", repeating.Risk.Value.ToString());
    }

    [Fact]
    public void A_managed_stop_at_or_beyond_the_fill_adds_nothing_and_never_offsets_another()
    {
        // Round 3: a stop at 105 on a long filled at 100 is +5 locked in, which is no risk, never −5.
        var locked = Plan(entries: Entry("e1", "100", "100", Stop("105")));
        Assert.Equal("0", Risk(locked, Fill(OrderLinkRole.Entry, 1, 100)));
        Assert.Equal("0", Risk(Plan(entries: Entry("e1", "100", "100", Stop("100"))), Fill(OrderLinkRole.Entry, 1, 100)));
        // Half locked in at 105 and half at risk to 95: 2.5 at risk, not 0.
        var split = Plan(entries: Entry("e1", "100", "100", Stop("105", "50", id: "a"), Stop("95", "50", id: "b")));
        Assert.Equal("2.5", Risk(split, Fill(OrderLinkRole.Entry, 1, 100)));
    }

    [Fact]
    public void The_open_part_counts_from_the_average_fill()
    {
        // Filled at 102 against a plan at 100: the loss to 95 is 7, not 5.
        Assert.Equal("7", Risk(Plan(), Fill(OrderLinkRole.Entry, 1, 102)));
        // Two fills average 101.
        Assert.Equal("6", Risk(Plan(), Fill(OrderLinkRole.Entry, 0.5m, 100), Fill(OrderLinkRole.Entry, 0.5m, 102)));
    }

    [Fact]
    public void Exits_reduce_what_remains_and_executed_stops_have_done_their_part()
    {
        // A target took half: half remains at the stop.
        Assert.Equal("2.5", Risk(Plan(), Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Target, 0.5m, 110, level: "t1")));
        // The first of two stops filled: what remains faces the other one alone.
        var two = Plan(entries: Entry("e1", "100", "100", Stop("95", "50", id: "a"), Stop("90", "50", id: "b")));
        Assert.Equal("5", Risk(two, Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Stop, 0.5m, 95, level: "a")));
        // Fully exited: nothing remains.
        Assert.Equal("0", Risk(Plan(), Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Target, 1, 110, level: "t1")));
    }

    [Fact]
    public void An_unfilled_remainder_of_the_plan_counts_at_its_planned_price()
    {
        // Planned 2 at 100, 1 filled at 101: 1 × 6 open plus 1 × 5 planned.
        Assert.Equal("11", Risk(Plan(size: "2"), Fill(OrderLinkRole.Entry, 1, 101)));
        // Sol round 1: a planned 10 at 100 with its stop at 90, filled 1, risks 10 open plus 90 still planned.
        Assert.Equal("100", Risk(Plan(size: "10", entries: Entry("e1", "100", "100", Stop("90"))), Fill(OrderLinkRole.Entry, 1, 100)));
        // Two entries, one filled: the other still counts as planned.
        var two = Plan(size: "2", entries: [Entry("e1", "50", "100", Stop("95")), Entry("e2", "50", "90", Stop("85"))]);
        Assert.Equal("10", Risk(two, Fill(OrderLinkRole.Entry, 1, 100)));
    }

    [Fact]
    public void A_position_level_exit_belongs_to_the_only_filled_entry()
    {
        Assert.Equal("2.5", Risk(Plan(), Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Exit, 0.5m, 104, entry: null)));
        var two = Plan(size: "2", entries: [Entry("e1", "50", "100", Stop("95")), Entry("e2", "50", "90", Stop("85"))]);
        var result = OpenRiskCalculator.Calculate(two, [Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Entry, 1, 90, "e2"),
            Fill(OrderLinkRole.Exit, 0.5m, 104, entry: null)], true);
        Assert.Equal((null, OpenRiskCalculator.Unattributed), (result.Risk, result.Reason));
    }

    [Fact]
    public void A_stop_that_cannot_be_measured_makes_the_play_unknown()
    {
        // D-04's case: 50% at 95 and 50% unpriced is unknown, never the known half.
        var unpriced = Plan(entries: Entry("e1", "100", "100", Stop("95", "50", id: "a"), Stop("", "50", id: "b")));
        Assert.Equal((null, OpenRiskCalculator.UnpricedStop), Unknown(unpriced));
        // Stops covering half the entry leave the other half without one.
        Assert.Equal((null, OpenRiskCalculator.StopShares), Unknown(Plan(entries: Entry("e1", "100", "100", Stop("95", "50")))));
    }

    private static (Ratio?, string?) Unknown(PlayPlanDocument plan)
    {
        var result = OpenRiskCalculator.Calculate(plan, [], true);
        return (result.Risk, result.Reason);
    }

    [Fact]
    public void Other_unknowns_are_named()
    {
        Assert.Equal(OpenRiskCalculator.NoStop, OpenRiskCalculator.Calculate(Plan(entries: Entry("e1", "100", "100")), [], true).Reason);
        Assert.Equal(OpenRiskCalculator.NoQuantity, OpenRiskCalculator.Calculate(Plan(size: ""), [], true).Reason);
        Assert.Equal(OpenRiskCalculator.Units, OpenRiskCalculator.Calculate(Plan(), [], usdQuoted: false).Reason);
        Assert.Equal(OpenRiskCalculator.ExitsExceed,
            OpenRiskCalculator.Calculate(Plan(), [Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Stop, 2, 95, level: "s1")], true).Reason);
        Assert.Equal(OpenRiskCalculator.Contradicting,
            OpenRiskCalculator.Calculate(Plan(), [new(OrderLinkRole.Entry, "e1", null, 1, 100, Contradicts: true)], true).Reason);
        // Every plan must allocate the whole position (Sol round 1 on #36): half an entry is unknown, not half the risk.
        Assert.Equal(OpenRiskCalculator.EntryShares,
            OpenRiskCalculator.Calculate(Plan(sizing: "margin", size: "100", entries: Entry("e1", "50", "100", Stop("95"))), [], true).Reason);
        Assert.Equal(OpenRiskCalculator.EntryShares,
            OpenRiskCalculator.Calculate(Plan(size: "10", entries: Entry("e1", "50", "100", Stop("90"))), [], true).Reason);
        // Margin sizing also needs every entry priced.
        Assert.Equal(OpenRiskCalculator.NoQuantity, OpenRiskCalculator.Calculate(Plan(sizing: "margin", size: "100",
            entries: [Entry("e1", "50", "100", Stop("95")), Entry("e2", "50", "", Stop("90", id: "s2"))]), [], true).Reason);
    }

    [Fact]
    public void A_zero_share_entry_cannot_hide_exits_beyond_its_fills()
    {
        // Sol round 1 on #36: entry A has no share and no fill, but a linked stop of A executed one unit.
        var plan = Plan(entries: [Entry("a", "0", "100", Stop("95", id: "sa")), Entry("b", "100", "100", Stop("95", id: "sb"))]);
        var result = OpenRiskCalculator.Calculate(plan, [Fill(OrderLinkRole.Stop, 1, 95, entry: "a", level: "sa")], true);
        Assert.Equal((null, OpenRiskCalculator.ExitsExceed), (result.Risk, result.Reason));
    }
}
