using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;
using Vessel.Application.Venues;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Search;

namespace Vessel.Tests;

/// <summary>
/// Issue #88: the golden journal stored in PostgreSQL and read through the real stores, with the same checks as the
/// in-memory run, and the Plays list projection (docs/architecture/golden-journal.md, section 8).
/// </summary>
public sealed class GoldenJournalPostgresTests
{
    private static async Task<GoldenJournal> SeedAsync(CoreDatabase database)
    {
        var j = new GoldenJournal();
        await using var db = database.Context(GoldenJournal.Owner);
        db.Portfolios.Add(j.Swing);
        db.Accounts.AddRange(j.Accounts);
        db.Plays.AddRange(j.Plays);
        db.PlanRevisions.AddRange(j.Revisions);
        db.StatusChanges.AddRange(j.StatusChanges);
        db.Orders.AddRange(j.Orders);
        db.Fills.AddRange(j.Fills);
        db.OrderLinks.AddRange(j.Links);
        db.Snapshots.AddRange(j.Snapshots);
        await db.SaveChangesAsync();
        // The list reads the search projection; build it as the one-time initialization does.
        var clock = new GoldenClock();
        await new PlaySearchRebuild(db, Projected(db), clock).RunAsync(true, true, 100, default);
        return j;
    }

    private static UnitOfWork Projected(VesselDbContext db) => new(db, [new PlaySearchProjector(db, new GoldenClock())]);

    private static GoldenJournalScreens Screens(VesselDbContext db)
    {
        var unit = TestUnitOfWork.For(db);
        var sizing = new SizingStore(db, unit);
        return GoldenJournalScreens.Create(new ActivityStore(db), new WorkspaceStore(db, unit), new ReviewStore(db, sizing), sizing,
            new RiskStore(db, unit), unit);
    }

    private static async Task<Dictionary<string, PlayOutcomeSummaryDto>> ListAsync(VesselDbContext db, GoldenJournal j)
    {
        var unit = TestUnitOfWork.For(db);
        var list = await new PlayService(new PlayStore(db, unit, new GoldenClock()), new MemoryObjectStore(), new GoldenClock(), unit)
            .ListAsync("all", null, null, null, "100", default);
        return list.Plays.ToDictionary(p => GoldenJournalChecks.Name(j, p.Id), p => p.Outcome!);
    }

    [PostgresFact]
    public async Task The_golden_journal_reads_the_same_through_the_real_stores_and_the_plays_list()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var j = await SeedAsync(database);
        await using var db = database.Context(GoldenJournal.Owner);
        var screens = Screens(db);
        await GoldenJournalChecks.ActivityAsync(j, screens);
        await GoldenJournalChecks.OverviewAsync(j, screens);
        await GoldenJournalChecks.ReviewPlaysAsync(j, screens);
        await GoldenJournalChecks.ReviewPositionsAsync(j, screens);
        await GoldenJournalChecks.SizingAsync(j, screens);
        await GoldenJournalChecks.OpenRiskAsync(j, screens);
        await GoldenJournalChecks.IdentitiesAsync(j, screens);

