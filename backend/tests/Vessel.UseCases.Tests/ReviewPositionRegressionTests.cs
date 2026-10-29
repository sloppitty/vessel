using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Review;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Uncommitted independent reviewer-A reproductions and controls for 6f261c2.</summary>
public sealed class ReviewPositionRegressionTests : ReviewPositionRegressionTestsSupport
{
    private sealed class ActivityFactsStore(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) =>
            Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    private static ReviewService Service(ReviewFixture f, ActivityFacts facts, MemorySizingStore? sizing = null) =>
        new(f, new ReviewClock(), TestVenues.With(), new ActivityReviewPositions(
            new ActivityService(new ActivityFactsStore(facts), new MemoryWorkspaceStore(f.Owner), new ReviewClock()),
            sizing ?? new MemorySizingStore()));

    [Fact]
    public async Task Omitted_closed_position_count_must_not_include_a_known_open_incomplete_operation()
    {
        var f = new ReviewFixture();
        var account = f.Account();
        var fill = Fill(account, 0);
        var snapshot = new AccountSnapshot
        {
            OwnerId = f.Owner, AccountId = account.Id, ObservedAtUtc = ReviewClock.Now,
            Positions = [new() { ContractId = "BTC", SignedQuantity = 3 }]
        };
        var operation = Assert.Single(OperationAggregator.Aggregate([fill], snapshots: [snapshot]));
        Assert.Equal(("incomplete", "3"), (operation.Status, operation.OpenQuantity));
        Assert.Null(operation.ClosedAtUtc); // Missing entries, but it is provably still open.

        var report = await Service(f, new(f.Accounts, [fill], [snapshot], new Dictionary<Guid, Guid>()))
            .GetAsync(new(Positions: "include"), default);

        Assert.Equal(0, report.Metrics.Count);
        Assert.Equal(0, report.Positions!.Omitted);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    public async Task Check_Unassigned_only_linked_and_mixed_operations_are_not_duplicated(int claims, int expected)
    {
        var f = new ReviewFixture();
        var account = f.Account();
        var fills = new[] { Fill(account, 0), Fill(account, 1, "sell", "close", pnl: 10) };
        var play = Guid.NewGuid();
        var links = new Dictionary<Guid, Guid>();
        if (claims > 0) links[fills[0].Id] = play;
        if (claims > 1) links[fills[1].Id] = claims == 3 ? Guid.NewGuid() : play;
        var report = await Service(f, new(f.Accounts, fills, [], links)).GetAsync(new(Positions: "include"), default);
        Assert.Equal(expected, report.Metrics.Positions);
        Assert.Equal(0, report.Positions!.Omitted);
    }

    [Fact]
    public async Task Check_Unscorable_closed_operations_count_as_omitted_but_open_ones_do_not()
    {
        var f = new ReviewFixture();
        var account = f.Account();
        ImportedFill[] fills = [Fill(account, 0, fee: 1, token: "ETH"), Fill(account, 1, "sell", "close", pnl: 10),
            Fill(account, 2)];
        var report = await Service(f, new(f.Accounts, fills, [], new Dictionary<Guid, Guid>()))
            .GetAsync(new(Positions: "include"), default);
        Assert.Equal(0, report.Metrics.Positions);
        Assert.Equal(1, report.Positions!.Omitted);
    }

    [Fact]
    public async Task Check_Auto_counts_scored_plays_of_exact_scope_period_and_instrument_not_exclusions()
    {
        var f = new ReviewFixture();
        var account = f.Account();
        f.Add(0); // A scored scratch is still a scored Play for auto.
        f.Add(1, exit: ReviewClock.Now.AddDays(-31));
        f.Add(1, account: f.Account("Elsewhere"));
        f.Add(1, account: account, instrument: "ETH");
        var excluded = f.Add(1, account: account);
        excluded.Fills[1].Fill.Quantity = 2;
        ImportedFill[] fills = [Fill(account, 0), Fill(account, 1, "sell", "close", pnl: 10)];
        var service = Service(f, new(f.Accounts, fills, [], new Dictionary<Guid, Guid>()));
        var below = await service.GetAsync(new(AccountId: account.Id, Instrument: "BTC", Positions: "auto", MinPlays: 2), default);
        Assert.Equal((true, 1, 1), (below.Positions!.Included, below.Positions.Plays, below.Metrics.Positions));
        Assert.Equal(1, below.Periods.Single(p => p.Id == "all").Positions);
        var boundary = await service.GetAsync(new(AccountId: account.Id, Instrument: "BTC", Positions: "auto", MinPlays: 1), default);
        Assert.Equal((false, 1, 0), (boundary.Positions!.Included, boundary.Positions.Plays, boundary.Metrics.Positions));
        Assert.Null(boundary.Positions.Omitted);
    }

    [Fact]
    public async Task Check_Weighted_averages_use_exact_sums_even_when_a_weighted_term_exceeds_decimal_range()
    {
        const decimal amount = 40_000_000_000_000_000_000_000_000_000m;
        var f = new ReviewFixture();
        var play = f.Add(1, risk: false);
        play.Fills[1].Fill.ClosedPnlUsd = amount;
        var account = f.Accounts[0];
        ImportedFill[] fills = [Fill(account, 0), Fill(account, 1, "sell", "close", pnl: -amount)];
        var report = await Service(f, new(f.Accounts, fills, [], new Dictionary<Guid, Guid>()))
            .GetAsync(new(Positions: "include"), default);
        Assert.Equal(("0", "1", "1"), (report.Metrics.Net, report.Metrics.ProfitFactor, report.Metrics.Payoff));
        Assert.Equal("0.6666666666666666666666666667", report.Metrics.Batting);
        Assert.Equal("13333333333333333333333333333.3333333333333333333333333333", report.Metrics.Expectancy);
        Assert.Equal("0", report.Series.Last().Cumulative);
    }

    [Fact]
    public async Task Check_Mixed_rate_metrics_weight_each_play_twice_and_keep_money_counts_chart_and_streaks_real()
    {
        var f = new ReviewFixture();
        f.Add(10, risk: false);
        f.Add(-20, exit: ReviewClock.Now.AddHours(-2), entryPrice: 200, risk: false);
        f.Add(0, exit: ReviewClock.Now.AddHours(-3), risk: false);
        var account = f.Accounts[0];
        ImportedFill[] fills = [Fill(account, 0), Fill(account, 1, "sell", "close", pnl: 40),
            Fill(account, 10), Fill(account, 11, "sell", "close", pnl: -10),
            Fill(account, 20), Fill(account, 21, "sell", "close")];
        var report = await Service(f, new(f.Accounts, fills, [], new Dictionary<Guid, Guid>()))
            .GetAsync(new(Positions: "include"), default);
        var m = report.Metrics;
        Assert.Equal((6, 2, 2, 2, 3), (m.Count, m.Wins, m.Losses, m.Scratches, m.Positions));
        Assert.Equal(("20", "10", "2", "20"), (m.AverageGain, m.AverageLoss, m.Payoff, m.AverageWin));
        Assert.Equal("16.6666666666666666666666666667", m.AverageLossUsd);
        Assert.Equal(("0.5", "1.1111111111111111111111111111"), (m.Batting, m.Expectancy));
        Assert.Equal(("20", "1.6666666666666666666666666667", "30", 1, 2),
            (m.Net, m.ProfitFactor, m.Drawdown, m.MaxWins, m.MaxLosses));
        Assert.Equal(("20", 6), (report.Series.Last().Cumulative, report.Series.Sum(p => p.Count)));
        Assert.Equal(m, Assert.Single(report.Groups.Accounts).Metrics);
        Assert.Equal(m, Assert.Single(report.Groups.Assets).Metrics);
    }

    [Fact]
    public async Task Check_Mixed_rows_at_equal_exit_times_page_deterministically_without_duplicate_or_missing_ids()
    {
        var f = new ReviewFixture();
        var play = f.Add(1, exit: ReviewClock.Now.AddDays(-2).AddMinutes(1));
        var account = f.Accounts[0];
        var fills = Enumerable.Range(0, 52).SelectMany(i => new[]
        {
            Fill(account, 0, contract: $"P{i}"), Fill(account, 1, "sell", "close", pnl: 1, contract: $"P{i}")
        }).ToArray();
        var service = Service(f, new(f.Accounts, fills, [], new Dictionary<Guid, Guid>()));
        var first = await service.GetAsync(new(Positions: "include"), default);
        var second = await service.GetAsync(new(Positions: "include", Offset: 50), default);
        Assert.Equal((53, 50, 3, 50), (first.Metrics.Count, first.Plays.Count, second.Plays.Count, first.NextOffset));
        var all = first.Plays.Concat(second.Plays).ToList();
        Assert.Equal(53, all.Select(p => p.OperationId ?? p.Id!.Value.ToString()).Distinct().Count());
        Assert.Equal(play.Play.Id, all.Last().Id);
        Assert.Equal(first.Plays.Select(p => p.OperationId), (await service.GetAsync(new(Positions: "include"), default)).Plays.Select(p => p.OperationId));
        Assert.Equal("53", first.Series.Last().Cumulative);
    }
}
