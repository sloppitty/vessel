using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

public sealed class ReviewTests : ReviewTestsSupport
{
    private Task<ReviewDto> Read(ReviewQuery? query = null) => f.Service.GetAsync(query ?? new(), default);

    [Fact]
    public async Task Aggregates_individual_outcomes_without_averaging_account_percentages()
    {
        var first = f.Account();
        var second = f.Account("Small");
        f.Add(20, ReviewClock.Now.AddDays(-4), first);
        f.Add(40, ReviewClock.Now.AddDays(-3), first, entryPrice: 200);
        f.Add(0, ReviewClock.Now.AddDays(-2), first);
        f.Add(-10, ReviewClock.Now.AddDays(-1), second, risk: false);
        var report = await Read();
        Assert.Equal((4, 2, 1, 1, "50"), (report.Metrics.Count, report.Metrics.Wins, report.Metrics.Losses, report.Metrics.Scratches, report.Metrics.Net));
        Assert.Equal(2m / 3, decimal.Parse(report.Metrics.Batting!));
        Assert.Equal(("20", "10", "2", "6", "12.5"), (report.Metrics.AverageGain, report.Metrics.AverageLoss,
            report.Metrics.Payoff, report.Metrics.ProfitFactor, report.Metrics.Expectancy));
        Assert.Equal(("10", 2, 1, 3), (report.Metrics.Drawdown, report.Metrics.MaxWins, report.Metrics.MaxLosses, report.Metrics.RiskCount));
        Assert.Equal("50", report.Series.Last().Cumulative);
        Assert.Equal(50m, report.Groups.Accounts.Sum(g => decimal.Parse(g.Metrics.Net!)));
        Assert.Equal("50", report.Periods.Single(p => p.Id == "month").Net);
    }

    [Fact]
    public async Task Empty_is_unknown_not_a_zero_return_and_scratches_have_no_win_denominator()
    {
        var empty = await Read();
        Assert.Null(empty.Metrics.Net);
        Assert.Null(empty.Metrics.Drawdown);
        Assert.Empty(empty.Series);
        f.Add(0);
        var scratch = await Read();
        Assert.Equal(("0", 1, "0"), (scratch.Metrics.Net, scratch.Metrics.Scratches, scratch.Metrics.Expectancy));
        Assert.Null(scratch.Metrics.Batting);
        Assert.Null(scratch.Metrics.ProfitFactor);
        Assert.Null(scratch.Metrics.Payoff);
    }

    [Fact]
    public async Task Uses_exit_time_not_sync_time_and_excludes_the_exact_rolling_boundary()
    {
        f.Add(3, ReviewClock.Now.AddDays(-1));
        f.Add(5, ReviewClock.Now.AddDays(-1).AddTicks(1));
        f.Add(7, ReviewClock.Now.AddDays(-8));
        f.Add(9, ReviewClock.Now.AddDays(-31));
        f.Add(11, ReviewClock.Now.AddDays(-366));
        var report = await Read(new("day"));
        Assert.Equal(1, report.Metrics.Count);
        Assert.Equal(["5", "8", "15", "24", "35"], report.Periods.Select(p => p.Net));
        Assert.Equal(ReviewClock.Now.AddDays(-1).AddTicks(1), report.Plays.Single().ClosedAtUtc);
    }

    [Theory]
    [InlineData("no-entry")]
    [InlineData("no-exit")]
    [InlineData("duplicate")]
    [InlineData("quantity")]
    [InlineData("side")]
    [InlineData("flip")]
    [InlineData("foreign-account")]
    [InlineData("instrument")]
    [InlineData("early-exit")]
    [InlineData("future-entry")]
    [InlineData("zero-price")]
    public async Task Incomplete_executions_are_not_zero_result_plays(string defect)
    {
        var facts = f.Add(10);
        var fills = facts.Fills.ToList();
        var entry = fills[0].Fill; var exit = fills[1].Fill;
        switch (defect)
        {
            case "no-entry": fills.RemoveAt(0); break;
            case "no-exit": fills.RemoveAt(1); break;
            case "duplicate": fills.Add(fills[0]); break;
            case "quantity": exit.Quantity = 2; break;
            case "side": exit.Side = ExecutionFacts.Buy; break;
            case "flip": exit.PositionEffect = ExecutionFacts.Flip; break;
            case "foreign-account": entry.AccountId = Guid.NewGuid(); break;
            case "instrument": exit.ContractId = "ETH"; break;
            case "early-exit": exit.OccurredAtUtc = entry.OccurredAtUtc.AddSeconds(-1); break;
            case "future-entry": entry.OccurredAtUtc = ReviewClock.Now.AddSeconds(1); break;
            case "zero-price": entry.Price = 0; break;
        }
        f.Closed[0] = facts with { Fills = fills };
        var report = await Read();
        Assert.Null(report.Metrics.Net);
        Assert.Equal((1, 1), (report.Coverage.ClosedCandidates, report.Coverage.IncompleteExecutions));
    }

