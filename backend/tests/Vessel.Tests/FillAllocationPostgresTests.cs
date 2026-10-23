using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 11 (G-18, C2; <c>correctness-dotnet/Exec.cs</c> <c>SharedExit</c> and <c>FlipOrder</c>): an owner-confirmed
/// allocation settles the Plays a shared exit or a flip left stuck, unscored with the reason "not allocated".
/// </summary>
public sealed partial class ExecutionPostgresTests
{
    /// <summary>The execution service with the diary's request store, which allocation requests are idempotent through.</summary>
    private static PlayExecutionService Allocating(Harness h) => new(new PlayStore(h.Db, TestUnitOfWork.For(h.Db)),
        new ExecutionStore(h.Db, TestUnitOfWork.For(h.Db)), TestVenues.With(h.Orders), TimeProvider.System, TestUnitOfWork.For(h.Db),
        diary: new DiaryStore(h.Db, TestUnitOfWork.For(h.Db)));

    private static async Task<PlayDto> PlannedShort(Harness h, Guid account, string title)
    {
        var plan = TestPlays.Plan() with { Direction = "short" };
        plan = plan with { Entries = [plan.Entries[0] with { Stops = [new PlanExit("stop-1", "price", "105", "100")],
            Targets = [new PlanExit("target-1", "price", "90", "100")] }] };
        var play = await h.Plays.CreateAsync(new CreatePlayRequest(account, "BTC", "venue", title, plan), default);
        return await h.Plays.ChangeStatusAsync(play.Summary.Id, new(play.Summary.Version, "planned"), default);
    }

    private static async Task<long> VersionOf(Harness h, Guid play) => (await h.Plays.GetAsync(play, default)).Summary.Version;

    private static async Task<AllocateFillRequest> Allocation(Harness h, Guid fill, int revision, string reason, params AllocationLineInput[] lines)
    {
        // One context: the versions are read one after another.
        var plays = new List<AllocationPlayVersion>();
        foreach (var line in lines) plays.Add(new(line.PlayId, await VersionOf(h, line.PlayId)));
        return new(Guid.NewGuid(), fill, revision, reason, "Closed together in one order.", plays, lines);
    }

    private static AllocationLineInput ExitLine(Guid play, string quantity) => new(play, "exit", null, quantity);

    /// <summary>The status the API answers for a refusal: a problem's own, 400 for a domain validation, 409 for a domain rule.</summary>
    private static async Task<int> Refusal(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        return error switch
        {
            ProblemException problem => problem.StatusCode,
            Vessel.Domain.Common.DomainValidationException => 400,
            Vessel.Domain.Common.DomainRuleException => 409,
            _ => throw error,
        };
    }

    /// <summary>
    /// Two long Plays, entered 1 and 2 on their own orders, closed by one sell of 3 that the owner linked to the first as an
    /// unplanned exit: the first over-exits and the second never exits.
    /// </summary>
    private static async Task<(Guid A, Guid B, Guid Fill)> SharedExitAsync(Harness h, Guid account)
    {
        var a = (await PlannedPlay(h, account, "First")).Summary.Id;
        var b = (await PlannedPlay(h, account, "Second")).Summary.Id;
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        h.Orders.Orders.AddRange([Order("21", "buy", 100m, "filled"), Order("22", "buy", 100m, "filled", size: 2m), Order("23", "sell", 105m, "filled", size: 3m)]);
        AddFills(h, Fill("e1", "21", "Open Long", 100m, 1m, at), Fill("e2", "22", "Open Long", 100m, 2m, at.AddSeconds(1)),
            Fill("x1", "23", "Close Long", 105m, 3m, at.AddMinutes(1)));
        await h.CheckAsync(a);
        // Both Plays could own each entry, so the owner links them; then the shared sell to the first.
        await h.Execution.LinkAsync(a, new("21", "entry", "entry-1"), default);
        await h.Execution.LinkAsync(b, new("22", "entry", "entry-1"), default);
        var linked = await h.Execution.LinkAsync(a, new("23", "exit"), default);
        Assert.Equal("open", linked.Status);
        Assert.Contains("Linked exits exceed the entered quantity; a fill shared with another play can be allocated between them.", linked.Issues);
        var shared = Assert.Single(linked.SharedFills);
        Assert.Equal(("shared-exit", 0, "23", "3"), (shared.Kind, shared.Revision, shared.OrderId, shared.Fill.Quantity));
        Assert.Empty(shared.Lines);
        // The dialog's Plays, with each one's open quantity without this fill.
        Assert.Equal([a, b], linked.AllocationPlays.Select(p => p.PlayId));
        Assert.Equal(["1", "2"], shared.Open.Select(o => o.OpenQuantity));
        return (a, b, shared.FillId);
    }

