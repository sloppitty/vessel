using System.Text.Json.Nodes;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Phase 2, item 7: a Hyperliquid fill left out keeps a marker with its trade, market, order and time.</summary>
public sealed partial class HyperliquidAdapterTests
{
    [Fact]
    public async Task A_fill_on_an_unlisted_primary_market_leaves_a_marker_and_out_of_scope_fills_leave_none()
    {
        var unknown = Mutate(Fill, node => { node["coin"] = "SOL"; node["tid"] = "42"; });
        var spot = Mutate(Fill, node => { node["coin"] = "@107"; node["tid"] = "43"; });
        var result = await Read(fills: $"[{Fill},{unknown},{spot}]");
        var marker = Assert.Single(result.Omissions);
        Assert.Equal(("42", ExecutionOmission.UnrecognizedMarket, (string?)null, "SOL", "9007199254740993"),
            (marker.SourceId, marker.Kind, marker.ContractId, marker.VenueContractId, marker.OrderId));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790812799000), marker.OccurredAtUtc);
        Assert.Equal(1, result.OmittedExecutions);
    }

    [Fact]
    public async Task A_fill_on_an_unlisted_primary_market_without_a_trade_ID_fails_the_read_rather_than_leave_no_marker()
    {
        // Sol round 1: counting it without a durable marker would lose the evidence once the row leaves recent history.
        var anonymous = Mutate(Fill, node => { node["coin"] = "SOL"; node["tid"] = null; });
        await Assert.ThrowsAsync<Vessel.Application.Venues.VenueReadException>(() => Read(fills: $"[{Fill},{anonymous}]"));
    }

    [Fact]
    public async Task A_fill_with_an_unknown_side_leaves_a_marker_and_the_other_fills_import()
    {
        // Issue #118: one unrecognized row no longer stops the account's import, and is never imported with a guessed side.
        var unknown = Mutate(Fill, node => { node["side"] = "X"; node["tid"] = "42"; node["oid"] = 77; });
        var result = await Read(fills: $"[{Fill},{unknown}]");
        var fill = Assert.Single(result.Fills);
        Assert.Equal("18446744073709551615", fill.SourceFillId);
        var marker = Assert.Single(result.Omissions);
        Assert.Equal(("42", ExecutionOmission.UnrecognizedRow, (string?)"BTC", (string?)null, (string?)"77"),
            (marker.SourceId, marker.Kind, marker.ContractId, marker.VenueContractId, marker.OrderId));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790812799000), marker.OccurredAtUtc);
        Assert.Equal(1, result.OmittedExecutions);
    }

    [Theory]
    [InlineData("tid", "null")]
    [InlineData("time", "null")]
    [InlineData("px", "\"1e3\"")]
    [InlineData("fee", "\"0.00000000000000000000000000001\"")]
    [InlineData("dir", "1")]
    [InlineData("sz", "\"0\"")]
    [InlineData("closedPnl", "1")]
    [InlineData("feeToken", "null")]
    [InlineData("oid", "\"x\"")]
    [InlineData("hash", "null")]
    public async Task A_fill_with_an_unknown_side_and_a_malformed_field_still_fails_the_read(string field, string json)
    {
        // Response-level: a row without a usable identity or time, or with a field a strict reader rejects, is not omitted.
        var unknown = Mutate(Fill, node => { node["side"] = "X"; node[field] = JsonNode.Parse(json); });
        await Assert.ThrowsAsync<Vessel.Application.Venues.VenueReadException>(() => Read(fills: $"[{Fill},{unknown}]"));
    }
}
