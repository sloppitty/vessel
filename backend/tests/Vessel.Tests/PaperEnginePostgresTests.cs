using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PaperEnginePostgresTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstOpen = new(2026, 10, 20, 12, 1, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task Atomic_execution_cash_margin_checkpoint_and_lifecycle_are_idempotent()
    {
        await using var f = await Fixture.CreateAsync();
        var playId = await f.ArmAsync("100");
        f.Clock.Now = FirstOpen.AddMinutes(2);
        var candidate = Assert.Single(await f.DueAsync());
        Assert.True(await f.EvaluateAsync(candidate, [Candle(0, 100, 99, 101), Candle(1, 110, 109, 111)]));
        Assert.False(await f.EvaluateAsync(candidate, [Candle(0, 100, 99, 101), Candle(1, 110, 109, 111)]));
        await using var db = f.Db.Context(f.Owner);
        var facts = await db.PaperExecutions.OrderBy(x => x.OccurredAtUtc).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(("entry", 100m, 1m, 0m, 100m), (facts[0].Role, facts[0].Price, facts[0].Quantity, facts[0].PnlUsd, facts[0].MarginUsd));
        Assert.Equal(("target", 110m, 1m, 10m, -100m), (facts[1].Role, facts[1].Price, facts[1].Quantity, facts[1].PnlUsd, facts[1].MarginUsd));
        Assert.All(facts, x => Assert.Equal(x.CandleCloseAtUtc, x.OccurredAtUtc));
        Assert.Equal(4010, await db.PaperLedgerEntries.SumAsync(x => x.AmountUsd));
        Assert.Equal(0, await db.PaperReservations.SumAsync(x => x.AmountUsd));
        Assert.Equal(0, await db.PaperExecutions.SumAsync(x => x.MarginUsd));
        Assert.Equal(PlayStatus.Closed, (await db.Plays.FindAsync(playId))!.Status);
        Assert.Equal(FirstOpen.AddMinutes(2), (await db.PaperSimulations.FindAsync(playId))!.CheckpointUtc);
        Assert.Equal(2, await db.StatusChanges.CountAsync(x => x.Source == StatusChangeSource.Paper));
        Assert.Empty(await db.Fills.ToListAsync());
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.ManualExecutions.ToListAsync());
    }

    [PostgresFact]
    public async Task Concurrent_play_reservations_cannot_spend_the_same_seed()
    {
        await using var f = await Fixture.CreateAsync();
        async Task<bool> TryArm()
        {
            try { await f.ArmAsync("3000"); return true; }
            catch (ProblemException error) when (error.StatusCode == 409) { return false; }
        }
        var admitted = await Task.WhenAll(TryArm(), TryArm());
        Assert.Single(admitted, x => x);
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal(3000, await db.PaperReservations.SumAsync(x => x.AmountUsd));
        Assert.Equal(4000, await db.PaperLedgerEntries.SumAsync(x => x.AmountUsd));
    }

    [PostgresFact]
    public async Task Another_plays_realized_loss_cannot_spend_an_old_pending_reservation_into_a_deficit()
    {
        await using var f = await Fixture.CreateAsync();
        var losing = Plan("1000") with { Leverage = "10", Entries = [
            new("e", "Loss", "#ffffff", "100", "100", [new("se", "price", "70", "100")], [new("te", "price", "110", "100")])] };
        var lossId = await f.ArmAsync("1000", losing);
        var waitingId = await f.ArmAsync("2900", Plan("2900") with { Entries = [
            new("e", "Later entry", "#ffffff", "100", "100", [], [])] }, "ETH");
        f.Clock.Now = FirstOpen.AddMinutes(3);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
        {
            ["BTC"] = [Candle(0, 100, 99, 101), Candle(1, 70, 69, 71), Candle(2, 70, 69, 71)],
            ["ETH"] = [Candle(0, 200, 199, 201), Candle(1, 200, 199, 201), Candle(2, 100, 99, 101)]
        });
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal(1000, await db.PaperLedgerEntries.SumAsync(x => x.AmountUsd));
        Assert.Empty(await db.PaperExecutions.Where(x => x.PlayId == waitingId).ToListAsync());
        var state = (await db.PaperSimulations.FindAsync(waitingId))!;
        Assert.Equal("gap", state.State);
        Assert.Contains("Insufficient paper margin", state.IssuesJson);
        Assert.Equal(2900, (await db.PaperReservations.FindAsync(waitingId))!.AmountUsd);
        Assert.Equal(PlayStatus.Closed, (await db.Plays.FindAsync(lossId))!.Status);
    }

    [PostgresFact]
    public async Task R2_Simultaneous_reserved_entries_must_not_depend_on_whole_play_evaluation_order()
    {
        async Task<(decimal Entered, string State, decimal Cash)> EvaluateOrder(bool lossFirst)
        {
            await using var f = await Fixture.CreateAsync();
            var lossPlan = Plan("1000") with { Leverage = "10", Entries = [
                new("e", "Loss", "#ffffff", "100", "100", [new("se", "price", "70", "100")], [])] };
            var otherPlan = Plan("2900") with { Entries = [new("e", "B", "#ffffff", "100", "100", [], [])] };
            var lossId = await f.ArmAsync("1000", lossPlan);
            var otherId = await f.ArmAsync("2900", otherPlan);
            f.Clock.Now = FirstOpen.AddMinutes(2);
            var due = await f.DueAsync();
            var candles = new[] { Candle(0, 100, 99, 101), Candle(1, 70, 69, 71) };
            foreach (var id in lossFirst ? new[] { lossId, otherId } : new[] { otherId, lossId })
                await f.EvaluateAsync(due.Single(x => x.PlayId == id), candles);
            await using var db = f.Db.Context(f.Owner);
            return (await db.PaperExecutions.Where(e => e.PlayId == otherId && e.Role == "entry").SumAsync(e => e.Quantity),
                (await db.PaperSimulations.FindAsync(otherId))!.State, await db.PaperLedgerEntries.SumAsync(e => e.AmountUsd));
        }
        var lossFirst = await EvaluateOrder(true);
        var otherFirst = await EvaluateOrder(false);
        Assert.Equal((29m, "ready", 1000m), lossFirst);
        Assert.Equal(lossFirst, otherFirst);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_instrument_and_reversed_candidate_order_share_the_same_pre_minute_capital(bool reverse)
    {
        await using var f = await Fixture.CreateAsync();
        await f.ArmAsync("1000", Plan("1000") with { Leverage = "10", Entries = [
            new("e", "Loss", "#ffffff", "100", "100", [new("se", "price", "70", "100")], [])] });
        var other = await f.ArmAsync("2900", Plan("2900") with { Entries = [
            new("e", "Other market", "#ffffff", "100", "100", [], [])] }, "ETH");
        f.Clock.Now = FirstOpen.AddMinutes(2);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
        {
            ["ETH"] = [Candle(0, 100, 99, 101), Candle(1, 100, 99, 101)],
            ["BTC"] = [Candle(0, 100, 99, 101), Candle(1, 70, 69, 71)]
        }, reverse);
        await using var db = f.Db.Context(f.Owner);
        Assert.Equal(29, await db.PaperExecutions.Where(e => e.PlayId == other && e.Role == "entry").SumAsync(e => e.Quantity));
        Assert.Equal("ready", (await db.PaperSimulations.FindAsync(other))!.State);
        Assert.Equal(1000, await db.PaperLedgerEntries.SumAsync(e => e.AmountUsd));
    }

    [PostgresFact]
    public async Task A_due_account_includes_later_armed_not_yet_due_siblings_before_later_losses()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ArmAsync("1000", Plan("1000") with { Leverage = "10", Entries = [
            new("e", "Loss", "#ffffff", "100", "100", [new("se", "price", "70", "100")], [])] });
        f.Clock.Now = FirstOpen.AddSeconds(30);
        var later = await f.ArmAsync("2900", Plan("2900") with { Entries = [
            new("e", "Later armed", "#ffffff", "100", "100", [], [])] }, "ETH");
        await using (var db = f.Db.Context(f.Owner))
        {
            (await db.PaperSimulations.FindAsync(later))!.NextCheckAtUtc = FirstOpen.AddHours(1);
            await db.SaveChangesAsync();
        }
        f.Clock.Now = FirstOpen.AddMinutes(2);
        Assert.Single(await f.DueAsync());
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
        {
            ["BTC"] = [Candle(0, 100, 99, 101), Candle(1, 70, 69, 71)],
            ["ETH"] = [Candle(1, 100, 99, 101)]
        });
        await using var result = f.Db.Context(f.Owner);
        Assert.Equal(29, await result.PaperExecutions.Where(e => e.PlayId == later && e.Role == "entry").SumAsync(e => e.Quantity));
        Assert.Equal(FirstOpen.AddMinutes(2), (await result.PaperSimulations.FindAsync(later))!.CheckpointUtc);
        Assert.Equal(1000, await result.PaperLedgerEntries.SumAsync(e => e.AmountUsd));
    }

    [PostgresTheory]
    [InlineData("new-arm")]
    [InlineData("revision")]
    [InlineData("disable")]
    [InlineData("pause")]
    [InlineData("settings")]
    public async Task Public_reads_cannot_apply_a_stale_or_incomplete_account_set(string change)
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.ArmAsync("100");
        f.Clock.Now = FirstOpen.AddMinutes(1);
        PaperAccountCandidate snapshot;
        await using (var db = f.Db.Context(f.Owner))
            snapshot = await f.Service(db).PrepareAccountAsync(f.AccountId, default);
        if (change == "new-arm") await f.ArmAsync("100");
        else
        {
            await using var db = f.Db.Context(f.Owner);
            if (change == "settings")
                await f.Service(db).UpdateSettingsAsync(new(true, 2), default);
            else if (change == "disable")
                (await db.Accounts.FindAsync(f.AccountId))!.UpdateSettings("Disabled", (await db.Accounts.FindAsync(f.AccountId))!.PortfolioId, false);
            else
            {
                var play = (await db.Plays.FindAsync(first))!;
                if (change == "pause") play.Pause(f.Clock.Now);
                else db.PlanRevisions.Add(play.ChangePlan(PlayDocuments.Serialize(Plan("101")), "Revision during public read", f.Clock.Now)!);
            }
            await db.SaveChangesAsync();
        }
        await using var result = f.Db.Context(f.Owner);
        Assert.False(await f.Service(result).EvaluateAccountAsync(snapshot, new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 100, 99, 101)] }, f.Clock.Now, default));
        Assert.Empty(await result.PaperExecutions.ToListAsync());
        Assert.Equal(FirstOpen, (await result.PaperSimulations.FindAsync(first))!.CheckpointUtc);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Neither_partial_sibling_sets_nor_partial_instrument_windows_can_commit(bool missingWindow)
    {
        await using var f = await Fixture.CreateAsync();
        await f.ArmAsync("100");
        await f.ArmAsync("100", instrument: "ETH");
        f.Clock.Now = FirstOpen.AddMinutes(1);
        await using var db = f.Db.Context(f.Owner);
        var service = f.Service(db);
        var candidate = await service.PrepareAccountAsync(f.AccountId, default);
        if (!missingWindow) candidate = candidate with { Plays = candidate.Plays.Take(1).ToArray() };
        var windows = new Dictionary<string, IReadOnlyList<PaperCandle>> { ["BTC"] = [Candle(0, 100, 99, 101)] };
        if (!missingWindow) windows["ETH"] = [Candle(0, 100, 99, 101)];
        Assert.False(await service.EvaluateAccountAsync(candidate, windows, f.Clock.Now, default));
        Assert.Empty(await db.PaperExecutions.ToListAsync());
        Assert.All(await db.PaperSimulations.ToListAsync(), s => Assert.Equal(FirstOpen, s.CheckpointUtc));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_unequal_frontiers_never_replay_against_a_later_retained_execution_even_on_closed_plays(bool closed)
    {
        await using var f = await Fixture.CreateAsync();
        var advanced = await f.ArmAsync("1000", Plan("1000") with { Leverage = "10" });
        var waiting = await f.ArmAsync("2900");
        await using (var db = f.Db.Context(f.Owner))
        {
            var play = (await db.Plays.FindAsync(advanced))!;
            play.ApplyPaperExecutionState(true, false, FirstOpen.AddMinutes(1));
            var entry = new PaperExecution { Id = Guid.NewGuid(), OwnerId = f.Owner, AccountId = f.AccountId, PlayId = advanced,
                EntryId = "e", Role = "entry", Price = 100, Quantity = 100, MarginUsd = 1000, PlanVersion = 1,
                CandleOpenAtUtc = FirstOpen, CandleCloseAtUtc = FirstOpen.AddMinutes(1), OccurredAtUtc = FirstOpen.AddMinutes(1) };
            db.Add(entry);
            var state = (await db.PaperSimulations.FindAsync(advanced))!;
            state.CheckpointUtc = FirstOpen.AddMinutes(closed ? 2 : 1);
            (await db.PaperReservations.FindAsync(advanced))!.AmountUsd = 0;
            if (closed)
            {
                var exit = new PaperExecution { Id = Guid.NewGuid(), OwnerId = f.Owner, AccountId = f.AccountId, PlayId = advanced,
                    EntryId = "e", LevelId = "se", Role = "stop", Price = 70, Quantity = 100, PnlUsd = -3000,
                    MarginUsd = -1000, PlanVersion = 1, CandleOpenAtUtc = FirstOpen.AddMinutes(1),
                    CandleCloseAtUtc = FirstOpen.AddMinutes(2), OccurredAtUtc = FirstOpen.AddMinutes(2) };
                db.AddRange(exit, PaperLedgerEntry.Realized(exit));
                play.ApplyPaperExecutionState(true, true, FirstOpen.AddMinutes(2));
                state.NextCheckAtUtc = null;
            }
            await db.SaveChangesAsync();
        }
        f.Clock.Now = FirstOpen.AddMinutes(3);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 100, 99, 101), Candle(1, 100, 99, 101), Candle(2, 100, 99, 101)] });
        await using var result = f.Db.Context(f.Owner);
        Assert.Empty(await result.PaperExecutions.Where(e => e.PlayId == waiting).ToListAsync());
        Assert.Equal("gap", (await result.PaperSimulations.FindAsync(waiting))!.State);
        Assert.Contains("Incompatible paper account checkpoints", (await result.PaperSimulations.FindAsync(waiting))!.IssuesJson);
        Assert.Equal(closed ? 1000 : 4000, await result.PaperLedgerEntries.SumAsync(e => e.AmountUsd));
    }

    [PostgresFact]
    public async Task Missing_minute_freezes_frontier_and_new_admissions_without_fake_facts()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.ArmAsync("100");
        f.Clock.Now = FirstOpen.AddMinutes(2);
        var candidate = Assert.Single(await f.DueAsync());
        await f.EvaluateAsync(candidate, [Candle(1, 110, 109, 111)]);
        await using var db = f.Db.Context(f.Owner);
        var state = (await db.PaperSimulations.FindAsync(id))!;
        Assert.Equal(("gap", FirstOpen), (state.State, state.CheckpointUtc));
        Assert.NotEqual("[]", state.IssuesJson);
        Assert.Null(state.NextCheckAtUtc);
        Assert.Empty(await db.PaperExecutions.ToListAsync());
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.ArmAsync("100"));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(100, await db.PaperReservations.SumAsync(x => x.AmountUsd));
    }

    [PostgresFact]
    public async Task Failed_read_holds_new_admissions_but_keeps_retry_and_frontier()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.ArmAsync("100");
        f.Clock.Now = FirstOpen.AddMinutes(1);
        var due = Assert.Single(await f.DueAsync());
        await using (var db = f.Db.Context(f.Owner))
            await f.Service(db).RecordReadFailureAsync(due, default);
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.ArmAsync("100"));
        Assert.Equal(409, error.StatusCode);
        await using (var db = f.Db.Context(f.Owner))
        {
            var state = (await db.PaperSimulations.FindAsync(id))!;
            Assert.Equal(FirstOpen, state.CheckpointUtc);
            Assert.NotNull(state.NextCheckAtUtc);
        }
        f.Clock.Now = FirstOpen.AddMinutes(2);
        due = Assert.Single(await f.DueAsync());
        await f.EvaluateAsync(due, [Candle(0, 100, 99, 101), Candle(1, 100, 99, 101)]);
        // Own transient read issue is recoverable by a complete replay, not a permanent admission freeze.
        await f.ArmAsync("100");
    }

    [PostgresFact]
    public async Task Settings_are_owner_private_validated_and_suspend_without_replaying_the_disabled_window()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.ArmAsync("100");
        await using (var db = f.Db.Context(f.Owner))
        {
            var service = f.Service(db);
            Assert.Equal(new(true, 1), await service.GetSettingsAsync(default));
            await Assert.ThrowsAsync<ProblemException>(() => service.UpdateSettingsAsync(new(true, 0), default));
            await service.UpdateSettingsAsync(new(false, 60), default);
            Assert.Equal("disabled", (await service.StateAsync(id, default)).Status);
        }
        await using (var db = f.Db.Context(Guid.NewGuid()))
        {
            var service = f.Service(db);
            Assert.Equal(new(false, 60), await service.GetSettingsAsync(default));
            await Assert.ThrowsAsync<ProblemException>(() => service.StateAsync(id, default));
        }
        f.Clock.Now = FirstOpen.AddHours(3);
        Assert.Empty(await f.DueAsync());
        await using (var db = f.Db.Context(f.Owner))
        {
            var service = f.Service(db);
            await service.UpdateSettingsAsync(new(true, 1), default);
            var state = await service.StateAsync(id, default);
            Assert.Equal("gap", state.Status);
            Assert.Equal("0", state.EnteredQuantity);
            Assert.Single(state.Issues);
        }
        Assert.Empty(await f.DueAsync());
    }

    [PostgresFact]
    public async Task Revision_reserves_each_entries_largest_pending_allocation_not_only_largest_plan_total()
    {
        await using var f = await Fixture.CreateAsync();
        var plan = Plan("100") with
        {
            Entries = [Entry("a", "90", "100"), Entry("b", "10", "100")]
        };
        var id = await f.ArmAsync("100", plan);
        f.Clock.Now = FirstOpen.AddMinutes(5); // An overdue check cannot be postponed by a new plan.
        await using var db = f.Db.Context(f.Owner);
        var work = TestUnitOfWork.For(db);
        await work.ExecuteAsync(() => new PaperStore(db, work).WithAccountLockAsync(f.AccountId, async () =>
        {
            var play = (await db.Plays.FindAsync(id))!;
            var revised = plan with { Entries = [Entry("a", "10", "100"), Entry("b", "90", "100")] };
            db.PlanRevisions.Add(play.ChangePlan(PlayDocuments.Serialize(revised), "Redistribute", f.Clock.Now.AddSeconds(1))!);
            await f.Service(db).ActivateRevisionAsync(play, default);
            return true;
        }, default), default);
        Assert.Equal(180, (await db.PaperReservations.FindAsync(id))!.AmountUsd);
        Assert.Equal(FirstOpen.AddMinutes(1), (await db.PaperSimulations.FindAsync(id))!.NextCheckAtUtc);
    }

    private static PlanEntry Entry(string id, string share, string price) =>
        new(id, id, "#ffffff", share, price, [new("s" + id, "price", "90", "100")], [new("t" + id, "price", "110", "100")]);
    private static PlayPlanDocument Plan(string size) =>
        new("long", "margin", size, "1", null, [Entry("e", "100", "100")], new("", "", "", ""));
    private static PaperCandle Candle(int minute, decimal value, decimal low, decimal high) =>
        new(FirstOpen.AddMinutes(minute), FirstOpen.AddMinutes(minute + 1), value, high, low, value);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture(CoreDatabase database, Guid owner, Guid accountId) : IAsyncDisposable
    {
        public CoreDatabase Db { get; } = database;
        public Guid Owner { get; } = owner;
        public Guid AccountId { get; } = accountId;
        public Clock Clock { get; } = new();
        public PaperTradingService Service(VesselDbContext db) => new(new PaperStore(db, TestUnitOfWork.For(db)),
            TestUnitOfWork.For(db), new ConfiguredOwnerDirectory(db.CurrentOwnerId), TestVenues.With(), Clock);
        public static async Task<Fixture> CreateAsync()
        {
            var db = await CoreDatabase.CreateAsync();
            var owner = Guid.NewGuid();
            await using var context = db.Context(owner);
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", 4000);
            account.Configure(portfolio.Id, null, null);
            context.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, 4000, At),
                new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = 1 });
            await context.SaveChangesAsync();
            return new(db, owner, account.Id);
        }
        public async Task<Guid> ArmAsync(string size, PlayPlanDocument? plan = null, string instrument = "BTC")
        {
            await using var db = Db.Context(Owner);
            var unit = TestUnitOfWork.For(db);
            return await unit.ExecuteAsync(() => new PaperStore(db, unit).WithAccountLockAsync(AccountId, async () =>
            {
                var account = (await db.Accounts.FindAsync(AccountId))!;
                var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", instrument),
                    InstrumentSource.Venue, PlayDocuments.Serialize(plan ?? Plan(size)), Clock.Now);
                var planned = play.MarkPlanned(Clock.Now);
                db.AddRange(play, planned.Change, planned.Revision);
                await Service(db).ArmAsync(play, default);
                return play.Id;
            }, default), default);
        }
        public async Task<List<PaperDuePlay>> DueAsync()
        {
            await using var db = Db.Context(Owner);
            return await new PaperStore(db, TestUnitOfWork.For(db)).DueAsync(Clock.Now, 100, default);
        }
        public async Task<bool> EvaluateAsync(PaperDuePlay due, PaperCandle[] candles)
        {
            await using var db = Db.Context(Owner);
            return await Service(db).EvaluateAsync(due, candles, Clock.Now, default);
        }
        public async Task<bool> EvaluateAccountAsync(IReadOnlyDictionary<string, IReadOnlyList<PaperCandle>> candles, bool reverse = false)
        {
            await using var db = Db.Context(Owner);
            var service = Service(db);
            var candidate = await service.PrepareAccountAsync(AccountId, default);
            if (reverse) candidate = candidate with { Plays = candidate.Plays.Reverse().ToArray() };
            return await service.EvaluateAccountAsync(candidate, candles, Clock.Now, default);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