    [PostgresFact]
    public async Task A_shared_exit_allocated_by_quantity_closes_both_Plays_unscored_with_their_Play_local_results()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var (a, b, fill) = await SharedExitAsync(h, account);
        var versions = (await VersionOf(h, a), await VersionOf(h, b));

        var execution = await Allocating(h).AllocateAsync(a, await Allocation(h, fill, 0, "shared-close", ExitLine(a, "1"), ExitLine(b, "2")), default);
        Assert.Equal("closed", execution.Status);
        Assert.Empty(execution.Issues);
        Assert.Equal(("1", "1", "0"), (execution.Totals.EnteredQuantity, execution.Totals.ExitedQuantity, execution.Totals.OpenQuantity));
        // The venue's average-cost P&L cannot be split: no venue result; the Play-local result is 105 − 100 less the entry
        // fee and its share 0.003333 of the shared fee.
        Assert.True(execution.Totals.VenueNotAllocated);
        Assert.Null(execution.Totals.VenueNetUsd);
        Assert.Equal("4.986667", execution.Totals.Local!.NetUsd);
        Assert.False(execution.Totals.Local.SignDiffers);
        var shared = Assert.Single(execution.SharedFills);
        Assert.Equal((1, "shared-close", "Closed together in one order."), (shared.Revision, shared.Reason, shared.Note));
        Assert.Equal([(a, "exit", "1", "0.003333", (string?)null), (b, "exit", "2", "0.006667", "0.000001")],
            shared.Lines.OrderBy(l => l.Quantity).Select(l => (l.PlayId, l.Role, l.Quantity, l.Fee, l.FeeRemainder)));
        Assert.Equal(PlayReceipts.NotAllocatedNote, execution.Receipt!.EstimatedRReason);

        var second = await h.Execution.GetAsync(b, default);
        Assert.Equal(("closed", "2", "2", "9.983333", true), (second.Status, second.Totals.EnteredQuantity, second.Totals.ExitedQuantity,
            second.Totals.Local!.NetUsd, second.Totals.VenueNotAllocated));
        // B reaches the fill only through its line, and both moved to their next versions.
        Assert.Single(second.SharedFills);
        Assert.True(await VersionOf(h, a) > versions.Item1 && await VersionOf(h, b) > versions.Item2);
        var history = await h.Plays.HistoryAsync(b, default);
        Assert.Equal(("open", "closed", "venue", "Linked and allocated exits covered the 2 entered."),
            (history.StatusChanges.Last().From, history.StatusChanges.Last().To, history.StatusChanges.Last().Source, history.StatusChanges.Last().Note));
        // The allocation never changed a link: the sell is still A's unplanned exit.
        Assert.Equal("23", Assert.Single(execution.Links, l => l.Role == "exit").Order!.OrderId);

