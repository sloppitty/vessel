using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.3 (D-01 display, F-09, I-04, I-05): the Play-local result beside the scored venue result. Hyperliquid and
/// Lighter realize P&amp;L at the venue position's average cost, so with concurrent Plays (or an unjournaled position) a Play's
/// venue result can have the wrong sign. The scored basis stays the venue result; the local figure is shown beside it.
/// Ported from the review probes <c>round3-dotnet/R3.cs</c> and <c>correctness-dotnet/More.cs</c>.
/// </summary>
public sealed class PlayLocalResultTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-10T10:00:00Z");
    private readonly Account account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Venue");

    private static DateTimeOffset T(int minute) => T0.AddMinutes(minute);

    private ImportedFill Fill(int minute, string side, decimal quantity, decimal price, string effect, decimal pnl = 0, decimal fee = 0,
        string token = "USDC", string pnlBasis = ExecutionFacts.PnlGross, string feeBasis = ExecutionFacts.FeeReported, string contract = "BTC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = contract, SourceFillId = Guid.NewGuid().ToString("N"),
        ExternalOrderId = "o" + minute, Side = side, Direction = "", PositionEffect = effect, Price = price, Quantity = quantity, Fee = fee,
        FeeToken = token, FeeBasis = feeBasis, ClosedPnlUsd = pnl, PnlBasis = pnlBasis, OccurredAtUtc = T(minute), TransactionHash = "",
    };

    private ImportedFill Buy(int minute, decimal quantity, decimal price, decimal pnl = 0, decimal fee = 0) =>
        Fill(minute, ExecutionFacts.Buy, quantity, price, ExecutionFacts.Open, pnl, fee);
    private ImportedFill Sell(int minute, decimal quantity, decimal price, decimal pnl = 0, decimal fee = 0) =>
        Fill(minute, ExecutionFacts.Sell, quantity, price, ExecutionFacts.Close, pnl, fee);

    /// <summary>A Play of <paramref name="direction"/> closed by a later check, with these linked fills.</summary>
    private ClosedPlayFacts Closed(string direction, params (OrderLinkRole Role, ImportedFill Fill)[] fills) =>
        Facts(direction, true, InstrumentSource.Venue, fills);

    private ClosedPlayFacts Facts(string direction, bool close, InstrumentSource source, params (OrderLinkRole Role, ImportedFill Fill)[] fills)
    {
        var plan = TestPlays.Plan("100", direction == "long" ? "95" : "105") with { Direction = direction };
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), source, PlayDocuments.Serialize(plan), T(-10));
        var revision = play.MarkPlanned(T(-10)).Revision;
        play.MarkOpen("Filled.", T(-5));
        if (close) play.MarkClosed("Exited.", T(60));
        return new(play, [revision], fills.Select(f => new LinkedFill(f.Role, f.Fill)).ToList());
    }

    private static PlayLocalResult Local(ClosedPlayFacts facts, string? quote = "USDC") => PlayLocalResult.From(facts, quote);
    private static PlayOutcome Venue(ClosedPlayFacts facts) => PlayOutcomeCalculator.Outcome(facts)!;

    [Fact]
    public void Concurrent_plays_keep_their_own_result_beside_the_venue_allocation()
    {
        // A buys 1 at 100, B buys 1 at 110; the venue position averages 105, so each exit at 108 reports +3.
        var a = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 108, pnl: 3)));
        var b = Closed("long", (OrderLinkRole.Entry, Buy(5, 1, 110)), (OrderLinkRole.Stop, Sell(15, 1, 108, pnl: 3)));

        Assert.Equal((3m, 3m), (Venue(a).NetResult, Venue(b).NetResult));
        var (localA, localB) = (Local(a), Local(b));
        Assert.Equal(("8", "8", PlayLocalResult.Executions, (string?)null), (localA.Gross.ToString(), localA.Net.ToString(), localA.Basis, localA.Reason));
        Assert.Equal(("-2", "-2"), (localB.Gross.ToString(), localB.Net.ToString()));
        Assert.False(localA.SignDiffers(Venue(a).NetResult));
        Assert.True(localB.SignDiffers(Venue(b).NetResult));
        // The scored basis is unchanged: B stays a venue win (decision 1).
        Assert.Equal(PlayOutcomeKind.Win, Venue(b).Kind);
        // Both bases conserve the pair's total.
        Assert.Equal(6m, Venue(a).NetResult + Venue(b).NetResult);
        Assert.Equal(6m, decimal.Parse(localA.Net.ToString()!) + decimal.Parse(localB.Net.ToString()!));

        var dto = localB.ToDto(Venue(b).NetResult);
        Assert.Equal(new PlayLocalResultDto("-2", PlayLocalResult.Executions, null, true), dto);
    }

    [Fact]
    public void A_single_flat_to_flat_play_agrees_with_the_venue()
    {
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100, fee: 0.05m)), (OrderLinkRole.Target, Sell(10, 1, 110, pnl: 10, fee: 0.05m)));
        Assert.Equal(9.9m, Venue(facts).NetResult);
        var local = Local(facts);
        Assert.Equal(("10", "9.9"), (local.Gross.ToString(), local.Net.ToString()));
        Assert.False(local.SignDiffers(Venue(facts).NetResult));
    }

    [Fact]
    public void A_short_gains_when_it_buys_back_lower()
    {
        var facts = Closed("short", (OrderLinkRole.Entry, Fill(0, ExecutionFacts.Sell, 2, 50, ExecutionFacts.Open, fee: 0.1m)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Buy, 2, 45, ExecutionFacts.Close, pnl: 10, fee: 0.1m)));
        var local = Local(facts);
        Assert.Equal(("10", "9.8"), (local.Gross.ToString(), local.Net.ToString()));
        Assert.Equal(9.8m, Venue(facts).NetResult);
    }

    [Fact]
    public void Net_of_fee_fills_are_charged_every_fee_once()
    {
        // RISEx style: the realized PnL already has the fee taken off and the fee is retained beside it.
        var facts = Closed("long",
            (OrderLinkRole.Entry, Fill(0, ExecutionFacts.Buy, 1, 100, ExecutionFacts.Open, pnl: -0.05m, fee: 0.05m, pnlBasis: ExecutionFacts.PnlNetOfFee)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Sell, 1, 110, ExecutionFacts.Close, pnl: 9.945m, fee: 0.055m, pnlBasis: ExecutionFacts.PnlNetOfFee)));
        Assert.Equal(9.895m, Venue(facts).NetResult);
        var local = Local(facts);
        // Charging only gross-basis fills' fees would leave 10.
        Assert.Equal(("10", "9.895"), (local.Gross.ToString(), local.Net.ToString()));
        // The venue figure before fees adds back the fees already inside net-of-fee P&L.
        Assert.Equal(ExactAmount.From(10m), Venue(facts).GrossResult);
    }

    [Fact]
    public void An_unjournaled_position_cannot_lend_the_play_its_average_cost()
    {
        // A long 1 at 90 opened outside Vessel; the Play buys 1 at 100 and sells 1 at 105, and the venue reports +10 at average 95.
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 105, pnl: 10)));
        Assert.Equal(10m, Venue(facts).NetResult);
        Assert.Equal("5", Local(facts).Net.ToString());
        Assert.False(Local(facts).SignDiffers(Venue(facts).NetResult));
    }

    [Fact]
    public void An_unjournaled_position_can_turn_a_winning_play_into_a_venue_loss()
    {
        // A long 1 at 120 opened outside Vessel; the Play buys 1 at 100 and sells 1 at 105. At the venue's average 110 the
        // exit reports -5, so the Play scores a loss although its own fills gained 5.
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 105, pnl: -5)));
        var venue = Venue(facts);
        Assert.Equal((-5m, PlayOutcomeKind.Loss), (venue.NetResult, venue.Kind));
        Assert.Equal("5", Local(facts).Net.ToString());
        Assert.True(Local(facts).SignDiffers(venue.NetResult));
        // A loss before fees too, so not a fee-only loss.
        Assert.False(venue.FeeOnlyLoss);
    }

    [Fact]
    public void A_zero_result_against_a_nonzero_venue_result_differs_in_sign()
    {
        // Scratch is exactly zero; a zero beside a venue win would change the win/loss/scratch class.
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 100, pnl: 3)));
        Assert.Equal("0", Local(facts).Net.ToString());
        Assert.True(Local(facts).SignDiffers(3));
        Assert.False(Local(facts).SignDiffers(0));
    }

    [Theory]
    [InlineData("HYPE", ExecutionFacts.FeeReported, ExecutionFacts.PnlGross)]
    [InlineData("HYPE", ExecutionFacts.FeeReported, ExecutionFacts.PnlNetOfFee)]
    [InlineData("USDC", "unknown", ExecutionFacts.PnlGross)]
    [InlineData("", ExecutionFacts.FeeReported, ExecutionFacts.PnlGross)]
    public void Unverified_fees_leave_the_local_result_unknown(string token, string feeBasis, string pnlBasis)
    {
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Sell, 1, 110, ExecutionFacts.Close, pnl: 10, fee: 0.01m, token: token, feeBasis: feeBasis, pnlBasis: pnlBasis)));
        var local = Local(facts);
        Assert.Null(local.Net);
        Assert.Null(local.Gross);
        Assert.Contains("fee", local.Reason);
        Assert.False(local.SignDiffers(10));
        Assert.Equal(new PlayLocalResultDto(null, PlayLocalResult.Executions, local.Reason, false), local.ToDto(10));
    }

    [Fact]
    public void Known_zero_fees_and_fee_free_standard_accounts_are_verified()
    {
        var free = Closed("long", (OrderLinkRole.Entry, Fill(0, ExecutionFacts.Buy, 1, 100, ExecutionFacts.Open, feeBasis: ExecutionFacts.FeeStandardAccountFree)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Sell, 1, 104, ExecutionFacts.Close, pnl: 4, token: "HYPE")));
        Assert.Equal("4", Local(free).Net.ToString());
        // USDG (Lighter Robinhood) is a listed stablecoin.
        var usdg = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Sell, 1, 104, ExecutionFacts.Close, pnl: 4, fee: 0.5m, token: "USDG")));
        Assert.Equal("3.5", Local(usdg, "USDG").Net.ToString());
    }

    [Fact]
    public void Only_settled_closed_plays_have_a_local_result()
    {
        // Unbalanced: two entered, one exited.
        var unbalanced = Closed("long", (OrderLinkRole.Entry, Buy(0, 2, 100)), (OrderLinkRole.Target, Sell(10, 1, 110, pnl: 10)));
        Assert.False(ExecutionIntegrity.Settled(unbalanced));
        Assert.Null(Local(unbalanced).Net);
        Assert.Contains("settle", Local(unbalanced).Reason);
        // Known missing executions.
        var balanced = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 110, pnl: 10)));
        Assert.NotNull(Local(balanced).Net);
        Assert.Null(Local(balanced with { IncompleteOrders = ["o0"] }).Net);
        // Still open.
        var open = Facts("long", false, InstrumentSource.Venue, (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 110, pnl: 10)));
        Assert.Equal(PlayStatus.Open, open.Play.Status);
        Assert.Contains("not closed", Local(open).Reason);
    }

    [Fact]
    public void Only_linear_plays_quoted_in_a_listed_stablecoin_have_a_local_result()
    {
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 110, pnl: 10)));
        Assert.Equal("10", Local(facts, "USDC").Net.ToString());
        Assert.Contains("quote", Local(facts, null).Reason);
        Assert.Null(Local(facts, null).Net);
        Assert.Contains("BTC", Local(facts, "BTC").Reason);
        Assert.Null(Local(facts, "usdc").Net);
        // A hand-entered instrument has no catalogue quote.
        var typed = Facts("long", true, InstrumentSource.Manual, (OrderLinkRole.Entry, Buy(0, 1, 100)), (OrderLinkRole.Target, Sell(10, 1, 110, pnl: 10)));
        Assert.Null(Local(typed).Net);
        // A fill on another instrument is not this Play's.
        var other = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Sell, 1, 110, ExecutionFacts.Close, pnl: 10, contract: "ETH")));
        Assert.Null(Local(other).Net);
    }

    [Fact]
    public void A_result_beyond_exact_precision_is_unknown_not_rounded()
    {
        // Each product has 29 decimal places; their difference (1e-29) is not a decimal.
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 0.00000000000001m, 1.000000000000001m)),
            (OrderLinkRole.Target, Sell(10, 0.00000000000001m, 1.000000000000002m)));
        var local = Local(facts);
        Assert.Null(local.Net);
        Assert.Contains("28 decimal places", local.Reason);
    }

    [Fact]
    public void A_manual_ledger_result_is_the_play_local_result()
    {
        var manual = new Account(Guid.NewGuid(), account.OwnerId, "manual", "Paper");
        var play = new Play(Guid.NewGuid(), manual, new PerpetualInstrument("manual", "BTC"), InstrumentSource.Manual,
            PlayDocuments.Serialize(TestPlays.Plan()), T(-10));
        play.MarkPlanned(T(-10));
        ManualExecution Row(string role, string quantity, string price, int minute, string fee) => new()
        {
            Id = Guid.NewGuid(), OwnerId = play.OwnerId, PlayId = play.Id, Role = role, EntryId = role == "entry" ? "entry-1" : null,
            Side = role == "entry" ? "buy" : "sell", Quantity = quantity, Price = price, Fee = fee, FeeCurrency = "USDC",
            Source = "Paper broker", OccurredAtUtc = T(minute),
        };
        // Exits of a third each carry a rounded average-cost allocation (cost 302 over 3); the final close consumes the residual.
        var ledger = ManualLedger.Calculate(play, [Row("entry", "1", "100", 0, "0.1"), Row("entry", "1", "101", 1, "0.1"),
            Row("entry", "1", "101", 2, "0.1"), Row("exit", "1", "110", 3, "0.1"), Row("exit", "1", "95", 4, "0.1"), Row("exit", "1", "103", 5, "0.1")]);
        play.RecordManualOpen(T(0));
        play.RecordManualClose(T(5), T(6));
        var facts = new ClosedPlayFacts(play, [], ledger.Projection, [], true, ledger.FeesComplete);

        var local = PlayLocalResult.From(facts, null);
        Assert.Equal(PlayLocalResult.ManualLedger, local.Basis);
        Assert.Equal(5.4m, ledger.Net);
        Assert.Equal(ledger.Net, decimal.Parse(local.Net.ToString()!));
        Assert.Equal(ledger.Net, Venue(facts).NetResult);
        Assert.False(local.SignDiffers(Venue(facts).NetResult));

        // Unknown manual costs stay unknown.
        var unpriced = Row("exit", "1", "110", 2, "0.1");
        unpriced.Fee = null;
        unpriced.FeeCurrency = null;
        var unknown = ManualLedger.Calculate(play, [Row("entry", "1", "100", 0, "0.1"), unpriced]);
        Assert.Null(PlayLocalResult.From(new ClosedPlayFacts(play, [], unknown.Projection, [], true, unknown.FeesComplete), null).Net);
    }

    [Theory]
    [InlineData("0", "0.05", true)]      // exact zero before fees: fees alone made the loss
    [InlineData("0.05", "0.05", true)]   // a small gain eaten by fees
    [InlineData("-1", "0.05", false)]    // a loss before fees too
    [InlineData("1", "0.05", false)]     // a win after fees
    public void Fee_only_losses_have_a_venue_result_before_fees_of_zero_or_better(string pnl, string fee, bool feeOnly)
    {
        var closed = decimal.Parse(pnl, System.Globalization.CultureInfo.InvariantCulture);
        var cost = decimal.Parse(fee, System.Globalization.CultureInfo.InvariantCulture);
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 1, 100, fee: cost)), (OrderLinkRole.Target, Sell(10, 1, 100 + closed, pnl: closed, fee: cost)));
        var outcome = Venue(facts);
        Assert.Equal(ExactAmount.From(closed), outcome.GrossResult);
        Assert.Equal(feeOnly, outcome.FeeOnlyLoss);
        // The win/loss/scratch class is unchanged (decision 8).
        Assert.Equal(closed - 2 * cost > 0 ? PlayOutcomeKind.Win : PlayOutcomeKind.Loss, outcome.Kind);
    }

    [Fact]
    public void A_fee_only_loss_is_decided_from_the_exact_result_before_fees()
    {
        // Gross 8.0000000000000000000000000001 has 29 significant digits, beyond decimal; fees 9.0000000000000000000000000001
        // leave a net of exactly -1. Narrowing the gross first made it unknown and the fee-only loss disappeared.
        var facts = Closed("long", (OrderLinkRole.Entry, Buy(0, 2, 100, fee: 3)),
            (OrderLinkRole.Target, Sell(10, 1, 104, pnl: 4.0000000000000000000000000001m, fee: 3)),
            (OrderLinkRole.Target, Sell(20, 1, 104, pnl: 4, fee: 3.0000000000000000000000000001m)));
        var outcome = Venue(facts);
        Assert.Equal((-1m, PlayOutcomeKind.Loss), (outcome.NetResult, outcome.Kind));
        Assert.Equal("8.0000000000000000000000000001", outcome.GrossResult.ToString());
        Assert.True(outcome.FeeOnlyLoss);
    }

    [Fact]
    public void An_unlisted_fee_inside_net_of_fee_pnl_leaves_the_result_before_fees_unknown()
    {
        var facts = Closed("long",
            (OrderLinkRole.Entry, Fill(0, ExecutionFacts.Buy, 1, 100, ExecutionFacts.Open, pnl: -0.05m, fee: 0.05m, token: "HYPE", pnlBasis: ExecutionFacts.PnlNetOfFee)),
            (OrderLinkRole.Target, Fill(10, ExecutionFacts.Sell, 1, 100, ExecutionFacts.Close, pnl: -0.05m, fee: 0.05m, token: "HYPE", pnlBasis: ExecutionFacts.PnlNetOfFee)));
        var outcome = Venue(facts);
        Assert.Equal((-0.1m, true), (outcome.NetResult, outcome.FeesComplete));
        Assert.Null(outcome.GrossResult);
        Assert.False(outcome.FeeOnlyLoss);
    }
}
