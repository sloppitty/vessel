using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Domain.Accounts;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// October 20, 2026: Review can add closed imported positions that no Play claims. Auto adds them while the selection has
/// fewer scored Plays than the owner's threshold; a Play weighs twice a position in rate metrics, never in dollar totals.
/// </summary>
public sealed class ReviewPositionTests
{
    private readonly ReviewFixture f = new();
    private readonly PositionSource source = new();

    private sealed class PositionSource : IReviewPositionSource
    {
        public List<ReviewPosition> Items { get; } = [];
        public int Reads { get; private set; }
        public Task<IReadOnlyList<ReviewPosition>> ReadAsync(IReadOnlySet<Guid> accountIds, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<ReviewPosition>>(Items.Where(p => accountIds.Contains(p.AccountId)).ToList());
        }
    }

    private ReviewService Service => new(f, new ReviewClock(), TestVenues.With(), source);
    private Task<ReviewDto> Read(ReviewQuery query) => Service.GetAsync(query, default);

    private ReviewPosition Position(decimal net, decimal notional = 100, Account? account = null, string instrument = "BTC",
        DateTimeOffset? closed = null, bool scorable = true)
    {
        account ??= f.Accounts.FirstOrDefault() ?? f.Account();
        var at = closed ?? ReviewClock.Now.AddHours(-2);
        var position = new ReviewPosition($"{Guid.NewGuid()}:long", account.Id, instrument, "long", at,
            scorable ? new PlayOutcome(Guid.NewGuid(), at, net, notional, null, true) : null);
        source.Items.Add(position);
        return position;
    }

    [Fact]
    public async Task Exclude_never_reads_positions_and_reports_plays_only()
    {
        f.Add(10);
        Position(-5);
        var report = await Read(new(Positions: "exclude"));
        Assert.Equal(0, source.Reads);
        Assert.Equal((1, 0), (report.Metrics.Count, report.Metrics.Positions));
        Assert.Equal(("exclude", false, 1), (report.Positions!.Mode, report.Positions.Included, report.Positions.Plays));
        Assert.Null(report.Positions.Positions);
        Assert.DoesNotContain("Imported positions are included", report.Coverage.Notice);
    }

    [Fact]
    public async Task Auto_includes_positions_below_the_threshold_and_not_at_it()
    {
        f.Account();
        Position(4);
        for (var i = 0; i < 4; i++) f.Add(1);
        var below = await Read(new(Positions: "auto", MinPlays: 5));
        Assert.True(below.Positions!.Included);
        Assert.Equal((5, 1, 4, 1), (below.Metrics.Count, below.Metrics.Positions, below.Positions.Plays, below.Positions.Positions));

        f.Add(1);
        var at = await Read(new(Positions: "auto", MinPlays: 5));
        Assert.False(at.Positions!.Included);
        Assert.Equal((5, 0), (at.Metrics.Count, at.Metrics.Positions));

        Assert.False((await Read(new(Positions: "auto", MinPlays: 0))).Positions!.Included);
        Assert.True((await Read(new(Positions: "include", MinPlays: 0))).Positions!.Included);
    }

    [Fact]
    public async Task With_no_plays_positions_become_the_report()
    {
        Position(10);
        Position(-5, closed: ReviewClock.Now.AddHours(-3));
        Position(0, scorable: false);
        var report = await Read(new(Positions: "auto"));
        Assert.Equal((2, 1, 1, 2), (report.Metrics.Count, report.Metrics.Wins, report.Metrics.Losses, report.Metrics.Positions));
        Assert.Equal(("5", "0.5"), (report.Metrics.Net, report.Metrics.Batting));
        Assert.Equal((0, 0, 0), (report.Metrics.RiskCount, report.Metrics.Reviewed, report.Metrics.SignDiffers));
        Assert.Equal(1, report.Positions!.Omitted);
        Assert.Equal((2, 2), (report.Periods.Single(p => p.Id == "month").Count, report.Periods.Single(p => p.Id == "month").Positions));
        Assert.Contains("Imported positions are included", report.Coverage.Notice);
        var row = report.Plays[0];
        Assert.Null(row.Id);
        Assert.Equal(("position", "BTC Long", "10"), (row.Origin, row.Title, row.Net));
        Assert.NotNull(row.OperationId);
        Assert.Null(row.RMultiple); Assert.Null(row.Local); Assert.False(row.Reviewed);
        Assert.Single(report.Groups.Assets);
    }

