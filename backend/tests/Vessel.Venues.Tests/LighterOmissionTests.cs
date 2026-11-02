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
}
