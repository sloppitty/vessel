using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ActivityOperationsTests
{
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static ImportedFill Fill(int n, string side = "buy", string effect = "open", decimal quantity = 1m,
        decimal price = 100m, decimal pnl = 0m, decimal fee = 0m, string basis = "gross", string token = "USDC", string? order = null) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Owner, AccountId = AccountId, ContractId = "BTC", Side = side,
        Direction = "Display text must not control aggregation", PositionEffect = effect, Quantity = quantity, Price = price,
        ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, PnlBasis = basis, FeeBasis = "reported",
        OccurredAtUtc = Start.AddMinutes(n), ExternalOrderId = order ?? n.ToString(), SourceFillId = n.ToString(), TransactionHash = ""
    };

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

    private static AccountSnapshot Snapshot(decimal quantity = 0, int observedMinute = 100) => new()
    {
        AccountId = AccountId, ObservedAtUtc = Start.AddMinutes(observedMinute),
        Positions = quantity == 0 ? [] : [new() { ContractId = "BTC", SignedQuantity = quantity }]
    };

    [Fact]
    public async Task Canonical_only_and_native_entries_share_the_unambiguous_snapshot_anchor()
    {
        var legacy = Fill(0); legacy.ContractId = "DOGE";
        var native = Fill(1); native.ContractId = "DOGE"; native.VenueContractId = "14";
        var snapshot = Snapshot(2); snapshot.Positions[0].ContractId = "DOGE"; snapshot.Positions[0].VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [native, legacy], [snapshot],
            new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations);
        Assert.Equal("long", op.Direction); Assert.Equal("open", op.Status);
        Assert.Equal("2", op.TotalEnteredQuantity); Assert.Equal("0", op.TotalExitedQuantity); Assert.Equal("2", op.OpenQuantity);
        Assert.Equal(new[] { legacy.Id, native.Id }, op.Fills.Select(f => f.Fill.Id));
        Assert.All(op.Fills, f => Assert.Equal("entry", f.Role));
        Assert.Empty(op.Coverage); Assert.Empty(result.Coverage);
        Assert.Null(legacy.VenueContractId);
    }

    [Fact]
    public async Task Canonical_only_open_and_native_close_form_one_closed_operation_with_a_flat_snapshot()
    {
        var legacy = Fill(0); legacy.ContractId = "DOGE";
        var native = Fill(1, "sell", "close"); native.ContractId = "DOGE"; native.VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [native, legacy], [Snapshot()],
            new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations);
        Assert.Equal("long", op.Direction); Assert.Equal("closed", op.Status);
        Assert.Equal("1", op.TotalEnteredQuantity); Assert.Equal("1", op.TotalExitedQuantity); Assert.Equal("0", op.OpenQuantity);
        Assert.Empty(op.Coverage); Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task Canonical_only_fill_resolves_a_native_id_observed_only_in_the_snapshot()
    {
        var snapshot = Snapshot(1); snapshot.Positions[0].VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [Fill(0)], [snapshot],
            new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations);
        Assert.Equal("long", op.Direction); Assert.Equal("open", op.Status); Assert.Equal("1", op.OpenQuantity);
        Assert.Empty(op.Coverage); Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task Legacy_fill_and_snapshot_resolve_to_the_same_unambiguous_native_market()
    {
        var legacy = Fill(0); var native = Fill(1); native.VenueContractId = "14";
        var snapshot = Snapshot(2);
        var result = await Read(new([Account(AccountId)], [native, legacy], [snapshot],
            new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations);
        Assert.Equal("long", op.Direction); Assert.Equal("open", op.Status); Assert.Equal("2", op.OpenQuantity);
        Assert.Empty(op.Coverage); Assert.Empty(result.Coverage);
        Assert.Null(legacy.VenueContractId); Assert.Null(snapshot.Positions[0].VenueContractId);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("flat")]
    [InlineData("missing")]
    public async Task Unresolved_canonical_fills_use_the_forward_walk_and_cover_every_operation(string snapshotKind)
    {
        var retired = Fill(0); retired.VenueContractId = "13";
        var active = Fill(1); active.VenueContractId = "14";
        var closedOpen = Fill(2); var closedExit = Fill(3, "sell", "close"); var open = Fill(4);
        var snapshot = Snapshot(snapshotKind == "native" ? 1 : 0);
        if (snapshot.Positions.Count > 0) snapshot.Positions[0].VenueContractId = "14";
        var result = await Read(new([Account(AccountId)],
            [retired, active, closedOpen, closedExit, open], snapshotKind == "missing" ? [] : [snapshot],
            new Dictionary<Guid, Guid>()));
        Assert.Equal(4, result.TotalOperationCount);
        var unresolved = result.Operations.Where(o => o.Fills.Any(f => f.Fill.Id == closedOpen.Id || f.Fill.Id == open.Id)).ToList();
        Assert.Equal(2, unresolved.Count);
        var closed = unresolved.Single(o => o.Status == "closed");
        Assert.Equal("0", closed.OpenQuantity); Assert.Equal("1", closed.TotalEnteredQuantity); Assert.Equal("1", closed.TotalExitedQuantity);
        var remaining = unresolved.Single(o => o.Status == "open");
        Assert.Equal("1", remaining.OpenQuantity); Assert.Equal("1", remaining.TotalEnteredQuantity);
        Assert.All(unresolved, o =>
        {
            Assert.Equal("long", o.Direction);
            Assert.Contains(o.Coverage, c => c.Contains("native") && c.Contains("ambiguous"));
        });
        Assert.Contains(result.Coverage, c => c.ContractId == "BTC" && c.Notice.Contains("native") && c.Notice.Contains("ambiguous"));
    }

    [Fact]
    public async Task Native_candidates_from_fills_and_snapshots_both_make_legacy_fills_unresolved()
    {
        var legacy = Fill(0); var native = Fill(1); native.VenueContractId = "13";
        var snapshot = Snapshot(2); snapshot.Positions[0].VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [legacy, native], [snapshot],
            new Dictionary<Guid, Guid>()));
        Assert.Equal(2, result.TotalOperationCount);
        var op = result.Operations.Single(o => o.Fills[0].Fill.Id == legacy.Id);
        Assert.Equal("long", op.Direction); Assert.Equal("open", op.Status); Assert.Equal("1", op.OpenQuantity);
        Assert.Contains(op.Coverage, c => c.Contains("native") && c.Contains("ambiguous"));
        Assert.Contains(result.Coverage, c => c.Notice.Contains("native") && c.Notice.Contains("ambiguous"));
    }

    [Fact]
    public async Task Unresolved_canonical_identity_never_collides_with_an_equal_native_identifier()
    {
        var legacy = Fill(0);
        var first = Fill(1); first.VenueContractId = first.ContractId;
        var second = Fill(2); second.VenueContractId = "14";
        var snapshot = Snapshot(1); snapshot.Positions[0].VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [legacy, first, second], [snapshot],
            new Dictionary<Guid, Guid>()));
        Assert.Equal(3, result.TotalOperationCount);
        var op = result.Operations.Single(o => o.Fills[0].Fill.Id == legacy.Id);
        Assert.Equal(1, op.FillCount); Assert.Equal("long", op.Direction); Assert.Equal("1", op.OpenQuantity);
        Assert.Contains(op.Coverage, c => c.Contains("native") && c.Contains("ambiguous"));
    }

    [Fact]
    public async Task Native_identity_resolution_is_scoped_to_the_account_and_canonical_key()
    {
        var legacy = Fill(0); var native = Fill(1); native.VenueContractId = "14";
        var otherAccount = Guid.NewGuid();
        var foreignMarket = Fill(2); foreignMarket.AccountId = otherAccount; foreignMarket.VenueContractId = "13";
        var otherContract = Fill(3); otherContract.ContractId = "ETH"; otherContract.VenueContractId = "15";
        var snapshot = Snapshot(2); snapshot.Positions[0].VenueContractId = "14";
        snapshot.Positions.Add(new() { ContractId = "ETH", VenueContractId = "15", SignedQuantity = 1 });
        var otherSnapshot = new AccountSnapshot { AccountId = otherAccount, ObservedAtUtc = Start.AddMinutes(100),
            Positions = [new() { ContractId = "BTC", VenueContractId = "13", SignedQuantity = 1 }] };
        var result = await Read(new([Account(AccountId), Account(otherAccount)],
            [legacy, native, foreignMarket, otherContract], [snapshot, otherSnapshot], new Dictionary<Guid, Guid>()));
        var op = result.Operations.Single(o => o.Fills.Any(f => f.Fill.Id == legacy.Id));
        Assert.Equal(2, op.FillCount); Assert.Equal("2", op.OpenQuantity);
        Assert.Empty(op.Coverage); Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task Native_markets_sharing_a_canonical_key_keep_round_trips_and_snapshot_anchors_separate()
    {
        var retiredOpen = Fill(0); retiredOpen.VenueContractId = "13";
        var retiredClose = Fill(1, "sell", "close", pnl: 10); retiredClose.VenueContractId = "13";
        var activeClose = Fill(2, "sell", "close", pnl: 20); activeClose.VenueContractId = "14";
        var snapshot = Snapshot(1); snapshot.Positions[0].VenueContractId = "14";
        foreach (var fill in new[] { retiredOpen, retiredClose, activeClose }) fill.ContractId = "DOGE";
        snapshot.Positions[0].ContractId = "DOGE";
        var result = await Read(new([Account(AccountId)],
            [retiredOpen, retiredClose, activeClose], [snapshot], new Dictionary<Guid, Guid>()));
        Assert.Equal(2, result.TotalOperationCount);
        var retired = result.Operations.Single(o => o.Status == "closed");
        Assert.Equal("10", retired.ReportedClosedPnlUsd); Assert.Equal("0", retired.OpenQuantity); Assert.Empty(retired.Coverage);
        var active = result.Operations.Single(o => o.Status == "incomplete");
        Assert.Equal("20", active.ReportedClosedPnlUsd); Assert.Equal("1", active.OpenQuantity);
        Assert.Null(active.OpenedAtUtc); Assert.Null(active.Quantity);
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task Legacy_snapshot_without_native_id_anchors_only_one_unambiguous_market()
    {
        var fill = Fill(0, "sell", "close"); fill.VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [fill], [Snapshot(1)],
            new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations);
        Assert.Equal("long", op.Direction); Assert.Equal("1", op.OpenQuantity);
        Assert.Contains(op.Coverage, c => c.Contains("entries") && c.Contains("missing"));
        Assert.Empty(result.Coverage);
    }

    [Fact]
    public async Task Legacy_snapshot_without_native_id_never_anchors_multiple_markets_to_the_same_position()
    {
        var first = Fill(0); first.VenueContractId = "13";
        var second = Fill(1, "sell", "close"); second.VenueContractId = "14";
        var result = await Read(new([Account(AccountId)], [first, second], [Snapshot(1)],
            new Dictionary<Guid, Guid>()));
        Assert.Equal(2, result.TotalOperationCount);
        Assert.Null(result.Operations.Single(o => o.Fills[0].Fill.Id == second.Id).OpenQuantity);
        Assert.All(result.Operations, o => Assert.Contains(o.Coverage, c => c.Contains("native") && c.Contains("ambiguous")));
        Assert.Contains(result.Coverage, c => c.Notice.Contains("native") && c.Notice.Contains("ambiguous"));
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

    [Fact]
    public async Task Snapshot_index_is_built_once_instead_of_scanning_for_each_contract()
    {
        var fills = Enumerable.Range(0, 100).Select(n => { var fill = Fill(n); fill.ContractId = $"C{n}"; return fill; }).ToList();
        var snapshots = new CountedSnapshots([Snapshot()]);
        await Service(new([Account(AccountId)], fills, snapshots, new Dictionary<Guid, Guid>())).PageAsync(new(), default);
        Assert.InRange(snapshots.Enumerations, 1, 2);
    }

    private sealed class CountedSnapshots(IReadOnlyList<AccountSnapshot> snapshots) : IReadOnlyList<AccountSnapshot>
    {
        public int Enumerations { get; private set; }
        public int Count => snapshots.Count;
        public AccountSnapshot this[int index] => snapshots[index];
        public IEnumerator<AccountSnapshot> GetEnumerator() { Enumerations++; return snapshots.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task Pages_materialize_no_execution_rows_and_expansion_keeps_the_earliest_thousand_without_losing_totals()
    {
        var fills = Enumerable.Range(0, 15000).Select(n => Fill(n, pnl: 1, fee: 0.1m)).ToList();
        var service = Service(new([Account(AccountId)], fills, [], new Dictionary<Guid, Guid>()));
        _ = OperationAggregator.Aggregate(fills); _ = await service.PageAsync(new(), default);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = OperationAggregator.Aggregate(fills);
        var fullBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var result = await service.PageAsync(new(), default);
        var pageBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var op = Assert.Single(result.Operations);
        Assert.Equal(15000, op.FillCount); Assert.Equal("15000", op.ReportedClosedPnlUsd); Assert.Equal("13500", op.NetResultUsd);
        Assert.True(pageBytes < fullBytes * 0.7, $"Page allocation {pageBytes} should be below 70% of full materialization {fullBytes}.");
        var expanded = await service.FillsAsync(AccountId, op.Id, default);
        Assert.Equal(op.Id, expanded.OperationId); Assert.Equal(1000, expanded.Fills.Count); Assert.Equal(14000, expanded.OmittedFillCount);
        Assert.Equal(fills.Take(1000).Select(f => f.Id), expanded.Fills.Select(f => f.Fill.Id));
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
    public async Task Page_rows_name_their_linked_play_by_its_current_title()
    {
        // F-05: the Play column showed only a UUID prefix. Titles are read for the shown linked rows, never for unlinked
        // or mixed ones, and a Play the owner cannot read keeps a null title.
        var named = Guid.NewGuid(); var unreadable = Guid.NewGuid();
        var fills = new[] { Fill(0), Fill(1, "sell", "close"), Fill(2), Fill(3, "sell", "close"), Fill(4), Fill(5, "sell", "close"), Fill(6), Fill(7, "sell", "close") };
        var facts = new ActivityFacts([Account(AccountId)], fills, [], new Dictionary<Guid, Guid>
        {
            [fills[0].Id] = named, [fills[1].Id] = named, [fills[2].Id] = unreadable, [fills[3].Id] = unreadable, [fills[4].Id] = named,
        });
        var store = new Store(facts, new Dictionary<Guid, string> { [named] = "BTC breakout" });
        var page = await new ActivityService(store, Workspace(facts), new Clock(Now)).PageAsync(new(), default);

        var rows = page.Operations.OrderBy(o => o.OpenedAtUtc).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal((named, "BTC breakout"), (rows[0].PlayId!.Value, rows[0].PlayTitle));
        Assert.Equal(unreadable, rows[1].PlayId); Assert.Null(rows[1].PlayTitle);
        Assert.Equal("mixed", rows[2].PlayAssociation); Assert.Null(rows[2].PlayTitle);
        Assert.Equal("unassigned", rows[3].PlayAssociation); Assert.Null(rows[3].PlayTitle);
        Assert.Equal(new[] { named, unreadable }.Order(), store.RequestedTitles.Order());
        // The version identifies operation facts, not titles: renaming a Play does not flag Activity as changed.
        var renamed = await new ActivityService(new Store(facts, new Dictionary<Guid, string> { [named] = "Renamed" }), Workspace(facts),
            new Clock(Now)).PageAsync(new(), default);
        Assert.Equal("Renamed", renamed.Operations.Single(o => o.PlayId == named && o.PlayAssociation == "linked").PlayTitle);
        Assert.Equal(page.Version, renamed.Version);
    }

    [Fact]
    public async Task Play_titles_are_read_only_for_the_rows_of_the_shown_page()
    {
        // 30 round trips, each linked to its own Play: page 2 of 25 shows the oldest five, and only their titles are read.
        var plays = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        var fills = Enumerable.Range(0, 30).SelectMany(n => new[] { Fill(2 * n), Fill(2 * n + 1, "sell", "close") }).ToList();
        var links = fills.Select((f, i) => (f.Id, Play: plays[i / 2])).ToDictionary(x => x.Id, x => x.Play);
        var store = new Store(new ActivityFacts([Account(AccountId)], fills, [], links), plays.ToDictionary(p => p, p => $"Play {plays.IndexOf(p)}"));
        var page = await new ActivityService(store, Workspace(store.Facts), new Clock(Now)).PageAsync(new(Page: "2", PageSize: "25"), default);
        Assert.Equal(5, page.Operations.Count);
        Assert.Equal(plays.Take(5).Order(), store.RequestedTitles.Order());
        Assert.All(page.Operations, o => Assert.Equal($"Play {plays.IndexOf(o.PlayId!.Value)}", o.PlayTitle));
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

    [Fact]
    public async Task Snapshot_comparison_uses_the_remaining_position_not_uuid_order_at_a_timestamp_tie()
    {
        var entry = Fill(0, quantity: 2); entry.Id = Guid.Empty;
        var opened = Fill(1, "sell", "open", quantity: 3); opened.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var snapshot = Snapshot(-3);
        var result = await Read(new([Account(AccountId)], [entry, Fill(1, "sell", "close", quantity: 2), opened],
            [snapshot], new Dictionary<Guid, Guid>()));
        Assert.Equal(2, result.Operations.Count); Assert.Equal("closed", result.Operations[0].Status);
        Assert.Empty(result.Coverage); Assert.All(result.Operations, o => Assert.Empty(o.Coverage));
        Assert.Equal("3", result.Operations.Single(o => o.Status == "open").OpenQuantity);
    }

    [Fact]
    public async Task Snapshot_older_than_fills_is_not_treated_as_a_position_disagreement()
    {
        var snapshot = new AccountSnapshot { AccountId = AccountId, ObservedAtUtc = Start, Positions = [] };
        var result = await Read(new([Account(AccountId)], [Fill(1)], [snapshot], new Dictionary<Guid, Guid>()));
        Assert.Contains(result.Coverage, c => c.Notice.Contains("predates"));
        Assert.DoesNotContain(result.Coverage, c => c.Notice.Contains("disagrees"));
    }

    private static Account Account(Guid id, bool enabled = true, Guid? portfolio = null)
    {
        var account = new Account(id, Owner, "manual", "Fixture");
        account.UpdateSettings(account.Name, portfolio, enabled);
        return account;
    }
    private sealed class Store(ActivityFacts facts, IReadOnlyDictionary<Guid, string>? titles = null) : IActivityStore
    {
        public ActivityFacts Facts => facts;
        public List<Guid> RequestedTitles { get; } = [];
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct)
        {
            RequestedTitles.AddRange(playIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>((titles ?? new Dictionary<Guid, string>())
                .Where(t => playIds.Contains(t.Key)).ToDictionary());
        }
    }
    private static readonly DateTimeOffset Now = Start.AddDays(10);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private static MemoryWorkspaceStore Workspace(ActivityFacts facts, params Portfolio[] portfolios)
    {
        var workspace = new MemoryWorkspaceStore(Owner);
        workspace.Accounts.AddRange(facts.Accounts); workspace.Portfolios.AddRange(portfolios);
        return workspace;
    }
    private static ActivityService Service(ActivityFacts facts, MemoryWorkspaceStore? workspace = null, DateTimeOffset? now = null) =>
        new(new Store(facts), workspace ?? Workspace(facts), new Clock(now ?? Now));

    private sealed record Report(IReadOnlyList<ActivityOperationDto> Operations, int TotalOperationCount, int RetainedFillCount,
        IReadOnlyList<ActivityCoverageDto> Coverage);
    // Every page of the unfiltered scope, each row with its expanded constituent rows: the full aggregate report.
    private static async Task<Report> Read(ActivityFacts facts)
    {
        var service = Service(facts);
        var operations = new List<ActivityOperationDto>();
        ActivityPageDto page;
        var number = 1;
        do
        {
            page = await service.PageAsync(new(Page: number.ToString(), PageSize: "100"), default);
            foreach (var o in page.Operations)
            {
                var rows = await service.FillsAsync(o.AccountId, o.Id, default);
                operations.Add(new(o.Id, o.AccountId, o.ContractId, o.Direction, o.Status, o.OpenedAtUtc, o.ClosedAtUtc, o.LastOccurredAtUtc,
                    o.Quantity, o.TotalEnteredQuantity, o.TotalExitedQuantity, o.OpenQuantity, o.AverageEntryPrice, o.AverageExitPrice,
                    o.EntryOrderCount, o.ExitOrderCount, o.FillCount, o.ReportedClosedPnlUsd, o.StablecoinFeesNominalUsd, o.OtherFees,
                    o.NetResultUsd, o.PnlBasis, o.PlayId, o.PlayAssociation, o.Coverage, rows.Fills, rows.OmittedFillCount)
                { GrossClosedPnlUsd = o.GrossClosedPnlUsd });
            }
        } while (number++ * 100 < page.TotalCount);
        return new(operations, page.TotalCount, page.RetainedFillCount, page.Coverage);
    }

    [Fact]
    public async Task Restricting_facts_to_accounts_preserves_operations_and_coverage_for_those_accounts()
    {
        var second = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        var opening = Fill(0, quantity: 2); opening.VenueContractId = "14";
        var flip = Fill(1, "sell", "flip", quantity: 3, pnl: 10, fee: 0.1m); flip.VenueContractId = "14";
        var legacy = Fill(2, "buy", "close"); // Resolves only against this account's native ID.
        var missingOpen = Fill(0, "sell", "close", pnl: 5); missingOpen.AccountId = second;
        var outside = Fill(0); outside.AccountId = unrelated; outside.VenueContractId = "15";
        var snapshot = new AccountSnapshot { OwnerId = Owner, AccountId = AccountId, ObservedAtUtc = Start.AddDays(1), ValueScope = "fixture" };
        var otherSnapshot = new AccountSnapshot { OwnerId = Owner, AccountId = unrelated, ObservedAtUtc = Start.AddDays(1), ValueScope = "fixture" };
        var play = Guid.NewGuid();
        var links = new Dictionary<Guid, Guid> { [opening.Id] = play, [flip.Id] = play, [legacy.Id] = play };
        var all = new ActivityFacts([Account(AccountId), Account(second), Account(unrelated)],
            [opening, flip, legacy, missingOpen, outside], [snapshot, otherSnapshot], links);
        var ids = new HashSet<Guid> { AccountId, second };
        var restricted = all with
        {
            Accounts = all.Accounts.Where(a => ids.Contains(a.Id)).ToList(),
            Fills = all.Fills.Where(f => ids.Contains(f.AccountId)).ToList(),
            Snapshots = all.Snapshots.Where(s => ids.Contains(s.AccountId)).ToList()
        };
        var full = await Read(all);
        var scoped = await Read(restricted);
        Assert.Equal(JsonSerializer.Serialize(full.Operations.Where(o => ids.Contains(o.AccountId))), JsonSerializer.Serialize(scoped.Operations));
        Assert.Equal(JsonSerializer.Serialize(full.Coverage.Where(c => ids.Contains(c.AccountId))), JsonSerializer.Serialize(scoped.Coverage));
        Assert.Equal(4, scoped.RetainedFillCount);
    }

    [Fact]
    public async Task Service_uses_more_than_100_retained_fills_and_excludes_disabled_accounts()
    {
        var disabled = Guid.NewGuid(); var foreign = Guid.NewGuid();
        var fills = Enumerable.Range(0, 120).Select(n => Fill(n, n % 2 == 0 ? "buy" : "sell", n % 2 == 0 ? "open" : "close")).ToList();
        var hidden = Fill(130); hidden.AccountId = disabled; fills.Add(hidden);
        var outside = Fill(131); outside.AccountId = foreign; fills.Add(outside);
        var result = await Read(new([Account(AccountId), Account(disabled, false)], fills, [], new Dictionary<Guid, Guid>()));
        Assert.Equal(120, result.RetainedFillCount); Assert.Equal(60, result.TotalOperationCount);
        Assert.All(result.Operations, o => Assert.Equal(AccountId, o.AccountId));
    }

    [Fact]
    public async Task Snapshot_anchor_keeps_missing_entries_unknown_and_reports_snapshot_only_positions()
    {
        var snapshot = new AccountSnapshot { AccountId = AccountId, ObservedAtUtc = Start.AddDays(1), Positions =
            [new() { ContractId = "BTC", SignedQuantity = 3 }, new() { ContractId = "ETH", SignedQuantity = -2 }] };
        var result = await Read(new([Account(AccountId)], [Fill(0)], [snapshot], new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations); Assert.Equal("incomplete", op.Status); Assert.Equal("long", op.Direction); Assert.Equal("3", op.OpenQuantity);
        Assert.Null(op.AverageEntryPrice); Assert.Contains(op.Coverage, s => s.Contains("entries") && s.Contains("missing"));
        var disagreement = Assert.Single(result.Coverage); Assert.Equal("ETH", disagreement.ContractId);
        Assert.Contains("disagrees", disagreement.Notice);
        Assert.Equal(3, snapshot.Positions[0].SignedQuantity);
    }

    [Fact]
    public async Task Pages_have_no_operation_cap_and_never_change_aggregate_totals()
    {
        var fills = Enumerable.Range(0, 250).Select(n => Fill(n)).ToList();
        var result = await Read(new([Account(AccountId)], fills, [], new Dictionary<Guid, Guid>()));
        var op = Assert.Single(result.Operations); Assert.Equal("250", op.Quantity); Assert.Equal(250, op.FillCount);
        Assert.Equal(250, op.Fills.Count); Assert.Equal(0, op.OmittedFillCount);
        fills = Enumerable.Range(0, 1100).Select(n => Fill(n, n % 2 == 0 ? "buy" : "sell", n % 2 == 0 ? "open" : "close")).ToList();
        var page = await Service(new([Account(AccountId)], fills, [], new Dictionary<Guid, Guid>())).PageAsync(new(Page: "6", PageSize: "100"), default);
        Assert.Equal((550, 550, 1100, 6, 100), (page.TotalCount, page.ScopeCount, page.RetainedFillCount, page.Page, page.PageSize));
        Assert.Equal(fills[1].OccurredAtUtc, page.Operations[^1].LastOccurredAtUtc);
        Assert.Equal(50, page.Operations.Count);
    }

    // Fixture: the first account is in Swing, the second is Unassigned and a disabled Swing account is hidden.
    // First: closed BTC long (minute 1), open ETH short (2), partially closed SOL long (4).
    // Second: incomplete BTC (5) and an ETH long opened 40 days before the clock.
    private static readonly DateTimeOffset FixtureNow = Start.AddDays(1);
    private sealed record Fixture(ActivityService Service, Guid Second, Guid Disabled, Portfolio Swing, List<ImportedFill> Fills);
    private static Fixture Filters(Action<List<ImportedFill>>? change = null, Dictionary<Guid, Guid>? links = null)
    {
        var swing = new Portfolio(Guid.NewGuid(), Owner, "Swing", "wallet");
        Guid second = Guid.Parse("00000000-0000-0000-0000-000000000002"), disabled = Guid.NewGuid();
        ImportedFill On(ImportedFill fill, Guid account, string contract) { fill.AccountId = account; fill.ContractId = contract; return fill; }
        var old = On(Fill(0), second, "ETH"); old.OccurredAtUtc = FixtureNow.AddDays(-40);
        List<ImportedFill> fills =
        [
            On(Fill(0), AccountId, "BTC"), On(Fill(1, "sell", "close"), AccountId, "BTC"),
            On(Fill(2, "sell", "open", quantity: 2), AccountId, "ETH"),
            On(Fill(3, quantity: 2), AccountId, "SOL"), On(Fill(4, "sell", "close"), AccountId, "SOL"),
            On(Fill(5, "sell", "close"), second, "BTC"), old, On(Fill(6), disabled, "BTC")
        ];
        change?.Invoke(fills);
        var facts = new ActivityFacts([Account(AccountId, portfolio: swing.Id), Account(second), Account(disabled, false, swing.Id)],
            fills, [], links ?? new Dictionary<Guid, Guid>());
        return new(Service(facts, Workspace(facts, swing), FixtureNow), second, disabled, swing, fills);
    }

    [Fact]
    public async Task Each_filter_ports_the_client_semantics_after_aggregation()
    {
        var (service, second, _, swing, _) = Filters();
        async Task<string[]> Ids(ActivityQuery query)
        {
            var page = await service.PageAsync(query, default);
            Assert.Equal(5, page.ScopeCount); Assert.Equal(page.Operations.Count, page.TotalCount);
            return page.Operations.Select(o => $"{(o.AccountId == AccountId ? "first" : "second")}:{o.ContractId}").ToArray();
        }
        Assert.Equal(["second:BTC", "first:SOL", "first:ETH", "first:BTC", "second:ETH"], await Ids(new()));
        Assert.Equal(["first:SOL", "first:ETH", "first:BTC"], await Ids(new(Account: AccountId.ToString())));
        Assert.Equal(["second:BTC", "second:ETH"], await Ids(new(Account: second.ToString())));
        Assert.Equal(["first:SOL", "first:ETH", "first:BTC"], await Ids(new(Portfolio: swing.Id.ToString())));
        Assert.Equal(["second:BTC", "second:ETH"], await Ids(new(Portfolio: "unassigned")));
        Assert.Empty(await Ids(new(Portfolio: Guid.NewGuid().ToString())));
        Assert.Equal(["second:BTC", "first:BTC"], await Ids(new(Contract: "BTC")));
        Assert.Empty(await Ids(new(Contract: "btc")));
        Assert.Equal(["first:SOL", "first:BTC", "second:ETH"], await Ids(new(Direction: "long")));
        Assert.Equal(["first:ETH"], await Ids(new(Direction: "short")));
        Assert.Equal(["first:SOL", "first:ETH", "second:ETH"], await Ids(new(Status: "open")));
        Assert.Equal(["first:SOL"], await Ids(new(Status: "partial")));
        Assert.Equal(["first:BTC"], await Ids(new(Status: "closed")));
        Assert.Equal(["second:BTC"], await Ids(new(Status: "incomplete")));
        Assert.Equal(["first:SOL", "first:ETH"], await Ids(new(Account: AccountId.ToString(), Status: "open")));
        Assert.Equal(4, (await Ids(new(Period: "7"))).Length);
        Assert.Equal(4, (await Ids(new(Period: "30"))).Length);
        Assert.Equal(5, (await Ids(new(Period: "90"))).Length);
        Assert.Equal(5, (await Ids(new(Period: "all"))).Length);
        Assert.Equal(4, (await Ids(new(Period: "custom", From: "2026-10-12", To: "2026-10-12"))).Length);
        Assert.Equal(4, (await Ids(new(Period: "custom", From: "2026-10-12"))).Length);
        Assert.Equal(["second:ETH"], await Ids(new(Period: "custom", To: "2026-09-03")));
        Assert.Empty(await Ids(new(Period: "custom", To: "2026-09-02")));
        Assert.Equal(5, (await Ids(new(Period: "custom"))).Length);
        // Empty values mean no filter, as the browser's reset does.
        Assert.Equal(5, (await Ids(new(Account: "", Portfolio: "", Contract: "", Direction: "", Status: "", From: "", To: ""))).Length);
    }

    [Fact]
    public async Task Rolling_periods_count_back_from_the_reported_clock()
    {
        var (service, _, _, _, _) = Filters();
        var page = await service.PageAsync(new(Period: "30"), default);
        Assert.Equal(FixtureNow, page.AsOfUtc);
        var edge = Filters(fills => fills.Single(f => f.OccurredAtUtc < Start).OccurredAtUtc = FixtureNow.AddDays(-30));
        Assert.Equal(5, (await edge.Service.PageAsync(new(Period: "30"), default)).TotalCount);
        edge = Filters(fills => fills.Single(f => f.OccurredAtUtc < Start).OccurredAtUtc = FixtureNow.AddDays(-30).AddTicks(-1));
        Assert.Equal(4, (await edge.Service.PageAsync(new(Period: "30"), default)).TotalCount);
    }

    [Fact]
    public async Task Pages_clamp_past_the_end_and_report_the_page_returned()
    {
        var (service, _, _, _, _) = Filters();
        var last = await service.PageAsync(new(Page: "99", PageSize: "2"), default);
        Assert.Equal((3, 2, 5), (last.Page, last.PageSize, last.TotalCount));
        Assert.Equal("ETH", Assert.Single(last.Operations).ContractId);
        var middle = await service.PageAsync(new(Page: "2", PageSize: "2"), default);
        Assert.Equal(["ETH", "BTC"], middle.Operations.Select(o => o.ContractId));
        var empty = await service.PageAsync(new(Contract: "XRP", Page: "4"), default);
        Assert.Equal((1, 25, 0, 5), (empty.Page, empty.PageSize, empty.TotalCount, empty.ScopeCount));
        Assert.Empty(empty.Operations);
        Assert.Equal(25, (await service.PageAsync(new(), default)).PageSize);
    }

    [Fact]
    public async Task Ties_order_by_account_then_operation_id()
    {
        Guid low = Guid.Parse("10000000-0000-0000-0000-000000000000"), high = Guid.Parse("f0000000-0000-0000-0000-000000000000");
        var a = Fill(0); a.AccountId = high; a.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Fill(0); b.AccountId = low; b.Id = Guid.Parse("00000000-0000-0000-0000-000000000009");
        var c = Fill(0); c.AccountId = low; c.ContractId = "ETH"; c.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var page = await Service(new([Account(high), Account(low)], [a, b, c], [], new Dictionary<Guid, Guid>())).PageAsync(new(), default);
        Assert.Equal([c.Id, b.Id, a.Id], page.Operations.Select(o => Guid.Parse(o.Id.Split(':')[0])));
    }

    [Fact]
    public async Task Version_identifies_the_scoped_set_independent_of_filters_and_pages()
    {
        var fixture = Filters();
        var service = fixture.Service;
        var version = (await service.PageAsync(new(), default)).Version;
        Assert.Matches("^[0-9a-f]{32}$", version);
        Assert.Equal(version, (await service.PageAsync(new(Contract: "BTC", Page: "2", PageSize: "1"), default)).Version);
        Assert.Equal(version, (await service.PageAsync(new(), default)).Version);
        Assert.NotEqual(version, (await service.PageAsync(new(Scope: "unassigned"), default)).Version);
        // Facts outside the scope do not matter; a new fill on any scoped operation, even outside the filter, does.
        var outside = Fill(7); outside.AccountId = fixture.Disabled; fixture.Fills.Add(outside);
        Assert.Equal(version, (await service.PageAsync(new(Contract: "ETH"), default)).Version);
        var exit = Fill(7, "sell", "close"); exit.ContractId = "SOL"; fixture.Fills.Add(exit);
        var changed = await service.PageAsync(new(Contract: "ETH"), default);
        Assert.NotEqual(version, changed.Version); Assert.Equal(2, changed.TotalCount);
        var play = Guid.NewGuid();
        var fill = Fill(0);
        var unlinked = await Service(new([Account(AccountId)], [fill], [], new Dictionary<Guid, Guid>())).PageAsync(new(), default);
        var associated = await Service(new([Account(AccountId)], [fill], [], new Dictionary<Guid, Guid> { [fill.Id] = play })).PageAsync(new(), default);
        Assert.NotEqual(unlinked.Version, associated.Version);
        Assert.Equal(play, Assert.Single(associated.Operations).PlayId);
    }

    [Fact]
    public async Task Scope_limits_operations_facets_coverage_and_retained_fills()
    {
        var (service, second, disabled, swing, _) = Filters();
        var all = await service.PageAsync(new(), default);
        Assert.Equal(new[] { AccountId, second }.OrderBy(id => id.ToString()), all.Facets.AccountIds);
        Assert.Equal(["BTC", "ETH", "SOL"], all.Facets.Contracts);
        Assert.Equal(new[] { AccountId, second }.ToHashSet(), all.Coverage.Select(c => c.AccountId).ToHashSet());
        Assert.Equal(7, all.RetainedFillCount);
        var unassigned = await service.PageAsync(new(Scope: "unassigned", Account: AccountId.ToString()), default);
        Assert.Equal((0, 2, 2), (unassigned.TotalCount, unassigned.ScopeCount, unassigned.RetainedFillCount));
        Assert.Equal([second], unassigned.Facets.AccountIds); Assert.Equal(["BTC", "ETH"], unassigned.Facets.Contracts);
        Assert.All(unassigned.Coverage, c => Assert.Equal(second, c.AccountId));
        var portfolio = await service.PageAsync(new(Scope: $"portfolio:{swing.Id}"), default);
        Assert.Equal((3, 3), (portfolio.TotalCount, portfolio.ScopeCount));
        Assert.All(portfolio.Coverage, c => Assert.Equal(AccountId, c.AccountId));
        var account = await service.PageAsync(new(Scope: $"account:{second}"), default);
        Assert.Equal([second], account.Facets.AccountIds);
        Assert.Equal(2, (await service.PageAsync(new(AccountId: second), default)).ScopeCount);
        Assert.Equal(0, (await service.PageAsync(new(AccountId: second, Scope: $"portfolio:{swing.Id}"), default)).ScopeCount);
        foreach (var hidden in new ActivityQuery[] { new(Scope: $"account:{disabled}"), new(AccountId: disabled) })
        {
            var page = await service.PageAsync(hidden, default);
            Assert.Equal((0, 0, 0), (page.ScopeCount, page.RetainedFillCount, page.Coverage.Count));
            Assert.Empty(page.Facets.AccountIds);
        }
        foreach (var missing in new ActivityQuery[] { new(Scope: $"portfolio:{Guid.NewGuid()}"), new(Scope: $"account:{Guid.NewGuid()}"), new(AccountId: Guid.NewGuid()) })
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.PageAsync(missing, default))).StatusCode);
    }

    [Theory]
    [InlineData("page", "0")] [InlineData("page", "-1")] [InlineData("page", "1.5")] [InlineData("page", "x")] [InlineData("page", "")]
    [InlineData("page", "99999999999")] [InlineData("pageSize", "0")] [InlineData("pageSize", "101")] [InlineData("pageSize", " 25")]
    [InlineData("scope", "everything")] [InlineData("scope", "portfolio:nope")] [InlineData("scope", "account:00000000-0000-0000-0000-000000000000")]
    [InlineData("account", "nope")] [InlineData("account", "00000000-0000-0000-0000-000000000000")] [InlineData("portfolio", "nope")]
    [InlineData("direction", "up")] [InlineData("status", "pending")] [InlineData("status", "Open")] [InlineData("period", "14")]
    [InlineData("period", "ALL")] [InlineData("from", "2026-10-12")] [InlineData("to", "2026-10-12")] [InlineData("customFrom", "2026-13-01")]
    [InlineData("customFrom", "20261012")] [InlineData("customTo", "2026-10-12T00:00:00Z")] [InlineData("contract", "long")]
    public async Task Invalid_activity_queries_are_400(string key, string value)
    {
        var query = key switch
        {
            "page" => new ActivityQuery(Page: value),
            "pageSize" => new ActivityQuery(PageSize: value),
            "scope" => new ActivityQuery(Scope: value),
            "account" => new ActivityQuery(Account: value),
            "portfolio" => new ActivityQuery(Portfolio: value),
            "direction" => new ActivityQuery(Direction: value),
            "status" => new ActivityQuery(Status: value),
            "period" => new ActivityQuery(Period: value),
            "from" => new ActivityQuery(From: value),
            "to" => new ActivityQuery(Period: "all", To: value),
            "customFrom" => new ActivityQuery(Period: "custom", From: value),
            "customTo" => new ActivityQuery(Period: "custom", To: value),
            _ => new ActivityQuery(Contract: new string('C', 129))
        };
        var service = Filters().Service;
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.PageAsync(query, default))).StatusCode);
    }

    [Fact]
    public async Task Fill_expansion_is_chronological_and_changed_history_is_404()
    {
        var (service, second, disabled, _, _) = Filters();
        var page = await service.PageAsync(new(Contract: "SOL"), default);
        var op = Assert.Single(page.Operations);
        var rows = await service.FillsAsync(AccountId, op.Id, default);
        Assert.Equal((op.Id, 0), (rows.OperationId, rows.OmittedFillCount));
        Assert.Equal(["entry", "exit"], rows.Fills.Select(f => f.Role));
        Assert.True(rows.Fills[0].Fill.OccurredAtUtc < rows.Fills[1].Fill.OccurredAtUtc);
        foreach (var (account, id) in new[] { (second, op.Id), (AccountId, "missing:long"), (disabled, op.Id) })
            Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.FillsAsync(account, id, default))).StatusCode);
        foreach (var (account, id) in new (Guid?, string?)[] { (null, op.Id), (Guid.Empty, op.Id), (AccountId, null), (AccountId, " "), (AccountId, new string('x', 201)) })
            Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.FillsAsync(account, id, default))).StatusCode);
    }

    [Fact]
    public async Task Activity_endpoint_requires_authentication_before_reading_facts()
    {
        await using var factory = new CoreApiFactory(Owner); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/activity/fills?accountId={AccountId}&operationId=x")).StatusCode);
    }

    [Fact]
    public async Task Activity_endpoints_have_exact_page_and_fill_payloads_with_no_store()
    {
        var facts = new ActivityFacts([Account(AccountId)], [Fill(0), Fill(1, "sell", "close", pnl: 0.0000000000000000000000000001m)], [], new Dictionary<Guid, Guid>());
        await using var factory = new CoreApiFactory(Owner, store: Workspace(facts), configure: services => services.AddSingleton<IActivityStore>(new Store(facts)));
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync($"/api/activity?accountId={AccountId}&scope=all&status=closed&period=custom&from=2026-10-12&to=2026-10-12&page=1&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["operations", "page", "pageSize", "totalCount", "scopeCount", "retainedFillCount", "asOfUtc", "version", "facets", "coverage", "scopeNote",
                "unassignedFundingPayments", "omittedUnassignedFundingPaymentCount"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["accountIds", "contracts"], json.GetProperty("facets").EnumerateObject().Select(p => p.Name));
        var row = Assert.Single(json.GetProperty("operations").EnumerateArray());
        // Paper provenance is optional and paper-only: the default real wire remains byte-contract compatible.
        Assert.False(json.TryGetProperty("tradingMode", out _)); Assert.False(json.TryGetProperty("paperExecutionCount", out _));
        Assert.False(row.TryGetProperty("tradingMode", out _)); Assert.False(row.TryGetProperty("paperExecutionCount", out _));
        Assert.False(row.TryGetProperty("fills", out _)); Assert.False(row.TryGetProperty("omittedFillCount", out _));
        Assert.Equal("0.0000000000000000000000000001", row.GetProperty("netResultUsd").GetString());
        Assert.Equal("0.0000000000000000000000000001", row.GetProperty("grossClosedPnlUsd").GetString());
        Assert.DoesNotContain("500 operations", json.GetProperty("scopeNote").GetString());
        var fills = await client.GetAsync($"/api/activity/fills?accountId={AccountId}&operationId={row.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, fills.StatusCode); Assert.True(fills.Headers.CacheControl?.NoStore);
        var expanded = await fills.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["operationId", "fills", "omittedFillCount", "fundingPayments", "omittedFundingPaymentCount"], expanded.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, expanded.GetProperty("fills").GetArrayLength());
        Assert.False(expanded.TryGetProperty("paperExecutions", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity/fills?accountId={AccountId}&operationId=gone")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity/fills?operationId=gone")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/activity?accountId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity?accountId=not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/activity?pageSize=500")).StatusCode);
    }
}
