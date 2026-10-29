using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Common;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

[CollectionDefinition("Paper corrective PostgreSQL", DisableParallelization = true)]
public sealed class PaperCorrectivePostgresCollection;

[Collection("Paper corrective PostgreSQL")]
public sealed class PaperCorrectivePostgresTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstOpen = new(2026, 10, 20, 12, 1, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PlanEntry Entry(string id, string share, string price, PlanExit[] stops, PlanExit[] targets) =>
        new(id, id, "#ffffff", share, price, stops, targets);
    private static PlayPlanDocument Plan(string sizingMode, string size, string leverage, params PlanEntry[] entries) =>
        new("long", sizingMode, size, leverage, null, entries, new("", "", "", ""));
    private static PaperCandle Candle(int minute, decimal value, decimal low, decimal high) =>
        new(FirstOpen.AddMinutes(minute), FirstOpen.AddMinutes(minute + 1), value, high, low, value);

    // ------------------------------------------------------------------ service-level harness (real PlayService)

    private sealed class Engine(CoreDatabase database, Guid owner, Guid accountId) : IAsyncDisposable
    {
        public CoreDatabase Db { get; } = database;
        public Guid Owner { get; } = owner;
        public Guid AccountId { get; } = accountId;
        public Clock Clock { get; } = new();

        public PaperTradingService Paper(VesselDbContext db) => new(new PaperStore(db, TestUnitOfWork.For(db)),
            TestUnitOfWork.For(db), new ConfiguredOwnerDirectory(db.CurrentOwnerId), TestVenues.With(), Clock);
        public PlayService Plays(VesselDbContext db) => new(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(),
            Clock, TestUnitOfWork.For(db), Paper(db));

        public static async Task<Engine> CreateAsync(decimal capital = 4000, int interval = 1)
        {
            var db = await CoreDatabase.CreateAsync();
            var owner = Guid.NewGuid();
            await using var context = db.Context(owner);
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", capital);
            account.Configure(portfolio.Id, null, null);
            context.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, capital, At),
                new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = interval });
            await context.SaveChangesAsync();
            return new(db, owner, account.Id);
        }

        /// <summary>Production path: PlayService.CreateAsync then ChangeStatusAsync("planned") (which arms).</summary>
        public async Task<Guid> PlanAsync(PlayPlanDocument plan, string instrument)
        {
            Guid id;
            await using (var db = Db.Context(Owner))
                id = (await Plays(db).CreateAsync(new CreatePlayRequest(AccountId, instrument, "venue", "Probe " + instrument, plan),
                    default, "paper")).Summary.Id;
            await StatusAsync(id, "planned");
            return id;
        }

        public async Task<PlayDto> StatusAsync(Guid id, string status, string? reason = null)
        {
            await using var db = Db.Context(Owner);
            var plays = Plays(db);
            var current = await plays.GetAsync(id, default);
            return await plays.ChangeStatusAsync(id, new ChangePlayStatusRequest(current.Summary.Version, status, reason), default);
        }

        public async Task<PlayDto> ReviseAsync(Guid id, Func<PlayPlanDocument, PlayPlanDocument> change, string reason)
        {
            await using var db = Db.Context(Owner);
            var plays = Plays(db);
            var current = await plays.GetAsync(id, default);
            return await plays.UpdateAsync(id, new UpdatePlayRequest(current.Summary.Version, AccountId, current.Summary.Instrument,
                "venue", current.Summary.Title, change(current.Plan), current.Drawings, current.Review, reason), default);
        }

        public async Task<bool> EvaluateAccountAsync(IReadOnlyDictionary<string, IReadOnlyList<PaperCandle>> candles)
        {
            await using var db = Db.Context(Owner);
            var service = Paper(db);
            var candidate = await service.PrepareAccountAsync(AccountId, default);
            return await service.EvaluateAccountAsync(candidate, candles, PaperTradingService.SettledThrough(Clock.Now), default);
        }

        public async Task RecordAccountReadFailureAsync()
        {
            await using var db = Db.Context(Owner);
            var service = Paper(db);
            await service.RecordAccountReadFailureAsync(await service.PrepareAccountAsync(AccountId, default), default);
        }

        public async Task<PaperPlayState> StateAsync(Guid id)
        {
            await using var db = Db.Context(Owner);
            return await Paper(db).StateAsync(id, default);
        }

        public async Task<AccountDto> PaperAccountAsync()
        {
            await using var db = Db.Context(Owner);
            var unit = TestUnitOfWork.For(db);
            var workspace = new WorkspaceService(new WorkspaceStore(db, unit), new CoreOwner(Owner), TestVenues.With(), unit);
            return Assert.Single(await workspace.AccountsAsync(default, "paper"));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static readonly PlayPlanDocument Btc = Plan("margin", "100", "1",
        Entry("e", "100", "100", [new("s", "price", "90", "100")], [new("t", "price", "110", "100")]));
    private static readonly PlayPlanDocument Eth = Plan("margin", "100", "1",
        Entry("e", "100", "200", [new("s", "price", "180", "100")], [new("t", "price", "220", "100")]));

    private static Dictionary<string, IReadOnlyList<PaperCandle>> QuietThenBtcEntry() => new()
    {
        // Minute 0 does not touch either entry; minute 1 touches the BTC entry at 100; minute 2 is flat.
        ["BTC"] = [Candle(0, 105, 104, 106), Candle(1, 100, 99, 101), Candle(2, 102, 101, 103)],
        ["ETH"] = [Candle(0, 205, 204, 206), Candle(1, 205, 204, 206), Candle(2, 205, 204, 206)]
    };

    // Round-four report N1: these unchecked touches used to disappear without any audit receipt.
    private static Dictionary<string, IReadOnlyList<PaperCandle>> HourWithEntryThenStop(int from = 0, int to = 60) => new()
    {
        ["BTC"] = Enumerable.Range(from, to - from).Select(m => m == 5 ? Candle(5, 100, 99.5m, 100.5m)
            : m == 10 ? Candle(10, 89, 88, 90) : m is > 5 and < 10 ? Candle(m, 95, 94, 96) : Candle(m, 105, 104, 106)).ToList()
    };

    [PostgresTheory]
    [InlineData("cancelled")]
    [InlineData("paused")]
    public async Task Unchecked_pre_action_history_is_receipted_and_a_resumed_win_never_scores(string status)
    {
        await using var f = await Engine.CreateAsync(interval: 60);
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(30);
        await f.StatusAsync(id, status, status == "cancelled" ? "changed-mind" : null);
        var stopped = await f.StateAsync(id);
        var receipt = Assert.Single(stopped.Suspensions);
        Assert.Equal(f.Clock.Now, receipt.SuspendedAtUtc);
        Assert.Null(receipt.ResumedAtUtc);
        Assert.Contains("pre-action", receipt.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FirstOpen.ToString("O"), receipt.Reason);
        Assert.Contains(f.Clock.Now.ToString("O"), receipt.Reason);
        Assert.Contains("unscored", receipt.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopped.HasUnscoredHistory);
        Assert.Empty(stopped.Executions);
        Assert.Empty(stopped.Issues);
        Assert.Null(stopped.NextCheckAtUtc);
        var account = await f.PaperAccountAsync();
        Assert.Equal("4000", account.BalanceUsd);
        Assert.Equal("4000", account.AvailableBalanceUsd);
        if (status == "paused")
        {
            await f.StatusAsync(id, "planned");
            Assert.Equal(FirstOpen.AddMinutes(31), Assert.Single((await f.StateAsync(id)).Suspensions).ResumedAtUtc);
            f.Clock.Now = FirstOpen.AddMinutes(91).AddSeconds(15);
            var candles = HourWithEntryThenStop(31, 91);
            candles["BTC"] = candles["BTC"].Select(c => c.OpenAtUtc == FirstOpen.AddMinutes(40)
                ? Candle(40, 100, 99.5m, 100.5m) : c.OpenAtUtc == FirstOpen.AddMinutes(45)
                    ? Candle(45, 110, 109, 111) : c).ToList();
            Assert.True(await f.EvaluateAccountAsync(candles));
            var state = await f.StateAsync(id);
            Assert.Equal(2, state.Executions.Count);
            Assert.Equal(10m, decimal.Parse(state.RealizedPnlUsd, CultureInfo.InvariantCulture));
            Assert.True(state.HasUnscoredHistory);
            Assert.Equal("4010", (await f.PaperAccountAsync()).BalanceUsd);
        }
        else
        {
            f.Clock.Now = FirstOpen.AddMinutes(60).AddSeconds(15);
            Assert.False(await f.EvaluateAccountAsync(HourWithEntryThenStop()));
            Assert.Empty((await f.StateAsync(id)).Executions);
        }
        await using var db = f.Db.Context(f.Owner);
        var unit = TestUnitOfWork.For(db);
        var sizingStore = new SizingStore(db, unit);
        var sizing = await new SizingService(sizingStore, new CoreOwner(f.Owner), f.Clock, unit).GetAsync(default, "paper");
        Assert.Equal(0, sizing.Record.ClosedPlays);
        Assert.Equal("paper", sizing.Record.Source);
        var review = new ReviewService(new ReviewStore(db, sizingStore), f.Clock);
        var query = new ReviewQuery(Period: "all", TradingMode: "paper");
        var report = await review.GetAsync(query, default);
        Assert.Equal((0, 0, 0), (report.Metrics.Count, report.Metrics.Wins, report.Metrics.Losses));
        Assert.Equal(1, report.Coverage.ClosedCandidates);
        Assert.Equal(id, Assert.Single((await review.ExclusionsAsync(query, default)).Items).PlayId);
    }

    [PostgresFact]
    public async Task Unchecked_hour_without_owner_action_records_the_intervening_loss()
    {
        await using var f = await Engine.CreateAsync(interval: 60);
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(60).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(HourWithEntryThenStop()));
        var state = await f.StateAsync(id);
        Assert.Equal(2, state.Executions.Count);
        Assert.Equal(-10m, decimal.Parse(state.RealizedPnlUsd, CultureInfo.InvariantCulture));
        Assert.Equal("3990", (await f.PaperAccountAsync()).BalanceUsd);
        Assert.False(state.HasUnscoredHistory);
        await using var db = f.Db.Context(f.Owner);
        var report = await new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), f.Clock)
            .GetAsync(new ReviewQuery(Period: "all", TradingMode: "paper"), default);
        Assert.Equal((1, 0, 1), (report.Metrics.Count, report.Metrics.Wins, report.Metrics.Losses));
    }

    [PostgresTheory]
    [InlineData("paused", 0, -1, false)]
    [InlineData("paused", 0, 0, false)]
    [InlineData("paused", 0, 1, true)]
    [InlineData("cancelled", 0, -1, false)]
    [InlineData("cancelled", 0, 0, false)]
    [InlineData("cancelled", 0, 1, true)]
    [InlineData("paused", 0, 60000, true)]
    [InlineData("paused", 0, 69999, true)]
    [InlineData("paused", 0, 70000, true)]
    [InlineData("paused", 0, 70001, true)]
    [InlineData("cancelled", 0, 60000, true)]
    [InlineData("cancelled", 0, 69999, true)]
    [InlineData("cancelled", 0, 70000, true)]
    [InlineData("cancelled", 0, 70001, true)]
    [InlineData("paused", 2, 15001, true)]
    [InlineData("paused", 2, 60000, true)]
    [InlineData("paused", 2, 69999, true)]
    [InlineData("paused", 2, 70000, true)]
    [InlineData("paused", 2, 70001, true)]
    [InlineData("cancelled", 2, 15001, true)]
    [InlineData("cancelled", 2, 60000, true)]
    [InlineData("cancelled", 2, 69999, true)]
    [InlineData("cancelled", 2, 70000, true)]
    [InlineData("cancelled", 2, 70001, true)]
    public async Task Owner_receipt_uses_retained_frontier_including_partial_and_unsettled_minutes(
        string status, int evaluatedMinutes, int elapsedMilliseconds, bool skipped)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        var frontier = FirstOpen.AddMinutes(evaluatedMinutes);
        if (evaluatedMinutes > 0)
        {
            f.Clock.Now = frontier.AddSeconds(15);
            Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
                { ["BTC"] = Enumerable.Range(0, evaluatedMinutes).Select(m => Candle(m, 105, 104, 106)).ToList() }));
        }
        f.Clock.Now = frontier.AddMilliseconds(elapsedMilliseconds);
        await f.StatusAsync(id, status, status == "cancelled" ? "changed-mind" : null);
        var state = await f.StateAsync(id);
        Assert.Equal(skipped, state.HasUnscoredHistory);
        if (skipped)
        {
            var receipt = Assert.Single(state.Suspensions);
            Assert.Equal(f.Clock.Now, receipt.SuspendedAtUtc);
            Assert.Contains(frontier.ToString("O"), receipt.Reason);
            Assert.Contains(f.Clock.Now.ToString("O"), receipt.Reason);
        }
        else Assert.Empty(state.Suspensions);
        Assert.Empty(state.Executions);
        Assert.Empty(state.Issues);
        Assert.Equal("4000", (await f.PaperAccountAsync()).AvailableBalanceUsd);
    }

    [PostgresTheory]
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Owner_receipt_falls_back_to_first_activation_when_checkpoint_is_unset(string status)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        await using (var db = f.Db.Context(f.Owner))
        {
            (await db.PaperSimulations.SingleAsync()).CheckpointUtc = null;
            await db.SaveChangesAsync();
        }
        f.Clock.Now = FirstOpen.AddMilliseconds(1);
        await f.StatusAsync(id, status, status == "cancelled" ? "changed-mind" : null);
        var receipt = Assert.Single((await f.StateAsync(id)).Suspensions);
        Assert.Equal(f.Clock.Now, receipt.SuspendedAtUtc);
        Assert.Contains(FirstOpen.ToString("O"), receipt.Reason);
    }

    [PostgresTheory]
    [InlineData("stale", HttpStatusCode.Conflict)]
    [InlineData("foreign", HttpStatusCode.NotFound)]
    [InlineData("real", HttpStatusCode.NotFound)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    public async Task Rejected_owner_stop_cannot_write_a_receipt_or_release_capital(string fence, HttpStatusCode expected)
    {
        await using var f = await Engine.CreateAsync(interval: 60);
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(30);
        PlayDto before;
        await using (var db = f.Db.Context(f.Owner)) before = await f.Plays(db).GetAsync(id, default);
        await using var factory = new CoreApiFactory(fence == "foreign" ? Guid.NewGuid() : f.Owner,
            connection: f.Db.ConnectionString, configure: services => services.AddSingleton<TimeProvider>(f.Clock));
        using var client = fence == "anonymous" ? factory.CreateClient() : factory.AuthorizedClient();
        var mode = fence == "real" ? "real" : "paper";
        var request = new ChangePlayStatusRequest(before.Summary.Version - (fence == "stale" ? 1 : 0),
            "cancelled", "changed-mind");
        using var response = await client.PostAsJsonAsync($"/api/plays/{id}/status?tradingMode={mode}", request);
        Assert.Equal(expected, response.StatusCode);
        var state = await f.StateAsync(id);
        Assert.Empty(state.Suspensions);
        Assert.Empty(state.Executions);
        Assert.NotNull(state.NextCheckAtUtc);
        Assert.Equal("3900", (await f.PaperAccountAsync()).AvailableBalanceUsd);
        await using var check = f.Db.Context(f.Owner);
        var retained = await f.Plays(check).GetAsync(id, default);
        Assert.Equal((before.Summary.Version, "planned"), (retained.Summary.Version, retained.Summary.Status));
    }

    [PostgresTheory]
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Known_open_quantity_rejects_owner_stop_without_a_receipt_or_lifecycle_mutation(string status)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 100, 99, 101)] }));
        f.Clock.Now = FirstOpen.AddMinutes(30);
        await Assert.ThrowsAsync<DomainRuleException>(() => f.StatusAsync(id, status, status == "cancelled" ? "changed-mind" : null));
        var state = await f.StateAsync(id);
        Assert.Equal("1", state.OpenQuantity);
        Assert.Single(state.Executions);
        Assert.Empty(state.Suspensions);
        Assert.NotNull(state.NextCheckAtUtc);
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal("open", (await f.Plays(db).GetAsync(id, default)).Summary.Status);
    }

    [PostgresTheory]
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Failed_owner_transaction_rolls_back_receipt_lifecycle_and_released_reservation(string status)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(30);
        await using (var db = f.Db.Context(f.Owner))
        {
            var plays = f.Plays(db);
            var before = await plays.GetAsync(id, default);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestUnitOfWork.For(db).ExecuteAsync<bool>(async () =>
            {
                await plays.ChangeStatusAsync(id, new(before.Summary.Version, status,
                    status == "cancelled" ? "changed-mind" : null), default);
                throw new InvalidOperationException("Abort owner transaction after the nested lifecycle save.");
            }, default));
            Assert.Contains("Abort owner transaction", failure.Message);
        }
        var state = await f.StateAsync(id);
        Assert.Empty(state.Suspensions);
        Assert.NotNull(state.NextCheckAtUtc);
        Assert.Equal("3900", (await f.PaperAccountAsync()).AvailableBalanceUsd);
        await using var check = f.Db.Context(f.Owner);
        Assert.Equal("planned", (await f.Plays(check).GetAsync(id, default)).Summary.Status);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_retry_or_awaiting_publication_is_not_an_excluded_closed_outcome(bool awaitingPublication)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(2).AddSeconds(15);
        if (awaitingPublication)
            Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
                { ["BTC"] = [Candle(0, 105, 104, 106)] }));
        else await f.RecordAccountReadFailureAsync();
        var active = await f.StateAsync(id);
        Assert.NotEmpty(active.Issues);
        Assert.NotNull(active.NextCheckAtUtc);
        await using (var db = f.Db.Context(f.Owner))
        {
            Assert.Equal("planned", (await f.Plays(db).GetAsync(id, default)).Summary.Status);
            var unit = TestUnitOfWork.For(db);
            var store = new SizingStore(db, unit);
            var review = new ReviewService(new ReviewStore(db, store), f.Clock);
            var query = new ReviewQuery(Period: "all", TradingMode: "paper", Positions: "include");
            var report = await review.GetAsync(query, default);
            Assert.Equal(0, report.Coverage.ClosedCandidates);
            Assert.Equal(0, report.Coverage.IncompleteExecutions);
            Assert.Equal(0, report.Metrics.Count);
            Assert.Empty((await review.ExclusionsAsync(query, default)).Items);
            var sizing = await new SizingService(store, new CoreOwner(f.Owner), f.Clock, unit).GetAsync(default, "paper");
            Assert.Equal(("paper", 0), (sizing.Record.Source, sizing.Record.ClosedPlays));
        }
        f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 105, 104, 106), Candle(1, 105, 104, 106),
                Candle(2, 100, 99, 101), Candle(3, 110, 109, 111)] }));
        Assert.Empty((await f.StateAsync(id)).Issues);
        await using var recovered = f.Db.Context(f.Owner);
        var recoveredStore = new SizingStore(recovered, TestUnitOfWork.For(recovered));
        var recoveredReview = new ReviewService(new ReviewStore(recovered, recoveredStore), f.Clock);
        var recoveredQuery = new ReviewQuery(Period: "all", TradingMode: "paper");
        var scored = await recoveredReview.GetAsync(recoveredQuery, default);
        Assert.Equal((1, 1, 1), (scored.Coverage.ClosedCandidates, scored.Metrics.Count, scored.Metrics.Wins));
        Assert.Empty((await recoveredReview.ExclusionsAsync(recoveredQuery, default)).Items);
    }

    [PostgresFact]
    public async Task A_completed_simulation_with_retained_issues_is_still_an_excluded_closed_outcome()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var paper = PaperReportingTestsValues.Paper(owner, issues: ["Retained unresolved evidence."]);
        await using var db = database.Context(owner);
        db.AddRange(paper.Portfolio, paper.Account, paper.Facts.Play);
        db.PlanRevisions.AddRange(paper.Facts.Revisions);
        db.PaperExecutions.AddRange(paper.Facts.Paper!.Executions);
        db.PaperSimulations.Add(new()
        {
            OwnerId = owner, AccountId = paper.Account.Id, PlayId = paper.Facts.Play.Id,
            State = "ready", IssuesJson = JsonSerializer.Serialize(paper.Facts.Paper.Issues)
        });
        await db.SaveChangesAsync();
        var unit = TestUnitOfWork.For(db);
        var store = new SizingStore(db, unit);
        var clock = new PaperReportingTestsSupport.Clock();
        var review = new ReviewService(new ReviewStore(db, store), clock);
        var query = new ReviewQuery(Period: "all", TradingMode: "paper");
        var report = await review.GetAsync(query, default);
        Assert.Equal((1, 1, 0), (report.Coverage.ClosedCandidates, report.Coverage.IncompleteExecutions, report.Metrics.Count));
        Assert.Equal(paper.Facts.Play.Id, Assert.Single((await review.ExclusionsAsync(query, default)).Items).PlayId);
        Assert.Equal(0, (await new SizingService(store, new CoreOwner(owner), clock, unit).GetAsync(default, "paper")).Record.ClosedPlays);
    }

    [PostgresTheory]
    [InlineData("paused", false)]
    [InlineData("cancelled", false)]
    [InlineData("paused", true)]
    [InlineData("cancelled", true)]
    public async Task Owner_suspension_of_unfilled_transient_failure_recovers_capital_but_retains_skipped_history(
        string status, bool awaitingPublication)
    {
        await using var f = await Engine.CreateAsync();
        var a = await f.PlanAsync(Btc, "BTC");
        var b = await f.PlanAsync(Eth, "ETH");
        f.Clock.Now = FirstOpen.AddMinutes(awaitingPublication ? 2 : 1).AddSeconds(15);
        if (awaitingPublication)
            Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            {
                ["BTC"] = [Candle(0, 105, 104, 106), Candle(1, 105, 104, 106)],
                ["ETH"] = [Candle(0, 205, 204, 206)]
            }));
        else await f.RecordAccountReadFailureAsync();
        await f.StatusAsync(b, status, status == "cancelled" ? "changed-mind" : null);
        var suspended = await f.StateAsync(b);
        Assert.Equal("disabled", suspended.Status);
        Assert.Empty(suspended.Issues);
        Assert.Equal(f.Clock.Now, Assert.Single(suspended.Suspensions).SuspendedAtUtc);
        Assert.True(suspended.HasUnscoredHistory);
        f.Clock.Now = FirstOpen.AddMinutes(3).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(QuietThenBtcEntry()));
        var sibling = await f.StateAsync(a);
        Assert.Equal(1m, decimal.Parse(sibling.EnteredQuantity));
        Assert.Empty(sibling.DeclinedEntries);
        Assert.Empty(sibling.Issues);
        // Settle the entered sibling, then every exact account total is known (including no open margin).
        f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(3, 110, 109, 111)] }));
        var account = await f.PaperAccountAsync();
        Assert.Equal(4010m, decimal.Parse(account.BalanceUsd!));
        Assert.Equal(4010m, decimal.Parse(account.AccountValueUsd!));
        Assert.Equal(4010m, decimal.Parse(account.AvailableBalanceUsd!));
        await using (var db = f.Db.Context(f.Owner))
        {
            var sizingStore = new SizingStore(db, TestUnitOfWork.For(db));
            var sizing = await new SizingService(sizingStore, new CoreOwner(f.Owner), f.Clock, TestUnitOfWork.For(db))
                .GetAsync(default, "paper");
            Assert.Equal(1, sizing.Record.ClosedPlays);
            var review = new ReviewService(new ReviewStore(db, sizingStore), f.Clock);
            var query = new ReviewQuery(Period: "all", TradingMode: "paper");
            Assert.Equal(status == "cancelled" ? 2 : 1, (await review.GetAsync(query, default)).Coverage.ClosedCandidates);
            var exclusions = (await review.ExclusionsAsync(query, default)).Items;
            if (status == "cancelled") Assert.Equal(b, Assert.Single(exclusions).PlayId);
            else Assert.Empty(exclusions);
        }
        if (status == "paused") await f.StatusAsync(b, "planned");
        _ = await f.PlanAsync(Btc, "SOL");
    }

    [PostgresTheory]
    [InlineData("paused", 0, 0)]
    [InlineData("resumed", 0, 0)]
    [InlineData("open", 0, 1)]
    [InlineData("closed", 1, 0)]
    [InlineData("cancelled", 1, 0)]
    public async Task Suspension_history_enters_closed_reporting_only_after_the_play_ends(
        string stage, int closedCandidates, int openPlays)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        await f.StatusAsync(id, stage == "cancelled" ? "cancelled" : "paused",
            stage == "cancelled" ? "changed-mind" : null);
        Assert.True((await f.StateAsync(id)).HasUnscoredHistory);

        if (stage is "resumed" or "open" or "closed")
        {
            await f.StatusAsync(id, "planned");
            if (stage is "open" or "closed")
            {
                f.Clock.Now = FirstOpen.AddMinutes(3).AddSeconds(15);
                Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
                    { ["BTC"] = [Candle(2, 100, 99, 101)] }));
            }
            if (stage == "closed")
            {
                f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
                Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
                    { ["BTC"] = [Candle(3, 110, 109, 111)] }));
                Assert.Equal("4010", (await f.PaperAccountAsync()).BalanceUsd);
            }
        }

        await using var db = f.Db.Context(f.Owner);
        var unit = TestUnitOfWork.For(db);
        var store = new SizingStore(db, unit);
        var review = new ReviewService(new ReviewStore(db, store), f.Clock);
        var query = new ReviewQuery(Period: "all", TradingMode: "paper");
        var report = await review.GetAsync(query, default);
        Assert.Equal((closedCandidates, openPlays, closedCandidates),
            (report.Coverage.ClosedCandidates, report.Coverage.OpenPlays, report.Coverage.IncompleteExecutions));
        Assert.Empty(report.Plays);
        Assert.Equal(0, report.Metrics.Count);
        Assert.Null(report.Metrics.Net);
        var exclusions = (await review.ExclusionsAsync(query, default)).Items;
        if (closedCandidates == 0) Assert.Empty(exclusions);
        else Assert.Equal(id, Assert.Single(exclusions).PlayId);
        var sized = await new SizingService(store, new CoreOwner(f.Owner), f.Clock, unit).GetAsync(default, "paper");
        Assert.Equal(("paper", 0), (sized.Record.Source, sized.Record.ClosedPlays));
        Assert.Empty(sized.Record.Recent);
        var retained = await f.StateAsync(id);
        Assert.True(retained.HasUnscoredHistory);
        Assert.Single(retained.Suspensions);
    }

    [PostgresFact]
    public async Task Explicit_rearm_closes_suspension_receipt_prospectively_after_owner_pause()
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        await using (var db = f.Db.Context(f.Owner))
            await f.Paper(db).UpdateSettingsAsync(new(false, 1), default);
        await f.StatusAsync(id, "paused");
        f.Clock.Now = FirstOpen.AddMinutes(3).AddSeconds(15);
        await using (var db = f.Db.Context(f.Owner))
            await f.Paper(db).UpdateSettingsAsync(new(true, 1), default);
        Assert.Null(Assert.Single((await f.StateAsync(id)).Suspensions).ResumedAtUtc);
        f.Clock.Now = FirstOpen.AddMinutes(5).AddSeconds(15);
        await f.StatusAsync(id, "planned");
        var state = await f.StateAsync(id);
        Assert.Equal(FirstOpen.AddMinutes(6), Assert.Single(state.Suspensions).ResumedAtUtc);
        Assert.True(state.HasUnscoredHistory);
        // All touched prices in the disabled window are ignored, never replayed on rearm.
        f.Clock.Now = FirstOpen.AddMinutes(7).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 100, 99, 101), Candle(6, 105, 104, 106)] }));
        Assert.Empty((await f.StateAsync(id)).Executions);
    }
    private static PlayPlanDocument Awkward(string t1Share, string t2Share) => Plan("margin", "1000", "10",
        Entry("e", "100", "97.3", [new("s", "price", "95", "100")],
            [new("t1", "price", "98", t1Share), new("t2", "price", "99", t2Share)]));

    private static async Task<Guid> EnterAndTakeFirstTargetAsync(Engine f)
    {
        var id = await f.PlanAsync(Awkward("50", "50"), "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(2).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 97.3m, 97.2m, 97.4m), Candle(1, 98, 97.9m, 98.5m)] }));
        var state = await f.StateAsync(id);
        Assert.Equal("102.774922918807", state.EnteredQuantity);
        Assert.Equal("51.387461459403", state.ExitedQuantity);
        return id;
    }

    private static Dictionary<string, IReadOnlyList<PaperCandle>> SecondTarget() => new()
        { ["BTC"] = [Candle(2, 98.5m, 98.2m, 98.8m), Candle(3, 99, 98.9m, 99.1m)] };

    [PostgresFact]
    public async Task Control_unrevised_second_half_target_closes_exactly_the_remainder()
    {
        await using var f = await Engine.CreateAsync();
        var id = await EnterAndTakeFirstTargetAsync(f);
        f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(SecondTarget()));
        var state = await f.StateAsync(id);
        Assert.Equal(0m, decimal.Parse(state.OpenQuantity, CultureInfo.InvariantCulture));
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal(PlayStatus.Closed, (await db.Plays.FindAsync(id))!.Status);
    }

    [PostgresFact]
    public async Task Revision_of_executed_target_does_not_resize_its_facts_or_a_later_ten_percent_exit()
    {
        await using var f = await Engine.CreateAsync();
        var id = await EnterAndTakeFirstTargetAsync(f);
        var before = (await f.StateAsync(id)).Executions;
        // The owner now wants t2 to take only 10% of the tranche (t1 shown as 90% so the targets total 100%).
        _ = await f.ReviseAsync(id, p => p with { Entries = [p.Entries[0] with
            { Targets = [new("t1", "price", "98", "90"), new("t2", "price", "99", "10")] }] }, "Smaller second target");
        f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(SecondTarget()));
        var state = await f.StateAsync(id);
        var t2 = Assert.Single(state.Executions, e => e.LevelId == "t2");
        Assert.Equal(10.277492291880m, decimal.Parse(t2.Quantity));
        Assert.Equal(17.471736896196m, decimal.Parse(t2.PnlUsd));
        Assert.Equal(41.109969167524m, decimal.Parse(state.OpenQuantity, CultureInfo.InvariantCulture));
        Assert.Equal(before, state.Executions.Where(e => e.LevelId != "t2").ToArray());
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal(400.000000000010m, await db.PaperExecutions.SumAsync(e => e.MarginUsd));
        Assert.Equal(53.442959917778m, await db.PaperExecutions.SumAsync(e => e.PnlUsd));
    }

    [PostgresFact]
    public async Task Revision_of_executed_target_preserves_its_original_half_share_and_exact_final_remainder()
    {
        await using var f = await Engine.CreateAsync();
        var id = await EnterAndTakeFirstTargetAsync(f);
        _ = await f.ReviseAsync(id, p => p with { Entries = [p.Entries[0] with
            { Targets = [new("t1", "price", "98", "40"), new("t2", "price", "99", "50")] }] }, "Record t1 as 40%");
        f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
        Assert.True(await f.EvaluateAccountAsync(SecondTarget()));
        var state = await f.StateAsync(id);
        Assert.Equal(0m, decimal.Parse(state.OpenQuantity));
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal(PlayStatus.Closed, (await db.Plays.FindAsync(id))!.Status);
        Assert.Equal(0m, await db.PaperExecutions.SumAsync(e => e.MarginUsd));
        Assert.NotNull((await f.PaperAccountAsync()).AccountValueUsd);
    }


    [PostgresTheory]
    [InlineData("paused", "gap")]
    [InlineData("cancelled", "gap")]
    [InlineData("paused", "ambiguous")]
    [InlineData("cancelled", "ambiguous")]
    public async Task Owner_suspension_preserves_terminal_evidence_held_capital_and_reporting(string status, string terminal)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(3).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = terminal == "gap" ? [Candle(0, 105, 104, 106), Candle(2, 105, 104, 106)]
                : [Candle(0, 100, 89, 111)] });
        var gap = await f.StateAsync(id);
        Assert.Equal(terminal, gap.Status);
        await using (var db = f.Db.Context(f.Owner))
        {
            var review = new ReviewService(new ReviewStore(db, new SizingStore(db, TestUnitOfWork.For(db))), f.Clock);
            Assert.Equal(1, (await review.GetAsync(new(Period: "all", TradingMode: "paper"), default)).Coverage.ClosedCandidates);
        }
        await f.StatusAsync(id, status, status == "cancelled" ? "changed-mind" : null);
        var suspended = await f.StateAsync(id);
        Assert.Equal(gap.Issues, suspended.Issues);
        Assert.Equal(terminal, suspended.Status);
        Assert.Empty(suspended.Suspensions);
        Assert.Empty(suspended.Executions);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => f.PlanAsync(Btc, "SOL"))).StatusCode);
        var account = await f.PaperAccountAsync();
        Assert.Null(account.AvailableBalanceUsd);
        Assert.Null(account.AccountValueUsd);
        await using var check = f.Db.Context(f.Owner);
        var store = new SizingStore(check, TestUnitOfWork.For(check));
        var retainedReview = new ReviewService(new ReviewStore(check, store), f.Clock);
        var query = new ReviewQuery(Period: "all", TradingMode: "paper");
        var report = await retainedReview.GetAsync(query, default);
        Assert.Equal((1, 0), (report.Coverage.ClosedCandidates, report.Metrics.Count));
        Assert.Equal(id, Assert.Single((await retainedReview.ExclusionsAsync(query, default)).Items).PlayId);
        var sized = await new SizingService(store, new CoreOwner(f.Owner), f.Clock, TestUnitOfWork.For(check)).GetAsync(default, "paper");
        Assert.Equal(0, sized.Record.ClosedPlays);
    }

    [PostgresTheory]
    [InlineData("revision")]
    [InlineData("settings")]
    [InlineData("disabled")]
    public async Task Deterministic_source_expiry_is_fenced_by_versions_and_settings(string change)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>> { ["BTC"] = [Candle(0, 100, 99, 101)] });
        PaperAccountCandidate candidate;
        await using (var db = f.Db.Context(f.Owner))
        {
            var service = f.Paper(db);
            await service.RecordAccountReadFailureAsync(await service.PrepareAccountAsync(f.AccountId, default), default,
                new HashSet<string> { "BTC" });
        }
        await using (var db = f.Db.Context(f.Owner)) candidate = await f.Paper(db).PrepareAccountAsync(f.AccountId, default);
        if (change == "revision")
            await f.ReviseAsync(id, p => p with { Entries = [p.Entries[0] with { Stops = [new("s", "price", "95", "100")] }] }, "Tighten");
        else
        {
            await using var db = f.Db.Context(f.Owner);
            await f.Paper(db).UpdateSettingsAsync(new(change != "disabled", 60), default);
        }
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        await using (var db = f.Db.Context(f.Owner))
            await f.Paper(db).RecordAccountReadFailureAsync(candidate, default, new HashSet<string> { "BTC" });
        Assert.NotEqual("gap", (await f.StateAsync(id)).Status);
        Assert.Equal(1m, decimal.Parse((await f.StateAsync(id)).OpenQuantity));
    }

    [PostgresFact]
    public async Task A_validated_recovery_clears_first_failure_and_a_later_outage_does_not_expire_it()
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        await using (var db = f.Db.Context(f.Owner))
        {
            var service = f.Paper(db);
            await service.RecordAccountReadFailureAsync(await service.PrepareAccountAsync(f.AccountId, default), default,
                new HashSet<string> { "BTC" });
        }
        f.Clock.Now = FirstOpen.AddMinutes(2).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 105, 104, 106), Candle(1, 105, 104, 106)] });
        f.Clock.Now = f.Clock.Now.AddMinutes(20);
        await using (var db = f.Db.Context(f.Owner))
        {
            var service = f.Paper(db);
            await service.RecordAccountReadFailureAsync(await service.PrepareAccountAsync(f.AccountId, default), default,
                new HashSet<string> { "BTC" });
        }
        Assert.NotEqual("gap", (await f.StateAsync(id)).Status);
        f.Clock.Now = f.Clock.Now.AddMinutes(20);
        await f.RecordAccountReadFailureAsync(); // transport cannot expire the deterministic timer
        Assert.NotEqual("gap", (await f.StateAsync(id)).Status);
        await f.StatusAsync(id, "paused"); // safe suspension clears the failure rather than poisoning capital
        await f.StatusAsync(id, "planned");
        await using (var db = f.Db.Context(f.Owner))
        {
            var service = f.Paper(db);
            await service.RecordAccountReadFailureAsync(await service.PrepareAccountAsync(f.AccountId, default), default,
                new HashSet<string> { "BTC" });
        }
        Assert.NotEqual("gap", (await f.StateAsync(id)).Status);
    }

    [PostgresTheory]
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Recovered_account_keeps_exact_zero_cash_equity_and_available_known(string status)
    {
        await using var f = await Engine.CreateAsync();
        var a = await f.PlanAsync(Plan("margin", "3900", "2", Entry("e", "100", "100",
            [new("s", "price", "50", "100")], [new("t", "price", "200", "100")])), "BTC");
        var b = await f.PlanAsync(Eth, "ETH");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        await f.RecordAccountReadFailureAsync();
        await f.StatusAsync(b, status, status == "cancelled" ? "changed-mind" : null);
        f.Clock.Now = FirstOpen.AddMinutes(2).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 105, 104, 106), Candle(1, 105, 104, 106)] });
        await f.ReviseAsync(a, p => p with { Size = "4000" }, "Use released reservation");
        f.Clock.Now = FirstOpen.AddMinutes(4).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(2, 105, 104, 106), Candle(3, 100, 99, 101)] });
        f.Clock.Now = FirstOpen.AddMinutes(5).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(4, 50, 49, 51)] });
        var state = await f.StateAsync(a);
        Assert.Equal(0m, decimal.Parse(state.OpenQuantity));
        Assert.Equal(-4000m, decimal.Parse(state.RealizedPnlUsd));
        var account = await f.PaperAccountAsync();
        Assert.Equal(0m, decimal.Parse(account.BalanceUsd!));
        Assert.Equal(0m, decimal.Parse(account.AccountValueUsd!));
        Assert.Equal(0m, decimal.Parse(account.AvailableBalanceUsd!));
        var admission = await Assert.ThrowsAsync<ProblemException>(() => f.PlanAsync(Btc, "SOL"));
        Assert.Contains("Insufficient", admission.Message); // exhausted, not falsely unresolved
    }
}
