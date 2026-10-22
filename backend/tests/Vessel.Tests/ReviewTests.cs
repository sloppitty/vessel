using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

internal sealed class ReviewClock : TimeProvider
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class ReviewFixture : IReviewStore
{
    public Guid Owner { get; } = Guid.NewGuid();
    public List<Account> Accounts { get; } = [];
    public List<Portfolio> Portfolios { get; } = [];
    public List<ClosedPlayFacts> Closed { get; } = [];
    public List<EndedPlayFacts> Cancelled { get; } = [];
    public List<UnresolvedPlayFacts> Unresolved { get; } = [];
    /// <summary>With the Hyperliquid descriptor, so Play-local results know the venue's quote asset.</summary>
    public ReviewService Service => new(this, new ReviewClock(), TestVenues.With());
    public ReviewQuery? LastQuery { get; private set; }
    public Task<IReadOnlyList<string>> MistakeTagsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>The owner's reporting setting (Phase 2, item 2); null reads as UTC and Monday.</summary>
    public Vessel.Domain.Reporting.ReportingSettings? Reporting { get; set; }
    public Task<Vessel.Domain.Reporting.ReportingSettings?> ReportingAsync(CancellationToken ct) => Task.FromResult(Reporting);
    public Task<ReviewFacts> ReadAsync(ReviewQuery query, DateTimeOffset asOf, CancellationToken ct)
    {
        LastQuery = query;
        return Task.FromResult(new ReviewFacts(Portfolios.ToList(), Accounts.ToList(), Closed.ToList(), 0, 0, null)
            { CancelledPlays = Cancelled.ToList(), UnresolvedPlays = Unresolved.ToList() }); // A snapshot, like the store
    }
    public Account Account(string name = "Main", Guid? portfolio = null)
    {
        var account = new Account(Guid.NewGuid(), Owner, "hyperliquid", name);
        account.Configure(portfolio, null, null);
        Accounts.Add(account);
        return account;
    }
    public ClosedPlayFacts Add(decimal net, DateTimeOffset? exit = null, Account? account = null,
        string instrument = "BTC", decimal entryPrice = 100, bool risk = true, PlayPlanDocument? customPlan = null)
    {
        account ??= Accounts.FirstOrDefault() ?? Account();
        var at = exit ?? ReviewClock.Now.AddHours(-1);
        var plan = customPlan ?? new PlayPlanDocument("long", "margin", "100", "1", null,
            [new PlanEntry("e", "Entry", "#ffffff", "100", "100",
                risk ? [new PlanExit("s", "price", "95", "100")] : [], [])], new PlanNotes("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument(account.VenueId, instrument),
            InstrumentSource.Venue, PlayDocuments.Serialize(plan), at.AddDays(-2));
        var revision = play.MarkPlanned(at.AddDays(-2)).Revision;
        play.MarkOpen("filled", at.AddDays(-1));
        play.MarkClosed("synced later", ReviewClock.Now);
        ImportedFill Fill(bool opening) => new()
        {
            Id = Guid.NewGuid(), OwnerId = Owner, AccountId = account.Id, ContractId = instrument,
            SourceFillId = Guid.NewGuid().ToString(), ExternalOrderId = Guid.NewGuid().ToString(), TransactionHash = "",
            Side = opening ? ExecutionFacts.Buy : ExecutionFacts.Sell, Direction = "",
            PositionEffect = opening ? ExecutionFacts.Open : ExecutionFacts.Close,
            Quantity = 1, Price = opening ? entryPrice : entryPrice + net,
            ClosedPnlUsd = opening ? 0 : net, Fee = 0, FeeToken = "USDC",
            OccurredAtUtc = opening ? at.AddDays(-1) : at,
        };
        var facts = new ClosedPlayFacts(play, [revision],
            [new LinkedFill(OrderLinkRole.Entry, Fill(true)), new LinkedFill(OrderLinkRole.Exit, Fill(false))]);
        Closed.Add(facts);
        return facts;
    }
}

public sealed class ReviewTests
{
    private readonly ReviewFixture f = new();
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

    [Fact]
    public async Task Endpoint_requires_auth_validates_filters_and_serializes_exact_decimal_strings()
    {
        f.Add(.1234567890123456789m);
        await using var factory = new CoreApiFactory(f.Owner, configure: services =>
        {
            services.AddSingleton<IReviewStore>(f);
            services.AddSingleton<TimeProvider>(new ReviewClock());
        });
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/review")).StatusCode);
        using var client = factory.AuthorizedClient();
        // The drill-downs validate the report's filters too, positions included.
        foreach (var route in new[] { "/api/review?", "/api/review/exclusions?", "/api/review/process?lane=unanswered&" })
        {
            foreach (var query in new[] { "period=bad", "accountId=bad", "offset=-1", "portfolio=bad", "positions=bad", "positions=auto&minPlays=-1" })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + query)).StatusCode);
            (await client.GetAsync(route + "positions=exclude&minPlays=1000")).EnsureSuccessStatusCode();
        }
        var response = await client.GetAsync("/api/review");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var report = await response.Content.ReadFromJsonAsync<ReviewDto>();
        Assert.Equal("0.1234567890123456789", report!.Metrics.Net);
    }

    [PostgresFact]
    public async Task Store_is_owner_scoped_joins_real_links_and_counts_only_unassigned_scoped_fills()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var foreign = new ReviewFixture();
        var owned = f.Add(10);
        var other = foreign.Add(99);
        var disabledAccount = f.Account("Disabled");
        var disabled = f.Add(30, account: disabledAccount);
        disabledAccount.UpdateSettings("Disabled", null, false);
        foreach (var fixture in new[] { f, foreign })
        {
            await using var db = database.Context(fixture.Owner);
            db.Accounts.AddRange(fixture.Accounts);
            foreach (var facts in fixture.Closed)
            {
                db.Plays.Add(facts.Play);
                db.PlanRevisions.AddRange(facts.Revisions);
                foreach (var linked in facts.Fills)
                {
                    db.Fills.Add(linked.Fill);
                    // Links reference retained orders; each order here executed exactly its one fill.
                    var order = TestOrders.For(facts.Play, linked.Fill.ExternalOrderId, linked.Fill.Side, size: linked.Fill.Quantity);
                    db.Orders.Add(order);
                    db.OrderLinks.Add(new PlayOrderLink(facts.Play, order, linked.Role,
                        linked.Role == OrderLinkRole.Entry ? "e" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
                }
            }
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(f.Owner))
        {
            db.Fills.Add(new ImportedFill
            {
                Id = Guid.NewGuid(), OwnerId = f.Owner, AccountId = owned.Play.AccountId, ContractId = "ETH",
                SourceFillId = "unlinked", ExternalOrderId = "unlinked", Side = ExecutionFacts.Buy, Direction = "",
                Quantity = 1, Price = 100, FeeToken = "USDC", TransactionHash = "", OccurredAtUtc = ReviewClock.Now,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = database.Context(f.Owner))
        {
            var service = new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock());
            var report = await service.GetAsync(new(), default);
            Assert.Equal(("10", 1, 1), (report.Metrics.Net, report.Options.Accounts.Count, report.Coverage.UnassignedFills));
            Assert.Equal(0, (await service.GetAsync(new(Instrument: "BTC"), default)).Coverage.UnassignedFills);
            Assert.Null((await service.GetAsync(new(AccountId: other.Play.AccountId), default)).Metrics.Net);
            Assert.DoesNotContain(report.Plays, p => p.Id == disabled.Play.Id);
        }
        await using (var db = database.Context(foreign.Owner))
        {
            var report = await new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock()).GetAsync(new(), default);
            Assert.Equal(("99", 0), (report.Metrics.Net, report.Coverage.UnassignedFills));
        }
        await using (var db = database.Context(f.Owner))
        {
            ImportedOrder Order(string id, decimal size) => new()
            {
                Id = Guid.NewGuid(), OwnerId = f.Owner, AccountId = owned.Play.AccountId, ContractId = "BTC",
                ExternalOrderId = id, Side = ExecutionFacts.Buy, OrderType = "Limit", LimitPrice = 100,
                OriginalSize = size, RemainingSize = 0, PlacedAtUtc = ReviewClock.Now.AddDays(-2),
                Status = "filled", VenueStatus = "filled", StatusAtUtc = ReviewClock.Now, ObservedAtUtc = ReviewClock.Now,
            };
            var known = await db.Orders.SingleAsync(o => o.AccountId == owned.Play.AccountId && o.ExternalOrderId == owned.Fills[0].Fill.ExternalOrderId);
            known.OriginalSize = 2;
            await db.SaveChangesAsync();
            var service = new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), new ReviewClock());
            var partial = await service.GetAsync(new(), default);
            Assert.Equal((0, 1), (partial.Metrics.Count, partial.Coverage.IncompleteExecutions));
            known.OriginalSize = 1;
            var missing = Order("executed-with-no-retained-fills", 1);
            db.Orders.Add(missing);
            db.OrderLinks.Add(new PlayOrderLink(owned.Play, missing, OrderLinkRole.Entry, "e", null,
                OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
            await db.SaveChangesAsync();
            var absent = await service.GetAsync(new(), default);
            Assert.Equal((0, 1), (absent.Metrics.Count, absent.Coverage.IncompleteExecutions));
            // Zero remaining does not establish a fill for rejected/cancelled/triggered orders.
            foreach (var status in new[] { "rejected", "canceled", "triggered", "other", "open" })
            {
                missing.Status = status;
                await db.SaveChangesAsync();
                Assert.Equal(1, (await service.GetAsync(new(), default)).Metrics.Count);
            }
            missing.Status = "filled"; missing.IsPositionTpsl = true;
            await db.SaveChangesAsync();
            Assert.Equal(1, (await service.GetAsync(new(), default)).Metrics.Count);
        }
    }
}
