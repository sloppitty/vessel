using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ExecutionQuantityPrecisionTests : ExecutionQuantityPrecisionTestsSupport
{
    private const string HugeText = "10000000000000000000000000000";

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task Linking_huge_entries_keeps_the_small_residual_open_and_reports_exact_quantities(string direction)
    {
        var f = new Fixture(direction);
        f.Add(OrderLinkRole.Entry, Huge, 0);
        f.Add(OrderLinkRole.Entry, 0.1m, 1, price: 1);
        f.Add(OrderLinkRole.Exit, Huge, 2, pnl: 0.1m);

        var result = await f.LinkAll();

        Assert.Equal(("open", HugeText + ".1", HugeText, "0.1"),
            (result.Status, result.Totals.EnteredQuantity, result.Totals.ExitedQuantity, result.Totals.OpenQuantity));
        Assert.Empty(result.Issues);
        Assert.Equal(HugeText + ".1", Assert.Single(result.Entries).FilledQuantity);
        Assert.Equal(new[] { HugeText, "0.1", HugeText }, result.Links.Select(l => l.FilledQuantity));
        Assert.Equal(PlayStatus.Open, f.Play.Status);
        Assert.DoesNotContain(f.Plays.Changes, c => c.To == PlayStatus.Closed);
        Assert.False(ExecutionIntegrity.Settled(f.Facts));
        Assert.Equal(new[] { Huge, 0.1m, Huge }, f.Store.Fills.Select(fill => fill.Quantity));
    }

    [Theory]
    [InlineData(false, "20000000000000000000000000000.1")]
    [InlineData(true, "100000000000000000000000000000.1")]
    public async Task Execution_dtos_preserve_small_reported_pnl_and_fees_between_large_values(bool aboveDecimalRange, string feeTotal)
    {
        var f = new Fixture();
        var large = aboveDecimalRange ? Huge * 5 : Huge;
        f.Add(OrderLinkRole.Entry, 3, 0, price: 1);
        f.Add(OrderLinkRole.Exit, 1, 1, price: 1, pnl: 0.1m).Fee = 0.1m;
        f.Add(OrderLinkRole.Exit, 1, 2, price: 1, pnl: large).Fee = large;
        f.Add(OrderLinkRole.Exit, 1, 3, price: 1, pnl: -large).Fee = large;

        var result = await f.LinkAll();

        Assert.Equal("closed", result.Status);
        Assert.Equal("0.1", result.Totals.ClosedPnlUsd);
        Assert.Equal(feeTotal, Assert.Single(result.Totals.Fees).Amount);
        Assert.Equal("USDC", Assert.Single(result.Totals.Fees).Token);
        Assert.Equal(ExecutionFacts.PnlGross, result.Totals.ClosedPnlBasis);
        Assert.Equal(new[] { 0m, 0.1m, large, -large }, f.Store.Fills.Select(fill => fill.ClosedPnlUsd));
        Assert.Equal(new[] { 0m, 0.1m, large, large }, f.Store.Fills.Select(fill => fill.Fee));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task A_small_over_exit_is_not_rounded_into_a_closed_play(string direction)
    {
        var f = new Fixture(direction);
        f.Add(OrderLinkRole.Entry, Huge, 0);
        // Link the small exit first so the large exit cannot close the Play before both are associated.
        f.Add(OrderLinkRole.Exit, 0.1m, 2);
        f.Add(OrderLinkRole.Exit, Huge, 1);

        var result = await f.LinkAll();

        Assert.Equal(("open", HugeText, HugeText + ".1", "0"),
            (result.Status, result.Totals.EnteredQuantity, result.Totals.ExitedQuantity, result.Totals.OpenQuantity));
        Assert.Contains("exceed the entered quantity", Assert.Single(result.Issues));
        Assert.False(ExecutionIntegrity.Settled(f.Facts));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task Exact_balance_still_rejects_a_small_exit_before_its_entry(string direction)
    {
        var f = new Fixture(direction);
        f.Add(OrderLinkRole.Entry, Huge, 0);
        f.Add(OrderLinkRole.Entry, 0.1m, 3);
        f.Add(OrderLinkRole.Exit, 0.1m, 1);
        f.Add(OrderLinkRole.Exit, Huge, 2);

        var result = await f.LinkAll();

        Assert.Equal(("open", HugeText + ".1", HugeText + ".1", "0"),
            (result.Status, result.Totals.EnteredQuantity, result.Totals.ExitedQuantity, result.Totals.OpenQuantity));
        Assert.Equal("A linked exit filled before the entry it would close.", Assert.Single(result.Issues));
        Assert.False(ExecutionIntegrity.Chronological(f.Fills));
        Assert.False(ExecutionIntegrity.Settled(f.Facts));
    }

    [Theory]
    [InlineData("long", false)]
    [InlineData("short", false)]
    [InlineData("long", true)]
    [InlineData("short", true)]
    public async Task Balanced_quantities_above_decimal_range_close_and_same_time_fills_net_together(string direction, bool sameTime)
    {
        var f = new Fixture(direction);
        f.Add(OrderLinkRole.Entry, Huge * 5, 0);
        f.Add(OrderLinkRole.Entry, Huge * 5, sameTime ? 0 : 1);
        f.Add(OrderLinkRole.Entry, 0.1m, sameTime ? 0 : 2, price: 1);
        f.Add(OrderLinkRole.Exit, Huge * 5, sameTime ? 0 : 3);
        f.Add(OrderLinkRole.Exit, Huge * 5, sameTime ? 0 : 4);
        f.Add(OrderLinkRole.Exit, 0.1m, sameTime ? 0 : 5, price: 1, pnl: 0.1m);

        var result = await f.LinkAll();
        const string total = "100000000000000000000000000000.1";

        Assert.Equal(("closed", total, total, "0"),
            (result.Status, result.Totals.EnteredQuantity, result.Totals.ExitedQuantity, result.Totals.OpenQuantity));
        Assert.Empty(result.Issues);
        Assert.Equal(total, Assert.Single(result.Entries).FilledQuantity);
        Assert.Equal("0.00000001", Assert.Single(result.Entries).AverageFillPrice);
        Assert.Contains(total, Assert.Single(f.Plays.Changes, c => c.To == PlayStatus.Closed).Note);
        Assert.True(ExecutionIntegrity.Settled(f.Facts));
        if (sameTime) Assert.True(ExecutionIntegrity.Chronological(f.Fills.AsEnumerable().Reverse()));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public async Task Per_order_retained_quantities_do_not_round_up_and_hide_missing_executions(string direction)
    {
        var f = new Fixture(direction);
        f.Add(OrderLinkRole.Entry, Huge - 1, 0, orderId: "in");
        f.Add(OrderLinkRole.Entry, 0.9m, 1, orderId: "in");
        f.Add(OrderLinkRole.Exit, Huge - 1, 2, orderId: "out");
        f.Add(OrderLinkRole.Exit, 0.9m, 3, orderId: "out");
        foreach (var order in f.Store.Orders) order.OriginalSize = Huge;

        var result = await f.LinkAll();
        const string retained = "9999999999999999999999999999.9";

        Assert.Equal(("open", retained, retained, "0"),
            (result.Status, result.Totals.EnteredQuantity, result.Totals.ExitedQuantity, result.Totals.OpenQuantity));
        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, issue => Assert.Contains("not all of its executions are retained", issue));
        Assert.All(result.Links, link => Assert.Equal(retained, link.FilledQuantity));
        Assert.All(f.Store.Orders, order =>
            Assert.True(ExecutionIntegrity.MissingExecutions(order, f.Store.Fills.Where(fill => fill.ExternalOrderId == order.ExternalOrderId))));
        Assert.False(ExecutionIntegrity.Settled(f.Facts with { IncompleteOrders = ["in", "out"] }));
    }

    [Theory]
    [InlineData("long", "residual")]
    [InlineData("short", "residual")]
    [InlineData("long", "over-exit")]
    [InlineData("short", "over-exit")]
    [InlineData("long", "early-exit")]
    [InlineData("short", "early-exit")]
    public async Task Legacy_false_closed_plays_with_inexact_quantity_history_are_excluded_from_review_and_sizing(string direction, string history)
    {
        var f = new Fixture(direction);
        f.Add(OrderLinkRole.Entry, Huge, 0);
        if (history != "over-exit") f.Add(OrderLinkRole.Entry, 0.1m, history == "early-exit" ? 3 : 1, price: 1);
        if (history != "residual") f.Add(OrderLinkRole.Exit, 0.1m, 1, price: 1);
        f.Add(OrderLinkRole.Exit, Huge, 2, pnl: 0.1m);
        f.Play.MarkOpen("Legacy entry", At);
        f.Play.MarkClosed("Legacy rounded equality", At.AddMinutes(4));
        // The financial result is supported. Quantity integrity, not an unrelated outcome overflow, must exclude it.
        Assert.NotNull(PlayOutcomeCalculator.Outcome(f.Facts));
        Assert.False(ExecutionIntegrity.Settled(f.Facts));
        var review = new ReviewFixture();
        review.Accounts.Add(f.Account);
        review.Closed.Add(f.Facts);
        var sizing = new MemorySizingStore();
        sizing.Closed.Add(f.Facts);

        var report = await review.Service.GetAsync(new("all"), default);
        var record = await new SizingService(sizing, new CoreOwner(f.Account.OwnerId), new ReviewClock(), TestUnitOfWork.None).GetAsync(default);

        Assert.Equal(0, report.Metrics.Count);
        Assert.Equal(1, report.Coverage.IncompleteExecutions);
        Assert.Empty(report.Plays);
        Assert.Equal(0, record.Record.ClosedPlays);
        Assert.Empty(record.Record.Recent);
    }
}
