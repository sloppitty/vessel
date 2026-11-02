using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vessel.Application.Events;
using Vessel.Persistence.Events;
using Vessel.Domain.Workspace;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Review;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed partial class ExecutionPostgresTests
{
    private static ReviewService Review(Harness h) =>
        new(new ReviewStore(h.Db, new SizingStore(h.Db, TestUnitOfWork.For(h.Db))), TimeProvider.System);

    private static async Task<int> StatusOf(Func<Task> action) =>
        (await Assert.ThrowsAsync<ProblemException>(action)).StatusCode;

    [PostgresFact]
    public async Task A_stuck_open_Play_is_declared_unresolved_left_out_of_Review_and_sizing_and_returns_to_open()
    {
        // Phase 1.3a acceptance (More4.CloseApi, Exec.SharedExit, Exec.FlipOrder): the Play's fills can never settle it.
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var id = play.Summary.Id;
        h.Orders.Orders.Add(Order("21", "buy", 100.01m, "filled"));
        AddFills(h, Fill("e1", "21", "Open Long", 100m, 1m));
        Assert.Equal("open", (await h.CheckAsync(id)).Status);
        var open = await h.Plays.GetAsync(id, default);
        var version = open.Summary.Version;

        Assert.Equal(400, await StatusOf(() => h.Execution.DeclareUnresolvedAsync(id, new(version, "liquidated", "Gone."), default)));
        Assert.Equal(400, await StatusOf(() => h.Execution.DeclareUnresolvedAsync(id, new(version, "shared-close", " "), default)));
        Assert.Equal(409, await StatusOf(() => h.Execution.DeclareUnresolvedAsync(id, new(version - 1, "shared-close", "Gone."), default)));
        var declared = await h.Execution.DeclareUnresolvedAsync(id, new(version, "shared-close", "Closed with another play's position."), default);
        Assert.Equal("unresolved", declared.Summary.Status);
        Assert.NotNull(declared.Summary.EndedAtUtc);

        // The declaration keeps the execution facts at that moment.
        var change = (await h.Plays.HistoryAsync(id, default)).StatusChanges.Last();
        Assert.Equal(("open", "unresolved", "shared-close", "Closed with another play's position.", "owner"),
            (change.From, change.To, change.UnresolvedReason, change.Note, change.Source));
        var evidence = change.Evidence!;
        Assert.Equal(("1", "0", "1", 1), (evidence.Entered, evidence.Exited, evidence.Open, evidence.OrderCount));
        Assert.Equal(("21", "entry", "entry-1", "1"), (evidence.Orders[0].OrderId, evidence.Orders[0].Role, evidence.Orders[0].EntryId, evidence.Orders[0].Filled));
        Assert.Equal(PlayReceipts.UnresolvedNote, (await h.Execution.GetAsync(id, default)).Receipt!.EstimatedRReason);

        // Needs review and All list it; Closed does not. Counts add up.
        async Task<IReadOnlyList<Guid>> Ids(string view) => (await h.Plays.ListAsync(view, null, null, null, null, default)).Plays.Select(p => p.Id).ToList();
        Assert.Equal((id, id), (Assert.Single(await Ids("review")), Assert.Single(await Ids("all"))));
        Assert.Empty(await Ids("closed"));
        var counts = await h.Plays.CountsAsync(null, null, default);
        Assert.Equal((1, 1, 0, 1), (counts.Unresolved, counts.Review, counts.Closed, counts.All));

        // No result: Review excludes it with its declaration, and sizing counts it beside the ladder only.
        var report = await Review(h).GetAsync(new(Period: "all"), default);
        Assert.Equal((1, 0), (report.Coverage.Unresolved, report.Coverage.ClosedCandidates));
        Assert.Empty(report.Plays);
        var exclusion = Assert.Single((await Review(h).ExclusionsAsync(new(Period: "all"), default)).Items);
        Assert.Equal((id, "shared-close", "Closed with another play's position.", "1"),
            (exclusion.PlayId, exclusion.Unresolved!.Reason, exclusion.Unresolved.Note, exclusion.Unresolved.Evidence!.Entered));
        Assert.Contains("a shared close", Assert.Single(exclusion.Reasons));
        var sizing = await h.Sizing.GetAsync(default);
        Assert.Equal((1, 0), (sizing.Record.Unresolved, sizing.Record.ClosedPlays));

        // It takes no new links until it returns to Open.
        Assert.Equal(409, await StatusOf(() => h.Execution.LinkAsync(id, new LinkOrderRequest("21", "exit"), default)));

        // It is reviewed like a Closed Play; returning to Open clears the completion and says so.
        var completed = await h.Plays.CompleteReviewAsync(id, new(declared.Summary.Version), default);
        Assert.Equal("completed", completed.Summary.ReviewCompletion);
        Assert.Single(await Ids("reviewed"));
        Assert.Equal(400, await StatusOf(() => h.Execution.ReturnToOpenAsync(id, new(completed.Summary.Version, ""), default)));
        Assert.Equal(409, await StatusOf(() => h.Execution.ReturnToOpenAsync(id, new(completed.Summary.Version - 1, "Back."), default)));
        var reopened = await h.Execution.ReturnToOpenAsync(id, new(completed.Summary.Version, "The shared close was another play's."), default);
        Assert.Equal(("open", (DateTimeOffset?)null, (string?)null), (reopened.Summary.Status, reopened.Summary.EndedAtUtc, reopened.Summary.ReviewCompletion));
        var history = await h.Plays.HistoryAsync(id, default);
        Assert.Equal(("unresolved", "open", "The shared close was another play's.", "shared-close"), (history.StatusChanges.Last().From,
            history.StatusChanges.Last().To, history.StatusChanges.Last().Note, history.StatusChanges.Last().UnresolvedReason));
        Assert.Equal(ReviewActions.Cleared, history.Reviews!.Last().Action);
        Assert.Equal(0, (await h.Plays.CountsAsync(null, null, default)).Unresolved);

    }

    [PostgresFact]
    public async Task Only_a_Play_with_imported_executions_is_declared_unresolved()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        // Planned, not Open.
        Assert.Equal(409, await StatusOf(() => h.Execution.DeclareUnresolvedAsync(play.Summary.Id, new(play.Summary.Version, "other", "Stuck."), default)));
        Assert.Equal(409, await StatusOf(() => h.Execution.ReturnToOpenAsync(play.Summary.Id, new(play.Summary.Version, "Back."), default)));
    }

    [PostgresFact]
    public async Task A_late_exit_fill_on_a_linked_order_closes_an_unresolved_Play_with_the_closed_event()
    {
        // Phase 1.3a acceptance: an account with no other active Play; the exit order was linked before the declaration.
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var id = play.Summary.Id;
        h.Orders.Orders.AddRange([Order("21", "buy", 100.01m, "filled"), Order("23", "sell", 110m, reduceOnly: true)]);
        AddFills(h, Fill("e1", "21", "Open Long", 100m, 1m));
        var execution = await h.CheckAsync(id);
        Assert.Equal(("open", "target"), (execution.Status, Assert.Single(execution.Links, l => l.Order!.OrderId == "23").Role));
        var open = await h.Plays.GetAsync(id, default);
        var declared = await h.Execution.DeclareUnresolvedAsync(id, new(open.Summary.Version, "flip", "Looked flipped in the venue history."), default);
        await h.Plays.CompleteReviewAsync(id, new(declared.Summary.Version), default);

        h.Orders.Orders[1] = Order("23", "sell", 110m, "filled", reduceOnly: true);
        AddFills(h, Fill("x1", "23", "Close Long", 110m, 1m));
        execution = await h.CheckAsync(id);
        Assert.Equal("closed", execution.Status);
        var closed = await h.Plays.GetAsync(id, default);
        Assert.Null(closed.Summary.ReviewCompletion);
        var history = await h.Plays.HistoryAsync(id, default);
        Assert.Equal(["open", "unresolved", "closed"], history.StatusChanges.Select(c => c.To).TakeLast(3));
        Assert.Equal(("unresolved", "venue"), (history.StatusChanges.Last().From, history.StatusChanges.Last().Source));
        Assert.Equal(ReviewActions.Cleared, history.Reviews!.Last().Action);
        // Closed from venue facts, it is scored like any other.
        Assert.Single((await Review(h).GetAsync(new(Period: "all"), default)).Plays);
    }

    [PostgresFact]
    public async Task An_unresolved_Play_s_linked_stop_is_watched_from_each_declaration()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var id = play.Summary.Id;
        h.Orders.Orders.AddRange([Order("21", "buy", 100.01m, "filled"), Order("22", "sell", 94m, trigger: 95m, type: "Stop Market", reduceOnly: true)]);
        AddFills(h, Fill("e1", "21", "Open Long", 100m, 1m));
        var execution = await h.CheckAsync(id);
        var stop = Assert.Single(execution.Links, l => l.Role == "stop");
        Assert.Equal("22", stop.Order!.OrderId);

        async Task<(DateTimeOffset Ended, MonitorExitWatch Watch)> DeclareAndCheck(string note, PlayExecutionService? service = null)
        {
            var current = await h.Plays.GetAsync(id, default);
            var declared = await (service ?? h.Execution).DeclareUnresolvedAsync(id, new(current.Summary.Version, "other", note), default);
            await h.CheckAsync(id);
            // The filled entry order is watched too; this is the stop's watch.
            var watch = await h.Db.Set<MonitorExitWatch>().AsNoTracking().SingleAsync(w => w.LinkId == stop.Id);
            // PostgreSQL keeps microseconds; the response carries the time as it was set.
            var ended = declared.Summary.EndedAtUtc!.Value;
            return (ended.AddTicks(-(ended.Ticks % 10)), watch);
        }
        var (first, firstWatch) = await DeclareAndCheck("Stuck once.");
        // The watch starts at the declaration, the Play's end, as a Closed Play's does at its close.
        Assert.Equal((first, (string?)null), (firstWatch.StartedAtUtc, firstWatch.Outcome));

        var current = await h.Plays.GetAsync(id, default);
        await h.Execution.ReturnToOpenAsync(id, new(current.Summary.Version, "Relinking."), default);
        var (second, secondWatch) = await DeclareAndCheck("Stuck again.");
        Assert.True(second > first);
        Assert.Equal((second, (string?)null), (secondWatch.StartedAtUtc, secondWatch.Outcome));

        // The clock steps back before a third declaration: the return to Open dropped the earlier watch, so the third
        // still starts its own rather than keeping the second's window.
        current = await h.Plays.GetAsync(id, default);
        await h.Execution.ReturnToOpenAsync(id, new(current.Summary.Version, "Relinking again."), default);
        var stepped = new SteppedBack(second.AddTicks(-10));
        var (third, thirdWatch) = await DeclareAndCheck("Stuck a third time.", new(new PlayStore(h.Db, TestUnitOfWork.For(h.Db)),
            new ExecutionStore(h.Db, TestUnitOfWork.For(h.Db)), TestVenues.With(h.Orders), stepped, TestUnitOfWork.For(h.Db)));
        Assert.True(third < second);
        Assert.Equal((third, (string?)null), (thirdWatch.StartedAtUtc, thirdWatch.Outcome));
    }

    private sealed class SteppedBack(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [PostgresFact]
    public async Task Rolling_back_the_UnresolvedStatus_migration_is_refused_while_an_unresolved_history_exists()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var play = await PlannedPlay(h, account);
        var id = play.Summary.Id;
        h.Orders.Orders.Add(Order("21", "buy", 100.01m, "filled"));
        AddFills(h, Fill("e1", "21", "Open Long", 100m, 1m));
        await h.CheckAsync(id);
        var open = await h.Plays.GetAsync(id, default);
        var declared = await h.Execution.DeclareUnresolvedAsync(id, new(open.Summary.Version, "other", "Stuck."), default);
        // Even back at Open, the history still names Unresolved, which older code cannot read.
        await h.Execution.ReturnToOpenAsync(id, new(declared.Summary.Version, "Back."), default);
        var migrator = h.Db.GetService<IMigrator>();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261020093407_ReviewCompletion"));
        Assert.Contains("Restore the pre-upgrade backup", refused.MessageText);
    }
}