        // Review leaves both out with the reason and lists them; sizing counts them beside the record.
        var report = await Review(h).GetAsync(new(Period: "all"), default);
        Assert.Equal((2, 2), (report.Coverage.NotAllocated, report.Coverage.ClosedCandidates));
        Assert.Empty(report.Plays);
        var exclusions = (await Review(h).ExclusionsAsync(new(Period: "all"), default)).Items;
        Assert.Equal(new[] { a, b }.Order(), exclusions.Select(e => e.PlayId).Order());
        Assert.All(exclusions, e => Assert.Equal([FillAllocations.NotAllocatedReason], e.Reasons));
        var sizing = await h.Sizing.GetAsync(default);
        Assert.Equal((2, 0), (sizing.Record.NotAllocated, sizing.Record.ClosedPlays));

        // A Closed Play keeps its part: moving quantity off it, or removing the allocation, is refused and changes nothing.
        var replaced = await Allocation(h, fill, 1, "shared-close", ExitLine(a, "1.5"), ExitLine(b, "1.5"));
        Assert.Equal(409, await Refusal(() => Allocating(h).AllocateAsync(a, replaced, default)));
        Assert.Equal(409, await Refusal(async () => await Allocating(h).AllocateAsync(a, (await Allocation(h, fill, 1, "other")) with
            { Plays = [new(a, await VersionOf(h, a)), new(b, await VersionOf(h, b))] }, default)));
        var stored = await h.Db.FillAllocations.Include(x => x.Lines).AsNoTracking().SingleAsync();
        Assert.Equal((1, 2), (stored.Revision, stored.Lines.Count));
        Assert.Equal(1, await h.Db.FillAllocationRevisions.CountAsync());
    }

    [PostgresFact]
    public async Task A_flip_split_closes_one_Play_and_opens_the_other()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var a = (await PlannedPlay(h, account, "Long")).Summary.Id;
        var c = (await PlannedShort(h, account, "Short")).Summary.Id;
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        h.Orders.Orders.AddRange([Order("21", "buy", 100m, "filled"), Order("24", "sell", 100m, "filled", size: 2m)]);
        AddFills(h, Fill("e1", "21", "Open Long", 100m, 1m, at), Fill("f1", "24", "Long > Short", 100m, 2m, at.AddMinutes(1)));
        var execution = await h.CheckAsync(a);
        // The flip contradicts both roles, so no Play links it, and the long Play cannot settle.
        Assert.Equal(("open", "planned"), (execution.Status, (await h.Execution.GetAsync(c, default)).Status));
        var flip = Assert.Single(execution.SharedFills);
        Assert.Equal(("flip", "24"), (flip.Kind, flip.OrderId));
        Assert.Equal("1", flip.Open.Single(o => o.PlayId == a).OpenQuantity);

        // The exit must close the long Play exactly.
        var wrong = await Allocation(h, flip.FillId, 0, "flip", ExitLine(a, "0.5"), new(c, "entry", "entry-1", "1.5"));
        var refused = await Assert.ThrowsAsync<ProblemException>(() => Allocating(h).AllocateAsync(a, wrong, default));
        Assert.Equal((409, "A flip's exit closes Long exactly: its open quantity without this fill is 1."), (refused.StatusCode, refused.Message));
        Assert.Equal(0, await h.Db.FillAllocations.CountAsync());

        execution = await Allocating(h).AllocateAsync(a, await Allocation(h, flip.FillId, 0, "flip", ExitLine(a, "1"), new(c, "entry", "entry-1", "1")), default);
        Assert.Equal(("closed", true), (execution.Status, execution.Totals.VenueNotAllocated));
        var opened = await h.Execution.GetAsync(c, default);
        Assert.Equal(("open", "1", "0", "1"), (opened.Status, opened.Totals.EnteredQuantity, opened.Totals.ExitedQuantity, opened.Entries.Single().FilledQuantity));
        Assert.Equal("100", opened.Entries.Single().AverageFillPrice);
        var history = await h.Plays.HistoryAsync(c, default);
        Assert.Equal(("planned", "open", "Entry fill of 1 at 100 on order 24, allocated from a shared fill."),
            (history.StatusChanges.Last().From, history.StatusChanges.Last().To, history.StatusChanges.Last().Note));
        // The opened Play's entry counts as filled for its plan, as a linked entry fill does.
        Assert.Contains("entry-1", await new PlayStore(h.Db, TestUnitOfWork.For(h.Db)).FilledEntryIdsAsync(c, default));
    }

    [PostgresFact]
    public async Task An_unresolved_Play_receives_its_part_and_closes_from_venue_facts()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var (a, b, fill) = await SharedExitAsync(h, account);
        var declared = await h.Execution.DeclareUnresolvedAsync(b, new(await VersionOf(h, b), "shared-close", "The sell closed both."), default);
        Assert.Equal("unresolved", declared.Summary.Status);
        // Its panel offers nothing of its own, but A's allocation may name it.
        await Allocating(h).AllocateAsync(a, await Allocation(h, fill, 0, "shared-close", ExitLine(a, "1"), ExitLine(b, "2")), default);
        var closed = await h.Plays.GetAsync(b, default);
        Assert.Equal("closed", closed.Summary.Status);
        var history = await h.Plays.HistoryAsync(b, default);
        Assert.Equal(["open", "unresolved", "closed"], history.StatusChanges.Select(c => c.To).TakeLast(3));
        Assert.Equal(("unresolved", "venue"), (history.StatusChanges.Last().From, history.StatusChanges.Last().Source));
        var report = await Review(h).GetAsync(new(Period: "all"), default);
        Assert.Equal((2, 0), (report.Coverage.NotAllocated, report.Coverage.Unresolved));
    }

    [PostgresFact]
    public async Task Stale_versions_and_revisions_conflict_and_write_nothing()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var (a, b, fill) = await SharedExitAsync(h, account);
        var before = (await VersionOf(h, a), await VersionOf(h, b));
        async Task Unchanged()
        {
            Assert.Equal(0, await h.Db.FillAllocations.CountAsync());
            Assert.Equal(0, await h.Db.FillAllocationRevisions.CountAsync());
            Assert.Equal(before, (await VersionOf(h, a), await VersionOf(h, b)));
            Assert.Equal(("open", "open"), ((await h.Plays.GetAsync(a, default)).Summary.Status, (await h.Plays.GetAsync(b, default)).Summary.Status));
        }
        var good = await Allocation(h, fill, 0, "shared-close", ExitLine(a, "1"), ExitLine(b, "2"));

        // A stale Play version, an unknown revision, a missing Play and a quantity off by one 28th place.
        Assert.Equal(409, await Refusal(() => Allocating(h).AllocateAsync(a, good with { RequestId = Guid.NewGuid(),
            Plays = [new(a, before.Item1), new(b, before.Item2 - 1)] }, default)));
        await Unchanged();
        Assert.Equal(409, await Refusal(() => Allocating(h).AllocateAsync(a, good with { RequestId = Guid.NewGuid(), ExpectedRevision = 1 }, default)));
        await Unchanged();
        Assert.Equal(400, await Refusal(() => Allocating(h).AllocateAsync(a, good with { RequestId = Guid.NewGuid(), Plays = [new(a, before.Item1)] }, default)));
        Assert.Equal(400, await Refusal(() => Allocating(h).AllocateAsync(a, good with { RequestId = Guid.NewGuid(),
            Lines = [ExitLine(a, "1"), ExitLine(b, "1.9999999999999999999999999999")] }, default)));
        Assert.Equal(400, await Refusal(() => Allocating(h).AllocateAsync(a, good with { RequestId = Guid.NewGuid(), Reason = "liquidated" }, default)));
        await Unchanged();

        // The good request applies once; a retry with its request ID writes nothing again, another payload conflicts.
        await Allocating(h).AllocateAsync(a, good, default);
        var after = (await VersionOf(h, a), await VersionOf(h, b));
        await Allocating(h).AllocateAsync(a, good, default);
        Assert.Equal(after, (await VersionOf(h, a), await VersionOf(h, b)));
        Assert.Equal(1, await h.Db.FillAllocationRevisions.CountAsync());
        Assert.Equal(409, await Refusal(() => Allocating(h).AllocateAsync(a, good with { Note = "Another payload." }, default)));
        // A request that still expects no allocation is now stale.
        Assert.Equal(409, await Refusal(async () => await Allocating(h).AllocateAsync(a, await Allocation(h, fill, 0, "shared-close",
            ExitLine(a, "1"), ExitLine(b, "2")), default)));
        Assert.Equal(1, (await h.Db.FillAllocations.AsNoTracking().SingleAsync()).Revision);
    }

    [PostgresFact]
    public async Task A_Play_that_cannot_take_a_part_or_a_fill_that_is_not_the_route_Play_s_is_refused()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var (a, b, fill) = await SharedExitAsync(h, account);
        // A planned Play has no entry to close; the fill's order is linked to A, so a split without A is refused too.
        var planned = (await PlannedPlay(h, account, "Third")).Summary.Id;
        Assert.Equal(409, await Refusal(async () => await Allocating(h).AllocateAsync(a,
            await Allocation(h, fill, 0, "shared-close", ExitLine(a, "1"), ExitLine(planned, "2")), default)));
        Assert.Equal(409, await Refusal(async () => await Allocating(h).AllocateAsync(b,
            await Allocation(h, fill, 0, "shared-close", ExitLine(b, "3")), default)));
        // The route's Play must be on the lines or hold the fill's order.
        Assert.Equal(409, await Refusal(async () => await Allocating(h).AllocateAsync(planned,
            await Allocation(h, fill, 0, "shared-close", ExitLine(a, "1"), ExitLine(b, "2")), default)));
        Assert.Equal(404, await Refusal(async () => await Allocating(h).AllocateAsync(a,
            await Allocation(h, Guid.NewGuid(), 0, "shared-close", ExitLine(a, "1"), ExitLine(b, "2")), default)));
        Assert.Equal(0, await h.Db.FillAllocations.CountAsync());
    }

    [PostgresFact]
    public async Task Rolling_back_the_FillAllocations_migration_is_refused_while_an_allocation_exists()
    {
        var (database, h, account) = await SetUp();
        await using var _ = database;
        var (a, b, fill) = await SharedExitAsync(h, account);
        await Allocating(h).AllocateAsync(a, await Allocation(h, fill, 0, "shared-close", ExitLine(a, "1"), ExitLine(b, "2")), default);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(h.Db);
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20261023075337_JournalSearch"));
        Assert.Contains("Restore the pre-upgrade backup", refused.MessageText);
    }

    [PostgresFact]
    public async Task A_paper_request_for_an_allocation_is_refused_before_any_read()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString);
        using var client = factory.AuthorizedClient();
        var response = await client.PostAsync($"/api/plays/{Guid.NewGuid()}/execution/allocations?tradingMode=paper",
            System.Net.Http.Json.JsonContent.Create(new { requestId = Guid.NewGuid() }));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
    }
}

