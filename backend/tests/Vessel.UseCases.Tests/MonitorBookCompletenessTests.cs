using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

/// <summary>
/// #95: an open order the adapter attributes to its market but cannot read (a Hyperliquid order with attached TP/SL
/// children, a Lighter TWAP or flagged order) leaves only that market's book incomplete. Plays on other markets keep every
/// stop check; Plays on that market stay unknown with the order kind as the reason and never resolve. An order that cannot
/// be attributed leaves the whole book incomplete, as before.
/// </summary>
public sealed class MonitorBookCompletenessTests
{
    private static readonly VenueIncompleteBook BtcAttachedTpsl = new("BTC", null, VenueIncompleteBook.AttachedTpsl);

    /// <summary>The fixture's BTC Play with its resting stop, and an ETH long of 1 with no stop at the venue.</summary>
    private static (DiscrepancyFixture Fixture, Play Eth) TwoMarkets()
    {
        var f = new DiscrepancyFixture();
        f.Markets["ETH"] = 1;
        var eth = f.AddPlay("long", 1, "eth-entry", "ETH");
        return (f, eth);
    }

    [Fact]
    public async Task An_unreadable_order_on_one_market_leaves_the_other_markets_fully_checked()
    {
        var (f, eth) = TwoMarkets();
        (f.OrdersComplete, f.IncompleteContracts) = (false, [BtcAttachedTpsl]);
        var start = f.Clock.Now;
        var first = await f.Evaluate();

        var coverage = first.Coverage!.ToDictionary(x => x.PlayId);
        Assert.Equal((MonitorCoverageStates.Known, (bool?)false, "no-stop"), (coverage[eth.Id].State, coverage[eth.Id].Covered, coverage[eth.Id].Reason));
        Assert.Equal(MonitorStopEvidence.NotSeen, Assert.Single(coverage[eth.Id].Stops).Evidence);
        // The BTC Play's resting stop is not enough: the unread children could be, or replace, its protection.
        Assert.Equal((MonitorCoverageStates.Unknown, (bool?)null, "incomplete-book-attached-tpsl"),
            (coverage[f.Play.Id].State, coverage[f.Play.Id].Covered, coverage[f.Play.Id].Reason));
        // The stop seen resting is still positive evidence for its level; only the coverage is undecided.
        Assert.Equal(MonitorStopEvidence.Resting, Assert.Single(coverage[f.Play.Id].Stops).Evidence);

        f.Advance(60); await f.Evaluate();
        var missing = f.Store.Rows.Single(x => x.Kind == "protection.missing");
        Assert.Equal((eth.Id, "open", "critical", start), (missing.PlayId, missing.State, missing.Severity, missing.OpenedAtUtc));
        Assert.Equal(["discrepancy.opened"], f.Events.Items.Select(x => x.Type).ToArray());
        Assert.DoesNotContain(f.Store.Rows, x => x.PlayId == f.Play.Id && x.State == "open");
    }

    [Fact]
    public async Task A_market_with_an_unreadable_order_never_raises_or_resolves_its_own_stop_warnings()
    {
        var f = new DiscrepancyFixture(); f.VenueOrders.Clear();
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.missing");
        Assert.Equal("open", row.State);

        // The stop seems back, but an unread order rests on the same market: nothing is decided there.
        f.AddOrder("stop-2", OrderLinkRole.Stop, 1, 90);
        (f.OrdersComplete, f.IncompleteContracts) = (false, [BtcAttachedTpsl]);
        f.Advance(60); await f.Evaluate();
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
        Assert.Single(f.Events.Items);

        // Gone again while unread: still no new decision either way.
        f.VenueOrders.Clear();
        f.Advance(60); var result = await f.Evaluate();
        Assert.Equal("incomplete-book-attached-tpsl", Assert.Single(result.Coverage!).Reason);
        Assert.Single(f.Events.Items);
    }

    [Theory]
    [InlineData("unattributed")]
    [InlineData("same-native")]
    public async Task An_order_that_cannot_be_attributed_still_leaves_every_market_unknown(string scope)
    {
        var (f, eth) = TwoMarkets();
        f.OrdersComplete = false;
        // Unattributed: a whole-read condition (no contract named). Same native: the reader names ETH by its native market.
        f.IncompleteContracts = scope == "unattributed" ? null : [BtcAttachedTpsl, new("OTHER", "ETH", VenueIncompleteBook.Twap)];
        await f.Evaluate(); f.Advance(60);
        var result = await f.Evaluate();

        var coverage = result.Coverage!.Single(x => x.PlayId == eth.Id);
        Assert.Equal((MonitorCoverageStates.Unknown, scope == "unattributed" ? "incomplete-book" : "incomplete-book-twap"), (coverage.State, coverage.Reason));
        Assert.True(result.Incomplete);
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind is "protection.missing" or "protection.cancelled");
        Assert.Empty(f.Events.Items);
    }

    [Fact]
    public void Contract_completeness_follows_the_canonical_contract_or_its_native_market()
    {
        var read = new VenueOrderReadResult([], DateTimeOffset.UnixEpoch, "n")
            { IncompleteContracts = [new("1000PEPE", "kPEPE", VenueIncompleteBook.AttachedTpsl)] };
        Assert.False(read.OpenOrdersCompleteFor("1000PEPE", "kPEPE"));
        Assert.False(read.OpenOrdersCompleteFor("1000PEPE", null));
        Assert.False(read.OpenOrdersCompleteFor("OTHER", "kPEPE"));
        Assert.True(read.OpenOrdersCompleteFor("BTC", null));
        Assert.False((read with { IncompleteContracts = null }).OpenOrdersCompleteFor("BTC", null));
        Assert.True(new VenueOrderReadResult([], DateTimeOffset.UnixEpoch, "n", OpenOrdersComplete: true).OpenOrdersCompleteFor("BTC", null));
    }
}
