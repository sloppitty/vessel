using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
public sealed class PaperExitActivationIntegrationTests(ITestOutputHelper output) : PaperExitActivationTestsValues
{

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
