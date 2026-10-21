using Microsoft.EntityFrameworkCore;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

[Collection("Paper corrective PostgreSQL")]
public sealed class PaperExitOrderingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset First = At.AddSeconds(55);
    private static PaperCandle Candle(int minute, decimal open, decimal low, decimal high, decimal? close = null) =>
        new(First.AddMinutes(minute), First.AddMinutes(minute + 1), open, high, low, close ?? open);

    private static (PlayPlanDocument Plan, PaperCandle[] Candles, decimal Pnl) Scenario(string role, bool shortSide, bool farFirst)
    {
        var near = new PlanExit("near", "price", role == "stop" ? shortSide ? "105" : "95" : shortSide ? "90" : "110", "50");
        var far = new PlanExit("far", "price", role == "stop" ? shortSide ? "110" : "90" : shortSide ? "80" : "120", "50");
        PlanExit[] levels = farFirst ? [far, near] : [near, far];
        var prior = new PlanExit("prior", "price", role == "stop" ? shortSide ? "90" : "110" : shortSide ? "110" : "90", "50");
        var plan = new PlayPlanDocument(shortSide ? "short" : "long", "quantity", "10", "1", null,
            [new("e", "Entry", "#ffffff", "100", "100", role == "stop" ? levels : [prior],
                role == "stop" ? [prior] : levels)], new("", "", "", ""));
        var origin = role == "stop" ? shortSide ? 90m : 110m : shortSide ? 110m : 90m;
        var final = role == "stop" ? shortSide ? 110m : 90m : shortSide ? 80m : 120m;
        return (plan, [Candle(0, 100, 99, 101), Candle(1, origin, origin - 1, origin + 1),
            Candle(2, origin, Math.Min(origin, final) - 1, Math.Max(origin, final) + 1, final)], role == "stop" ? 25 : 0);
    }

    [Theory]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    public void Partial_exit_remainder_follows_proven_crossing_not_saved_array_order(string role, bool shortSide, bool farFirst)
    {
        var (plan, candles, pnl) = Scenario(role, shortSide, farFirst);
        var result = Evaluate(plan, candles);
        Assert.Null(result.Issue);
        Assert.Equal(pnl, result.Executions.Sum(e => e.PnlUsd));
        Assert.Equal("near", Assert.Single(result.Executions, e => e.Role == role).LevelId);
        Assert.Equal(0m, result.Executions.Sum(e => e.Role == "entry" ? e.Quantity : -e.Quantity));
        Assert.Equal(0m, result.Executions.Sum(e => e.MarginUsd));
    }

    [PostgresTheory]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    public async Task Saved_exit_reordering_cannot_change_closed_result_or_virtual_cash(string role, bool shortSide, bool farFirst)
    {
        var (plan, candles, pnl) = Scenario(role, shortSide, farFirst);
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var clock = new Clock();
        var portfolio = new Portfolio(Guid.NewGuid(), owner, "Exit ordering", tradingMode: "paper");
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Exit ordering", "paper", 4000);
        account.Configure(portfolio.Id, null, null);
        await using var db = database.Context(owner);
        db.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, 4000, At),
            new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = 1 });
        await db.SaveChangesAsync();
        var work = TestUnitOfWork.For(db);
        var paper = new PaperTradingService(new PaperStore(db, work), work, new ConfiguredOwnerDirectory(owner), TestVenues.With(), clock);
        var plays = new PlayService(new PlayStore(db, work), new MemoryObjectStore(), clock, work, paper);
        var saved = await plays.CreateAsync(new(account.Id, "BTC", "venue", "Exit ordering", plan), default, "paper");
        await plays.ChangeStatusAsync(saved.Summary.Id, new(saved.Summary.Version, "planned"), default);
        clock.Now = First.AddMinutes(3).AddSeconds(10);
        var candidate = await paper.PrepareAccountAsync(account.Id, default);
        Assert.True(await paper.EvaluateAccountAsync(candidate, new Dictionary<string, IReadOnlyList<PaperCandle>> { ["BTC"] = candles },
            PaperTradingService.SettledThrough(clock.Now), default));
        var state = await paper.StateAsync(saved.Summary.Id, default);
        Assert.Empty(state.Issues);
        Assert.Equal(pnl, state.Executions.Sum(e => decimal.Parse(e.PnlUsd, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("near", Assert.Single(state.Executions, e => e.Role == role).LevelId);
        Assert.Equal("closed", (await plays.GetAsync(saved.Summary.Id, default)).Summary.Status);
        var workspace = new WorkspaceService(new WorkspaceStore(db, work), new CoreOwner(owner), TestVenues.With(), work);
        var balance = Assert.Single(await workspace.AccountsAsync(default, "paper"));
        var expected = (4000 + pnl).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, balance.BalanceUsd);
        Assert.Equal(expected, balance.AvailableBalanceUsd);
        Assert.Equal(0m, await db.PaperReservations.SumAsync(r => r.AmountUsd));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void An_unchanged_level_crossed_before_the_open_precedes_intraminute_touches(bool farFirst)
    {
        var (plan, candles, _) = Scenario("target", false, farFirst);
        candles[2] = Candle(2, 125, 109, 126, 110);
        var result = Evaluate(plan, candles);
        Assert.Null(result.Issue);
        Assert.Equal("near", Assert.Single(result.Executions, e => e.Role == "target").LevelId);
        Assert.Equal(0m, result.Executions.Sum(e => e.PnlUsd));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void A_revised_level_does_not_gain_opening_jump_priority_over_an_unchanged_exit(bool shortSide, bool farFirst)
    {
        var (current, candles, _) = Scenario("target", shortSide, farFirst);
        var original = current with { Entries = [current.Entries[0] with
        {
            Targets = current.Entries[0].Targets.Select(t => t.Id == "near"
                ? t with { Value = shortSide ? "70" : "130" } : t).ToArray()
        }] };
        var prior = Evaluate(original, candles[..2]);
        var entry = prior.Executions[0];
        var result = PaperEvaluator.Evaluate(entry.OwnerId, entry.AccountId, entry.PlayId,
            [new() { OwnerId = entry.OwnerId, AccountId = entry.AccountId, PlayId = entry.PlayId, PlanVersion = 1,
                PlanJson = PlayDocuments.Serialize(original), EffectiveAtUtc = First },
             new() { OwnerId = entry.OwnerId, AccountId = entry.AccountId, PlayId = entry.PlayId, PlanVersion = 2,
                PlanJson = PlayDocuments.Serialize(current), EffectiveAtUtc = First.AddMinutes(2) }],
            prior.Executions, First.AddMinutes(2), shortSide ? 110 : 90,
            [shortSide ? Candle(2, 75, 74, 91, 90) : Candle(2, 125, 109, 126, 110)], First.AddMinutes(3),
            previousCloseAtUtc: First.AddMinutes(2));
        Assert.Null(result.Issue);
        Assert.Equal("far", Assert.Single(result.Executions).LevelId);
        Assert.Equal(100m, Assert.Single(result.Executions).PnlUsd);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Competing_exit_touches_on_opposite_sides_of_the_open_are_ambiguous(bool farFirst)
    {
        var (plan, candles, _) = Scenario("target", false, farFirst);
        // Without a retained source close, the opening jump cannot establish which level was touched first.
        var prior = Evaluate(plan, candles[..2]);
        var changed = plan with { Entries = [plan.Entries[0] with { Targets = farFirst
            ? [new("far", "price", "120", "50"), new("near", "price", "110", "50")]
            : [new("near", "price", "110", "50"), new("far", "price", "120", "50")] }] };
        var owner = prior.Executions[0].OwnerId;
        var account = prior.Executions[0].AccountId;
        var play = prior.Executions[0].PlayId;
        var result = PaperEvaluator.Evaluate(owner, account, play,
            [new() { OwnerId = owner, AccountId = account, PlayId = play, PlanVersion = 1,
                PlanJson = PlayDocuments.Serialize(changed), EffectiveAtUtc = First }],
            prior.Executions, First.AddMinutes(2), null, [Candle(2, 115, 109, 121)], First.AddMinutes(3));
        Assert.True(result.Ambiguous);
        Assert.NotNull(result.Issue);
        Assert.Empty(result.Executions);
        Assert.Equal(First.AddMinutes(2), result.CheckpointUtc);
    }

    private static PaperEvaluation Evaluate(PlayPlanDocument plan, PaperCandle[] candles)
    {
        var owner = Guid.NewGuid(); var account = Guid.NewGuid(); var play = Guid.NewGuid();
        return PaperEvaluator.Evaluate(owner, account, play,
            [new() { OwnerId = owner, AccountId = account, PlayId = play, PlanVersion = 1,
                PlanJson = PlayDocuments.Serialize(plan), EffectiveAtUtc = First }],
            [], First, null, candles, First.AddMinutes(candles.Length));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
