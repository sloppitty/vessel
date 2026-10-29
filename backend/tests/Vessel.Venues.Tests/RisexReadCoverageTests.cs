using System.Text.Json.Nodes;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed partial class RisexAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_current_positions_cover_the_supported_scope_not_lifetime_history(bool empty)
    {
        var portfolio = JsonNode.Parse(Portfolio)!;
        if (empty) portfolio["data"]!["positions"] = new JsonArray();
        var (reader, routes) = Reader(new() { ["/v1/portfolio/details"] = portfolio.ToJsonString() });
        var result = await reader.ReadAsync(Address, default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.False(result.FillHistoryTruncated);
        Assert.Equal(Now.AddDays(-200), result.OldestRawFillAtUtc);
        Assert.Equal(empty ? 0 : 2, result.Snapshot.Positions.Count);
        var fill = Assert.Single(result.Fills, f => f.SourceFillId == "0xclose");
        Assert.Equal(("0xorder2", 0.086m, 12.5m, ExecutionFacts.PnlNetOfFee),
            (fill.OrderId, fill.Fee, fill.ClosedPnlUsd, fill.PnlBasis));
        Assert.Equal("1", fill.VenueContractId);
        Assert.DoesNotContain(routes.Requests, request => request.Contains("/positions?", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("empty-size")]
    [InlineData("unknown-market")]
    public async Task Coverage_skipped_position_rows_are_not_evidence_of_flatness(string gap)
    {
        var portfolio = JsonNode.Parse(Portfolio)!;
        var row = portfolio["data"]!["positions"]![1]!.DeepClone();
        if (gap == "empty-size") row["size"] = "";
        else row["market_id"] = "999";
        portfolio["data"]!["positions"]!.AsArray().Add(row);
        var result = await Reader(new() { ["/v1/portfolio/details"] = portfolio.ToJsonString() }).Reader.ReadAsync(Address, default);
        Assert.False(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(2, result.Snapshot.Positions.Count);
        Assert.Equal(0.702475m, Assert.Single(result.Snapshot.Positions, p => p.ContractId == "BTC").SignedQuantity);
    }

    [Fact]
    public async Task Coverage_capped_fill_pages_preserve_facts_and_never_establish_a_gap_free_window()
    {
        var trades = JsonNode.Parse(Trades)!;
        trades["data"]!["has_next_page"] = true;
        var (reader, routes) = Reader(new() { ["/v1/trade-history"] = trades.ToJsonString() });
        var result = await reader.ReadAsync(Address, default);
        Assert.True(result.PositionsComplete);
        Assert.False(result.FillHistoryComplete);
        Assert.True(result.FillHistoryTruncated);
        Assert.Equal(4, result.Fills.Count);
        Assert.Equal(2, routes.Requests.Count(request => request.Contains("/trade-history?", StringComparison.Ordinal)));
        Assert.All(result.Fills, fill => Assert.Equal(ExecutionFacts.PnlNetOfFee, fill.PnlBasis));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_raw_fill_pagination_boundary_precedes_market_exclusions(bool morePages)
    {
        var older = Now.AddDays(-300);
        var accepted = Trade("0xaccepted", "1", "BUY", "BUY", "0.086", "-0.086", Now.AddMinutes(-1));
        var excluded = Trade("0xexcluded", "999", "BUY", "BUY", "0.086", "-0.086", older);
        var body = "{\"data\":{\"trades\":[" + accepted + "," + string.Join(",", Enumerable.Repeat(excluded, 999)) +
            "],\"has_next_page\":" + (morePages ? "true" : "false") + "}}";
        var (reader, routes) = Reader(new() { ["/v1/trade-history"] = body });
        var result = await reader.ReadAsync(Address, default);
        Assert.Equal(morePages, result.FillHistoryTruncated);
        Assert.False(result.FillHistoryComplete);
        Assert.Equal(older, result.OldestRawFillAtUtc);
        Assert.Equal("0xaccepted", Assert.Single(result.Fills).SourceFillId);
        Assert.Equal(morePages ? 2 : 1, routes.Requests.Count(request => request.Contains("/trade-history?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Coverage_an_excluded_raw_timestamp_gap_does_not_turn_filtered_oldest_into_a_boundary()
    {
        var root = JsonNode.Parse(Trades)!;
        root["data"]!["trades"]![4]!.AsObject().Remove("time"); // Already excluded unknown market.
        var result = await Reader(new() { ["/v1/trade-history"] = root.ToJsonString() }).Reader.ReadAsync(Address, default);
        Assert.False(result.FillHistoryTruncated);
        Assert.Null(result.OldestRawFillAtUtc);
        Assert.Equal(4, result.Fills.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_bounded_order_history_is_not_a_complete_current_open_order_list(bool empty)
    {
        Dictionary<string, string>? extra = empty ? new()
        {
            ["/v1/orders"] = """{"data":{"orders":[],"has_next_page":false}}""",
            ["/v1/orders/tpsl"] = """{"data":{"orders":[],"total":"0","page":1,"limit":1000}}"""
        } : null;
        var (reader, routes) = Reader(extra);
        var result = await reader.ReadOrdersAsync(Address, default);
        Assert.False(result.OpenOrdersComplete);
        Assert.Equal(empty ? 0 : 3, result.Orders.Count);
        Assert.DoesNotContain(routes.Requests, request => request.Contains("/orders/open?", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("999", 1)] // Not in the catalogue at all: an in-scope fact Vessel cannot verify.
    [InlineData("77", 1)]  // A USDC market whose name the map cannot classify: left out, so omitted.
    [InlineData("88", 0)]  // Quoted in another asset: proven out of scope.
    public async Task Coverage_only_unrecognized_markets_count_as_omitted_executions(string market, int omitted)
    {
        var markets = JsonNode.Parse(Markets)!;
        var list = markets["data"]!["markets"]!.AsArray();
        var unclassified = JsonNode.Parse(Market("77", "Mystery Index"))!;
        var otherQuote = JsonNode.Parse(Market("88", "BTC/USDT"))!;
        otherQuote["quote_asset_symbol"] = "USDT";
        list.Add(unclassified);
        list.Add(otherQuote);
        var accepted = Trade("0xaccepted", "1", "BUY", "BUY", "0.086", "-0.086", Now.AddMinutes(-1));
        var dropped = Trade("0xdropped", market, "BUY", "BUY", "0.086", "-0.086", Now.AddMinutes(-2));
        var body = "{\"data\":{\"trades\":[" + accepted + "," + dropped + "," + dropped + "],\"has_next_page\":false}}";
        var result = await Reader(new() { ["/v1/markets"] = markets.ToJsonString(), ["/v1/trade-history"] = body })
            .Reader.ReadAsync(Address, default);
        Assert.Equal("0xaccepted", Assert.Single(result.Fills).SourceFillId);
        // The duplicated row is one execution.
        Assert.Equal(omitted, result.OmittedExecutions);
    }
}
