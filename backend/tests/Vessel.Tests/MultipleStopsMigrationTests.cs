using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class MultipleStopsMigrationTests
{
    private const string PreviousMigration = "20261003101827_PlayExecution";

    private const string SingleStopPlan = """
        {"direction":"long","sizingMode":"margin","size":"250","leverage":"3","budgetOverride":null,
         "entries":[{"id":"entry-1","name":"Entry 1","color":"#b9c9e4","share":"60","price":"100","stop":{"id":"stop-1","unit":"percent","value":"5"},"targets":[{"id":"target-1","unit":"price","value":"110","share":"100"}]},
                    {"id":"entry-2","name":"Entry 2","color":"#edd49e","share":"40","price":"98","stop":{"id":"stop-2","unit":"price","value":""},"targets":[]}],
         "notes":{"thesis":"","invalidation":"","strategy":"","evidence":""}}
        """;

    [PostgresFact]
    public async Task Migration_turns_each_single_stop_into_a_stop_list_and_names_it_on_stop_links()
    {
        await using var database = await CoreDatabase.CreateAsync(PreviousMigration);
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        Guid account = Guid.NewGuid(), play = Guid.NewGuid(), stopLink = Guid.NewGuid(), targetLink = Guid.NewGuid();
        var address = "0x" + new string('a', 40);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO accounts (\"Id\", \"OwnerId\", \"Name\", \"VenueId\", \"Address\", \"SyncStatus\", \"IsEnabled\") VALUES ({account}, {owner}, 'Main', 'hyperliquid', {address}, 'synced', true)");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO plays ("Id", "OwnerId", "AccountId", "VenueId", "ContractId", "Status", "InstrumentSource", "Title", "Review", "Plan", "PlanRevision", "Version", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({0}, {1}, {2}, 'hyperliquid', 'BTC', 'Planned', 'Venue', 'Breakout', '', {3}::jsonb, 1, 1, now(), now())
            """, play, owner, account, SingleStopPlan);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO play_plan_revisions ("Id", "OwnerId", "PlayId", "Number", "Status", "Reason", "CreatedAtUtc", "Plan")
            VALUES ({0}, {1}, {2}, 1, 'Planned', 'Planned', now(), {3}::jsonb)
            """, Guid.NewGuid(), owner, play, SingleStopPlan);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO play_order_links ("Id", "OwnerId", "PlayId", "AccountId", "OrderId", "Role", "EntryId", "TargetId", "State", "Source", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({stopLink}, {owner}, {play}, {account}, '12', 'Stop', 'entry-1', NULL, 'Linked', 'Automatic', now(), now()),
                   ({targetLink}, {owner}, {play}, {account}, '13', 'Target', 'entry-1', 'target-1', 'Linked', 'Automatic', now(), now())
            """);

        // The order identity change refuses retained links, so this data migration is checked just before it.
        await db.GetService<IMigrator>().MigrateAsync(TestOrders.BeforeOrderIdentity);

        // Only the plan: later migrations add Play columns this schema does not have yet.
        var plan = PlayDocuments.Normalize(PlayDocuments.Read(await db.Plays.Select(p => p.Plan).SingleAsync()));
        Assert.Equal([new PlanExit("stop-1", "percent", "5", "100")], plan.Entries[0].Stops);
        Assert.Equal([new PlanExit("stop-2", "price", "", "100")], plan.Entries[1].Stops);
        Assert.Equal("110", plan.Entries[0].Targets.Single().Value);
        // Only the plan: later migrations add revision columns (1.5's Preflight) this schema does not have yet.
        var revision = PlayDocuments.Read(await db.PlanRevisions.Select(r => r.Plan).SingleAsync());
        Assert.Equal("stop-1", revision.Entries[0].Stops.Single().Id);

        var links = await db.Database.SqlQueryRaw<MigratedLink>("""SELECT "Id", "Role", "EntryId", "LevelId" FROM play_order_links""")
            .ToDictionaryAsync(l => l.Id);
        string Key(MigratedLink link) => PlayOrderLink.Key(Enum.Parse<OrderLinkRole>(link.Role), link.EntryId, link.LevelId);
        Assert.Equal(("stop|entry-1|stop-1", "Stop"), (Key(links[stopLink]), links[stopLink].Role));
        Assert.Equal("target|entry-1|target-1", Key(links[targetLink]));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private sealed record MigratedLink(Guid Id, string Role, string? EntryId, string? LevelId);
}