/// <summary>Phase 1.3a: the declaration and a later settlement through an event-capturing unit of work.</summary>
public sealed class UnresolvedEventTests
{
    [VesselEventPostgresFact]
    public async Task A_declaration_is_a_status_change_with_its_reason_and_a_settled_unresolved_Play_closes_with_the_closed_event()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var now = DateTimeOffset.UtcNow;
        var (entryOrder, exitOrder) = (Order(owner, account.Id, "entry", "buy", now), Order(owner, account.Id, "exit", "sell", now));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play);
            await seed.SaveChangesAsync();
        }
        var capture = new VesselEventMutationCapture();
        await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(capture).Options, new CoreOwner(owner));
        var store = new VesselEventStore(db, capture, new NoOutbox(), Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
        var work = new UnitOfWork(db, [store]);
        await work.ExecuteAsync(async () =>
        {
            await store.EnsureMonitoringBaselineAsync(account.Id, default);
            var planned = (await db.Plays.SingleAsync()).MarkPlanned(now);
            db.StatusChanges.Add(planned.Change); db.PlanRevisions.Add(planned.Revision);
            db.Orders.Add(entryOrder);
            db.OrderLinks.Add(new(await db.Plays.SingleAsync(), entryOrder, OrderLinkRole.Entry, "entry-1", null,
                OrderLinkState.Linked, OrderLinkSource.Automatic, now));
            return true;
        }, default);
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            db.Fills.Add(Fill(owner, account.Id, "entry", "e1", "buy", 100, DateTimeOffset.UtcNow));
            db.StatusChanges.Add((await db.Plays.SingleAsync()).MarkOpen("Entry filled", now));
            return true;
        }, default);
        // Declared, returned, declared for another reason and returned again: each return names its own declaration.
        var steps = new (UnresolvedReason Reason, bool Return)[]
            { (UnresolvedReason.SharedClose, true), (UnresolvedReason.Flip, true), (UnresolvedReason.SharedClose, false) };
        foreach (var (step, (reason, back)) in steps.Index())
        {
            await work.ExecuteAsync(async () =>
            {
                db.StatusChanges.Add((await db.Plays.SingleAsync()).DeclareUnresolved(reason, "Declared.", "{}", now.AddMinutes(2 * step + 1)));
                return true;
            }, default);
            if (back) await work.ExecuteAsync(async () =>
            {
                var stored = await db.Plays.SingleAsync();
                var undone = PlayUnresolved.Current(await db.StatusChanges.Where(c => c.PlayId == stored.Id).ToListAsync(), stored.EndedAtUtc)!.UnresolvedReason;
                var (change, cleared) = stored.ReturnToOpen("The exit fill arrived after all.", now.AddMinutes(2 * step + 2), undone);
                db.StatusChanges.Add(change);
                if (cleared is not null) db.ReviewRevisions.Add(cleared);
                return true;
            }, default);
        }
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            var stored = await db.Plays.SingleAsync();
            db.Orders.Add(exitOrder);
            db.OrderLinks.Add(new(stored, exitOrder, OrderLinkRole.Exit, null, null, OrderLinkState.Linked, OrderLinkSource.Automatic, now));
            db.Fills.Add(Fill(owner, account.Id, "exit", "x1", "sell", 101, DateTimeOffset.UtcNow));
            db.StatusChanges.Add(stored.SettleUnresolved("Linked exits covered the 0.5 entered.", now).Change);
            return true;
        }, default);
        var events = (await EventReads.ReadAsync(db, 0, 200, default)).Events;
        var changes = events.Where(e => e.Type == "play.status-changed").Select(e => (e.Data.GetProperty("previous").GetString(),
            e.Data.GetProperty("current").GetString(), e.Data.GetProperty("source").GetString(), e.Data.GetProperty("reason").GetString()))
            .Where(c => c.Item1 == "unresolved" || c.Item2 == "unresolved").ToList();
        Assert.Equal([("open", "unresolved", "owner", "shared-close"), ("unresolved", "open", "owner", "shared-close"),
            ("open", "unresolved", "owner", "flip"), ("unresolved", "open", "owner", "flip"),
            ("open", "unresolved", "owner", "shared-close")], changes);
        var closed = Assert.Single(events, e => e.Type == "play.closed");
        Assert.Equal(($"play:{play.Id}:closed", play.Id), (closed.DedupKey, closed.Subject.PlayId));
        Assert.Equal(("0.5", "0.5"), (closed.Data.GetProperty("enteredQuantity").GetString(), closed.Data.GetProperty("exitedQuantity").GetString()));
    }

    private static ImportedOrder Order(Guid owner, Guid account, string id, string side, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", ExternalOrderId = id, Side = side,
        OrderType = "limit", OriginalSize = 0.5m, RemainingSize = 0, Status = "filled", VenueStatus = "filled",
        PlacedAtUtc = now.AddMinutes(-1), StatusAtUtc = now, ObservedAtUtc = now
    };

    private static ImportedFill Fill(Guid owner, Guid account, string order, string id, string side, decimal price, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", SourceFillId = id, Side = side,
        Direction = side == "buy" ? "Open Long" : "Close Long", PositionEffect = side == "buy" ? "open" : "close",
        Price = price, Quantity = 0.5m, Fee = 0.005m, FeeToken = "USDC", ClosedPnlUsd = side == "buy" ? 0 : 0.5m,
        OccurredAtUtc = now, ExternalOrderId = order, TransactionHash = "synthetic"
    };

    private sealed class NoOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
