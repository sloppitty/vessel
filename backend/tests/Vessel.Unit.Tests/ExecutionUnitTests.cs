using System.Globalization;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ExecutionMatcherTests
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-03T10:00:00Z");
    private readonly Account account = new(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Trading");

    private Play Planned(PlayPlanDocument? plan = null, string contract = "BTC")
    {
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", contract), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan ?? TestPlays.Plan()), Created);
        play.MarkPlanned(Created);
        return play;
    }

    private static ImportedOrder Order(string side, decimal price, decimal? trigger = null, string type = "Limit", bool reduceOnly = false,
        string contract = "BTC", DateTimeOffset? placed = null) => new()
    {
        ExternalOrderId = "1", ContractId = contract, Side = side, OrderType = type, LimitPrice = price, TriggerPrice = trigger,
        ReduceOnly = reduceOnly, PlacedAtUtc = placed ?? Created.AddMinutes(1), Status = "open", VenueStatus = "open",
    };

    private static List<MatchCandidate> Match(ImportedOrder order, params Play[] plays) => ExecutionMatcher.Candidates(order,
        plays.Select(p => (p, PlayDocuments.Read(p.Plan))), (_, _) => false, closesPosition: false);

    [Fact]
    public void Matches_entries_stops_and_targets_by_side_price_and_order_kind()
    {
        var play = Planned();
        Assert.Equal("entry|entry-1", Assert.Single(Match(Order("buy", 100.01m), play)).Level.Key);
        Assert.Empty(Match(Order("buy", 100.02m), play));
        Assert.Empty(Match(Order("sell", 100m), play));
        Assert.Equal("stop|entry-1|stop-1", Assert.Single(Match(Order("sell", 94m, 95m, "Stop Market", true), play)).Level.Key);
        // A resting limit at the stop price is not a stop.
        Assert.Empty(Match(Order("sell", 95m, reduceOnly: true), play));
        Assert.Equal("target|entry-1|target-1", Assert.Single(Match(Order("sell", 110m, reduceOnly: true), play)).Level.Key);
        Assert.Equal("target|entry-1|target-1", Assert.Single(Match(Order("sell", 109m, 110m, "Take Profit Market", true), play)).Level.Key);
        // A reduce-only buy is not a long entry.
        Assert.Empty(Match(Order("buy", 100m, reduceOnly: true), play));
    }

    [Fact]
    public void Resolves_percent_levels_as_returns_at_the_plan_leverage_and_short_sides()
    {
        var plan = TestPlays.Plan() with
        {
            Direction = "short", Leverage = "1",
            Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "percent", "5", "100")], Targets = [new PlanExit("target-1", "percent", "10", "100")] }],
        };
        var play = Planned(plan);
        Assert.Single(Match(Order("sell", 100m), play));
        Assert.Equal("stop|entry-1|stop-1", Assert.Single(Match(Order("buy", 106m, 105m, "Stop Market", true), play)).Level.Key);
        Assert.Equal("target|entry-1|target-1", Assert.Single(Match(Order("buy", 90m, reduceOnly: true), play)).Level.Key);

        // At 5x, a 5% loss on margin is a 1% move and a 10% gain is a 2% move.
        var levered = Planned(plan with { Leverage = "5" });
        Assert.Empty(Match(Order("buy", 106m, 105m, "Stop Market", true), levered));
        Assert.Equal("stop|entry-1|stop-1", Assert.Single(Match(Order("buy", 102m, 101m, "Stop Market", true), levered)).Level.Key);
        Assert.Equal("target|entry-1|target-1", Assert.Single(Match(Order("buy", 98m, reduceOnly: true), levered)).Level.Key);
    }

    [Fact]
    public void Each_stop_of_an_entry_is_its_own_level()
    {
        var plan = TestPlays.Plan() with
        {
            Entries = [TestPlays.Plan().Entries[0] with { Stops = [new PlanExit("stop-1", "price", "95", "50"), new PlanExit("stop-2", "price", "90", "50")] }],
        };
        var play = Planned(plan);
        Assert.Equal("stop|entry-1|stop-1", Assert.Single(Match(Order("sell", 94m, 95m, "Stop Market", true), play)).Level.Key);
        Assert.Equal("stop|entry-1|stop-2", Assert.Single(Match(Order("sell", 89m, 90m, "Stop Market", true), play)).Level.Key);
    }

    [Fact]
    public void Concurrent_plays_on_one_level_are_ambiguous_and_old_or_other_orders_are_ignored()
    {
        var first = Planned();
        var second = Planned();
        Assert.Equal(2, Match(Order("buy", 100m), first, second).Count);
        Assert.Empty(Match(Order("buy", 100m, placed: Created.AddMinutes(-1)), first));
        Assert.Empty(Match(Order("buy", 100m, contract: "ETH"), first));
        var dismissed = ExecutionMatcher.Candidates(Order("buy", 100m), [(first, PlayDocuments.Read(first.Plan)), (second, PlayDocuments.Read(second.Plan))],
            (play, _) => play == first, false);
        Assert.Same(second, Assert.Single(dismissed).Play);
    }

    [Fact]
    public void Unplanned_exits_need_an_open_play_and_a_reducing_order()
    {
        var play = Planned();
        Assert.Empty(ExecutionMatcher.Candidates(Order("sell", 103m), [(play, PlayDocuments.Read(play.Plan))], (_, _) => false, true));
        play.MarkOpen("Entry fill.", Created);
        var plays = new[] { (play, PlayDocuments.Read(play.Plan)) };
        Func<Play, DateTimeOffset?> opened = _ => Created.AddSeconds(30);
        Assert.Empty(ExecutionMatcher.Candidates(Order("sell", 103m), plays, (_, _) => false, false, opened));
        Assert.Equal("exit", Assert.Single(ExecutionMatcher.Candidates(Order("sell", 103m), plays, (_, _) => false, true, opened)).Level.Key);
        // An exit placed before the play's first entry fill belongs to something else.
        Assert.Empty(ExecutionMatcher.Candidates(Order("sell", 103m, placed: Created.AddSeconds(10)), plays, (_, _) => false, true, opened));
    }

    private static ImportedFill Executed(string side, string effect, int seconds) => new()
    {
        ExternalOrderId = "1", ContractId = "BTC", SourceFillId = "f" + seconds, Side = side, Direction = "", PositionEffect = effect, Price = 100, Quantity = 1,
        FeeToken = "USDC", TransactionHash = "", OccurredAtUtc = Created.AddSeconds(seconds),
    };

    private static List<MatchCandidate> Match(ImportedOrder order, ImportedFill fill, Func<Play, DateTimeOffset?> opened, params Play[] plays) =>
        ExecutionMatcher.Candidates(order, plays.Select(p => (p, PlayDocuments.Read(p.Plan))), (_, _) => false,
            ExecutionFacts.Closes(fill.PositionEffect), opened, fills: [fill]);

    [Fact]
    public void Exits_count_only_once_the_play_has_entered()
    {
        var play = Planned();
        var target = Order("sell", 110m, reduceOnly: true);
        var stop = Order("sell", 94m, 95m, "Stop Market", true);
        var closed = Executed("sell", "close", 120);
        // A resting stop or target may be placed before the entry fills.
        Assert.Equal("target|entry-1|target-1", Assert.Single(Match(target, play)).Level.Key);
        Assert.Empty(Match(target, closed, _ => null, play));
        Assert.Empty(Match(stop, closed, _ => Created.AddSeconds(150), play));
        Assert.Equal("stop|entry-1|stop-1", Assert.Single(Match(stop, closed, _ => Created.AddSeconds(120), play)).Level.Key);

        // Only the concurrent play that had entered when the exit filled can own it.
        var later = Planned();
        Assert.Same(play, Assert.Single(Match(target, closed, p => p == play ? Created.AddSeconds(60) : Created.AddSeconds(180), play, later)).Play);
    }

    [Fact]
    public void Fills_that_contradict_a_level_rule_it_out()
    {
        var play = Planned();
        var entry = Order("buy", 100m);
        Assert.Empty(Match(entry, Executed("buy", "close", 60), _ => null, play));
        Assert.Empty(Match(entry, Executed("buy", "flip", 60), _ => null, play));
        Assert.Single(Match(entry, Executed("buy", "open", 60), _ => null, play));
        // An unknown effect is not evidence against the level.
        Assert.Single(Match(entry, Executed("buy", "unknown", 60), _ => null, play));

        play.MarkOpen("Entry fill.", Created);
        Func<Play, DateTimeOffset?> opened = _ => Created.AddSeconds(30);
        Assert.Empty(Match(Order("sell", 110m), Executed("sell", "open", 60), opened, play));
        Assert.Empty(Match(Order("sell", 110m), Executed("sell", "flip", 60), opened, play));
        Assert.Empty(Match(Order("sell", 103m), Executed("sell", "flip", 60), opened, play));
        Assert.Equal("exit", Assert.Single(Match(Order("sell", 103m), Executed("sell", "close", 60), opened, play)).Level.Key);
    }

    [Theory]
    [InlineData(84541, 84541, true)]
    [InlineData(84542, 84541, true)]
    [InlineData(84543, 84541, false)]
    [InlineData(82850, 82850.18, true)]
    [InlineData(100.01, 100, true)]
    [InlineData(0.0012346, 0.0012345, true)]
    [InlineData(0.0012347, 0.0012345, false)]
    public void Prices_match_within_one_fifth_significant_figure_step(double actual, double planned, bool near) =>
        Assert.Equal(near, ExecutionMatcher.Near((decimal)actual, (decimal)planned));

    [Theory]
    [InlineData("1.0001e-24", "1e-24", null, true)]
    [InlineData("1.0002e-24", "1e-24", null, false)]
    [InlineData("1e-25", "1e-25", null, true)]
    [InlineData("1.001e-25", "1e-25", null, false)]
    [InlineData("1e-26", "1e-26", null, true)]
    [InlineData("1e-27", "1e-27", null, true)]
    [InlineData("1e-28", "1e-28", null, true)]
    [InlineData("2e-28", "1e-28", null, false)]
    [InlineData("1.001e-25", "1e-25", "1e-28", true)]
    [InlineData("1.002e-25", "1e-25", "1e-28", false)]
    [InlineData("0.01", "1e-25", "0.01", true)]
    [InlineData("0.02", "1e-25", "0.01", false)]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335", null, true)]
    public void Matching_handles_decimal_precision_boundaries_and_coarser_ticks(string actual, string planned, string? tick, bool near)
    {
        static decimal Parse(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        Assert.Equal(near, ExecutionMatcher.Near(Parse(actual), Parse(planned), tick is null ? null : Parse(tick)));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void Overflowing_percentage_stops_and_targets_keep_their_keys_but_have_no_resolved_price(string direction)
    {
        var plan = PlayDocuments.Normalize(TestPlays.Plan("100000") with
        {
            Direction = direction, Leverage = "1",
            Entries = [TestPlays.Plan("100000").Entries[0] with
            {
                Stops = [new PlanExit("stop-1", "percent", "10000000000000000000000000000", "100")],
                Targets = [new PlanExit("target-1", "percent", "10000000000000000000000000000", "100")]
            }]
        });
        var levels = ExecutionMatcher.Levels(plan).ToList();
        Assert.Equal(["entry|entry-1", "stop|entry-1|stop-1", "target|entry-1|target-1"], levels.Select(l => l.Key));
        Assert.Equal(new decimal?[] { 100000m, null, null }, levels.Select(l => l.Price));
    }
}
