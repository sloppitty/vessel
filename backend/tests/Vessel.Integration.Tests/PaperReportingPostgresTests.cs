using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Overview;
using Vessel.Application.Review;
using Vessel.Application.Sizing;
using Vessel.Application.Workspace;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Markets;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class PaperReportingPostgresTests
{
    [PostgresFact]
    public async Task Authenticated_paper_review_ignores_imported_positions_without_reading_activity_or_mixing_results()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var real = new ReviewFixture();
        var actual = real.Add(10);
        var paper = PaperReportingTestsValues.Paper(real.Owner, 30);
        var suspended = PaperReportingTestsValues.Paper(real.Owner, 1000);
        var gap = PaperReportingTestsValues.Paper(real.Owner, 2000, state: "gap", issues: ["Missing minute."]);
        var foreign = PaperReportingTestsValues.Paper(Guid.NewGuid(), 3000);
        foreach (var item in new[] { paper, suspended, gap, foreign })
        {
            await using var seed = database.Context(item.Account.OwnerId);
            seed.AddRange(item.Portfolio, item.Account, item.Facts.Play);
            seed.PlanRevisions.AddRange(item.Facts.Revisions);
            seed.PaperExecutions.AddRange(item.Facts.Paper!.Executions);
            seed.PaperSimulations.Add(new()
            {
                OwnerId = item.Account.OwnerId, AccountId = item.Account.Id, PlayId = item.Facts.Play.Id,
                State = item.Facts.Paper.State,
                IssuesJson = JsonSerializer.Serialize(item.Facts.Paper.Issues),
                SuspensionsJson = JsonSerializer.Serialize(item.Account.Id == suspended.Account.Id
                    ? new[] { new PaperSuspension("Suspended window.", PaperReportingTestsValues.At.AddHours(-1), PaperReportingTestsValues.At) }
                    : [])
            });
            await seed.SaveChangesAsync();
        }
        await using (var seed = database.Context(real.Owner))
        {
            seed.AddRange(real.Accounts.Single(), actual.Play);
            seed.PlanRevisions.AddRange(actual.Revisions);
            seed.Fills.AddRange(actual.Fills.Select(f => f.Fill));
            foreach (var linked in actual.Fills)
            {
                var order = TestOrders.For(actual.Play, linked.Fill.ExternalOrderId, linked.Fill.Side);
                seed.Orders.Add(order);
                seed.OrderLinks.Add(new PlayOrderLink(actual.Play, order, linked.Role,
                    linked.Role == OrderLinkRole.Entry ? "e" : null, null, OrderLinkState.Linked, OrderLinkSource.Owner, ReviewClock.Now));
            }
            // Another actual round trip, deliberately unassigned and therefore eligible for real Review's new source.
            seed.Fills.AddRange(
                DiaryPostgresTests.Imported(real.Accounts.Single(), "position-open", "p1", "buy", "open", 1, 100, 10),
                DiaryPostgresTests.Imported(real.Accounts.Single(), "position-close", "p2", "sell", "close", 1, 120, 11, 20));
            await seed.SaveChangesAsync();
        }
        var positionReads = 0;
        await using var factory = new CoreApiFactory(real.Owner, connection: database.ConnectionString, configure: services =>
        {
            services.AddSingleton<TimeProvider>(new PaperReportingTestsSupport.Clock());
            services.AddScoped<IReviewPositionSource>(sp => new ObservedPositions(
                () => sp.GetRequiredService<ActivityReviewPositions>(), () => positionReads++));
        });
        const string path = "/api/review?period=all&positions=include&minPlays=1000";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "&tradingMode=paper")).StatusCode);
        using var client = factory.AuthorizedClient();
        foreach (var positions in new[] { "include", "auto", "exclude" })
        {
            var report = (await client.GetFromJsonAsync<ReviewDto>(
                $"/api/review?period=all&tradingMode=paper&positions={positions}&minPlays=1000"))!;
            Assert.Equal(0, positionReads);
            Assert.Equal(("paper", "30", 1, 0), (report.TradingMode, report.Metrics.Net, report.Metrics.Count, report.Metrics.Positions));
            Assert.Equal(("exclude", false), (report.Positions!.Mode, report.Positions.Included));
            Assert.Null(report.Positions.Positions); Assert.Null(report.Positions.Omitted);
            var row = Assert.Single(report.Plays);
            Assert.Equal((paper.Facts.Play.Id, "paper", "paper-simulation"), (row.Id, row.ExecutionSource, row.Local!.Basis));
            Assert.Null(row.OperationId);
            Assert.All(report.Periods, p => Assert.Equal(0, p.Positions));
            Assert.Equal(2, report.Coverage.IncompleteExecutions);
            Assert.Equal(0, report.Coverage.UnassignedFills);
            Assert.DoesNotContain(report.Options.Accounts, a => a.Id == actual.Play.AccountId || a.Id == foreign.Account.Id);
        }
        var empty = (await client.GetFromJsonAsync<ReviewDto>(path + $"&tradingMode=paper&accountId={actual.Play.AccountId}"))!;
        Assert.Equal(0, positionReads);
        Assert.Empty(empty.Plays); Assert.Null(empty.Metrics.Net);
        var realReport = (await client.GetFromJsonAsync<ReviewDto>(path))!;
        Assert.Equal(1, positionReads);
        Assert.Equal(("real", "30", 2, 1), (realReport.TradingMode, realReport.Metrics.Net, realReport.Metrics.Count, realReport.Metrics.Positions));
        Assert.True(realReport.Positions!.Included);
        Assert.All(realReport.Plays, p => Assert.Equal("imported", p.ExecutionSource));
        Assert.Contains(realReport.Plays, p => p.Id == actual.Play.Id);
        Assert.Contains(realReport.Plays, p => p.Id is null && p.Origin == "position" && p.OperationId is not null);
    }

    private sealed class ObservedPositions(Func<IReviewPositionSource> source, Action onRead) : IReviewPositionSource
    {
        public Task<IReadOnlyList<ReviewPosition>> ReadAsync(IReadOnlySet<Guid> accountIds, CancellationToken ct)
        {
            onRead();
            return source().ReadAsync(accountIds, ct);
        }
    }

    [PostgresFact]
    public async Task Resumed_then_closed_simulation_stays_descriptively_excluded_from_review_and_sizing()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var paper = PaperReportingTestsValues.Paper(owner);
        await using var db = database.Context(owner);
        db.AddRange(paper.Portfolio, paper.Account, paper.Facts.Play);
        db.PlanRevisions.AddRange(paper.Facts.Revisions);
        db.PaperExecutions.AddRange(paper.Facts.Paper!.Executions);
        db.PaperSimulations.Add(new()
        {
            OwnerId = owner, AccountId = paper.Account.Id, PlayId = paper.Facts.Play.Id, State = "ready",
            SuspensionsJson = JsonSerializer.Serialize(new[] { new PaperSuspension(
                "Paper evaluation was suspended; the skipped window is not simulated.",
                PaperReportingTestsValues.At.AddHours(-2), PaperReportingTestsValues.At.AddHours(-1)) })
        });
        await db.SaveChangesAsync();
        var unit = TestUnitOfWork.For(db);
        var store = new SizingStore(db, unit);
        var clock = new PaperReportingTestsSupport.Clock();
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
        var paper = PaperReportingTestsValues.Paper(owner);
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
                InstrumentSource.Venue, paper.Facts.Play.Plan, PaperReportingTestsValues.At.AddHours(-2));
            db.Plays.Add(unresolved);
            db.PlanRevisions.Add(unresolved.MarkPlanned(PaperReportingTestsValues.At.AddHours(-2)).Revision);
            // Fatal evidence retains its terminal state; a transient issue alone is not a closed outcome.
            db.PaperSimulations.Add(new() { OwnerId = owner, AccountId = paper.Account.Id, PlayId = unresolved.Id, State = "gap",
                IssuesJson = "[\"A frozen unresolved window remains visible after paper is disabled.\"]" });
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Seed(owner, paper.Account.Id, 4000, PaperReportingTestsValues.At.AddDays(-1)));
            db.PaperLedgerEntries.Add(PaperLedgerEntry.Realized(paper.Facts.Paper.Executions[1]));
            await db.SaveChangesAsync();
            return true;
        }, default);

        var workspaceStore = new WorkspaceStore(db, unit);
        var workspace = new WorkspaceService(workspaceStore, new CoreOwner(owner), TestVenues.With(), unit);
        var clock = new PaperReportingTestsSupport.Clock();
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
        var paper = PaperReportingTestsValues.Paper(foreignOwner);
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
