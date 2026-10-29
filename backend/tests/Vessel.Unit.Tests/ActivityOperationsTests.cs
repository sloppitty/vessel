using Vessel.Application.Activity;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ActivityOperationsTests : ActivityOperationsTestsValues
{

    [Fact]
    public void Before_fee_display_restores_net_fees_without_deducting_them_twice()
    {
        var entry = Fill(0, fee: 0.19m, pnl: -0.19m, basis: "net-of-fee");
        var exit = Fill(1, "sell", "close", fee: 0.50m, pnl: -109.81m, basis: "net-of-fee");
        var op = Assert.Single(OperationAggregator.Aggregate([entry, exit]));
        Assert.Equal("-110", op.ReportedClosedPnlUsd);
        Assert.Equal("-109.31", op.GrossClosedPnlUsd);
        Assert.Equal("0.69", op.StablecoinFeesNominalUsd);
        Assert.Equal("-110", op.NetResultUsd);
        Assert.Equal(-109.81m, exit.ClosedPnlUsd);
    }

    [Fact]
    public void Before_fee_display_restores_negative_fee_rebates_exactly()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([
            Fill(0, basis: "net-of-fee"),
            Fill(1, "sell", "close", pnl: 0.0000000000000000000000000003m,
                fee: -0.0000000000000000000000000001m, basis: "net-of-fee")]));
        Assert.Equal("0.0000000000000000000000000002", op.GrossClosedPnlUsd);
        Assert.Equal("0.0000000000000000000000000003", op.NetResultUsd);
    }

    [Fact]
    public void Multi_order_entry_and_exit_form_one_round_trip()
    {
        List<ImportedFill> fills = [Fill(0), Fill(1, quantity: 2, price: 110), Fill(2, quantity: 2, price: 120)];
        fills.AddRange(Enumerable.Range(3, 5).Select(n => Fill(n, "sell", "close", price: 130, pnl: 4, fee: 0.1m)));
        var op = Assert.Single(OperationAggregator.Aggregate(fills.AsEnumerable().Reverse()));
        Assert.Equal("long", op.Direction); Assert.Equal("closed", op.Status);
        Assert.Equal("5", op.Quantity); Assert.Equal("5", op.TotalEnteredQuantity); Assert.Equal("5", op.TotalExitedQuantity);
        Assert.Equal("112", op.AverageEntryPrice); Assert.Equal("130", op.AverageExitPrice);
        Assert.Equal(3, op.EntryOrderCount); Assert.Equal(5, op.ExitOrderCount); Assert.Equal(8, op.FillCount);
        Assert.Equal("20", op.ReportedClosedPnlUsd); Assert.Equal("19.5", op.NetResultUsd);
        Assert.Equal("20", op.GrossClosedPnlUsd);
        Assert.Equal(Start, op.OpenedAtUtc); Assert.Equal(Start.AddMinutes(7), op.ClosedAtUtc);
    }

    [Fact]
    public void Partial_closes_and_reentry_track_peak_not_total_entered()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, quantity: 3), Fill(1, "sell", "close", quantity: 2),
            Fill(2, quantity: 1), Fill(3, "sell", "close", quantity: 1)]));
        Assert.Equal("open", op.Status); Assert.Equal("3", op.Quantity); Assert.Equal("4", op.TotalEnteredQuantity);
        Assert.Equal("3", op.TotalExitedQuantity); Assert.Equal("1", op.OpenQuantity); Assert.Null(op.ClosedAtUtc);
    }

    [Fact]
    public void Flips_allocate_all_money_to_close_and_preserve_original_fill()
    {
        var opening = Fill(0, quantity: 2, fee: 0.1m);
        var flip = Fill(1, "sell", "flip", quantity: 5, pnl: 10, fee: 0.5m);
        var exit = Fill(2, "buy", "close", quantity: 3, pnl: 6, fee: 0.2m);
        var ops = OperationAggregator.Aggregate([opening, flip, exit]);
        var longOp = ops.Single(o => o.Direction == "long"); var shortOp = ops.Single(o => o.Direction == "short");
        Assert.Equal("closed", longOp.Status); Assert.Equal("closed", shortOp.Status);
        Assert.Equal("2", longOp.Fills[1].Quantity); Assert.Equal("0.5", longOp.Fills[1].Fee);
        Assert.Equal("3", shortOp.Fills[0].Quantity); Assert.Equal("0", shortOp.Fills[0].Fee); Assert.Equal("0", shortOp.Fills[0].ClosedPnlUsd);
        Assert.Equal("5", shortOp.Fills[0].Fill.Quantity); Assert.Equal("0.5", shortOp.Fills[0].Fill.Fee);
        Assert.Equal("9.4", longOp.NetResultUsd); Assert.Equal("5.8", shortOp.NetResultUsd);
        Assert.Equal("5", flip.Quantity.ToString()); Assert.Equal(16m - 0.8m, decimal.Parse(longOp.NetResultUsd!) + decimal.Parse(shortOp.NetResultUsd!));
    }

    [Fact]
    public void Net_of_fee_and_gross_fills_are_not_charged_twice()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, fee: 1), Fill(1, "sell", "close", pnl: 10, fee: 2, basis: "net-of-fee")]));
        Assert.Equal("9", op.NetResultUsd); Assert.Equal("3", op.StablecoinFeesNominalUsd); Assert.Equal("mixed", op.PnlBasis);
        Assert.Equal("12", op.GrossClosedPnlUsd);
        var net = Assert.Single(OperationAggregator.Aggregate([Fill(0, basis: "net-of-fee", token: "ETH", fee: 1),
            Fill(1, "sell", "close", basis: "net-of-fee", token: "ETH", fee: 2, pnl: 10)]));
        Assert.Equal("10", net.NetResultUsd); Assert.Equal("3", Assert.Single(net.OtherFees).Amount);
        Assert.Null(net.GrossClosedPnlUsd);
    }

    [Fact]
    public void Unsupported_gross_fees_leave_net_unknown_without_conversion()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, fee: 1, token: "ETH")]));
        Assert.Null(op.NetResultUsd); Assert.Equal("ETH", Assert.Single(op.OtherFees).Token); Assert.NotEmpty(op.Coverage);
        var stable = Assert.Single(OperationAggregator.Aggregate([Fill(0, fee: 1, token: "USDT")]));
        Assert.Equal("1", stable.StablecoinFeesNominalUsd); Assert.Equal("-1", stable.NetResultUsd); Assert.Empty(stable.OtherFees);
        var lower = Assert.Single(OperationAggregator.Aggregate([Fill(0, fee: 1, token: "usdg")]));
        Assert.Null(lower.NetResultUsd); Assert.Equal("usdg", Assert.Single(lower.OtherFees).Token);
    }

    [Fact]
    public void Listed_stablecoin_fees_are_summed_nominally_across_tokens()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([
            Fill(0, fee: 0.1m, token: "USDC"),
            Fill(1, fee: 0.3m, token: "USDG", basis: "net-of-fee", pnl: -0.3m),
            Fill(2, "sell", "close", pnl: 5, fee: 0.0000000000000000000000000002m, token: "USDG"),
            Fill(3, "sell", "close", pnl: 4.6m, fee: 0.4m, token: "USDH", basis: "net-of-fee")]));
        Assert.Equal("0.8000000000000000000000000002", op.StablecoinFeesNominalUsd);
        Assert.Empty(op.OtherFees);
        Assert.Equal("9.1999999999999999999999999998", op.NetResultUsd);
        Assert.Equal("10", op.GrossClosedPnlUsd);
        Assert.DoesNotContain(op.Coverage, c => c.Contains("unknown"));
    }

    [Theory]
    [InlineData("sell", "close")]
    [InlineData("buy", "flip")]
    [InlineData("buy", "unknown")]
    public void Missing_initial_history_is_incomplete_and_never_guesses_a_starting_position(string side, string effect)
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, side, effect), Fill(1)]));
        Assert.Equal("incomplete", op.Status); Assert.Null(op.Direction); Assert.Null(op.OpenedAtUtc);
        Assert.Null(op.Quantity); Assert.Null(op.OpenQuantity); Assert.Null(op.TotalEnteredQuantity); Assert.Null(op.AverageEntryPrice);
        Assert.Equal(2, op.FillCount); Assert.NotEmpty(op.Coverage);
    }

    [Theory]
    [InlineData("buy", "close", 1)]
    [InlineData("sell", "close", 3)]
    [InlineData("sell", "flip", 1)]
    [InlineData("sell", "open", 1)]
    [InlineData("sell", "unknown", 1)]
    public void Fallback_contradictions_require_a_new_proof_of_flatness(string side, string effect, int quantity)
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, quantity: 2), Fill(1, side, effect, quantity), Fill(2)]));
        Assert.Equal("incomplete", op.Status); Assert.Equal(3, op.FillCount); Assert.Null(op.AverageExitPrice);
    }

    [Fact]
    public void Same_timestamp_peak_disagreement_keeps_boundaries_prices_and_money_but_quantity_unknown()
    {
        var ops = OperationAggregator.Aggregate([Fill(0, quantity: 2), Fill(1), Fill(1, "sell", "close", pnl: 10, fee: 1),
            Fill(2, "sell", "close", quantity: 2, pnl: 20, fee: 2)]);
        var op = Assert.Single(ops);
        Assert.Equal("closed", op.Status); Assert.Equal("long", op.Direction);
        Assert.Equal(Start, op.OpenedAtUtc); Assert.Equal(Start.AddMinutes(2), op.ClosedAtUtc);
        Assert.Null(op.Quantity); Assert.Equal("3", op.TotalEnteredQuantity); Assert.Equal("3", op.TotalExitedQuantity);
        Assert.Equal("100", op.AverageEntryPrice); Assert.Equal("100", op.AverageExitPrice); Assert.Equal("27", op.NetResultUsd);
        Assert.Contains(op.Coverage, c => c.Contains("peak") && c.Contains("timestamp"));
    }

    [Theory]
    [InlineData("sell", "buy", "long")]
    [InlineData("buy", "sell", "short")]
    public void Snapshot_anchor_recovers_after_missing_entries_and_keeps_known_exits(string reducing, string opening, string direction)
    {
        var first = Fill(0, reducing, "close", quantity: 2, price: 120, pnl: 10, fee: 1);
        var second = Fill(1, reducing, "close", quantity: 1, price: 150);
        var entry = Fill(2, opening, "open");
        var ops = OperationAggregator.Aggregate([first, second, entry, Fill(3, reducing, "close")], snapshots: [Snapshot()]);
        Assert.Equal(2, ops.Count);
        var missing = ops.Single(o => o.Status == "incomplete");
        Assert.Equal(direction, missing.Direction); Assert.Null(missing.OpenedAtUtc);
        Assert.Null(missing.Quantity); Assert.Null(missing.TotalEnteredQuantity); Assert.Null(missing.AverageEntryPrice);
        Assert.Equal("3", missing.TotalExitedQuantity); Assert.Equal("130", missing.AverageExitPrice);
        Assert.Equal("0", missing.OpenQuantity); Assert.Equal(Start.AddMinutes(1), missing.ClosedAtUtc);
        Assert.All(missing.Fills, f => Assert.Equal("exit", f.Role));
        Assert.Equal("9", missing.NetResultUsd); Assert.Contains(missing.Coverage, c => c.Contains("entries") && c.Contains("missing"));
        var clean = ops.Single(o => o.Status == "closed");
        Assert.Equal(entry.Id, clean.Fills[0].Fill.Id); Assert.Equal(direction, clean.Direction); Assert.Empty(clean.Coverage);
    }

    [Fact]
    public void Snapshot_anchor_uses_signed_open_position_and_exact_backward_arithmetic()
    {
        var ops = OperationAggregator.Aggregate([Fill(0, "buy", "close", quantity: 2), Fill(1, "sell", "open", quantity: 0.5m)],
            snapshots: [Snapshot(-1.5m)]);
        var missing = Assert.Single(ops);
        Assert.Equal("incomplete", missing.Status); Assert.Equal("short", missing.Direction);
        Assert.Equal("1.5", missing.OpenQuantity); Assert.Equal("2", missing.TotalExitedQuantity);
        Assert.Null(missing.AverageEntryPrice);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("close")]
    public void Anchored_liquidation_does_not_quarantine_following_round_trips(string liquidationEffect)
    {
        var ops = OperationAggregator.Aggregate([Fill(0, quantity: 2), Fill(1, "sell", liquidationEffect, quantity: 2),
            Fill(2, "sell", "open", quantity: 3), Fill(3, "buy", "close", quantity: 3)], snapshots: [Snapshot()]);
        Assert.Equal(2, ops.Count); Assert.All(ops, o => Assert.Equal("closed", o.Status));
        Assert.All(ops, o => Assert.Empty(o.Coverage));
        Assert.Equal("exit", ops.Single(o => o.Direction == "long").Fills[1].Role);
    }

    [Fact]
    public void Anchored_unknown_flip_preserves_quantity_money_and_play_allocations()
    {
        var a = Fill(0, quantity: 2, fee: 0.1m);
        var flip = Fill(1, "sell", "unknown", quantity: 5, pnl: 10, fee: 0.5m);
        var b = Fill(2, "buy", "close", quantity: 3, pnl: 6, fee: 0.2m);
        var play = Guid.NewGuid();
        var ops = OperationAggregator.Aggregate([a, flip, b], new Dictionary<Guid, Guid> { [a.Id] = play, [flip.Id] = play, [b.Id] = play }, [Snapshot()]);
        Assert.All(ops, o => { Assert.Equal("closed", o.Status); Assert.Equal(play, o.PlayId); });
        var closing = ops.Single(o => o.Direction == "long"); var opening = ops.Single(o => o.Direction == "short");
        Assert.Equal("2", closing.Fills[1].Quantity); Assert.Equal("0.5", closing.Fills[1].Fee);
        Assert.Equal("3", opening.Fills[0].Quantity); Assert.Equal("0", opening.Fills[0].Fee);
        Assert.Equal("9.4", closing.NetResultUsd); Assert.Equal("5.8", opening.NetResultUsd);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fallback_resynchronizes_at_an_open_opposite_to_the_reduced_position(bool staleSnapshot)
    {
        var entry = Fill(2, "sell", "open", quantity: 3);
        var ops = OperationAggregator.Aggregate([Fill(0, "sell", "close"), Fill(1, "sell", "close"), entry,
            Fill(3, "buy", "close", quantity: 3)], snapshots: staleSnapshot ? [Snapshot(observedMinute: 0)] : null);
        Assert.Equal(2, ops.Count);
        Assert.Equal(2, ops.Single(o => o.Status == "incomplete").FillCount);
        var clean = ops.Single(o => o.Status == "closed");
        Assert.Equal("short", clean.Direction); Assert.Equal("3", clean.TotalEnteredQuantity); Assert.Equal("3", clean.TotalExitedQuantity);
        Assert.Equal(entry.Id, clean.Fills[0].Fill.Id); Assert.Empty(clean.Coverage);
    }

    [Fact]
    public void Fallback_same_direction_open_and_flip_keep_size_unknown_until_an_opposite_open()
    {
        List<ImportedFill> fills = [Fill(0, "sell", "close"), Fill(1, "buy", "open"), Fill(2, "sell", "flip", quantity: 3)];
        var op = Assert.Single(OperationAggregator.Aggregate(fills));
        Assert.Equal("incomplete", op.Status); Assert.Null(op.OpenQuantity);
        fills.Add(Fill(3, "buy", "open")); fills.Add(Fill(4, "sell", "close"));
        var ops = OperationAggregator.Aggregate(fills);
        Assert.Equal(2, ops.Count); Assert.Equal(3, ops.Single(o => o.Status == "incomplete").FillCount);
        Assert.Equal("long", ops.Single(o => o.Status == "closed").Direction);
    }

    [Fact]
    public void Fallback_unknown_effect_can_recover_after_new_reductions_prove_direction()
    {
        var ops = OperationAggregator.Aggregate([Fill(0), Fill(1, "sell", "unknown"), Fill(2, "sell", "close"),
            Fill(3, "sell", "open"), Fill(4, "buy", "close")]);
        Assert.Equal(2, ops.Count); Assert.Equal(3, ops.Single(o => o.Status == "incomplete").FillCount);
        Assert.Equal("short", ops.Single(o => o.Status == "closed").Direction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_timestamp_close_then_opposite_position_open_is_effect_ordered(bool anchored)
    {
        var closing = Fill(1, "sell", "close", quantity: 2);
        var opening = Fill(1, "sell", "open", quantity: 3);
        // Deliberately put the opening first both in the input and in ID order.
        opening.Id = Guid.Empty; closing.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var ops = OperationAggregator.Aggregate([opening, Fill(0, quantity: 2), closing, Fill(2, "buy", "close", quantity: 3)],
            snapshots: anchored ? [Snapshot()] : null);
        Assert.Equal(2, ops.Count); Assert.All(ops, o => { Assert.Equal("closed", o.Status); Assert.Empty(o.Coverage); });
        Assert.Equal(closing.Id, ops.Single(o => o.Direction == "long").Fills[^1].Fill.Id);
        Assert.Equal(opening.Id, ops.Single(o => o.Direction == "short").Fills[0].Fill.Id);
    }

    [Fact]
    public void Timestamp_orders_with_different_boundaries_are_only_locally_incomplete()
    {
        var later = Fill(3);
        var ops = OperationAggregator.Aggregate([Fill(0), Fill(1, "sell", "close"), Fill(1), Fill(2, "sell", "close"),
            later, Fill(4, "sell", "close")], snapshots: [Snapshot()]);
        var clean = Assert.Single(ops, o => o.Status == "closed");
        Assert.Equal(later.Id, clean.Fills[0].Fill.Id); Assert.Empty(clean.Coverage);
        Assert.All(ops.Where(o => o.Status == "incomplete"), o => Assert.Contains(o.Coverage, c => c.Contains("timestamp")));
    }

    [Fact]
    public void Timestamp_with_no_consistent_effect_order_recovers_after_zero()
    {
        var later = Fill(3);
        var ops = OperationAggregator.Aggregate([Fill(0), Fill(1, "sell", "open"), Fill(1, "buy", "close"),
            Fill(2, "sell", "close"), later, Fill(4, "sell", "close")], snapshots: [Snapshot()]);
        Assert.Equal(later.Id, Assert.Single(ops, o => o.Status == "closed").Fills[0].Fill.Id);
        Assert.All(ops.Where(o => o.Status == "incomplete"), o => Assert.Contains(o.Coverage, c => c.Contains("timestamp")));
    }

    [Fact]
    public void Unknown_timestamp_order_cannot_supply_a_direction_proof_to_the_next_open()
    {
        var ops = OperationAggregator.Aggregate([Fill(0, "sell", "close"), Fill(0, "buy", "close"), Fill(1, "buy", "open")]);
        var op = Assert.Single(ops); Assert.Equal("incomplete", op.Status); Assert.Equal(3, op.FillCount);
    }

    [Fact]
    public void Effect_contradiction_is_coverage_on_its_anchored_operation_only()
    {
        var ops = OperationAggregator.Aggregate([Fill(0), Fill(1, "sell", "open"), Fill(2), Fill(3, "sell", "close")], snapshots: [Snapshot()]);
        Assert.Equal(2, ops.Count); Assert.All(ops, o => Assert.Equal("closed", o.Status));
        var affected = ops.Single(o => o.OpenedAtUtc == Start);
        Assert.Contains(affected.Coverage, c => c.Contains("contradicts")); Assert.Equal("exit", affected.Fills[1].Role);
        Assert.Empty(ops.Single(o => o.OpenedAtUtc == Start.AddMinutes(2)).Coverage);
    }

    [Fact]
    public void Same_role_timestamp_bursts_do_not_need_permutation_search()
    {
        var fills = Enumerable.Range(0, 20).Select(_ => Fill(0)).Append(Fill(1, "sell", "close", quantity: 20));
        var op = Assert.Single(OperationAggregator.Aggregate(fills, snapshots: [Snapshot()]));
        Assert.Equal("closed", op.Status); Assert.Empty(op.Coverage); Assert.Equal("20", op.Quantity);
    }

    [Fact]
    public void Large_timestamp_bursts_close_then_open_without_false_ambiguity()
    {
        var fills = Enumerable.Range(0, 20).Select(_ => Fill(1, "sell", "close"))
            .Concat(Enumerable.Range(0, 20).Select(_ => Fill(1, "sell", "open")))
            .Prepend(Fill(0, quantity: 20)).Append(Fill(2, "buy", "close", quantity: 20));
        var ops = OperationAggregator.Aggregate(fills, snapshots: [Snapshot()]);
        Assert.Equal(2, ops.Count); Assert.All(ops, o => { Assert.Equal("closed", o.Status); Assert.Empty(o.Coverage); });
    }

    [Fact]
    public void Same_side_unknown_effect_timestamp_bursts_are_harmless_with_an_anchor()
    {
        var fills = Enumerable.Range(0, 20).Select(_ => Fill(1, "sell", "unknown"))
            .Prepend(Fill(0, quantity: 20)).Append(Fill(2));
        var ops = OperationAggregator.Aggregate(fills, snapshots: [Snapshot(1)]);
        Assert.Equal(2, ops.Count); Assert.DoesNotContain(ops, o => o.Status == "incomplete");
        Assert.All(ops, o => Assert.Empty(o.Coverage));
    }

    [Fact]
    public void Differing_scales_and_very_large_totals_are_exact()
    {
        var large = Fill(0, quantity: decimal.MaxValue, price: 1, fee: decimal.MaxValue, pnl: decimal.MaxValue);
        var tiny = Fill(1, quantity: 0.0000000000000000000000000001m, price: 1, fee: 0.0000000000000000000000000001m, pnl: 0.0000000000000000000000000001m);
        var op = Assert.Single(OperationAggregator.Aggregate([large, tiny]));
        const string expected = "79228162514264337593543950335.0000000000000000000000000001";
        Assert.Equal(expected, op.Quantity); Assert.Equal(expected, op.ReportedClosedPnlUsd);
        Assert.Equal(expected, op.StablecoinFeesNominalUsd); Assert.Equal("0", op.NetResultUsd); Assert.Equal("1", op.AverageEntryPrice);
    }

    [Fact]
    public void Weighted_averages_round_nonterminating_results_at_28_places()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, price: 1), Fill(1, quantity: 2, price: 2)]));
        Assert.Equal("1.6666666666666666666666666667", op.AverageEntryPrice);
    }

    [Fact]
    public void Only_a_fully_linked_single_play_is_associated()
    {
        var a = Fill(0); var b = Fill(1, "sell", "close"); var play = Guid.NewGuid();
        var op = Assert.Single(OperationAggregator.Aggregate([a, b], new Dictionary<Guid, Guid> { [a.Id] = play, [b.Id] = play }));
        Assert.Equal(play, op.PlayId); Assert.Equal("linked", op.PlayAssociation);
        op = Assert.Single(OperationAggregator.Aggregate([a, b], new Dictionary<Guid, Guid> { [a.Id] = play }));
        Assert.Null(op.PlayId); Assert.Equal("mixed", op.PlayAssociation);
        op = Assert.Single(OperationAggregator.Aggregate([a, b], new Dictionary<Guid, Guid> { [a.Id] = play, [b.Id] = Guid.NewGuid() }));
        Assert.Equal("mixed", op.PlayAssociation); Assert.Null(op.PlayId);
    }

    [Fact]
    public void Distinct_orders_are_counted_once_across_partial_fills()
    {
        var op = Assert.Single(OperationAggregator.Aggregate([Fill(0, order: "entry"), Fill(1, order: "entry"),
            Fill(2, "sell", "close", order: "exit"), Fill(3, "sell", "close", order: "exit")]));
        Assert.Equal(1, op.EntryOrderCount); Assert.Equal(1, op.ExitOrderCount); Assert.Equal(4, op.FillCount);
    }

    [Fact]
    public void Opposing_timestamp_ties_do_not_use_random_ids_to_invent_a_round_trip()
    {
        var entry = Fill(0); var exit = Fill(0, "sell", "close");
        var op = Assert.Single(OperationAggregator.Aggregate([entry, exit]));
        Assert.Equal("incomplete", op.Status); Assert.Null(op.Quantity);
        Assert.Contains(op.Coverage, c => c.Contains("timestamp"));
    }

    [Fact]
    public void Different_contracts_and_accounts_never_merge_and_closed_history_survives_later_ambiguity()
    {
        var other = Fill(0); other.ContractId = "ETH";
        var otherAccount = Fill(0); otherAccount.AccountId = Guid.NewGuid();
        var ops = OperationAggregator.Aggregate([Fill(0), Fill(1, "sell", "close"), Fill(2, effect: "unknown"), other, otherAccount]);
        Assert.Equal(4, ops.Count); Assert.Single(ops, o => o.Status == "closed"); Assert.Single(ops, o => o.Status == "incomplete");
    }
}