        // Section 8: the list's projected outcomes are Review's, row for row: exit time, result and R.
        var list = await ListAsync(db, j);
        var review = await screens.Review.GetAsync(new(Period: "all", Positions: "exclude"), default);
        Assert.Equal(6, review.Plays.Count);
        foreach (var row in review.Plays)
            Assert.Equal(new PlayOutcomeSummaryDto("scored", row.ClosedAtUtc, row.Net, row.RMultiple, null), list[GoldenJournalChecks.Name(j, row.Id)]);
        Assert.Equal(("scored", GoldenJournal.D(12), "9.8", "1.96"), (list["P1"].State, list["P1"].ExitAtUtc, list["P1"].ResultUsd, list["P1"].EstimatedR));
        Assert.Equal(("executions", GoldenJournal.D(8), (string?)null, (string?)null),
            (list["P6"].State, list["P6"].ExitAtUtc, list["P6"].ResultUsd, list["P6"].EstimatedR));
        Assert.Equal(("active", "active"), (list["P8"].State, list["P9"].State));
    }

    /// <summary>
    /// Section 8.1: a later refresh learns that P1's exit order left an execution out, and nothing else about the order
    /// changes. Review and Sizing drop P1 at once; the list must withhold its result too.
    /// </summary>
    [PostgresFact]
    public async Task A_later_omission_on_a_scored_plays_order_withdraws_its_list_result()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var j = await SeedAsync(database);
        await using var db = database.Context(GoldenJournal.Owner);
        var exit = j.Orders.Single(o => o.ExternalOrderId == j.Fill["a4"].ExternalOrderId).Id;
        var unit = Projected(db);
        await unit.ExecuteAsync(async () =>
        {
            (await db.Orders.SingleAsync(o => o.Id == exit)).ExecutionsOmitted = true;
            return true;
        }, default);

        await using var read = database.Context(GoldenJournal.Owner);
        var review = await Screens(read).Review.GetAsync(new(Period: "all", Positions: "exclude"), default);
        Assert.DoesNotContain(j.Play["P1"].Id, review.Plays.Select(p => p.Id));
        Assert.Equal(2, review.Coverage.IncompleteExecutions);
        var p1 = (await ListAsync(read, j))["P1"];
        Assert.Equal(("executions", (string?)null, (string?)null), (p1.State, p1.ResultUsd, p1.EstimatedR));
    }

    /// <summary>
    /// #92 through the import path: a refresh whose only news about P1's exit order is an omission marker naming it. The
    /// refresh flags the order and changes nothing else about it, and the list withholds P1's result like Review.
    /// </summary>
    [PostgresFact]
    public async Task A_refresh_that_only_reports_an_omission_on_a_scored_plays_order_withdraws_its_list_result()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var j = await SeedAsync(database);
        await using var db = database.Context(GoldenJournal.Owner);
        var exit = j.Orders.Single(o => o.ExternalOrderId == j.Fill["a4"].ExternalOrderId);
        var account = await db.Accounts.SingleAsync(a => a.Id == exit.AccountId);
        var unit = Projected(db);
        var omission = new VenueOmission("golden-a4-omitted", ExecutionOmission.Fee, "fee-bearing or unverified fee", exit.ContractId, null,
            exit.ExternalOrderId, GoldenJournal.D(12));
        var refresh = new PerpetualVenueReadResult(new(GoldenJournal.Now, "synthetic", 100, 100, 0, []), [], [], "Synthetic",
            PositionsComplete: true, FillHistoryTruncated: false) { Omissions = [omission] };
        await unit.ExecuteAsync(async () =>
        {
            await new WorkspaceStore(db, unit, new GoldenClock()).SaveRefreshAsync(account, refresh, default);
            return true;
        }, default);

        await using var read = database.Context(GoldenJournal.Owner);
        Assert.True((await read.Orders.AsNoTracking().SingleAsync(o => o.Id == exit.Id)).ExecutionsOmitted);
        var review = await Screens(read).Review.GetAsync(new(Period: "all", Positions: "exclude"), default);
        Assert.DoesNotContain(j.Play["P1"].Id, review.Plays.Select(p => p.Id));
        var p1 = (await ListAsync(read, j))["P1"];
        Assert.Equal(("executions", (string?)null, (string?)null), (p1.State, p1.ResultUsd, p1.EstimatedR));
    }

    /// <summary>
    /// #92 repair: a row left stale before the fix holds the current projection version and no recheck time, so only
    /// <c>play-search-rebuild --apply --all</c> rewrites it. The stale row is made here by a write the capture never sees.
    /// </summary>
    [PostgresFact]
    public async Task Rebuilding_all_projections_repairs_a_list_row_left_stale_by_an_earlier_omission()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var j = await SeedAsync(database);
        await using var db = database.Context(GoldenJournal.Owner);
        var exit = j.Orders.Single(o => o.ExternalOrderId == j.Fill["a4"].ExternalOrderId).Id;
        await db.Orders.Where(o => o.Id == exit).ExecuteUpdateAsync(s => s.SetProperty(o => o.ExecutionsOmitted, true));
        Assert.Equal("scored", (await ListAsync(db, j))["P1"].State);

        Assert.Equal(0, (await new PlaySearchRebuild(db, Projected(db), new GoldenClock()).RunAsync(true, false, 100, default)).Projected);
        Assert.Equal("scored", (await ListAsync(db, j))["P1"].State);
        Assert.Equal(j.Plays.Count, (await new PlaySearchRebuild(db, Projected(db), new GoldenClock()).RunAsync(true, true, 100, default)).Projected);
        await using var read = database.Context(GoldenJournal.Owner);
        var p1 = (await ListAsync(read, j))["P1"];
        Assert.Equal(("executions", (string?)null, (string?)null), (p1.State, p1.ResultUsd, p1.EstimatedR));
    }
}
