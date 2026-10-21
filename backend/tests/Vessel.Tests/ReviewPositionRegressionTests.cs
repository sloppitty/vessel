using Microsoft.EntityFrameworkCore;
using Vessel.Application.Activity;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Uncommitted independent reviewer-A reproductions and controls for 6f261c2.</summary>
public sealed class ReviewPositionRegressionTests
{
    private sealed class ActivityFactsStore(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) =>
            Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    private sealed class AfterReviewRead(IReviewStore inner, Func<Task> commit) : IReviewStore
    {
        private bool committed;
        public async Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
        {
            var facts = await inner.ReadAsync(query, asOf, ct);
            // Deterministic interleaving of a second request, once, after ReviewStore's first snapshot commits.
            if (!committed) { committed = true; await commit(); }
            return facts;
        }
    }

    private static ReviewService Service(ReviewFixture f, ActivityFacts facts, MemorySizingStore? sizing = null) =>
        new(f, new ReviewClock(), TestVenues.With(), new ActivityReviewPositions(
            new ActivityService(new ActivityFactsStore(facts), new MemoryWorkspaceStore(f.Owner), new ReviewClock()),
            sizing ?? new MemorySizingStore()));

    private static ImportedFill Fill(Account account, int minute, string side = "buy", string effect = "open",
        decimal pnl = 0, string contract = "BTC", decimal fee = 0, string token = "USDC") => new()
    {
        Id = Guid.NewGuid(), OwnerId = account.OwnerId, AccountId = account.Id, ContractId = contract,
        Side = side, PositionEffect = effect, Direction = "", Price = 100, Quantity = 1,
        ClosedPnlUsd = pnl, Fee = fee, FeeToken = token, FeeBasis = "reported", PnlBasis = "gross",
        OccurredAtUtc = ReviewClock.Now.AddDays(-2).AddMinutes(minute),
        ExternalOrderId = Guid.NewGuid().ToString(), SourceFillId = Guid.NewGuid().ToString(), TransactionHash = ""
    };

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

    [PostgresFact]
    public async Task Retrospective_conversion_between_reads_must_not_erase_the_result()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var f = new ReviewFixture();
        var account = f.Account();
        var template = f.Add(10);
        var orders = template.Fills.Select(l => TestOrders.For(account, "BTC", l.Fill.ExternalOrderId, l.Fill.Side)).ToList();
        await using (var seed = database.Context(f.Owner))
        {
            seed.Accounts.Add(account);
            seed.Fills.AddRange(template.Fills.Select(l => l.Fill));
            seed.Orders.AddRange(orders);
            await seed.SaveChangesAsync(); // The complete operation is initially unassigned, with no saved Play.
        }

        await using var read = database.Context(f.Owner);
        var sizing = new SizingStore(read, TestUnitOfWork.For(read));
        var store = new ReviewStore(read, sizing);
        var source = new ActivityReviewPositions(ActivityTestSupport.Activity(read, new ReviewClock()), sizing);
        var before = await new ReviewService(store, new ReviewClock(), TestVenues.With(), source)
            .GetAsync(new(Positions: "include"), default);
        Assert.Equal(("10", 1, 1), (before.Metrics.Net, before.Metrics.Count, before.Metrics.Positions));

        var interleaved = new AfterReviewRead(store, async () =>
        {
            await using var write = database.Context(f.Owner);
            var emptyPlan = new PlayPlanDocument("long", "margin", "", "0", null, [], new PlanNotes("", "", "", ""));
            var retrospective = Play.Retrospective(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, "BTC"),
                PlayDocuments.Serialize(emptyPlan), template.Fills[1].Fill.OccurredAtUtc, ReviewClock.Now, "Reviewed BTC");
            write.Plays.Add(retrospective);
            for (var i = 0; i < orders.Count; i++)
                write.OrderLinks.Add(new PlayOrderLink(retrospective, orders[i], template.Fills[i].Role,
                    i == 0 ? "retrospective" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
            await write.SaveChangesAsync(); // Atomic conversion, as another request could commit.
        });
        var during = await new ReviewService(interleaved, new ReviewClock(), TestVenues.With(), source)
            .GetAsync(new(Positions: "include"), default);
        var after = await new ReviewService(store, new ReviewClock(), TestVenues.With(), source)
            .GetAsync(new(Positions: "include"), default);
        Assert.Equal(("10", 1, 0), (after.Metrics.Net, after.Metrics.Count, after.Metrics.Positions));

        // Before and after both contain the same $10 result. A coherent snapshot cannot contain zero results.
        Assert.Equal(("10", 1), (during.Metrics.Net, during.Metrics.Count));
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

    [PostgresFact]
    public async Task Check_Real_stores_exclude_foreign_and_disabled_accounts_and_intersect_portfolio()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var f = new ReviewFixture();
        var foreign = new ReviewFixture();
        var portfolio = new Portfolio(Guid.NewGuid(), f.Owner, "Selected");
        var selected = f.Account("Selected", portfolio.Id);
        var other = f.Account("Unassigned");
        var disabled = f.Account("Disabled");
        disabled.UpdateSettings(disabled.Name, null, false);
        foreign.Account("Foreign");
        foreach (var fixture in new[] { f, foreign })
        {
            await using var seed = database.Context(fixture.Owner);
            if (fixture == f) seed.Portfolios.Add(portfolio);
            seed.Accounts.AddRange(fixture.Accounts);
            foreach (var account in fixture.Accounts)
                seed.Fills.AddRange(Fill(account, 0), Fill(account, 1, "sell", "close", pnl: 10));
            await seed.SaveChangesAsync();
        }
        await using var db = database.Context(f.Owner);
        var sizing = new SizingStore(db, TestUnitOfWork.For(db));
        var service = new ReviewService(new ReviewStore(db, sizing), new ReviewClock(), TestVenues.With(),
            new ActivityReviewPositions(ActivityTestSupport.Activity(db, new ReviewClock()), sizing));
        Assert.Equal(("20", 2), ((await service.GetAsync(new(Positions: "include"), default)).Metrics.Net,
            (await service.GetAsync(new(Positions: "include"), default)).Metrics.Count));
        var scoped = await service.GetAsync(new(Portfolio: portfolio.Id.ToString(), Positions: "include"), default);
        Assert.Equal(("10", selected.Id), (scoped.Metrics.Net, Assert.Single(scoped.Plays).AccountId));
        foreach (var account in new[] { other, disabled, foreign.Accounts[0] })
        {
            var empty = await service.GetAsync(new(Portfolio: portfolio.Id.ToString(), AccountId: account.Id, Positions: "include"), default);
            Assert.Null(empty.Metrics.Net);
        }
        Assert.Equal("10", (await service.GetAsync(new(Portfolio: "unassigned", Positions: "include"), default)).Metrics.Net);
    }
}
