using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class VesselEventUnitOfWorkTests
{
    [VesselEventPostgresFact]
    public async Task Beforecommit_can_save_without_recursion_and_aftercommit_failure_is_not_a_failed_operation()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var probe = new Probe();
        var logs = new Notices();
        var work = new UnitOfWork(db, [probe], logs);
        probe.Before = async () =>
        {
            db.Accounts.Add(new Account(Guid.NewGuid(), owner, "manual", "Before commit"));
            await work.SaveChangesAsync(default);
            await using var outside = database.Context(owner);
            Assert.Empty(await outside.Accounts.ToListAsync());
        };
        probe.After = async () =>
        {
            await using var outside = database.Context(owner);
            Assert.Single(await outside.Accounts.ToListAsync());
            throw new InvalidOperationException("DO-NOT-LOG synthetic private connection and payload");
        };
        var result = await work.ExecuteAsync(() => work.ExecuteAsync(() => Task.FromResult(42), default), default);
        Assert.Equal(42, result);
        Assert.Equal((1, 1, 0), (probe.BeforeCalls, probe.AfterCalls, probe.RollbackCalls));
        Assert.Single(logs.Messages);
        Assert.DoesNotContain("DO-NOT-LOG", logs.Messages[0], StringComparison.Ordinal);
        Assert.Null(logs.Exception);
    }

    [VesselEventPostgresFact]
    public async Task Failed_beforecommit_rolls_back_saved_mutations_and_clears_participants_without_dispatch()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var probe = new Probe();
        var work = new UnitOfWork(db, [probe]);
        probe.Before = async () =>
        {
            await work.SaveChangesAsync(default);
            throw new InvalidOperationException("Synthetic staging failure.");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(() =>
        {
            db.Accounts.Add(new Account(Guid.NewGuid(), owner, "manual", "Rolled back"));
            return Task.FromResult(true);
        }, default));
        Assert.Equal((1, 0, 1), (probe.BeforeCalls, probe.AfterCalls, probe.RollbackCalls));
        Assert.Empty(db.ChangeTracker.Entries());
        probe.Before = () => Task.CompletedTask;
        await work.ExecuteAsync(() => Task.FromResult(true), default);
        await using var outside = database.Context(owner);
        Assert.Empty(await outside.Accounts.ToListAsync());
    }

    private sealed class Probe : IUnitOfWorkParticipant
    {
        public Func<Task> Before { get; set; } = () => Task.CompletedTask;
        public Func<Task> After { get; set; } = () => Task.CompletedTask;
        public int BeforeCalls { get; private set; }
        public int AfterCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public Task BeforeCommitAsync(CancellationToken ct) { BeforeCalls++; return Before(); }
        public Task AfterCommitAsync(CancellationToken ct) { AfterCalls++; return After(); }
        public Task RollbackAsync(CancellationToken ct) { RollbackCalls++; return Task.CompletedTask; }
    }

    private sealed class Notices : ILogger<UnitOfWork>
    {
        public List<string> Messages { get; } = [];
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, error)); Exception = error; }
    }
}
