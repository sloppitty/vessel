using Microsoft.EntityFrameworkCore;
using Vessel.Application.Paper;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

[Collection("Paper corrective PostgreSQL")]
public sealed class PaperExitOrderingIntegrationTests : PaperExitOrderingTestsValues
{

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

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
