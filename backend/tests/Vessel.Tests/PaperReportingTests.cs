using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PaperReportingTests
{
    internal static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid owner = Guid.NewGuid();

    internal static (Account Account, Portfolio Portfolio, ClosedPlayFacts Facts) Paper(Guid owner, decimal net = 20,
        string state = "ready", string instrument = "BTC", string[]? issues = null, decimal quantity = 1, bool revise = false)
    {
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper account", "paper", 4000);
        account.Configure(portfolio.Id, null, null);
        var plan = new PlayPlanDocument("long", "margin", "100", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "95", "100")], [])], new("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", instrument),
            InstrumentSource.Venue, PlayDocuments.Serialize(plan), At.AddHours(-3));
        var revision = play.MarkPlanned(At.AddHours(-3)).Revision;
        play.ApplyPaperExecutionState(true, false, At.AddHours(-2));
        List<PlayPlanRevision> revisions = [revision];
        if (revise)
        {
            var revised = plan with { Entries = [new("e", "Entry", "#ffffff", "100", "100", [new("s", "price", "80", "100")], [])] };
            revisions.Add(play.ChangePlan(PlayDocuments.Serialize(revised), "Later risk", At.AddHours(-1))!);
        }
        play.ApplyPaperExecutionState(true, true, At.AddHours(1));
        PaperExecution Execution(string role, DateTimeOffset at) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = account.Id, PlayId = play.Id, EntryId = "e",
            LevelId = role == "entry" ? null : "target", Role = role, Price = role == "entry" ? 100 : 100 + net,
            Quantity = quantity, PnlUsd = role == "entry" ? 0 : net * quantity, MarginUsd = role == "entry" ? 100 : -100,
            PlanVersion = revision.Number, OccurredAtUtc = at, CandleOpenAtUtc = at.AddMinutes(-1), CandleCloseAtUtc = at
        };
        var paper = new PaperPlayFacts(play, revisions, [Execution("entry", At.AddHours(-2)), Execution("target", At)], state, issues ?? []);
        return (account, portfolio, new(play, revisions, []) { Paper = paper });
    }

    private static ActivityFacts Facts(params (Account Account, Portfolio Portfolio, ClosedPlayFacts Facts)[] papers) =>
        new(papers.Select(p => p.Account).ToList(), [], [], new Dictionary<Guid, Guid>())
        { PaperPlays = papers.Select(p => p.Facts.Paper!).ToList() };

    [Fact]
    public async Task Equivalent_real_and_paper_outcomes_share_review_metric_formulas_but_never_sources()
    {
        var fixture = new ReviewFixture();
        fixture.Add(20); fixture.Add(-10); fixture.Add(0);
        var real = await fixture.Service.GetAsync(new(Period: "all"), default);
        foreach (var net in new[] { 20m, -10m, 0m })
        {
            var paper = Paper(fixture.Owner, net);
            fixture.Accounts.Add(paper.Account); fixture.Portfolios.Add(paper.Portfolio); fixture.Closed.Add(paper.Facts);
        }
        var paperResult = await new ReviewService(fixture, new Clock()).GetAsync(new(Period: "all", TradingMode: "paper"), default);
        Assert.Equal((real.Metrics.Count, real.Metrics.Wins, real.Metrics.Losses, real.Metrics.Net, real.Metrics.Payoff),
            (paperResult.Metrics.Count, paperResult.Metrics.Wins, paperResult.Metrics.Losses, paperResult.Metrics.Net, paperResult.Metrics.Payoff));
        Assert.All(paperResult.Plays, p => { Assert.Equal("paper", p.ExecutionSource); Assert.Equal("paper-simulation", p.Local!.Basis); });
        Assert.Equal("paper", paperResult.TradingMode);
        Assert.All(paperResult.Options.Accounts, a => Assert.Contains("Paper", a.Name));
        Assert.All((await fixture.Service.GetAsync(new(Period: "all"), default)).Plays, p => Assert.Equal("imported", p.ExecutionSource));
    }

    [Fact]
    public async Task Paper_wins_never_suppress_real_activity_fallback_or_change_its_ladder()
    {
        var paper = Paper(owner, -50);
        var real = new Account(Guid.NewGuid(), owner, "hyperliquid", "Real");
        var workspace = new MemoryWorkspaceStore(owner);
        workspace.Accounts.AddRange([real, paper.Account]);
        ImportedFill Fill(bool entry) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = real.Id, ContractId = "BTC", SourceFillId = Guid.NewGuid().ToString(),
            ExternalOrderId = "", Side = entry ? "buy" : "sell", PositionEffect = entry ? "open" : "close", Direction = "",
            Price = entry ? 100 : 110, Quantity = 1, ClosedPnlUsd = entry ? 0 : 10, FeeToken = "USDC", TransactionHash = "",
            OccurredAtUtc = entry ? At.AddHours(-1) : At
        };
        var facts = Facts(paper) with { Accounts = [real, paper.Account], Fills = [Fill(true), Fill(false)] };
        var activity = new ActivityService(new ActivityRows(facts), workspace, new Clock());
        var store = new MemorySizingStore();
        var service = new SizingService(store, new CoreOwner(owner), new Clock(), TestUnitOfWork.None, activity);
        var before = await service.GetAsync(default);
        store.Closed.Add(paper.Facts);
        var after = await service.GetAsync(default);
        Assert.Equal("activity", before.Record.Source);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
        var simulated = await service.GetAsync(default, "paper");
        Assert.Equal(("paper", "paper", 1, 0, 1),
            (simulated.TradingMode, simulated.Record.Source, simulated.Record.ClosedPlays, simulated.Record.Wins, simulated.Record.Losses));
        Assert.Equal("-50", simulated.Record.Recent.Single().NetResultUsd);
        Assert.Null(simulated.Record.Recent.Single().OperationId);
    }

    [Fact]
    public void Activity_uses_typed_simulation_rows_without_fake_imports_or_manual_actuals()
    {
        var paper = Paper(owner);
        var result = ActivityService.Aggregate(Facts(paper), tradingMode: "paper");
        var row = Assert.Single(result.Operations);
        Assert.Equal(("paper", "paper", "20", 0, 2), (row.TradingMode, row.ExecutionSource, row.NetResultUsd, row.FillCount, row.PaperExecutionCount));
        Assert.Empty(row.Fills); Assert.Empty(result.Facts.Fills); Assert.Empty(result.Facts.Snapshots);
        Assert.Equal(2, row.PaperExecutions.Count);
        Assert.Empty(ActivityService.Aggregate(Facts(paper)).Operations);
    }

    [Fact]
    public async Task Activity_pages_and_filters_only_the_selected_mode_before_totals_facets_and_version()
    {
        var papers = Enumerable.Range(0, 30).Select(i => Paper(owner, instrument: "PAPER-" + i)).ToArray();
        var workspace = new MemoryWorkspaceStore(owner);
        workspace.Accounts.AddRange(papers.Select(p => p.Account)); workspace.Portfolios.AddRange(papers.Select(p => p.Portfolio));
        var realAccount = new Account(Guid.NewGuid(), owner, "hyperliquid", "Real empty account");
        workspace.Accounts.Add(realAccount);
        var facts = Facts(papers) with { Accounts = workspace.Accounts };
        var service = new ActivityService(new ActivityRows(facts), workspace, new Clock());
        var real = await service.PageAsync(new(), default);
        Assert.Equal((0, 0, 0), (real.TotalCount, real.ScopeCount, real.RetainedFillCount));
        Assert.Empty(real.Facets.Contracts);
        var second = await service.PageAsync(new(Page: "2", TradingMode: "paper"), default);
        Assert.Equal((2, 25, 30, 30, 5, 60),
            (second.Page, second.PageSize, second.TotalCount, second.ScopeCount, second.Operations.Count, second.PaperExecutionCount));
        Assert.Equal(30, second.Facets.Contracts.Count);
        var filtered = await service.PageAsync(new(Contract: "PAPER-5", Page: "2", TradingMode: "paper"), default);
        Assert.Equal((1, 1, 30, second.Version), (filtered.Page, filtered.TotalCount, filtered.ScopeCount, filtered.Version));
        Assert.Equal("PAPER-5", Assert.Single(filtered.Operations).ContractId);
        Assert.Equal(30, filtered.Facets.Contracts.Count);
        papers[0].Facts.Paper!.Executions[1].PnlUsd = 999;
        Assert.Equal(real.Version, (await service.PageAsync(new(), default)).Version);
        Assert.NotEqual(second.Version, (await service.PageAsync(new(TradingMode: "paper"), default)).Version);
    }

    [Fact]
    public void Simulated_peak_quantity_is_not_cumulative_entries_and_minute_ordering_stays_unknown()
    {
        var paper = Paper(owner);
        var first = paper.Facts.Paper!;
        PaperExecution Copy(PaperExecution source, DateTimeOffset at) => new()
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = paper.Account.Id, PlayId = paper.Facts.Play.Id,
            EntryId = "second", LevelId = source.LevelId, Role = source.Role, Price = source.Price,
            Quantity = source.Quantity, PnlUsd = source.PnlUsd, MarginUsd = source.MarginUsd, PlanVersion = source.PlanVersion,
            OccurredAtUtc = at, CandleOpenAtUtc = at.AddMinutes(-1), CandleCloseAtUtc = at
        };
        var executions = first.Executions.Concat([Copy(first.Executions[0], At.AddMinutes(1)), Copy(first.Executions[1], At.AddMinutes(2))]).ToList();
        var facts = Facts(paper) with { PaperPlays = [first with { Executions = executions }] };
        var known = Assert.Single(ActivityService.Aggregate(facts, tradingMode: "paper").Operations);
        Assert.Equal(("1", "2", "2", "0"), (known.Quantity, known.TotalEnteredQuantity, known.TotalExitedQuantity, known.OpenQuantity));
        executions[2].OccurredAtUtc = At; executions[2].CandleOpenAtUtc = At.AddMinutes(-1); executions[2].CandleCloseAtUtc = At;
        var unordered = Assert.Single(ActivityService.Aggregate(facts, tradingMode: "paper").Operations);
        Assert.Null(unordered.Quantity);
        Assert.Contains(unordered.Coverage, c => c.Contains("minute-bucketed"));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("ambiguous")]
    public async Task Unresolved_simulations_are_excluded_from_review_and_sizing_with_explanations(string state)
    {
        var paper = Paper(owner, 20, state, issues: ["Synthetic source window unresolved."]);
        var store = new MemorySizingStore(); store.Closed.Add(paper.Facts);
        var sizing = await new SizingService(store, new CoreOwner(owner), new Clock(), TestUnitOfWork.None).GetAsync(default, "paper");
        Assert.Equal(0, sizing.Record.ClosedPlays);
        var fixture = new ReviewFixture(); fixture.Accounts.Add(paper.Account); fixture.Portfolios.Add(paper.Portfolio); fixture.Closed.Add(paper.Facts);
        var review = new ReviewService(fixture, new Clock());
        var result = await review.GetAsync(new(Period: "all", TradingMode: "paper"), default);
        Assert.Equal(0, result.Metrics.Count);
        Assert.Equal(1, result.Coverage.IncompleteExecutions);
        var exclusions = await review.ExclusionsAsync(new(Period: "all", TradingMode: "paper"), default);
        Assert.Contains(Assert.Single(exclusions.Items).Reasons, r => r.Contains("Synthetic source"));
        var operation = Assert.Single(ActivityService.Aggregate(Facts(paper), tradingMode: "paper").Operations);
        Assert.Equal("incomplete", operation.Status); Assert.Null(operation.NetResultUsd);
    }

    [Fact]
    public void Initial_risk_uses_first_execution_revision_not_latest_or_detection_time()
    {
        var paper = Paper(owner, revise: true);
        var original = paper.Facts.Paper!;
        Assert.Equal(2, paper.Facts.Play.PlanRevision);
        var outcome = original.Outcome(At.AddDays(1), out _)!;
        Assert.Equal((At, 20m, 5m, 4m), (outcome.ClosedAtUtc, outcome.NetResult, outcome.PlannedRisk, outcome.RMultiple));
    }

    [Fact]
    public async Task Paper_setting_write_is_rejected_without_touching_shared_real_preference()
    {
        var store = new MemorySizingStore();
        var service = new SizingService(store, new CoreOwner(owner), new Clock(), TestUnitOfWork.None);
        var problem = await Assert.ThrowsAsync<ProblemException>(() => service.UpdateSettingsAsync(new("2"), default, "paper"));
        Assert.Equal(409, problem.StatusCode); Assert.Equal(0, store.Saves); Assert.Null(store.Settings);
    }

    [Fact]
    public void Disabled_paper_settings_do_not_erase_valid_history_and_cache_keys_have_mode()
    {
        var paper = Paper(owner, state: "disabled");
        Assert.NotNull(paper.Facts.Paper!.Outcome(At.AddDays(1), out _));
        var real = new ActivitySummaryKey(owner, 1, null, "*");
        Assert.NotEqual(real, real with { TradingMode = "paper" });
    }

    [Fact]
    public async Task Financial_endpoints_serialize_typed_paper_provenance_and_reject_invalid_modes_or_paper_setting_writes()
    {
        var paper = Paper(owner);
        var workspace = new MemoryWorkspaceStore(owner); workspace.Accounts.Add(paper.Account); workspace.Portfolios.Add(paper.Portfolio);
        var sizing = new MemorySizingStore(); sizing.Closed.Add(paper.Facts);
        var review = new ReviewFixture(); review.Accounts.Add(paper.Account); review.Portfolios.Add(paper.Portfolio); review.Closed.Add(paper.Facts);
        await using var factory = new CoreApiFactory(owner, store: workspace, configure: services =>
        {
            services.AddSingleton<IActivityStore>(new ActivityRows(Facts(paper)));
            services.AddSingleton<ISizingStore>(sizing); services.AddSingleton<IReviewStore>(review);
            services.AddSingleton<TimeProvider>(new Clock());
        });
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/review?tradingMode=paper")).StatusCode);
        using var client = factory.AuthorizedClient();
        var pageResponse = await client.GetAsync("/api/activity?tradingMode=paper");
        pageResponse.EnsureSuccessStatusCode(); Assert.True(pageResponse.Headers.CacheControl!.NoStore);
        var page = await pageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("paper", page.GetProperty("tradingMode").GetString());
        Assert.Equal(0, page.GetProperty("retainedFillCount").GetInt32());
        Assert.Equal(2, page.GetProperty("paperExecutionCount").GetInt32());
        var row = Assert.Single(page.GetProperty("operations").EnumerateArray());
        Assert.Equal("paper", row.GetProperty("executionSource").GetString());
        Assert.Equal("paper-simulation", row.GetProperty("pnlBasis").GetString());
        var expanded = await client.GetFromJsonAsync<JsonElement>(
            $"/api/activity/fills?accountId={paper.Account.Id}&operationId={paper.Facts.Play.Id}&tradingMode=paper");
        Assert.Equal(0, expanded.GetProperty("fills").GetArrayLength());
        Assert.Equal(2, expanded.GetProperty("paperExecutions").GetArrayLength());
        Assert.False(expanded.GetProperty("paperExecutions")[0].TryGetProperty("fill", out _));
        Assert.Equal("paper", (await client.GetFromJsonAsync<SizingDto>("/api/sizing?tradingMode=paper"))!.Record.Source);
        Assert.Equal("paper", (await client.GetFromJsonAsync<ReviewDto>("/api/review?period=all&tradingMode=paper"))!.Plays.Single().ExecutionSource);
        foreach (var path in new[] { "/api/activity", "/api/review", "/api/review/exclusions", "/api/sizing", "/api/overview/summary" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?tradingMode=Paper")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/sizing/settings?tradingMode=paper", new UpdateSizingSettingsRequest("2"))).StatusCode);
        Assert.Null(sizing.Settings); Assert.Equal(0, sizing.Saves);
    }

    [Theory]
    [InlineData("exit-before-entry")]
    [InlineData("over-close")]
    [InlineData("foreign-owner")]
    [InlineData("duplicate")]
    [InlineData("future")]
    [InlineData("same-candle")]
    public void Invalid_paper_execution_proof_is_never_scored(string invalid)
    {
        var paper = Paper(owner).Facts.Paper!;
        var entry = paper.Executions[0]; var exit = paper.Executions[1];
        if (invalid == "exit-before-entry") entry.OccurredAtUtc = At.AddMinutes(5);
        if (invalid == "over-close") exit.Quantity = 2;
        if (invalid == "foreign-owner") exit.OwnerId = Guid.NewGuid();
        if (invalid == "future") exit.OccurredAtUtc = At.AddDays(5);
        if (invalid == "same-candle")
        {
            exit.OccurredAtUtc = entry.OccurredAtUtc; exit.CandleOpenAtUtc = entry.CandleOpenAtUtc; exit.CandleCloseAtUtc = entry.CandleCloseAtUtc;
        }
        if (invalid == "duplicate") exit.Id = entry.Id;
        Assert.Null(paper.Outcome(At.AddDays(1), out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Opposing_stop_and_target_in_one_candle_cannot_become_a_scored_win()
    {
        var paper = Paper(owner).Facts.Paper!;
        var exit = paper.Executions[1]; exit.Quantity = .5m; exit.PnlUsd = 10; exit.MarginUsd = -50;
        var stop = new PaperExecution
        {
            Id = Guid.NewGuid(), OwnerId = owner, AccountId = paper.Play.AccountId, PlayId = paper.Play.Id,
            EntryId = exit.EntryId, LevelId = "stop", Role = "stop", Price = 95, Quantity = .5m, PnlUsd = -2.5m,
            MarginUsd = -50, PlanVersion = exit.PlanVersion, OccurredAtUtc = exit.OccurredAtUtc,
            CandleOpenAtUtc = exit.CandleOpenAtUtc, CandleCloseAtUtc = exit.CandleCloseAtUtc
        };
        Assert.Null((paper with { Executions = paper.Executions.Append(stop).ToList() }).Outcome(At.AddDays(1), out var problem));
        Assert.Contains("opposing", problem);
    }

    private sealed class ActivityRows(ActivityFacts facts) : IActivityStore
    {
        public Task<ActivityFacts> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null) => Task.FromResult(facts);
        public Task<IReadOnlyDictionary<Guid, string>> PlayTitlesAsync(IReadOnlyCollection<Guid> playIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At.AddDays(1); }
}
