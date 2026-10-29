using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed partial class HyperliquidAdapterTests : HyperliquidAdapterTestsSupport
{
    [Fact]
    public async Task Coverage_defaults_are_unknown_for_existing_read_result_constructors()
    {
        var observed = await Read();
        var legacy = new PerpetualVenueReadResult(observed.Snapshot, observed.Instruments, observed.Fills, observed.HistoryNotice);
        Assert.False(legacy.PositionsComplete);
        Assert.False(legacy.FillHistoryComplete);
        Assert.Null(legacy.FillHistoryTruncated);
        Assert.Null(legacy.OldestRawFillAtUtc);
        Assert.False(new VenueOrderReadResult([], observed.Snapshot.ObservedAtUtc, "notice").OpenOrdersComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_validated_primary_positions_include_honest_empty_lists(bool empty)
    {
        var state = Mutate(State, node =>
        {
            var rows = node["assetPositions"]!.AsArray();
            node["assetPositions"] = empty ? new JsonArray() : new JsonArray(rows[0]!.DeepClone(), rows[1]!.DeepClone());
        });
        var result = await Read(state: state);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(empty ? 0 : 1, result.Snapshot.Positions.Count);
        Assert.Equal(12345.678901234567890123m, result.Snapshot.AccountValueUsd);
        Assert.Equal(Fill, Assert.Single(result.Fills).RawJson);
    }

    [Fact]
    public async Task Coverage_omitted_unknown_primary_position_does_not_establish_flatness()
    {
        var result = await Read();
        Assert.False(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(-0.01234567890123456789m, Assert.Single(result.Snapshot.Positions).SignedQuantity);
        Assert.Equal(-0.000123456789m, Assert.Single(result.Fills).Fee);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2000)]
    public async Task Coverage_recent_fills_never_establish_complete_history_even_below_the_cap(int count)
    {
        var fills = "[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            Mutate(Fill, node => node["tid"] = (9007199254740993L + i).ToString()))) + "]";
        var state = Mutate(State, node => node["assetPositions"] = new JsonArray());
        var result = await Read(state: state, fills: fills);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(count, result.Fills.Count);
        Assert.Equal(count == 2000, result.FillHistoryTruncated);
        Assert.Equal(count == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(StateTime - 1000),
            result.OldestRawFillAtUtc);
        Assert.All(result.Fills, fill =>
        {
            Assert.Equal("9007199254740993", fill.OrderId);
            Assert.Equal(1.234567890123456789m, fill.ClosedPnlUsd);
            Assert.Equal("gross", fill.PnlBasis);
        });
    }

    [Theory]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    public async Task Coverage_fill_cap_and_oldest_boundary_use_raw_rows_before_contract_filtering(int count, bool truncated)
    {
        var older = StateTime - 60000;
        var excluded = $$"""{"coin":"@123","time":{{older}}}""";
        var raw = "[" + Fill + "," + string.Join(",", Enumerable.Repeat(excluded, count - 1)) + "]";
        var result = await Read(fills: raw);
        Assert.Equal(truncated, result.FillHistoryTruncated);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(older), result.OldestRawFillAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(StateTime - 1000), Assert.Single(result.Fills).OccurredAtUtc);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"time":"invalid"}""")]
    [InlineData("""{"time":-1}""")]
    [InlineData("""{"time":253402300800000}""")]
    public async Task Coverage_invalid_excluded_raw_timestamps_leave_the_boundary_unknown_without_changing_imports(string gap)
    {
        var excluded = JsonNode.Parse(gap)!;
        excluded["coin"] = "@123";
        var result = await Read(fills: $"[{Fill},{excluded.ToJsonString()}]");
        Assert.False(result.FillHistoryTruncated);
        Assert.Null(result.OldestRawFillAtUtc);
        Assert.Equal(Fill, Assert.Single(result.Fills).RawJson);
    }
    [Theory]
    [InlineData("SOL", 1)]      // A primary-perpetual contract the catalogue does not list: an in-scope fact left out.
    [InlineData("@123", 0)]     // Spot.
    [InlineData("PURR/USDC", 0)] // Spot pair.
    [InlineData("xyz:BTC", 0)]  // HIP-3 builder-deployed perpetual DEX.
    public async Task Coverage_only_unrecognized_primary_fills_count_as_omitted(string coin, int omitted)
    {
        var other = Mutate(Fill, node => { node["coin"] = coin; node["tid"] = "42"; });
        var result = await Read(state: Mutate(State, node => node["assetPositions"] = new JsonArray()), fills: $"[{Fill},{other}]");
        Assert.Equal(Fill, Assert.Single(result.Fills).RawJson);
        Assert.Equal(omitted, result.OmittedExecutions);
        Assert.False(result.FillHistoryTruncated);
    }
}

public sealed partial class HyperliquidOrderTests
{
    private static async Task<VenueOrderReadResult> ReadCoverageOrders(string book, string history = "[]")
    {
        using var handler = new Handler(Meta, history, book);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/") };
        return await new HyperliquidPerpetualReader(client, TimeProvider.System).ReadOrdersAsync(Address, default);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    public async Task Coverage_current_open_book_is_complete_only_below_the_read_cap(int count, bool complete)
    {
        var book = "[" + string.Join(",", Enumerable.Range(1, count).Select(i => Order(i))) + "]";
        var result = await ReadCoverageOrders(book);
        Assert.Equal(complete, result.OpenOrdersComplete);
        Assert.Equal(count, result.Orders.Count);
        Assert.All(result.Orders, order => Assert.Equal(VenueOrderStatus.Open, order.Status));
    }

    [Fact]
    public async Task Coverage_capped_order_history_does_not_make_the_current_open_book_incomplete()
    {
        var history = "[" + string.Join(",", Enumerable.Range(1, 2000).Select(i =>
            $$"""{"order":{{Order(i, sz: "0")}},"status":"filled","statusTimestamp":1791022290435}""")) + "]";
        var result = await ReadCoverageOrders($"[{Order(2001, side: "A", trigger: true, type: "Stop Market")}]", history);
        Assert.True(result.OpenOrdersComplete);
        Assert.Equal(2001, result.Orders.Count);
        Assert.Equal(80000m, Assert.Single(result.Orders, o => o.Status == VenueOrderStatus.Open).TriggerPrice);
    }

    [Theory]
    [InlineData("UNKNOWN", false)]
    [InlineData("@123", true)]
    [InlineData("xyz:BTC", true)]
    public async Task Coverage_known_out_of_scope_orders_are_distinct_from_unrecognized_primary_orders(string coin, bool complete)
    {
        var result = await ReadCoverageOrders($"[{Order(1)},{Order(2, coin: coin)}]");
        Assert.Equal(complete, result.OpenOrdersComplete);
        Assert.Equal("1", Assert.Single(result.Orders).OrderId);
    }

    [Fact]
    public async Task Coverage_duplicate_current_ids_and_unprojected_children_are_incomplete_without_changing_facts()
    {
        var duplicate = await ReadCoverageOrders($"[{Order(1)},{Order(1)}]");
        Assert.False(duplicate.OpenOrdersComplete);
        Assert.Single(duplicate.Orders);
        var parent = JsonNode.Parse(Order(1))!;
        parent["children"] = new JsonArray(JsonNode.Parse(Order(2)));
        var nested = await ReadCoverageOrders("[" + parent.ToJsonString() + "]");
        Assert.False(nested.OpenOrdersComplete);
        Assert.Equal("1", Assert.Single(nested.Orders).OrderId);
    }

    [Fact]
    public async Task Coverage_exceeding_order_cap_and_invalid_current_payload_still_fail_the_read()
    {
        var book = "[" + string.Join(",", Enumerable.Range(1, 2001).Select(i => Order(i))) + "]";
        await Assert.ThrowsAsync<VenueReadException>(() => ReadCoverageOrders(book));
        await Assert.ThrowsAsync<VenueReadException>(() => ReadCoverageOrders("""[{"coin":"BTC","side":"invalid"}]"""));
    }

}