    [Fact]
    public async Task Gross_fees_are_subtracted_once_net_fees_never_twice_and_unsupported_fees_exclude()
    {
        var gross = f.Add(10);
        foreach (var fill in gross.Fills) fill.Fill.Fee = .25m;
        var net = f.Add(10);
        foreach (var fill in net.Fills)
        {
            fill.Fill.PnlBasis = ExecutionFacts.PnlNetOfFee;
            fill.Fill.Fee = .25m;
            fill.Fill.FeeToken = "OTHER";
        }
        var unknown = f.Add(10);
        unknown.Fills[0].Fill.Fee = 1;
        unknown.Fills[0].Fill.FeeToken = "OTHER";
        var report = await Read();
        Assert.Equal(("19.5", 2, 1), (report.Metrics.Net, report.Metrics.Count, report.Coverage.IncompleteFees));
    }

    [Fact]
    public async Task Listed_stablecoin_fees_pool_nominally_and_other_tokens_stay_unknown()
    {
        var usdg = f.Add(10);
        foreach (var fill in usdg.Fills) { fill.Fill.Fee = .25m; fill.Fill.FeeToken = "USDG"; }
        var mixed = f.Add(10);
        mixed.Fills[0].Fill.Fee = .1m; mixed.Fills[0].Fill.FeeToken = "USDC";
        mixed.Fills[1].Fill.Fee = .2m; mixed.Fills[1].Fill.FeeToken = "USDG";
        var unknown = f.Add(10);
        unknown.Fills[0].Fill.Fee = 1; unknown.Fills[0].Fill.FeeToken = "usdg";
        var report = await Read();
        Assert.Equal(("19.2", 2, 1), (report.Metrics.Net, report.Metrics.Count, report.Coverage.IncompleteFees));
    }

    [Fact]
    public async Task R_is_unknown_without_a_pre_entry_revision_or_complete_protective_stops()
    {
        var retrospective = f.Add(10);
        f.Closed[0] = retrospective with { Revisions = [] };
        var plan = PlayDocuments.Read(retrospective.Play.Plan);
        var entry = plan.Entries[0];
        f.Add(10, customPlan: plan with { Entries = [entry with { Share = "50" },
            entry with { Id = "second", Share = "50", Stops = [] }] });
        f.Add(10, customPlan: plan with { Entries = [entry with { Stops = [new PlanExit("s", "price", "105", "100")] }] });
        f.Add(10);
        var report = await Read();
        Assert.Equal((4, 1), (report.Metrics.Count, report.Metrics.RiskCount));
        Assert.Equal(3, report.Plays.Count(p => p.RMultiple is null));
    }

    [Fact]
    public async Task Current_membership_unassigned_disabled_and_exact_asset_keys_are_respected()
    {
        var portfolio = new Portfolio(Guid.NewGuid(), f.Owner, "Swing");
        f.Portfolios.Add(portfolio);
        var first = f.Account("First", portfolio.Id);
        var second = f.Account("Second");
        var disabled = f.Account("Disabled");
        disabled.UpdateSettings("Disabled", null, false);
        f.Add(10, account: first);
        f.Add(20, account: second, instrument: "kPEPE");
        f.Add(30, account: disabled);
        Assert.Equal("30", (await Read()).Metrics.Net);
        Assert.Equal("20", (await Read(new(Portfolio: "unassigned"))).Metrics.Net);
        Assert.Null((await Read(new(Portfolio: portfolio.Id.ToString(), AccountId: second.Id))).Metrics.Net);
        Assert.Null((await Read(new(Instrument: "1000PEPE"))).Metrics.Net);
        Assert.Equal("20", (await Read(new(Instrument: "kPEPE"))).Metrics.Net);
        Assert.Equal("10", (await Read(new(Portfolio: portfolio.Id.ToString().ToUpperInvariant()))).Metrics.Net);
        first.UpdateSettings("First", null, true);
        Assert.Null((await Read(new(Portfolio: portfolio.Id.ToString()))).Metrics.Net);
        Assert.Equal(2, (await Read()).Options.Accounts.Count);
    }

    [Fact]
    public async Task Pagination_and_bucketing_never_truncate_aggregate_totals()
    {
        for (var i = 0; i < 105; i++) f.Add(1, ReviewClock.Now.AddDays(-i * 20));
        var first = await Read(new("all"));
        var second = await Read(new("all", Offset: 50));
        var third = await Read(new("all", Offset: 100));
        Assert.Equal(("105", 105, 50), (first.Metrics.Net, first.Metrics.Count, first.NextOffset));
        Assert.Equal((50, 50, 5), (first.Plays.Count, second.Plays.Count, third.Plays.Count));
        Assert.Null(third.NextOffset);
        Assert.Empty(first.Plays.Select(p => p.Id).Intersect(second.Plays.Select(p => p.Id)));
        Assert.InRange(first.Series.Count, 1, 366);
        Assert.True(first.BucketDays > 1);
        Assert.Equal("105", first.Series.Last().Cumulative);
        Assert.Equal(105, first.Series.Sum(p => p.Count));
    }

    [Theory]
    [InlineData("forever", "all", 0, null)]
    [InlineData("all", "not-an-id", 0, null)]
    [InlineData("all", "all", -1, null)]
    [InlineData("all", "all", 1000001, null)]
    [InlineData("all", "all", 0, " BTC")]
    public async Task Invalid_queries_fail_before_store_access(string period, string portfolio, int offset, string? instrument)
    {
        await Assert.ThrowsAsync<ProblemException>(() => Read(new(period, portfolio, Instrument: instrument, Offset: offset)));
        Assert.Null(f.LastQuery);
    }
}
