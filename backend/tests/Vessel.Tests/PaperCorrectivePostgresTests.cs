using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Monitoring;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Worker;

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

        public static async Task<Engine> CreateAsync(decimal capital = 4000)
        {
            var db = await CoreDatabase.CreateAsync();
            var owner = Guid.NewGuid();
            await using var context = db.Context(owner);
            var portfolio = new Portfolio(Guid.NewGuid(), owner, "Paper", tradingMode: "paper");
            var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Paper", "paper", capital);
            account.Configure(portfolio.Id, null, null);
            context.AddRange(portfolio, account, PaperLedgerEntry.Seed(owner, account.Id, capital, At),
                new PaperSettings { OwnerId = owner, Enabled = true, CheckIntervalMinutes = 1 });
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

    [PostgresTheory]
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Owner_suspension_of_unfilled_transient_failure_recovers_without_false_unresolved_outcomes(string status)
    {
        await using var f = await Engine.CreateAsync();
        var a = await f.PlanAsync(Btc, "BTC");
        var b = await f.PlanAsync(Eth, "ETH");
        f.Clock.Now = FirstOpen.AddMinutes(1).AddSeconds(15);
        await f.RecordAccountReadFailureAsync();
        await f.StatusAsync(b, status, status == "cancelled" ? "changed-mind" : null);
        var suspended = await f.StateAsync(b);
        Assert.Equal("disabled", suspended.Status);
        Assert.Empty(suspended.Issues);
        Assert.Empty(suspended.Suspensions);
        Assert.False(suspended.HasUnscoredHistory);
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
            Assert.Equal(1, (await review.GetAsync(query, default)).Coverage.ClosedCandidates);
            Assert.Empty((await review.ExclusionsAsync(query, default)).Items);
        }
        if (status == "paused") await f.StatusAsync(b, "planned");
        _ = await f.PlanAsync(Btc, "SOL");
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
    [InlineData("paused")]
    [InlineData("cancelled")]
    public async Task Owner_suspension_preserves_true_gap_evidence_and_held_capital(string status)
    {
        await using var f = await Engine.CreateAsync();
        var id = await f.PlanAsync(Btc, "BTC");
        f.Clock.Now = FirstOpen.AddMinutes(3).AddSeconds(15);
        await f.EvaluateAccountAsync(new Dictionary<string, IReadOnlyList<PaperCandle>>
            { ["BTC"] = [Candle(0, 105, 104, 106), Candle(2, 105, 104, 106)] });
        var gap = await f.StateAsync(id);
        Assert.Equal("gap", gap.Status);
        await f.StatusAsync(id, status, status == "cancelled" ? "changed-mind" : null);
        var suspended = await f.StateAsync(id);
        Assert.Equal(gap.Issues, suspended.Issues);
        Assert.Equal("gap", suspended.Status);
        Assert.Empty(suspended.Executions);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => f.PlanAsync(Btc, "SOL"))).StatusCode);
        var account = await f.PaperAccountAsync();
        Assert.Null(account.AvailableBalanceUsd);
        Assert.Null(account.AccountValueUsd);
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
