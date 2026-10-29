using Microsoft.EntityFrameworkCore;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyReviewPostgresTests
{
    [PostgresFact] // Rule i
    public async Task Old_unrelated_fill_history_does_not_disable_every_check()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture { Position = 0 };
        await database.Seed(f);
        await using (var bulk = database.Scope(f))
        {
            for (var i = 0; i < 50_001; i++)
            {
                f.Fill($"old-{i:D6}", $"old-order-{i:D6}", 1, f.Clock.Now.AddDays(-400).AddSeconds(i));
                f.Store.Fills[^1].ContractId = "ETH";
                bulk.Db.Add(f.Store.Fills[^1]);
            }
            await bulk.Db.SaveChangesAsync();
        }
        await using (var first = database.Scope(f)) await first.Evaluate(f.Observation());
        f.Advance(60);
        await using var second = database.Scope(f);
        Assert.False((await second.Evaluate(f.Observation())).Incomplete);
        Assert.Contains(await second.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == "position.missing");
    }

    [PostgresFact] // Rule i
    public async Task Load_reads_only_watched_or_recent_plays_and_fills_after_the_earliest_open_entry()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        var settled = f.AddPlay("long", 1); settled.MarkClosed("fixture", f.Clock.Now.AddHours(-2));
        var watched = f.AddPlay("long", 1); watched.MarkClosed("fixture", f.Clock.Now.AddHours(-2));
        var recent = f.AddPlay("long", 1); recent.MarkClosed("fixture", f.Clock.Now.AddSeconds(-10));
        f.Fill("before-entry", "unrelated-1", 1, f.Clock.Now.AddDays(-1));
        f.Fill("after-entry", "unrelated-2", 1, f.Clock.Now.AddMinutes(-5));
        f.Fill("other-contract", "unrelated-3", 1, f.Clock.Now.AddMinutes(-5));
        f.Store.Fills[^1].ContractId = "ETH";
        await database.Seed(f);
        await using (var scope = database.Scope(f))
        {
            // Every linked order of settled and recent has a resolved, current watch; watched has none.
            foreach (var link in await scope.Db.OrderLinks.Where(x => x.PlayId == settled.Id || x.PlayId == recent.Id).ToListAsync())
                scope.Db.Add(new MonitorExitWatch { LinkId = link.Id, OwnerId = link.OwnerId, AccountId = link.AccountId, PlayId = link.PlayId,
                    LinkRevisionAtUtc = link.UpdatedAtUtc, StartedAtUtc = link.UpdatedAtUtc,
                    EndedAtUtc = f.Clock.Now.AddHours(-1), Outcome = MonitorWatchOutcomes.Absent });
            await scope.Db.SaveChangesAsync();
        }
        await using var read = database.Scope(f);
        await read.Work.ExecuteAsync(async () =>
        {
            var facts = await read.Store.LoadAsync(f.Account.Id, f.Clock.Now.AddMinutes(-1), [], default);
            Assert.Equal(new[] { f.Play.Id, watched.Id, recent.Id }.Order(), facts.Plays.Select(x => x.Id).Order());
            Assert.DoesNotContain(facts.Fills, x => x.SourceFillId is "before-entry" or "other-contract");
            Assert.Contains(facts.Fills, x => x.SourceFillId == "after-entry");
            Assert.Empty(facts.TruncatedFillContracts!);
            return true;
        }, default);
    }

    [PostgresFact] // Rule d
    public async Task Resting_terminal_entry_keeps_the_account_eligible_until_absent()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Store.Links.RemoveAll(x => x.Role != OrderLinkRole.Entry);
        f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
        await database.Seed(f);
        await using var scope = database.Scope(f);
        var store = MonitorTestPaths.Store(scope.Db, scope.Work);
        Assert.True(await store.EligibleAsync(f.Account.Id, f.Clock.Now, default));
        await scope.Work.ExecuteAsync(async () =>
        {
            await store.UpdateWatchesAsync(f.Account.Id, new([], f.Clock.Now, "Fresh absence", OpenOrdersComplete: true), f.Clock.Now, default);
            await scope.Work.SaveChangesAsync(default);
            return true;
        }, default);
        Assert.False(await store.EligibleAsync(f.Account.Id, f.Clock.Now, default));
    }
}