    [Fact]
    public async Task Plays_weigh_twice_a_position_in_rates_but_not_in_dollars()
    {
        f.Add(10, risk: false);                       // Play: +10 on 100 notional, +10%
        Position(-10, closed: ReviewClock.Now.AddHours(-3)); // position: -10 on 100 notional, -10%
        Position(-20, notional: 200, closed: ReviewClock.Now.AddHours(-4)); // position: -20 on 200, -10%
        var m = (await Read(new(Positions: "include"))).Metrics;
        Assert.Equal((3, 1, 2), (m.Count, m.Wins, m.Losses));
        Assert.Equal("-20", m.Net);                    // 10 − 10 − 20, unweighted
        Assert.Equal("0.5", m.Batting);                // 2 / (2 + 1 + 1)
        Assert.Equal("0.3333333333333333333333333333", m.ProfitFactor); // 10 / 30, unweighted
        Assert.Equal("-2.5", m.Expectancy);            // (2×10 − 10 − 20) / 4
        Assert.Equal(("15", "10", "1"), (m.AverageLossUsd, m.AverageLoss, m.Payoff));
        Assert.Equal("30", m.Drawdown);
        Assert.Equal("2", (await Read(new(Positions: "include"))).Positions!.PlayWeight);
    }

    [Fact]
    public async Task Positions_follow_scope_instrument_and_period_filters()
    {
        var main = f.Account("Main");
        var other = f.Account("Other");
        Position(1, account: main, instrument: "BTC");
        Position(2, account: main, instrument: "ETH");
        Position(3, account: other, instrument: "BTC");
        Position(4, account: main, closed: ReviewClock.Now.AddDays(-40));
        var scoped = await Read(new(AccountId: main.Id, Instrument: "BTC", Positions: "include"));
        Assert.Equal(("1", 1), (scoped.Metrics.Net, scoped.Metrics.Count));
        Assert.Equal(2, (await Read(new(Period: "all", AccountId: main.Id, Instrument: "BTC", Positions: "include"))).Metrics.Count);
        Assert.Contains("ETH", scoped.Options.Instruments);
    }

    [Theory]
    [InlineData("sometimes", 5)]
    [InlineData("auto", -1)]
    [InlineData("auto", 1001)]
    public async Task Invalid_position_modes_and_thresholds_are_rejected(string mode, int minPlays)
    {
        var error = await Assert.ThrowsAsync<ProblemException>(() => Read(new(Positions: mode, MinPlays: minPlays)));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Activity_source_skips_positions_whose_fills_belong_to_a_play()
    {
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "risex", "Venue");
        var start = ReviewClock.Now.AddDays(-1);
        var fills = new List<ImportedFill>();
        ImportedFill Fill(int minute, string side, string effect, decimal price, decimal pnl) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, ContractId = "BTC", Side = side, PositionEffect = effect,
            Direction = "fixture", Price = price, Quantity = 1, ClosedPnlUsd = pnl, Fee = 0, FeeToken = "USDC", PnlBasis = "gross",
            FeeBasis = "reported", OccurredAtUtc = start.AddMinutes(minute), ExternalOrderId = minute.ToString(),
            SourceFillId = Guid.NewGuid().ToString(), TransactionHash = ""
        };
        fills.AddRange([Fill(0, "buy", "open", 100, 0), Fill(10, "sell", "close", 110, 10)]);
        fills.AddRange([Fill(100, "buy", "open", 100, 0), Fill(110, "sell", "close", 95, -5)]);
        fills.AddRange([Fill(200, "buy", "open", 100, 0)]); // no exit: an open position, never listed
        var playId = Guid.NewGuid();
        var claimed = new Dictionary<Guid, Guid> { [fills[2].Id] = playId, [fills[3].Id] = playId };
        var workspace = new MemoryWorkspaceStore(owner);
        workspace.Accounts.Add(account);
        var activity = new ActivityService(new Store(new ActivityFacts([account], fills, [], claimed)), workspace, TimeProvider.System);
        var positions = await new ActivityReviewPositions(activity, new MemorySizingStore()).ReadAsync(new HashSet<Guid> { account.Id }, default);
        var position = Assert.Single(positions);
        Assert.Equal((10m, "long"), (position.Outcome!.NetResult, position.Direction));
        Assert.Empty(await new ActivityReviewPositions(activity, new MemorySizingStore()).ReadAsync(new HashSet<Guid>(), default));
    }

    private sealed class Store(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
    }
}
