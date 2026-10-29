using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Persistence;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

/// <summary>
/// Set-based fill capture: one baseline read, one linked-order read and one batched identity claim per unit.
/// Claims and events must match the former per-order capture exactly, including claims that suppress events.
/// </summary>
public sealed class VesselEventFillCaptureBatchingTests
{
    /// <summary>The current schema: links reference orders by internal ID.</summary>
    private const string? Migration = null;

    [VesselEventPostgresFact]
    public async Task Mixed_orders_claim_exactly_as_before_and_emit_only_watched_fills_in_order()
    {
        await using var database = await CoreDatabase.CreateAsync(Migration);
        var owner = Guid.NewGuid();
        var now = Fresh();
        var (account, play, retro) = await SeedAsync(database, owner, now);
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () => { await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
        await scope.Work.ExecuteAsync(async () =>
        {
            var planned = await scope.Db.Plays.SingleAsync(p => p.Id == play.Id);
            var retrospective = await scope.Db.Plays.SingleAsync(p => p.Id == retro.Id);
            Link(scope.Db, planned, "entry", OrderLinkRole.Entry);
            Link(scope.Db, planned, "exit", OrderLinkRole.Exit);
            Link(scope.Db, planned, "late", OrderLinkRole.Entry);
            Link(scope.Db, retrospective, "retro", OrderLinkRole.Entry);
            return true;
        }, default);
        var before = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Sequence();

        await scope.Work.ExecuteAsync(() =>
        {
            scope.Db.Fills.AddRange(
                Fill(owner, account.Id, "unlinked", "u1", now), Fill(owner, account.Id, "unlinked", "u2", now),
                Fill(owner, account.Id, "retro", "r1", now),
                Fill(owner, account.Id, "late", "p1", now.AddHours(-2)),
                Fill(owner, account.Id, "entry", "e1", now.AddMinutes(-2), quantity: 0.25m),
                Fill(owner, account.Id, "entry", "e0", now.AddMinutes(-3), quantity: 0.5m),
                Fill(owner, account.Id, "entry", "c1", now.AddMinutes(-1), side: "sell"),
                Fill(owner, account.Id, "entry", "e1", now, contract: "ETH"),
                Fill(owner, account.Id, "exit", "x1", now, side: "sell", effect: "close"));
            return Task.FromResult(true);
        }, default);

        var events = (await EventReads.ReadAsync(scope.Db, before, 200, default)).Events;
        var orders = await scope.Db.Orders.AsNoTracking().ToDictionaryAsync(o => o.ExternalOrderId, o => o.Id);
        Assert.Equal(["order:" + account.Id + ":" + orders["entry"] + ":filled:0.75", "order:" + account.Id + ":" + orders["exit"] + ":filled:0.5"],
            events.Select(e => e.DedupKey).ToArray());
        Assert.All(events, e => Assert.Equal("execution.filled", e.Type));
        // Fills keep the per-order (occurred, source fill id) order; the contradictory sell is claimed, not counted.
        Assert.Equal(["e0", "e1"], events[0].Source.FillIds!);
        Assert.Equal("0.75", events[0].Data.GetProperty("filledQuantity").GetString());
        Assert.Equal("100", events[0].Data.GetProperty("averagePrice").GetString());
        Assert.Equal(["x1"], events[1].Source.FillIds!);
        var claims = await ClaimsAsync(database, owner);
        Assert.Equal(["BTC/c1", "BTC/e0", "BTC/e1", "BTC/old", "BTC/p1", "BTC/r1", "BTC/x1"], claims);
    }

    [VesselEventPostgresFact]
    public async Task Repeated_refresh_and_relink_do_not_duplicate_events_and_later_fills_report_only_themselves()
    {
        await using var database = await CoreDatabase.CreateAsync(Migration);
        var owner = Guid.NewGuid();
        var now = Fresh();
        var (account, play, _) = await SeedAsync(database, owner, now);
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () => { await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
        await scope.Work.ExecuteAsync(async () => { Link(scope.Db, await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), "entry", OrderLinkRole.Entry); return true; }, default);
        await scope.Work.ExecuteAsync(() => { scope.Db.Fills.Add(Fill(owner, account.Id, "entry", "e1", now)); return Task.FromResult(true); }, default);
        // A refresh that changes nothing, then an unlink and relink of the same order.
        await scope.Work.ExecuteAsync(() => Task.FromResult(true), default);
        await scope.Work.ExecuteAsync(async () => { scope.Db.OrderLinks.Remove(await scope.Db.OrderLinks.SingleAsync()); return true; }, default);
        await scope.Work.ExecuteAsync(async () => { Link(scope.Db, await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), "entry", OrderLinkRole.Entry); return true; }, default);
        Assert.Single((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
        await scope.Work.ExecuteAsync(() => { scope.Db.Fills.Add(Fill(owner, account.Id, "entry", "e2", now, quantity: 0.25m)); return Task.FromResult(true); }, default);
        var filled = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events.Where(e => e.Type == "execution.filled").ToList();
        Assert.Equal(2, filled.Count);
        Assert.Equal(["e2"], filled[1].Source.FillIds!);
        Assert.Equal("0.25", filled[1].Data.GetProperty("filledQuantity").GetString());
        Assert.Equal("0.75", filled[1].Data.GetProperty("cumulativeQuantity").GetString());
    }

    [VesselEventPostgresFact]
    public async Task Rolled_back_capture_leaves_no_claims_and_a_retry_emits_the_fill()
    {
        await using var database = await CoreDatabase.CreateAsync(Migration);
        var owner = Guid.NewGuid();
        var now = Fresh();
        var (account, play, _) = await SeedAsync(database, owner, now);
        await using (var scope = new Scope(database, owner))
        {
            await scope.Work.ExecuteAsync(async () => { await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
            await scope.Work.ExecuteAsync(async () => { Link(scope.Db, await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), "entry", OrderLinkRole.Entry); return true; }, default);
        }
        await using (var failing = new Scope(database, owner, new FailAfterCapture()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Work.ExecuteAsync(() =>
            {
                failing.Db.Fills.Add(Fill(owner, account.Id, "entry", "e1", now));
                return Task.FromResult(true);
            }, default));
        Assert.Equal(["BTC/old"], await ClaimsAsync(database, owner));
        await using var retry = new Scope(database, owner);
        Assert.DoesNotContain((await EventReads.ReadAsync(retry.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
        await retry.Work.ExecuteAsync(() => { retry.Db.Fills.Add(Fill(owner, account.Id, "entry", "e1", now)); return Task.FromResult(true); }, default);
        var filled = Assert.Single((await EventReads.ReadAsync(retry.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
        Assert.Equal(["e1"], filled.Source.FillIds!);
        Assert.Equal(["BTC/e1", "BTC/old"], await ClaimsAsync(database, owner));
    }

    [VesselEventPostgresFact]
    public async Task One_source_fill_id_on_two_contracts_is_claimed_and_reported_per_contract()
    {
        await using var database = await CoreDatabase.CreateAsync(Migration);
        var owner = Guid.NewGuid();
        var now = Fresh();
        var (account, play, _) = await SeedAsync(database, owner, now);
        var eth = TestPlays.Create(account, new("hyperliquid", "ETH"));
        await using var scope = new Scope(database, owner);
        await scope.Work.ExecuteAsync(async () =>
        {
            scope.Db.Plays.Add(eth);
            var planned = eth.MarkPlanned(now.AddHours(-3));
            scope.Db.StatusChanges.Add(planned.Change); scope.Db.PlanRevisions.Add(planned.Revision);
            await scope.Store.EnsureMonitoringBaselineAsync(account.Id, default);
            return true;
        }, default);
        await scope.Work.ExecuteAsync(async () =>
        {
            Link(scope.Db, await scope.Db.Plays.SingleAsync(p => p.Id == play.Id), "btc-entry", OrderLinkRole.Entry);
            Link(scope.Db, await scope.Db.Plays.SingleAsync(p => p.Id == eth.Id), "eth-entry", OrderLinkRole.Entry);
            return true;
        }, default);
        var before = (await EventReads.ReadAsync(scope.Db, 0, 200, default)).Sequence();
        await scope.Work.ExecuteAsync(() =>
        {
            scope.Db.Fills.AddRange(Fill(owner, account.Id, "btc-entry", "same", now),
                Fill(owner, account.Id, "eth-entry", "same", now, contract: "ETH", quantity: 2));
            return Task.FromResult(true);
        }, default);
        var events = (await EventReads.ReadAsync(scope.Db, before, 200, default)).Events;
        Assert.Equal([("BTC", "0.5"), ("ETH", "2")], events.Select(e =>
            (e.Subject.ContractId!, e.Data.GetProperty("filledQuantity").GetString()!)).ToArray());
        Assert.All(events, e => Assert.Equal(["same"], e.Source.FillIds!));
        Assert.Equal(["BTC/old", "BTC/same", "ETH/same"], await ClaimsAsync(database, owner));
    }

    [VesselEventPostgresFact]
    public async Task Capture_for_many_unlinked_orders_uses_a_constant_number_of_commands()
    {
        var counts = new List<int>();
        foreach (var orders in new[] { 3, 60 })
        {
            await using var database = await CoreDatabase.CreateAsync(Migration);
            var owner = Guid.NewGuid();
            var now = Fresh();
            var (account, play, _) = await SeedAsync(database, owner, now);
            await using (var setup = new Scope(database, owner))
            {
                await setup.Work.ExecuteAsync(async () => { await setup.Store.EnsureMonitoringBaselineAsync(account.Id, default); return true; }, default);
                await setup.Work.ExecuteAsync(async () => { Link(setup.Db, await setup.Db.Plays.SingleAsync(p => p.Id == play.Id), "linked", OrderLinkRole.Entry); return true; }, default);
            }
            var counter = new CommandCounter();
            await using var scope = new Scope(database, owner, counter.Stop, counter.Start);
            using var listener = counter.Listen();
            await scope.Work.ExecuteAsync(() =>
            {
                for (var i = 0; i < orders; i++)
                    scope.Db.Fills.Add(Fill(owner, account.Id, $"unlinked-{i:D3}", $"u{i}", now));
                return Task.FromResult(true);
            }, default, counter);
            counts.Add(counter.Count);
            Assert.DoesNotContain((await EventReads.ReadAsync(scope.Db, 0, 200, default)).Events, e => e.Type == "execution.filled");
            Assert.Equal(["BTC/old"], await ClaimsAsync(database, owner));
        }
        // The baseline read and the order read (no retained order, so no link read); per-order capture issued two commands per order.
        Assert.Equal([2, 2], counts);
    }

    // Watched fills land after the baseline (set at commit time); seeded history lies hours before it.
    private static DateTimeOffset Fresh() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddMinutes(10);

    private static async Task<(Account Account, Play Play, Play Retro)> SeedAsync(CoreDatabase database, Guid owner, DateTimeOffset now)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Synthetic");
        var play = TestPlays.Create(account, new("hyperliquid", "BTC"));
        var retro = Play.Retrospective(Guid.NewGuid(), account, new("hyperliquid", "BTC"),
            PlayDocuments.Serialize(TestPlays.Plan()), now.AddHours(-1), now, "Retrospective");
        await using var seed = database.Context(owner);
        seed.Accounts.Add(account); seed.Plays.AddRange(play, retro);
        var planned = play.MarkPlanned(now.AddHours(-3));
        seed.StatusChanges.Add(planned.Change); seed.PlanRevisions.Add(planned.Revision);
        seed.Fills.Add(Fill(owner, account.Id, "old-order", "old", now.AddHours(-3)));
        await seed.SaveChangesAsync();
        return (account, play, retro);
    }

    /// <summary>Links the retained order with this venue ID on the Play's contract, retaining a minimal one first if needed.</summary>
    private static void Link(VesselDbContext db, Play play, string order, OrderLinkRole role)
    {
        var retained = db.Orders.Local.FirstOrDefault(o => o.AccountId == play.AccountId && o.ContractId == play.ContractId && o.ExternalOrderId == order)
            ?? db.Orders.FirstOrDefault(o => o.AccountId == play.AccountId && o.ContractId == play.ContractId && o.ExternalOrderId == order);
        if (retained is null)
        {
            retained = TestOrders.For(play, order, role == OrderLinkRole.Entry ? "buy" : "sell", "open");
            db.Orders.Add(retained);
        }
        LinkOrder(db, play, retained, role);
    }

    private static void LinkOrder(VesselDbContext db, Play play, ImportedOrder order, OrderLinkRole role) =>
        db.OrderLinks.Add(new(play, order, role, role == OrderLinkRole.Entry ? "entry-1" : null, null,
            OrderLinkState.Linked, OrderLinkSource.Owner, DateTimeOffset.UtcNow));

    private static ImportedFill Fill(Guid owner, Guid account, string order, string id, DateTimeOffset occurred,
        string contract = "BTC", decimal quantity = 0.5m, string side = "buy", string effect = "open") => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, AccountId = account, ContractId = contract, SourceFillId = id,
        Side = side, Direction = effect == "close" ? "Close Long" : "Open Long", PositionEffect = effect, Price = 100,
        Quantity = quantity, Fee = 0, FeeToken = "USDC", ClosedPnlUsd = 0, OccurredAtUtc = occurred, ExternalOrderId = order,
        TransactionHash = "synthetic"
    };

    private static async Task<string[]> ClaimsAsync(CoreDatabase database, Guid owner)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT contract_id || '/' || source_fill_id FROM event_fills WHERE owner_id=@owner ORDER BY (contract_id || '/' || source_fill_id) COLLATE \"C\"", connection);
        command.Parameters.AddWithValue("owner", owner);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private sealed class Scope : IAsyncDisposable
    {
        public VesselDbContext Db { get; }
        public VesselEventStore Store { get; }
        public ScopeWork Work { get; }
        public Scope(CoreDatabase database, Guid owner, IUnitOfWorkParticipant? after = null, IUnitOfWorkParticipant? before = null)
        {
            var capture = new VesselEventMutationCapture();
            Db = new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
                .AddInterceptors(capture).Options, new CoreOwner(owner));
            Store = new(Db, capture, new NullOutbox(), Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
            var participants = new List<IUnitOfWorkParticipant>();
            if (before is not null) participants.Add(before);
            participants.Add(Store);
            if (after is not null) participants.Add(after);
            Work = new(new UnitOfWork(Db, participants));
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ScopeWork(UnitOfWork work)
    {
        public Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct, CommandCounter? counter = null)
        {
            CommandCounter.Current.Value = counter;
            return work.ExecuteAsync(operation, ct);
        }
    }

    private sealed class NullOutbox : IVesselEventOutbox
    {
        public Task StageAsync(VesselEvent message, CancellationToken ct) => Task.CompletedTask;
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }

    private sealed class FailAfterCapture : IUnitOfWorkParticipant
    {
        public Task BeforeCommitAsync(CancellationToken ct) => throw new InvalidOperationException("Deliberate rollback after capture.");
        public Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Counts Npgsql commands of this async flow only while the event store's commit hook runs.</summary>
    private sealed class CommandCounter
    {
        public static readonly AsyncLocal<CommandCounter?> Current = new();
        private int count;
        private volatile bool counting;
        public int Count => count;
        public IUnitOfWorkParticipant Start => new Toggle(() => counting = true);
        public IUnitOfWorkParticipant Stop => new Toggle(() => counting = false);

        public ActivityListener Listen()
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = _ =>
                {
                    if (Current.Value is { counting: true } counter) Interlocked.Increment(ref counter.count);
                }
            };
            ActivitySource.AddActivityListener(listener);
            return listener;
        }

        private sealed class Toggle(Action action) : IUnitOfWorkParticipant
        {
            public Task BeforeCommitAsync(CancellationToken ct) { action(); return Task.CompletedTask; }
            public Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
