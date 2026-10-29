using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vessel.Application.Evidence;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Persistence;
using Vessel.Persistence.Events;

namespace Vessel.Tests;

public sealed class VesselEventParticipationTests
{
    [VesselEventPostgresFact]
    public async Task Completed_beforecommit_hook_cannot_reenter_during_next_hook()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception>? late = null;
        UnitOfWork work = null!;
        var first = new Hook { Before = () =>
        {
            late = Task.Run(async () =>
            {
                await gate.Task;
                return await Record.ExceptionAsync(() => work.ExecuteAsync(async () =>
                {
                    db.Accounts.Add(new Account(Guid.NewGuid(), owner, "manual", "Unawaited hook mutation"));
                    await work.SaveChangesAsync(default);
                    return true;
                }, default));
            });
            return Task.CompletedTask;
        }};
        Exception? admissionError = null;
        var second = new Hook { Before = async () => { gate.SetResult(); admissionError = await late!; } };
        work = new(db, [first, second]);
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        await using var outside = database.Context(owner);
        var committed = await outside.Accounts.CountAsync();
        Assert.True(admissionError is InvalidOperationException, $"Completed hook admitted: error={admissionError?.GetType().Name ?? "none"}; committed accounts={committed}");
        Assert.Equal(0, committed);
    }

    [VesselEventPostgresFact]
    public async Task Completed_nested_callback_cannot_append_event_during_sibling()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var store = new VesselEventStore(db, new(), new Outbox(), Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
        var work = new UnitOfWork(db, [store]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception>? late = null;
        Exception? admissionError = null;
        await work.ExecuteAsync(async () =>
        {
            await work.ExecuteAsync(() =>
            {
                late = Task.Run(async () =>
                {
                    await gate.Task;
                    return await Record.ExceptionAsync(() => store.AppendAsync(new VesselEventDraft("monitor.state-changed", Guid.NewGuid(), "stale-callback", "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"), JsonSerializer.SerializeToElement(new { current = "live" })), default));
                });
                return Task.FromResult(true);
            }, default);
            await work.ExecuteAsync(async () => { gate.SetResult(); admissionError = await late!; return true; }, default);
            return true;
        }, default);
        var events = (await EventReads.ReadAsync(db, 0, 200, default)).Events.Count;
        Assert.True(admissionError is InvalidOperationException, $"Completed nested event admission: error={admissionError?.GetType().Name ?? "none"}; committed events={events}");
        Assert.Equal(0, events);
    }

    [VesselEventPostgresFact]
    public async Task Original_callback_cannot_commit_unobserved_play_transition_during_event_staging()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var capture = new VesselEventMutationCapture();
        await using var db = new VesselDbContext(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(capture).Options, new CoreOwner(owner));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception>? late = null;
        Exception? admissionError = null;
        var outbox = new Outbox { OnStage = async () => { gate.TrySetResult(); admissionError = await late!; } };
        var store = new VesselEventStore(db, capture, outbox, Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
        var work = new UnitOfWork(db, [store]);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Original");
        var play = TestPlays.Create(account, new("manual", "BTC"));
        // The unawaited callback still holds the root participation, so its overlap poisons the live unit.
        var outer = await Record.ExceptionAsync(() => work.ExecuteAsync(async () =>
        {
            db.Accounts.Add(account); db.Plays.Add(play);
            await store.AppendAsync(new VesselEventDraft("monitor.state-changed", Guid.NewGuid(), "staging-yield", "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"), JsonSerializer.SerializeToElement(new { current = "live" })), default);
            late = Task.Run(async () =>
            {
                await gate.Task;
                return await Record.ExceptionAsync(() => work.ExecuteAsync(async () =>
                {
                    var planned = play.MarkPlanned(DateTimeOffset.UtcNow);
                    db.StatusChanges.Add(planned.Change); db.PlanRevisions.Add(planned.Revision);
                    await work.SaveChangesAsync(default);
                    return true;
                }, default));
            });
            return true;
        }, default));
        await using var outside = database.Context(owner);
        var statuses = await outside.Plays.Select(p => p.Status).ToListAsync();
        var events = (await EventReads.ReadAsync(db, 0, 200, default)).Events;
        Assert.True(admissionError is InvalidOperationException, $"Completed operation admitted during event hook: error={admissionError?.GetType().Name ?? "none"}; committed statuses={string.Join(",", statuses)}; status event count={events.Count(e => e.Type == "play.status-changed")}");
        Assert.IsType<InvalidOperationException>(outer);
        Assert.Empty(statuses);
        Assert.Empty(events);
    }

    [VesselEventPostgresFact]
    public async Task Raw_transaction_or_another_context_cannot_authorize_event_writes_or_run_metadata()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        await using var other = database.Context(owner);
        var store = Store(db);
        var otherStore = Store(other);
        async Task RejectAll(VesselEventStore target)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.AppendAsync(Draft("rejected"), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.EnsureMonitoringBaselineAsync(Guid.NewGuid(), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.EnqueueAsync(
                new(Guid.NewGuid(), Guid.NewGuid(), owner, "manual"), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.BeforeCommitAsync(default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.SetRunAsync(Guid.NewGuid(), "monitor", default));
        }

        await RejectAll(store);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RejectAll(store);
            await transaction.RollbackAsync();
        }
        // Even identical owners and TWO real transactions do not make the other context admissible.
        await using (var otherTransaction = await other.Database.BeginTransactionAsync())
        {
            var work = new UnitOfWork(db, [store]);
            await work.ExecuteAsync(async () =>
            {
                await RejectAll(otherStore);
                await store.AppendAsync(Draft("admitted"), default);
                return true;
            }, default);
            await otherTransaction.RollbackAsync();
        }
        Assert.Equal("admitted", Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events).DedupKey);
    }

    [VesselEventPostgresFact]
    public async Task Raw_save_cannot_seed_captured_lifecycle_changes_for_a_later_unit_of_work()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var capture = new VesselEventMutationCapture();
        await using var db = Context(database, owner, capture);
        var store = Store(db, capture);
        var work = new UnitOfWork(db, [store]);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
        var play = TestPlays.Create(account, new("manual", "BTC"));
        await work.ExecuteAsync(() =>
        {
            db.Accounts.Add(account); db.Plays.Add(play);
            return Task.FromResult(true);
        }, default);
        await using (var rawTransaction = await db.Database.BeginTransactionAsync())
        {
            var planned = play.MarkPlanned(DateTimeOffset.UtcNow);
            db.StatusChanges.Add(planned.Change); db.PlanRevisions.Add(planned.Revision);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            await rawTransaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        Assert.Empty((await EventReads.ReadAsync(db, 0, 200, default)).Events);
        Assert.Equal(PlayStatus.Draft, (await db.Plays.SingleAsync()).Status);
    }

    [VesselEventPostgresFact]
    public async Task Evidence_upload_with_event_capture_keeps_object_io_outside_database_transaction()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
        var play = TestPlays.Create(account, new("manual", "BTC"));
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account); seed.Plays.Add(play);
            await seed.SaveChangesAsync();
        }
        await using var db = Context(database, owner, new VesselEventMutationCapture());
        var objects = new EvidenceObjects(db);
        // The production service writes the object first, then a short unit of work owns the metadata transaction.
        var work = new UnitOfWork(db);
        var service = new EvidenceService(new EvidenceStore(db, work), objects, EvidenceLimits.Default,
            new CoreOwner(owner), TimeProvider.System, work);
        var uploaded = await service.UploadAsync(play.Id, new MemoryStream(EvidenceFixtures.Png),
            "Synthetic evidence", "upload", null, default);

        Assert.Null(db.Database.CurrentTransaction);
        await using var outside = database.Context(owner);
        var saved = await outside.Evidence.SingleAsync();
        Assert.Equal(uploaded.Id, saved.Id);
        Assert.Equal(play.Id, saved.PlayId);
        Assert.Equal("Synthetic evidence", saved.Note);
        Assert.Equal(saved.ObjectKey, Assert.Single(objects.Memory.Objects).Key);
        Assert.Equal(EvidenceFixtures.Png, objects.Memory.Objects[saved.ObjectKey]);
        Assert.Empty((await EventReads.ReadAsync(db, 0, 200, default)).Events);
    }

    [VesselEventPostgresTheory]
    [InlineData("nested")]
    [InlineData("hook")]
    [InlineData("committed")]
    [InlineData("rolled-back")]
    public async Task Closed_callback_cannot_change_run_metadata_in_active_or_later_transaction(string origin)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var capture = new VesselEventMutationCapture();
        await using var db = Context(database, owner, capture);
        var store = Store(db, capture);
        var gate = Signal();
        var validRun = Guid.CreateVersion7();
        var staleRun = Guid.CreateVersion7();
        Task<Exception>? late = null;
        Exception? admissionError = null;
        void StartLate() => late = Task.Run(async () =>
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await Record.ExceptionAsync(() => store.SetRunAsync(staleRun, "monitor", default));
        });
        async Task ReleaseLate()
        {
            gate.TrySetResult();
            admissionError = await late!.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var firstHook = new Hook { Before = () =>
        {
            if (origin == "hook" && late is null) StartLate();
            return Task.CompletedTask;
        }};
        var secondHook = new Hook { Before = async () =>
        {
            if (origin == "hook" && !gate.Task.IsCompleted) await ReleaseLate();
        }};
        var work = new UnitOfWork(db, [firstHook, secondHook, store]);
        if (origin is "committed" or "rolled-back")
        {
            var earlier = work.ExecuteAsync(() =>
            {
                StartLate();
                if (origin == "rolled-back") throw new InvalidOperationException("Synthetic rollback.");
                return Task.FromResult(true);
            }, default);
            if (origin == "rolled-back") await Assert.ThrowsAsync<InvalidOperationException>(() => earlier);
            else await earlier;
        }
        var account = new Account(Guid.NewGuid(), owner, "manual", "Synthetic");
        var play = TestPlays.Create(account, new("manual", "BTC"));
        await work.ExecuteAsync(async () =>
        {
            await store.SetRunAsync(validRun, "monitor", default);
            if (origin == "nested")
                await work.ExecuteAsync(() => { StartLate(); return Task.FromResult(true); }, default);
            if (origin != "hook")
                await work.ExecuteAsync(async () => { await ReleaseLate(); return true; }, default);
            db.Accounts.Add(account); db.Plays.Add(play);
            var planned = play.MarkPlanned(DateTimeOffset.UtcNow);
            db.StatusChanges.Add(planned.Change); db.PlanRevisions.Add(planned.Revision);
            return true;
        }, default);
        Assert.IsType<InvalidOperationException>(admissionError);
        var message = Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events);
        Assert.Equal(validRun, message.RunId);
        Assert.Equal("monitor", message.Source.Trigger);

        // No SetRun on the next operation: the committed baseline/run must not leak forward.
        await work.ExecuteAsync(() =>
        {
            db.StatusChanges.Add(play.Pause(DateTimeOffset.UtcNow));
            return Task.FromResult(true);
        }, default);
        var next = Assert.Single((await EventReads.ReadAsync(db, 1, 200, default)).Events);
        Assert.NotEqual(staleRun, next.RunId);
        Assert.NotEqual(validRun, next.RunId);
        Assert.Equal("owner", next.Source.Trigger);
    }

    [VesselEventPostgresTheory]
    [InlineData("append")]
    [InlineData("run")]
    [InlineData("baseline")]
    [InlineData("command")]
    public async Task Event_admission_during_final_save_poisons_transaction_even_when_rejection_is_swallowed(string action)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var gate = new SaveGate();
        await using var db = Context(database, owner, gate);
        var store = Store(db);
        var work = new UnitOfWork(db, [store]);
        var account = new Account(Guid.NewGuid(), owner, "manual", "Must roll back");
        Task<Exception>? late = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() =>
        {
            db.Accounts.Add(account);
            late = Task.Run(async () =>
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                try { return await Record.ExceptionAsync(() => Attempt(store, action, owner, account.Id)); }
                finally { gate.Release.TrySetResult(); }
            });
            return Task.FromResult(true);
        }, default));
        Assert.IsType<InvalidOperationException>(await late!.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var outside = database.Context(owner);
        Assert.Empty(await outside.Accounts.ToListAsync());
        Assert.Empty((await EventReads.ReadAsync(db, 0, 200, default)).Events);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [VesselEventPostgresFact]
    public async Task Rollback_closes_inherited_event_and_run_metadata_admission_before_database_rollback_yields()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var gate = new RollbackGate();
        await using var db = Context(database, owner, gate);
        var store = Store(db);
        var work = new UnitOfWork(db, [store]);
        Task? late = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<bool>(() =>
        {
            late = Task.Run(async () =>
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Draft("after-rollback"), default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetRunAsync(Guid.CreateVersion7(), "monitor", default));
                }
                finally { gate.Release.TrySetResult(); }
            });
            throw new InvalidOperationException("Synthetic rollback.");
        }, default));
        await late!.WaitAsync(TimeSpan.FromSeconds(10));
        await work.ExecuteAsync(async () => { await store.AppendAsync(Draft("next"), default); return true; }, default);
        Assert.Equal("next", Assert.Single((await EventReads.ReadAsync(db, 0, 200, default)).Events).DedupKey);
    }

    [VesselEventPostgresTheory]
    [InlineData("baseline")]
    [InlineData("command")]
    public async Task Unawaited_async_event_write_closes_its_lease_and_cannot_escape_rollback(string action)
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), owner, "manual", "Existing");
        await using (var seed = database.Context(owner))
        {
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
        }
        var gate = new ReadGate();
        await using var db = Context(database, owner, gate);
        var store = Store(db);
        var work = new UnitOfWork(db, [store]);
        Task? dangling = null;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(async () =>
            {
                dangling = Attempt(store, action, owner, account.Id);
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return true; // Deliberately fail to await the child.
            }, default));
        }
        finally { gate.Release.TrySetResult(); }
        Assert.NotNull(await Record.ExceptionAsync(() => dangling!.WaitAsync(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() => Task.FromResult(true), default));
        Assert.Empty((await EventReads.ReadAsync(db, 0, 200, default)).Events);
        await using var outside = database.Context(owner);
        Assert.Equal(0, await outside.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM event_baselines").SingleAsync());
    }

    private static Task Attempt(VesselEventStore store, string action, Guid owner, Guid account)
    {
        return action switch
        {
            "run" => store.SetRunAsync(Guid.CreateVersion7(), "monitor", default),
            "append" => store.AppendAsync(Draft("concurrent"), default),
            "baseline" => store.EnsureMonitoringBaselineAsync(account, default),
            "command" => store.EnqueueAsync(new(Guid.CreateVersion7(), account, owner, "manual"), default),
            _ => throw new ArgumentException("Unknown test action.")
        };
    }

    private static VesselEventDraft Draft(string key) => new("monitor.state-changed", Guid.CreateVersion7(), key,
        "info", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(), new("owner"),
        JsonSerializer.SerializeToElement(new { current = "live" }));
    private static VesselEventStore Store(VesselDbContext db, VesselEventMutationCapture? capture = null) =>
        new(db, capture ?? new(), new Outbox(), Options.Create(new VesselEventSettings()), NullLogger<VesselEventStore>.Instance);
    private static VesselDbContext Context(CoreDatabase database, Guid owner, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<VesselDbContext>().UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptors).Options, new CoreOwner(owner));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class RollbackGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class ReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class EvidenceObjects(VesselDbContext db) : IEvidenceObjectStore
    {
        public MemoryObjectStore Memory { get; } = new();
        public Task PutAsync(string key, Stream content, CancellationToken ct)
        {
            Assert.Null(db.Database.CurrentTransaction);
            return Memory.PutAsync(key, content, ct);
        }
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => Memory.OpenReadAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct)
        {
            Assert.Null(db.Database.CurrentTransaction);
            return Memory.DeleteAsync(key, ct);
        }
    }

    private sealed class Hook : IUnitOfWorkParticipant
    {
        public Func<Task> Before { get; init; } = () => Task.CompletedTask;
        public Task BeforeCommitAsync(CancellationToken ct) => Before();
        public Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Outbox : IVesselEventOutbox
    {
        public Func<Task> OnStage { get; init; } = () => Task.CompletedTask;
        public async Task StageAsync(VesselEvent message, CancellationToken ct) => await OnStage();
        public Task StageAsync(MonitorAccountCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public void Reset() { }
    }
}
