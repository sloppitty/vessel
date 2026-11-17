using System.Text.Json.Nodes;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Lighter;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 7: every row a Lighter import leaves out keeps a marker with its trade, market, order and time, and a
/// venue-forced one is also a forced-event fact with nullable money.
/// </summary>
public sealed partial class LighterAdapterTests
{
    private static LighterReader.FillPage Page(params string[] rows)
    {
        var catalogue = new LighterReader.Catalogue([new("1", "BTC", true, new("BTC", 5, 50, "USDC"))], new HashSet<string>(), LighterExchange.Mainnet);
        using var document = System.Text.Json.JsonDocument.Parse(Trades(string.Join(",", rows)));
        return LighterReader.ReadFills(document.RootElement, catalogue, Source, Now.AddMinutes(5), true);
    }

    private static string WithId(string row, long id) => Change(Change(row, "trade_id", JsonValue.Create(id)), "trade_id_str", JsonValue.Create(id.ToString()));

    [Fact]
    public void Omitted_rows_keep_their_identity_and_forced_ones_become_facts_with_unknown_fees()
    {
        var liquidation = WithId(Change(Trade, "type", JsonValue.Create("liquidation")), 1);
        var settlement = WithId(Change(Change(Trade, "type", JsonValue.Create("market-settlement")), "ask_account_pnl", null), 2);
        var fee = WithId(Change(Trade, "maker_fee", JsonValue.Create(5)), 3);
        // A closing execution without its realized P&L.
        var missing = WithId(Change(Trade, "ask_account_pnl", null), 4);
        var page = Page(Trade, liquidation, settlement, fee, missing);
        Assert.Single(page.Fills);
        Assert.Equal([("1", ExecutionOmission.Forced, "liquidation"), ("2", ExecutionOmission.Forced, "market settlement"),
            ("3", ExecutionOmission.Fee, "fee-bearing or unverified fee"), ("4", ExecutionOmission.MissingPnl, "no reported realized P&L")],
            page.Omissions.Select(o => (o.SourceId, o.Kind, o.Reason)));
        Assert.All(page.Omissions, o => Assert.Equal(("BTC", "1", "9007199254740995", Now.AddMinutes(-1)), (o.ContractId, o.VenueContractId, o.OrderId, o.OccurredAtUtc!.Value)));
        Assert.Equal([ForcedEvent.Liquidation, ForcedEvent.Settlement], page.ForcedEvents.Select(e => e.Kind));
        var forced = page.ForcedEvents[0];
        Assert.Equal(("sell", 0.001m, 80000.01m, (decimal?)null, 2.1234m), (forced.Side, forced.Quantity, forced.Price!.Value, forced.Fee, forced.RealizedPnl!.Value));
        // An unreported P&L stays unknown, never zero.
        Assert.Null(page.ForcedEvents[1].RealizedPnl);
    }

    [Fact]
    public void A_forced_row_with_an_invalid_time_still_leaves_a_marker_without_a_forced_fact()
    {
        var row = WithId(Change(Change(Trade, "type", JsonValue.Create("deleverage")), "timestamp", JsonValue.Create("soon")), 7);
        var page = Page(row);
        var marker = Assert.Single(page.Omissions);
        Assert.Equal(("7", ExecutionOmission.Forced, (DateTimeOffset?)null), (marker.SourceId, marker.Kind, marker.OccurredAtUtc));
        Assert.Empty(page.ForcedEvents);
        Assert.Equal(1, page.Deleverages);
    }

    [Fact]
    public async Task An_unknown_trade_type_leaves_a_marker_and_lets_the_other_rows_import()
    {
        // Issue #118: a new Lighter trade type no longer stops the account's whole import.
        var unknown = WithId(Change(Trade, "type", JsonValue.Create("rfq-trade")), 5);
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal)
            ? Response(Trades(string.Join(",", Trade, unknown))) : snapshotReply(request);
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.Equal("9223372036854775806", Assert.Single(result.Fills).SourceFillId);
        var marker = Assert.Single(result.Omissions);
        Assert.Equal(("5", ExecutionOmission.UnrecognizedRow, "unrecognized trade type", "BTC", "1", "9007199254740995", Now.AddMinutes(-1)),
            (marker.SourceId, marker.Kind, marker.Reason, marker.ContractId, marker.VenueContractId, marker.OrderId, marker.OccurredAtUtc!.Value));
        Assert.Empty(result.ForcedEvents);
        Assert.Equal(1, result.OmittedExecutions);
        Assert.Contains("Omitted executions: 1 of an unrecognized type.", result.HistoryNotice);
    }

    [Theory]
    [InlineData("type", "7")]
    [InlineData("timestamp", "\"soon\"")]
    [InlineData("trade_id_str", "\"x\"")]
    [InlineData("size", "\"-1\"")]
    [InlineData("maker_fee", "\"1\"")]
    // Every field an imported trade validates is validated before the row can become an unrecognized-row marker.
    [InlineData("price", "null")]
    [InlineData("price", "\"-1\"")]
    [InlineData("ask_account_pnl", "2.1")]
    [InlineData("tx_hash", "\"not-hex\"")]
    [InlineData("tx_hash", "null")]
    [InlineData("ask_id", "\"x\"")]
    [InlineData("ask_id_str", "\"3\"")]
    public void An_unknown_trade_type_with_a_malformed_field_still_fails_the_read(string field, string json)
    {
        // Response-level: a type of the wrong JSON type, or a row with any field a strict reader rejects, is not omitted.
        var row = Change(Change(Trade, "type", JsonValue.Create("rfq-trade")), field, JsonNode.Parse(json));
        Assert.Throws<Vessel.Application.Venues.VenueReadException>(() => Page(Trade, row));
    }

    [Fact]
    public async Task An_unknown_trade_type_on_overlapping_pages_counts_once()
    {
        // Issue #118: the count comes from the deduplicated markers, not from summed page counters.
        var unknown = WithId(Change(Trade, "type", JsonValue.Create("rfq-trade")), 5);
        using var handler = CoverageHandler();
        var snapshotReply = handler.Reply!;
        handler.Reply = request => !request.RequestUri!.AbsolutePath.EndsWith("/trades", StringComparison.Ordinal) ? snapshotReply(request)
            : request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal)
                ? Response(Trades(unknown))
                : Response($$"""{"code":200,"trades":[{{Trade}},{{unknown}}],"next_cursor":"page1"}""");
        var result = await Reader(handler).ReadAsync(Source, default);
        Assert.Single(result.Omissions);
        Assert.Equal(1, result.OmittedExecutions);
        Assert.Contains("Omitted executions: 1 of an unrecognized type.", result.HistoryNotice);
    }
}
