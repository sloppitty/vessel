using System.Text.Json;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Overview;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Markets;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PaperReportingPostgresTests
{
    [PostgresFact]
    public async Task Resumed_then_closed_simulation_stays_descriptively_excluded_from_review_and_sizing()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var paper = PaperReportingTests.Paper(owner);
        await using var db = database.Context(owner);
        db.AddRange(paper.Portfolio, paper.Account, paper.Facts.Play);
        db.PlanRevisions.AddRange(paper.Facts.Revisions);
        db.PaperExecutions.AddRange(paper.Facts.Paper!.Executions);
        db.PaperSimulations.Add(new()
        {
            OwnerId = owner, AccountId = paper.Account.Id, PlayId = paper.Facts.Play.Id, State = "ready",
            SuspensionsJson = JsonSerializer.Serialize(new[] { new PaperSuspension(
                "Paper evaluation was suspended; the skipped window is not simulated.",
                PaperReportingTests.At.AddHours(-2), PaperReportingTests.At.AddHours(-1)) })
        });
        await db.SaveChangesAsync();
        var unit = TestUnitOfWork.For(db);
        var store = new SizingStore(db, unit);
        var clock = new PaperReportingTests.Clock();
        var review = new ReviewService(new ReviewStore(db, store), clock);
        var sizing = new SizingService(store, new CoreOwner(owner), clock, unit,
            new ActivityService(new ActivityStore(db), new WorkspaceStore(db, unit), clock));
        Assert.Empty((await review.GetAsync(new(Period: "all", TradingMode: "paper"), default)).Plays);
        Assert.Contains(Assert.Single((await review.ExclusionsAsync(new(Period: "all", TradingMode: "paper"), default)).Items)
            .Reasons, r => r.Contains("suspended", StringComparison.OrdinalIgnoreCase));
        var sized = await sizing.GetAsync(default, "paper");
        Assert.Equal("paper", sized.Record.Source);
        Assert.Equal(0, sized.Record.ClosedPlays);
        Assert.Empty(sized.Record.Recent);
    }

    [PostgresFact]
    public async Task Reports_scope_mode_before_counts_facets_paging_coverage_and_real_fallback()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var realFixture = new ReviewFixture();
        var real = realFixture.Add(10);
        // Use the fixture's owner throughout this database owner scope.
        owner = realFixture.Owner;
        var paper = PaperReportingTests.Paper(owner);
        await using var db = database.Context(owner);
        var unit = TestUnitOfWork.For(db);
        await unit.ExecuteAsync(async () =>
        {
            db.Accounts.Add(realFixture.Accounts.Single()); db.Plays.Add(real.Play);
            db.PlanRevisions.AddRange(real.Revisions);
            db.Fills.AddRange(real.Fills.Select(f => f.Fill));
            db.Portfolios.Add(paper.Portfolio); db.Accounts.Add(paper.Account);
            db.Plays.Add(paper.Facts.Play); db.PlanRevisions.AddRange(paper.Facts.Revisions);
            db.PaperExecutions.AddRange(paper.Facts.Paper!.Executions);
            db.PaperSimulations.Add(new() { OwnerId = owner, AccountId = paper.Account.Id, PlayId = paper.Facts.Play.Id, State = "ready" });
            var unresolved = new Play(Guid.NewGuid(), paper.Account, new PerpetualInstrument("hyperliquid", "ETH"),
                InstrumentSource.Venue, paper.Facts.Play.Plan, PaperReportingTests.At.AddHours(-2));
            db.Plays.Add(unresolved);
            db.PlanRevisions.Add(unresolved.MarkPlanned(PaperReportingTests.At.AddHours(-2)).Revision);
            db.PaperSimulations.Add(new() { OwnerId = owner, AccountId = paper.Account.Id, PlayId = unresolved.Id, State = "disabled",
                IssuesJson = "[\"A frozen unresolved window remains visible after paper is disabled.\"]" });
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Seed(owner, paper.Account.Id, 4000, PaperReportingTests.At.AddDays(-1)));
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Realized(paper.Facts.Paper.Executions[1]));
            await db.SaveChangesAsync();
            return true;
        }, default);

        var workspaceStore = new WorkspaceStore(db, unit);
        var workspace = new WorkspaceService(workspaceStore, new CoreOwner(owner), TestVenues.With(), unit);
        var clock = new PaperReportingTests.Clock();
        var activity = new ActivityService(new ActivityStore(db), workspaceStore, clock);
        var sizingStore = new SizingStore(db, unit);
        var sizing = new SizingService(sizingStore, new CoreOwner(owner), clock, unit, activity);
        var review = new ReviewService(new ReviewStore(db, sizingStore), clock);
        var overview = new OverviewSummaryService(workspaceStore, workspace, activity, clock);

        var realActivity = await activity.PageAsync(new(), default);
        Assert.Equal(1, realActivity.TotalCount);
        Assert.Equal(2, realActivity.RetainedFillCount);
        Assert.Equal([real.Play.AccountId], realActivity.Facets.AccountIds);
        Assert.DoesNotContain(realActivity.Coverage, c => c.AccountId == paper.Account.Id);
        var paperActivity = await activity.PageAsync(new(TradingMode: "paper"), default);
        Assert.Equal((1, 0, 2), (paperActivity.TotalCount, paperActivity.RetainedFillCount, paperActivity.PaperExecutionCount));
        Assert.Equal([paper.Account.Id], paperActivity.Facets.AccountIds);
        var expanded = await activity.FillsAsync(paper.Account.Id, paper.Facts.Play.Id.ToString(), default, "paper");
        Assert.Empty(expanded.Fills); Assert.Equal(2, expanded.PaperExecutions!.Count);
        var mismatched = await Assert.ThrowsAsync<ProblemException>(() => activity.FillsAsync(paper.Account.Id, paper.Facts.Play.Id.ToString(), default));
        Assert.Equal(404, mismatched.StatusCode);
        Assert.Equal("activity", (await sizing.GetAsync(default)).Record.Source);
        var paperSizing = await sizing.GetAsync(default, "paper");
        Assert.Equal(("paper", "20"), (paperSizing.Record.Source, paperSizing.Record.Recent.Single().NetResultUsd));
        Assert.Empty((await review.GetAsync(new(Period: "all"), default)).Options.Portfolios);
        var paperReview = await review.GetAsync(new(Period: "all", TradingMode: "paper"), default);
        Assert.Equal(("20", 2, 0, 1), (paperReview.Metrics.Net, paperReview.Coverage.ClosedCandidates,
            paperReview.Coverage.UnassignedFills, paperReview.Coverage.IncompleteExecutions));
        var exclusions = await review.ExclusionsAsync(new(Period: "all", TradingMode: "paper"), default);
        Assert.Contains(Assert.Single(exclusions.Items).Reasons, r => r.Contains("frozen unresolved"));
        Assert.Equal([paper.Portfolio.Id.ToString()], paperReview.Options.Portfolios.Select(p => p.Id));
        var realOverview = await overview.GetAsync(null, "all", null, default);
        var paperOverview = await overview.GetAsync(null, "all", null, default, "paper");
        Assert.Equal(1, realOverview.Totals.AccountCount);
        Assert.Equal((1, "4020", "20", 0, 2), (paperOverview.Totals.AccountCount, paperOverview.Totals.Balance.AmountUsd,
            paperOverview.Totals.RealizedResult.AmountUsd, paperOverview.Totals.ImportedFillCount, paperOverview.Totals.PaperExecutionCount));

        var before = JsonSerializer.Serialize(realActivity);
        var oldSizing = JsonSerializer.Serialize(await sizing.GetAsync(default));
        var oldOverview = JsonSerializer.Serialize(realOverview);
        await unit.ExecuteAsync(async () =>
        {
            var state = await db.PaperSimulations.FindAsync(paper.Facts.Play.Id);
            state!.State = "gap"; state.IssuesJson = "[\"Synthetic gap after a previously retained result.\"]";
            paper.Account.UpdateSettings("Disabled paper", paper.Portfolio.Id, false);
            await db.SaveChangesAsync();
            return true;
        }, default);
        Assert.Equal(before, JsonSerializer.Serialize(await activity.PageAsync(new(), default)));
        Assert.Equal(oldSizing, JsonSerializer.Serialize(await sizing.GetAsync(default)));
        Assert.Equal(oldOverview, JsonSerializer.Serialize(await overview.GetAsync(null, "all", null, default)));
        Assert.Empty((await activity.PageAsync(new(TradingMode: "paper"), default)).Operations);
    }

    [PostgresFact]
    public async Task Paper_reports_are_owner_scoped_and_never_read_foreign_simulations()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var foreignOwner = Guid.NewGuid();
        var paper = PaperReportingTests.Paper(foreignOwner);
        await using (var foreign = database.Context(foreignOwner))
        {
            foreign.Portfolios.Add(paper.Portfolio); foreign.Accounts.Add(paper.Account); foreign.Plays.Add(paper.Facts.Play);
            foreign.PlanRevisions.AddRange(paper.Facts.Revisions); foreign.PaperExecutions.AddRange(paper.Facts.Paper!.Executions);
            foreign.PaperSimulations.Add(new() { OwnerId = foreignOwner, AccountId = paper.Account.Id, PlayId = paper.Facts.Play.Id });
            await foreign.SaveChangesAsync();
        }
        await using var db = database.Context(owner);
        var facts = await new ActivityStore(db).ReadAsync(null, "paper", default);
        Assert.Empty(facts.Accounts); Assert.Empty(facts.PaperPlays);
        Assert.Empty(await new SizingStore(db, TestUnitOfWork.For(db)).ClosedPlaysAsync("paper", default));
        var problem = await Assert.ThrowsAsync<ProblemException>(() => new ActivityStore(db).ReadAsync(paper.Account.Id, "paper", default));
        Assert.Equal(404, problem.StatusCode);
    }
}
