using System.Text.Json.Nodes;
using Vessel.Application.Venues;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Issue #118: a well-formed RISEx trade with a side Vessel does not recognize or a negative fee (a maker rebate) is
/// row-level. It leaves a marker with its trade, market, order and time, and the rest of the read applies. A malformed
/// row still fails the whole read.
/// </summary>
public sealed partial class RisexAdapterTests
{
    [Theory]
    [InlineData("fee", "-0.01", "fee", "negative fee", "1 with a negative fee")]
    [InlineData("side", "BOTH", "unrecognized-row", "unrecognized side", "1 with an unrecognized side")]
    [InlineData("position_side", "NONE", "unrecognized-row", "unrecognized side", "1 with an unrecognized side")]
    public async Task A_trade_with_an_unrecognized_value_leaves_a_marker_and_the_read_still_applies(string field, string value,
        string kind, string reason, string notice)
    {
        var history = JsonNode.Parse(Trades)!;
        var trade = history["data"]!["trades"]![0]!.AsObject();
        var id = trade["id"]!.GetValue<string>();
        trade[field] = value;
        var result = await Reader(new() { ["/v1/trade-history"] = history.ToJsonString() }).Reader.ReadAsync(Address, default);
        Assert.DoesNotContain(result.Fills, f => f.SourceFillId == id);
        Assert.Contains(result.Fills, f => f.SourceFillId == "0xclose");
        var omission = Assert.Single(result.Omissions, o => o.SourceId == id);
        Assert.Equal((kind, reason, "BTC", "1", trade["order_id"]!.GetValue<string>(), Now.AddMinutes(-30)),
            (omission.Kind, omission.Reason, omission.ContractId, omission.VenueContractId, omission.OrderId, omission.OccurredAtUtc!.Value));
        var baseline = await Reader(new() { ["/v1/trade-history"] = Trades }).Reader.ReadAsync(Address, default);
        Assert.Equal(baseline.OmittedExecutions + 1, result.OmittedExecutions);
        Assert.Contains(notice, result.HistoryNotice);
    }

    [Theory]
    [InlineData("side", "1")]
    [InlineData("fee", "\"-0.01.0\"")]
    [InlineData("time", "\"soon\"")]
    [InlineData("price", "\"not a price\"")]
    [InlineData("realized_pnl", "\"1e3\"")]
    public async Task A_trade_with_an_unrecognized_value_and_a_malformed_field_still_fails_the_read(string field, string json) =>
        await AssertMalformed("fee", "-0.01", field, json);

    [Theory]
    [InlineData("id", "null")]
    [InlineData("size", "\"0\"")]
    [InlineData("order_id", "null")]
    [InlineData("is_liquidation", "\"no\"")]
    [InlineData("blockchain_data", "{\"tx_hash\":5}")]
    [InlineData("leverage", "\"high\"")]
    [InlineData("realized_pnl", "5")]
    [InlineData("position_side", "1")]
    public async Task A_trade_with_an_unrecognized_side_and_a_malformed_field_still_fails_the_read(string field, string json) =>
        await AssertMalformed("side", "BOTH", field, json);

    private static async Task AssertMalformed(string trigger, string value, string field, string json)
    {
        // Response-level: a wrong type, an unusable time or a value a strict reader rejects fails the read, as before.
        var history = JsonNode.Parse(Trades)!;
        var trade = history["data"]!["trades"]![0]!.AsObject();
        trade[trigger] = value;
        trade[field] = JsonNode.Parse(json);
        var error = await Assert.ThrowsAsync<VenueReadException>(() =>
            Reader(new() { ["/v1/trade-history"] = history.ToJsonString() }).Reader.ReadAsync(Address, default));
        Assert.Equal("RISEx returned an invalid or unsupported response.", error.Message);
    }
}
