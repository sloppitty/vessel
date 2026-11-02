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
}