/// <summary>Phase 2, item 11: a Play closed through an allocation emits the existing closed event, with no venue P&amp;L.</summary>
public sealed class FillAllocationEventTests
{
    [VesselEventPostgresFact]
    public async Task A_Play_closed_through_its_line_has_the_line_s_quantities_and_fees_and_no_closed_PnL_in_its_event()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Vessel.Domain.Accounts.Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var other = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var now = DateTimeOffset.UtcNow;
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.AddRange(play, other);
            await seed.SaveChangesAsync();
        }
        var capture = new Vessel.Persistence.Events.VesselEventMutationCapture();
        await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(capture).Options, new CoreOwner(owner));
        var store = new Vessel.Persistence.Events.VesselEventStore(db, capture, new NoOutbox(),
            Microsoft.Extensions.Options.Options.Create(new Vessel.Persistence.Events.VesselEventSettings()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Vessel.Persistence.Events.VesselEventStore>.Instance);
        var work = new UnitOfWork(db, [store]);
        var entry = Order(owner, account.Id, "entry", "buy", now);
        var exit = Order(owner, account.Id, "exit", "sell", now);
        await work.ExecuteAsync(async () =>
        {
            await store.EnsureMonitoringBaselineAsync(account.Id, default);
            var stored = await db.Plays.SingleAsync(p => p.Id == play.Id);
            var planned = stored.MarkPlanned(now);
            db.StatusChanges.Add(planned.Change); db.PlanRevisions.Add(planned.Revision);
            db.Orders.AddRange(entry, exit);
            db.OrderLinks.Add(new(stored, entry, OrderLinkRole.Entry, "entry-1", null, OrderLinkState.Linked, OrderLinkSource.Automatic, now));
            return true;
        }, default);
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            db.Fills.Add(Fill(owner, account.Id, "entry", "e1", "buy", 100, 1m, 0.01m, now.AddMinutes(-2)));
            db.StatusChanges.Add((await db.Plays.SingleAsync(p => p.Id == play.Id)).MarkOpen("Entry filled", now));
            return true;
        }, default);
        // The shared sell of 3 is linked to no Play; its allocation gives this Play 1 of it.
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(Guid.CreateVersion7(), "monitor", default);
            var shared = Fill(owner, account.Id, "exit", "x1", "sell", 105, 3m, 0.01m, now.AddMinutes(-1));
            db.Fills.Add(shared);
            var (allocation, revision) = FillAllocation.Create(shared, AllocationReason.SharedClose, "Both.",
                [new(play.Id, "long", AllocationRole.Exit, null, 1m), new(other.Id, "long", AllocationRole.Exit, null, 2m)], now);
            db.FillAllocations.Add(allocation); db.FillAllocationRevisions.Add(revision);
            db.StatusChanges.Add((await db.Plays.SingleAsync(p => p.Id == play.Id)).MarkClosed("Linked and allocated exits covered the 1 entered.", now));
            return true;
        }, default);
        var closed = Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events, e => e.Type == "play.closed");
        Assert.Equal(("1", "1", System.Text.Json.JsonValueKind.Null), (closed.Data.GetProperty("enteredQuantity").GetString(),
            closed.Data.GetProperty("exitedQuantity").GetString(), closed.Data.GetProperty("closedPnlUsd").ValueKind));
        // The line's fee share: a third of 0.01 at six places, beside the entry's own fee.
        Assert.Equal("0.013333", closed.Data.GetProperty("fees")[0].GetProperty("amount").GetString());
        // Its source time is the allocated exit's, the latest valid exit.
        Assert.Equal(now.AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), closed.OccurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"));
    }

    private static ImportedOrder Order(Guid owner, Guid account, string id, string side, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", ExternalOrderId = id, Side = side,
        OrderType = "limit", OriginalSize = side == "buy" ? 1m : 3m, RemainingSize = 0, Status = "filled", VenueStatus = "filled",
        PlacedAtUtc = now.AddMinutes(-1), StatusAtUtc = now, ObservedAtUtc = now
    };

    private static ImportedFill Fill(Guid owner, Guid account, string order, string id, string side, decimal price, decimal quantity, decimal fee,
        DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = "BTC", SourceFillId = id, Side = side,
        Direction = side == "buy" ? "Open Long" : "Close Long", PositionEffect = side == "buy" ? "open" : "close",
        Price = price, Quantity = quantity, Fee = fee, FeeToken = "USDC", ClosedPnlUsd = side == "buy" ? 0 : 15m,
        OccurredAtUtc = at, ExternalOrderId = order, TransactionHash = "synthetic"
    };

    private sealed class NoOutbox : Vessel.Persistence.Events.IVesselEventOutbox
    {
        public Task StageAsync(Vessel.Application.Events.VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(Vessel.Application.Events.MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
