// Regression matrix derived from independent Sol review A2; assertions retained.
using System.Globalization;
using System.Text.Json;
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
using Xunit.Abstractions;

namespace Vessel.Tests;

[Collection("Paper corrective PostgreSQL")]
public sealed class PaperExitActivationTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Saved = new(2026, 10, 21, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset First = Saved.AddSeconds(55);
    private static readonly Guid Owner = Guid.NewGuid(), AccountId = Guid.NewGuid(), PlayId = Guid.NewGuid();
    private static PaperCandle C(int minute, decimal open, decimal low, decimal high, decimal? close = null, int trades = 1) =>
        new(First.AddMinutes(minute), First.AddMinutes(minute + 1), open, high, low, close ?? open, trades);
    private static PlanExit X(string id, decimal price, string share = "50") => new(id, "price", price.ToString(CultureInfo.InvariantCulture), share);
    private static PlanEntry E(string id, string share, IReadOnlyList<PlanExit>? stops = null, IReadOnlyList<PlanExit>? targets = null) =>
        new(id, id, "#ffffff", share, "100", stops ?? [], targets ?? []);
    private static PlayPlanDocument P(bool shortSide, IReadOnlyList<PlanEntry> entries) =>
        new(shortSide ? "short" : "long", "quantity", "10", "1", null, entries, new("", "", "", ""));
    private static PaperPlanActivation A(PlayPlanDocument plan, int version, int minute) => new()
    {
        OwnerId = Owner, AccountId = AccountId, PlayId = PlayId, PlanVersion = version,
        PlanJson = PlayDocuments.Serialize(plan), EffectiveAtUtc = First.AddMinutes(minute)
    };
    private static PaperEvaluation Eval(PlayPlanDocument plan, PaperCandle[] candles) =>
        PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(plan, 1, 0)], [], First, null, candles, First.AddMinutes(candles.Length));
    private static (PlayPlanDocument Old, PlayPlanDocument Current, PaperCandle[] Prefix, PaperCandle Last, decimal ExpectedPnl) Dormant(
        string role, bool shortSide, bool farFirst)
    {
        var down = role == "stop" != shortSide;
        var near = X("near", down ? 95 : 105);
        var far = X("far", down ? 90 : 110);
        if (role == "target") { near = X("near", down ? 90 : 110); far = X("far", down ? 80 : 120); }
        var prior = X("prior", role == "stop" ? shortSide ? 90 : 110 : shortSide ? 110 : 90);
        PlanExit[] exits = farFirst ? [far, near] : [near, far];
        var current = P(shortSide, [E("e", "100", role == "stop" ? exits : [prior], role == "stop" ? [prior] : exits)]);
        var old = current with { Entries = [current.Entries[0] with
        {
            Stops = current.Entries[0].Stops.Select(x => x.Id == "near" ? x with { Share = "0" } : x).ToArray(),
            Targets = current.Entries[0].Targets.Select(x => x.Id == "near" ? x with { Share = "0" } : x).ToArray()
        }] };
        var origin = decimal.Parse(prior.Value, CultureInfo.InvariantCulture);
        var end = decimal.Parse(far.Value, CultureInfo.InvariantCulture) + (down ? -5 : 5);
        var nearPrice = decimal.Parse(near.Value, CultureInfo.InvariantCulture);
        return (old, current, [C(0, 100, 99, 101), C(1, origin, origin - 1, origin + 1)],
            C(2, end, Math.Min(end, nearPrice) - 1, Math.Max(end, nearPrice) + 1, nearPrice), role == "stop" ? 0 : 50);
    }

    [Theory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public void H01_Zero_to_positive_exit_share_is_new_activation_not_proof_of_a_prior_gap_touch(string role, bool shortSide, bool farFirst)
    {
        var (old, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        var prior = Eval(old, prefix);
        Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(current, 2, 2)],
            prior.Executions, First.AddMinutes(2), prior.PreviousClose, [last], First.AddMinutes(3), previousCloseAtUtc: prior.PreviousCloseAtUtc);
        output.WriteLine(JsonSerializer.Serialize(new { role, shortSide, farFirst, result }));
        Assert.Null(result.Issue);
        // Existing far exit is active during the opening jump; newly allocated near exit is not.
        var execution = Assert.Single(result.Executions);
        Assert.Equal("far", execution.LevelId);
        Assert.Equal(expected, prior.Executions.Concat(result.Executions).Sum(x => x.PnlUsd));
    }

    [PostgresTheory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public async Task H02_Production_revision_of_zero_exit_share_cannot_retroactively_win_the_opening_jump(string role, bool shortSide, bool farFirst)
    {
        var (old, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        await using var f = await Fixture.CreateAsync();
        var id = await f.ArmAsync(old);
        f.Clock.Now = First.AddMinutes(1).AddSeconds(10);
        Assert.True(await f.EvaluateAsync([prefix[0]]));
        // Save after entry proof, effective at 12:03. The 12:02 candle still uses the original revision.
        f.Clock.Now = First.AddMinutes(1).AddSeconds(11);
        await f.ReviseAsync(id, current);
        f.Clock.Now = First.AddMinutes(3).AddSeconds(10);
        Assert.True(await f.EvaluateAsync([prefix[1], last]));
        var state = await f.StateAsync(id);
        var account = await f.AccountAsync();
        output.WriteLine(JsonSerializer.Serialize(new { role, shortSide, farFirst, play = (await f.PlayAsync(id)).Summary, state, account.BalanceUsd, account.AvailableBalanceUsd }));
        Assert.Empty(state.Issues);
        Assert.Equal("far", Assert.Single(state.Executions, x => x.Role == role).LevelId);
        Assert.Equal((4000 + expected).ToString(CultureInfo.InvariantCulture), account.BalanceUsd);
        Assert.Equal(account.BalanceUsd, account.AvailableBalanceUsd);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H03_Newly_allocated_exit_outside_current_range_cannot_use_a_pre_activation_crossing(bool shortSide)
    {
        var near = X("near", shortSide ? 90 : 110);
        var old = P(shortSide, [E("e", "100", [X("prior", shortSide ? 110 : 90)], [near with { Share = "0" }])]);
        var current = old with { Entries = [old.Entries[0] with { Targets = [near] }] };
        var origin = shortSide ? 110 : 90;
        var prefix = Eval(old, [C(0, 100, 99, 101), C(1, origin, origin - 1, origin + 1)]);
        Assert.Null(prefix.Issue); Assert.Equal(2, prefix.Executions.Count);
        var end = shortSide ? 85 : 115;
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(current, 2, 2)], prefix.Executions,
            First.AddMinutes(2), origin, [C(2, end, end - 1, end + 1)], First.AddMinutes(3), previousCloseAtUtc: First.AddMinutes(2));
        output.WriteLine(JsonSerializer.Serialize(result));
        Assert.Null(result.Issue);
        Assert.Empty(result.Executions);
        Assert.Equal(First.AddMinutes(3), result.CheckpointUtc);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H04_Equal_price_competing_exits_hold_the_candle_without_losing_prior_facts(bool farFirst)
    {
        var a = X("a", 110); var b = X("b", 110);
        var plan = P(false, [E("e", "100", [X("prior", 90)], farFirst ? [b, a] : [a, b])]);
        var result = Eval(plan, [C(0, 100, 99, 101), C(1, 90, 89, 91), C(2, 100, 99, 111)]);
        Assert.True(result.Ambiguous); Assert.NotNull(result.Issue);
        Assert.Equal(2, result.Executions.Count); Assert.Equal(-50, result.Executions.Sum(x => x.PnlUsd));
        Assert.Equal(First.AddMinutes(2), result.CheckpointUtc);
        Assert.All(result.Executions, x => Assert.True(x.CandleOpenAtUtc < First.AddMinutes(2)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H05_Straddling_revision_rolls_back_all_entries_exits_and_declines_from_only_current_candle(bool ambiguousEntryFirst)
    {
        var a = E("a", "50", targets: [X("a-target", 130, "100")]);
        var b = E("b", "50", targets: [X("near", 150), X("far", 160)]);
        var old = P(false, ambiguousEntryFirst ? [b, a] : [a, b]);
        var revised = old with { Entries = old.Entries.Select(e => e.Id == "b" ? e with { Targets = [X("near", 110), X("far", 120)] } : e).ToArray() };
        var prior = Eval(old, [C(0, 100, 99, 101)]);
        Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(revised, 2, 1)], prior.Executions,
            First.AddMinutes(1), 100, [C(1, 115, 109, 131)], First.AddMinutes(2), previousCloseAtUtc: First.AddMinutes(1));
        Assert.True(result.Ambiguous); Assert.NotNull(result.Issue);
        Assert.Empty(result.Executions); Assert.Empty(result.DeclinedEntries ?? []);
        Assert.Equal(First.AddMinutes(1), result.CheckpointUtc); Assert.Equal(100, result.PreviousClose);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void H06_Explicit_zero_trade_minute_preserves_last_trade_source_for_first_crossing_order(bool farFirst)
    {
        var (old, _, prefix, _, _) = Dormant("target", false, farFirst);
        var plan = old with { Entries = [old.Entries[0] with { Targets = old.Entries[0].Targets.Select(x => x with { Share = "50" }).ToArray() }] };
        var result = Eval(plan, [prefix[0], prefix[1], C(2, 90, 90, 90, trades: 0), C(3, 125, 109, 126, 110)]);
        Assert.Null(result.Issue); Assert.False(result.Ambiguous);
        Assert.Equal("near", Assert.Single(result.Executions, x => x.Role == "target").LevelId);
        Assert.Equal(0, result.Executions.Sum(x => x.PnlUsd));
        Assert.DoesNotContain(result.Executions, x => x.CandleOpenAtUtc == First.AddMinutes(2));
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task H07_Production_dormant_exit_cannot_close_without_a_post_activation_touch(bool shortSide)
    {
        var near = X("near", shortSide ? 90 : 110);
        var old = P(shortSide, [E("e", "100", [X("prior", shortSide ? 110 : 90)], [near with { Share = "0" }])]);
        var current = old with { Entries = [old.Entries[0] with { Targets = [near] }] };
        await using var f = await Fixture.CreateAsync();
        var id = await f.ArmAsync(old);
        f.Clock.Now = First.AddMinutes(1).AddSeconds(10);
        Assert.True(await f.EvaluateAsync([C(0, 100, 99, 101)]));
        f.Clock.Now = First.AddMinutes(1).AddSeconds(11);
        await f.ReviseAsync(id, current);
        var origin = shortSide ? 110 : 90; var end = shortSide ? 85 : 115;
        f.Clock.Now = First.AddMinutes(3).AddSeconds(10);
        Assert.True(await f.EvaluateAsync([C(1, origin, origin - 1, origin + 1), C(2, end, end - 1, end + 1)]));
        var state = await f.StateAsync(id); var account = await f.AccountAsync();
        output.WriteLine(JsonSerializer.Serialize(new { shortSide, play = (await f.PlayAsync(id)).Summary, state, account.BalanceUsd, account.AvailableBalanceUsd }));
        Assert.Empty(state.Issues);
        Assert.Equal(2, state.Executions.Count);
        Assert.Equal("5", state.OpenQuantity);
        Assert.Equal("3950", account.BalanceUsd);
        Assert.Null(account.AccountValueUsd);
    }

    [Theory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public void H08_Absent_level_control_has_no_gap_priority_and_old_active_level_wins(string role, bool shortSide, bool farFirst)
    {
        var (old, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        old = old with { Entries = [old.Entries[0] with
        {
            Stops = old.Entries[0].Stops.Where(x => x.Id != "near").ToArray(),
            Targets = old.Entries[0].Targets.Where(x => x.Id != "near").ToArray()
        }] };
        var prior = Eval(old, prefix); Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId, [A(old, 1, 0), A(current, 2, 2)], prior.Executions,
            First.AddMinutes(2), prior.PreviousClose, [last], First.AddMinutes(3), previousCloseAtUtc: prior.PreviousCloseAtUtc);
        Assert.Null(result.Issue); Assert.Equal("far", Assert.Single(result.Executions).LevelId);
        Assert.Equal(expected, prior.Executions.Concat(result.Executions).Sum(x => x.PnlUsd));
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task H09_Production_ambiguity_has_no_partial_current_candle_cash_or_exit_facts(bool ambiguousEntryFirst)
    {
        var a = E("a", "50", targets: [X("a-target", 130, "100")]);
        var b = E("b", "50", targets: [X("near", 150), X("far", 160)]);
        var old = P(false, ambiguousEntryFirst ? [b, a] : [a, b]);
        var revised = old with { Entries = old.Entries.Select(e => e.Id == "b" ? e with { Targets = [X("near", 110), X("far", 120)] } : e).ToArray() };
        await using var f = await Fixture.CreateAsync(); var id = await f.ArmAsync(old);
        f.Clock.Now = First.AddMinutes(1).AddSeconds(10);
        Assert.True(await f.EvaluateAsync([C(0, 100, 99, 101)]));
        f.Clock.Now = First.AddMinutes(1).AddSeconds(11);
        await f.ReviseAsync(id, revised);
        f.Clock.Now = First.AddMinutes(3).AddSeconds(10);
        Assert.True(await f.EvaluateAsync([C(1, 100, 99, 101), C(2, 115, 109, 131)]));
        var state = await f.StateAsync(id); var account = await f.AccountAsync();
        Assert.Equal("ambiguous", state.Status);
        Assert.Equal(2, state.Executions.Count); Assert.All(state.Executions, x => Assert.Equal("entry", x.Role));
        Assert.Equal("10", state.OpenQuantity); Assert.Equal("0", state.RealizedPnlUsd);
        Assert.Equal("4000", account.BalanceUsd); Assert.Null(account.AccountValueUsd); Assert.Null(account.AvailableBalanceUsd);
        await using var db = f.Db.Context(f.OwnerId);
        Assert.Equal(First.AddMinutes(2), (await db.PaperSimulations.SingleAsync()).CheckpointUtc);
        Assert.Equal(1, await db.PaperLedgerEntries.CountAsync()); // creation seed only, no candidate exit cash
        Assert.Equal(PlayStatus.Open, (await db.Plays.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("target", false, false)] [InlineData("target", false, true)]
    [InlineData("target", true, false)] [InlineData("target", true, true)]
    [InlineData("stop", false, false)] [InlineData("stop", false, true)]
    [InlineData("stop", true, false)] [InlineData("stop", true, true)]
    public void H10_Zero_share_intermediate_revision_breaks_continuous_gap_activation_across_native_empty_coverage(string role, bool shortSide, bool farFirst)
    {
        var (dormant, current, prefix, last, expected) = Dormant(role, shortSide, farFirst);
        var prior = Eval(current, prefix); Assert.Null(prior.Issue); Assert.Equal(2, prior.Executions.Count);
        var source = prior.PreviousClose!.Value;
        var movedLast = last with { OpenAtUtc = First.AddMinutes(3), CloseAtUtc = First.AddMinutes(4) };
        var result = PaperEvaluator.Evaluate(Owner, AccountId, PlayId,
            [A(current, 1, 0), A(dormant, 2, 2), A(current, 3, 3)], prior.Executions, First.AddMinutes(2), source,
            [C(2, source, source, source, trades: 0), movedLast], First.AddMinutes(4), previousCloseAtUtc: prior.PreviousCloseAtUtc);
        output.WriteLine(JsonSerializer.Serialize(new { role, shortSide, farFirst, result }));
        Assert.Null(result.Issue); Assert.Equal("far", Assert.Single(result.Executions).LevelId);
        Assert.Equal(expected, prior.Executions.Concat(result.Executions).Sum(x => x.PnlUsd));
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = Saved; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture(CoreDatabase database, Guid owner, Guid account) : IAsyncDisposable
    {
        public CoreDatabase Db = database; public Guid OwnerId = owner; public Guid Account = account; public Clock Clock = new();
        private PaperTradingService Paper(VesselDbContext db) => new(new PaperStore(db, TestUnitOfWork.For(db)), TestUnitOfWork.For(db), new ConfiguredOwnerDirectory(OwnerId), TestVenues.With(), Clock);
        private PlayService Plays(VesselDbContext db) => new(new PlayStore(db, TestUnitOfWork.For(db)), new MemoryObjectStore(), Clock, TestUnitOfWork.For(db), Paper(db));
        public static async Task<Fixture> CreateAsync()
        {
            var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
            await using var db = database.Context(owner);
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "A2", tradingMode: "paper");
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "A2", "paper", 4000); account.Configure(portfolio.Id, null, null);
            db.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, 4000, Saved), new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = 1 });
            await db.SaveChangesAsync(); return new(database, owner, account.Id);
        }
        public async Task<Guid> ArmAsync(PlayPlanDocument plan)
        {
            await using var db = Db.Context(OwnerId); var service = Plays(db);
            var saved = await service.CreateAsync(new(Account, "BTC", "venue", "A2 activation", plan), default, "paper");
            return (await service.ChangeStatusAsync(saved.Summary.Id, new(saved.Summary.Version, "planned"), default)).Summary.Id;
        }
        public async Task ReviseAsync(Guid id, PlayPlanDocument plan)
        {
            await using var db = Db.Context(OwnerId); var service = Plays(db); var saved = await service.GetAsync(id, default);
            await service.UpdateAsync(id, new(saved.Summary.Version, Account, "BTC", "venue", saved.Summary.Title, plan, saved.Drawings, saved.Review, "Activate previously zero-share exit"), default);
        }
        public async Task<bool> EvaluateAsync(PaperCandle[] candles)
        {
            await using var db = Db.Context(OwnerId); var service = Paper(db);
            return await service.EvaluateAccountAsync(await service.PrepareAccountAsync(Account, default),
                new Dictionary<string, IReadOnlyList<PaperCandle>> { ["BTC"] = candles }, PaperTradingService.SettledThrough(Clock.Now), default);
        }
        public async Task<PlayDto> PlayAsync(Guid id)
        { await using var db = Db.Context(OwnerId); return await Plays(db).GetAsync(id, default); }
        public async Task<PaperPlayState> StateAsync(Guid id)
        { await using var db = Db.Context(OwnerId); return await Paper(db).StateAsync(id, default); }
        public async Task<AccountDto> AccountAsync()
        {
            await using var db = Db.Context(OwnerId); var work = TestUnitOfWork.For(db);
            return await new WorkspaceService(new WorkspaceStore(db, work, Clock), new CoreOwner(OwnerId), TestVenues.With(), work).AccountAsync(Account, default, "paper");
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
