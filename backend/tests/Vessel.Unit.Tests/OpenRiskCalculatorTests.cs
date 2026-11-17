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

    /// <summary>A venue order linked to a stop, as the venue reported it, with its own retained executions.</summary>
    private static RiskOrder Order(string level, string status, decimal original, decimal remaining, decimal executed, string entry = "e1",
        bool positionTpsl = false) => new(entry, level, status, original, remaining, positionTpsl, executed);

    private static PlayRisk Measure(PlayPlanDocument plan, RiskOrder[] orders, params RiskExecution[] executions) =>
        OpenRiskCalculator.Calculate(plan, executions, usdQuoted: true, orders);

    private static string? Risk(PlayPlanDocument plan, RiskOrder[] orders, params RiskExecution[] executions) =>
        Measure(plan, orders, executions).Risk?.ToString();

    /// <summary>#117's plan: long or short 10 at 100, stops for 50% at the far price and 50% at the near one.</summary>
    private static PlayPlanDocument Halves(string direction, string far, string near, string farShare = "50", string nearShare = "50") =>
        Plan(direction, size: "10", entries: Entry("e1", "100", "100", Stop(far, farShare, id: "a"), Stop(near, nearShare, id: "b")));

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

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("01")]
    [InlineData(" 1")]
    public void Unfinished_or_invalid_leverage_cannot_produce_a_numeric_preview(string leverage)
    {
        var plan = Plan(sizing: "margin", size: "100", leverage: leverage,
            entries: Entry("e1", "100", "100", Stop("10", unit: "percent")));
        var result = OpenRiskCalculator.Calculate(plan, [], true);
        Assert.Equal((null, OpenRiskCalculator.NoQuantity),
            (result.Risk, result.Reason));
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
        // The first of two stops filled its whole order: what remains faces the other one alone, 0.5 × 10.
        var two = Plan(entries: Entry("e1", "100", "100", Stop("95", "50", id: "a"), Stop("90", "50", id: "b")));
        Assert.Equal("5", Risk(two, [Order("a", "filled", 0.5m, 0, 0.5m)], Fill(OrderLinkRole.Entry, 1, 100),
            Fill(OrderLinkRole.Stop, 0.5m, 95, level: "a")));
        // Fully exited: nothing remains.
        Assert.Equal("0", Risk(Plan(), Fill(OrderLinkRole.Entry, 1, 100), Fill(OrderLinkRole.Target, 1, 110, level: "t1")));
    }

    [Theory]
    [InlineData("long", "90", "99", 90, 110)]
    [InlineData("short", "110", "101", 110, 90)]
    public void A_part_filled_stop_counts_for_what_it_still_rests_for(string direction, string far, string near, int fill, int target)
    {
        // #117: the far stop's order for 5 filled 1 and still rests for 4 (5 − 1 = 1 executed, as retained). The 9 left open
        // face 4 at the far stop and 5 at the near one: 4 × 10 + 5 × 1 = 45, not 9 × 1 = 9 at the near stop alone.
        var plan = Halves(direction, far, near);
        RiskOrder[] orders = [Order("a", "open", 5, 4, 1)];
        Assert.Equal("45", Risk(plan, orders, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, fill, level: "a")));
        // A target that took 2 more shrinks what each stop covers in proportion to what it rests for, 4 : 5:
        // 7 × 4/9 × 10 + 7 × 5/9 × 1 = 280/9 + 35/9 = 315/9 = 35.
        Assert.Equal("35", Risk(plan, orders, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, fill, level: "a"),
            Fill(OrderLinkRole.Target, 2, target, level: "t1")));
    }

    [Theory]
    [InlineData("long", "90", "99", 90)]
    [InlineData("short", "110", "101", 110)]
    public void The_same_execution_reads_by_whether_its_order_is_complete(string direction, string far, string near, int fill)
    {
        // Review S1: both orders produce the same one-unit execution at the far stop. The order for 5 still rests for 4:
        // 4 × 10 + 5 × 1 = 45. The order for 1 is filled and rests for nothing, so the 9 left face the near stop: 9 × 1 = 9.
        var plan = Halves(direction, far, near);
        RiskExecution[] executions = [Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, fill, level: "a")];
        Assert.Equal("45", Risk(plan, [Order("a", "open", 5, 4, 1)], executions));
        Assert.Equal("9", Risk(plan, [Order("a", "filled", 1, 0, 1)], executions));
    }

    [Theory]
    [InlineData("long", "90", "99", 90)]
    [InlineData("short", "110", "101", 110)]
    public void A_fully_filled_stop_still_adds_nothing(string direction, string far, string near, int fill)
    {
        // The far stop's order for 5 filled all 5: the 5 left open face the near stop alone, 5 × 1 = 5.
        var plan = Halves(direction, far, near);
        RiskOrder[] five = [Order("a", "filled", 5, 0, 5)];
        Assert.Equal("5", Risk(plan, five, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 5, fill, level: "a")));
        // Split over two fills, the same.
        Assert.Equal("5", Risk(plan, five, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 2, fill, level: "a"),
            Fill(OrderLinkRole.Stop, 3, fill, level: "a")));
        // An order for 6, beyond the stop's share, filled: the 4 left face the near stop, 4 × 1 = 4.
        Assert.Equal("4", Risk(plan, [Order("a", "filled", 6, 0, 6)], Fill(OrderLinkRole.Entry, 10, 100),
            Fill(OrderLinkRole.Stop, 6, fill, level: "a")));
    }

    [Fact]
    public void Both_stops_part_filled_count_for_what_each_still_rests_for()
    {
        // Far 5 filled 1 rests for 4; near 5 filled 2 rests for 3. The 7 left: 7 × 4/7 × 10 + 7 × 3/7 × 1 = 40 + 3 = 43.
        Assert.Equal("43", Risk(Halves("long", "90", "99"), [Order("a", "open", 5, 4, 1), Order("b", "open", 5, 3, 2)],
            Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, 90, level: "a"), Fill(OrderLinkRole.Stop, 2, 99, level: "b")));
    }

    [Fact]
    public void An_asymmetric_allocation_shares_by_what_each_stop_rests_for()
    {
        // 20% at 90 (an order for 2) and 80% at 99. The far order filled 1 and rests for 1; the near stop, untouched, counts
        // for 80% of 10 = 8. The 9 left: 9 × 1/9 × 10 + 9 × 8/9 × 1 = 10 + 8 = 18.
        Assert.Equal("18", Risk(Halves("long", "90", "99", "20", "80"), [Order("a", "open", 2, 1, 1)],
            Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, 90, level: "a")));
    }

    [Fact]
    public void A_partial_entry_shares_its_open_part_and_plans_the_rest()
    {
        // Planned 10, filled 6 at 100. The far order for 3 (half of 6) filled 1 and rests for 2; the near stop counts for half
        // of 6 = 3. Open part, 5 left: 5 × 2/5 × 10 + 5 × 3/5 × 1 = 20 + 3 = 23. Planned part, 4 unfilled at 100 against
        // both stops by share: 4 × 1/2 × 10 + 4 × 1/2 × 1 = 20 + 2 = 22. Total 45.
        Assert.Equal("45", Risk(Halves("long", "90", "99"), [Order("a", "open", 3, 2, 1)],
            Fill(OrderLinkRole.Entry, 6, 100), Fill(OrderLinkRole.Stop, 1, 90, level: "a")));
    }

    [Fact]
    public void Two_entries_each_count_their_own_stops()
    {
        // 20 long: e1 10 at 100 with stops 50% at 90 (a) and 50% at 99 (b); e2 10 at 90 with one stop at 80 (c).
        // e1: a's order for 5 filled 1, rests for 4; 9 left: 4 × 10 + 5 × 1 = 45.
        // e2: c's order for 10 filled 2, rests for 8; 8 left face c alone: 8 × (90 − 80) = 80. Total 125.
        var plan = Plan(size: "20", entries: [Entry("e1", "50", "100", Stop("90", "50", id: "a"), Stop("99", "50", id: "b")),
            Entry("e2", "50", "90", Stop("80", id: "c"))]);
        Assert.Equal("125", Risk(plan, [Order("a", "open", 5, 4, 1), Order("c", "open", 10, 8, 2, entry: "e2")],
            Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Entry, 10, 90, "e2"),
            Fill(OrderLinkRole.Stop, 1, 90, level: "a"), Fill(OrderLinkRole.Stop, 2, 80, "e2", "c")));
    }

    [Fact]
    public void A_cancelled_part_filled_stop_rests_for_nothing_and_a_replacement_counts_for_itself()
    {
        var plan = Halves("long", "90", "99");
        RiskExecution[] executions = [Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, 90, level: "a")];
        // The far order filled 1 and was cancelled: it no longer rests, so the 9 left face the near stop, 9 × 1 = 9.
        Assert.Equal("9", Risk(plan, [Order("a", "canceled", 5, 4, 1)], executions));
        Assert.Equal("9", Risk(plan, [Order("a", "rejected", 5, 4, 1)], executions));
        // A new linked order for 4 replaced it: the stop rests for 4 again, 4 × 10 + 5 × 1 = 45.
        Assert.Equal("45", Risk(plan, [Order("a", "canceled", 5, 4, 1), Order("a", "open", 4, 4, 0)], executions));
        // Both stops done and something still open: no stop rests for it, so the Play is unknown, never zero.
        var done = Measure(plan, [Order("a", "filled", 1, 0, 1), Order("b", "filled", 1, 0, 1)], Fill(OrderLinkRole.Entry, 10, 100),
            Fill(OrderLinkRole.Stop, 1, 90, level: "a"), Fill(OrderLinkRole.Stop, 1, 99, level: "b"));
        Assert.Equal((null, OpenRiskCalculator.NoStop), (done.Risk, done.Reason));
    }

    public static TheoryData<RiskOrder[]> Unreconciled => new()
    {
        // No order facts at all.
        Array.Empty<RiskOrder>(),
        // Live, but 5 − 5 = 0 executed against the 1 retained.
        new[] { Order("a", "open", 5, 5, 1) },
        // Live and resting for nothing.
        new[] { Order("a", "triggered", 1, 0, 1) },
        // Filled, yet still reporting a remaining size.
        new[] { Order("a", "filled", 5, 4, 1) },
        // More remaining than the order's size.
        new[] { Order("a", "canceled", 5, 6, 1) },
        // A status that does not say whether it rests.
        new[] { Order("a", "other", 5, 4, 1) },
        // A whole-position TP/SL has no fixed resting size.
        new[] { Order("a", "open", 5, 4, 1, positionTpsl: true) },
        // An order for another entry's level of the same ID says nothing about this one.
        new[] { Order("a", "open", 5, 4, 1, entry: "e2") },
    };

    [Theory]
    [MemberData(nameof(Unreconciled))]
    public void An_executed_stop_whose_orders_do_not_reconcile_makes_the_play_unknown(RiskOrder[] orders)
    {
        var result = Measure(Halves("long", "90", "99"), orders, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Stop, 1, 90, level: "a"));
        Assert.Equal((null, OpenRiskCalculator.UnreconciledStop), (result.Risk, result.Reason));
    }

    [Fact]
    public void Plan_aligned_exits_before_any_stop_execution_keep_the_plan_shares()
    {
        var plan = Halves("long", "90", "99");
        // A target took 2: the 8 left, 4 and 4: 4 × 10 + 4 × 1 = 44.
        Assert.Equal("44", Risk(plan, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Target, 2, 110, level: "t1")));
        // A manual or unplanned close of 2, the same: 44.
        Assert.Equal("44", Risk(plan, Fill(OrderLinkRole.Entry, 10, 100), Fill(OrderLinkRole.Exit, 2, 104, entry: null)));
        // The far stop moved to the entry or beyond adds nothing: 5 × 0 + 5 × 1 = 5.
        Assert.Equal("5", Risk(Halves("long", "100", "99"), Fill(OrderLinkRole.Entry, 10, 100)));
        Assert.Equal("5", Risk(Halves("long", "101", "99"), Fill(OrderLinkRole.Entry, 10, 100)));
        // There, it filled 1 and rests for 4: 9 × 4/9 × 0 + 9 × 5/9 × 1 = 5.
        Assert.Equal("5", Risk(Halves("long", "101", "99"), [Order("a", "open", 5, 4, 1)], Fill(OrderLinkRole.Entry, 10, 100),
            Fill(OrderLinkRole.Stop, 1, 101, level: "a")));
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
