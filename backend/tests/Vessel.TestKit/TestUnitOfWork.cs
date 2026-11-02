using System.Runtime.CompilerServices;
using Vessel.Application.Ownership;
using Vessel.Persistence;

namespace Vessel.Tests;

internal static class TestUnitOfWork
{
    // Manually constructed test services still share exactly one boundary per context, as DI does.
    private static readonly ConditionalWeakTable<VesselDbContext, UnitOfWork> Units = new();
    public static UnitOfWork For(VesselDbContext db) => Units.GetValue(db, context => new UnitOfWork(context));
    public static IUnitOfWork None { get; } = new MemoryUnitOfWork();
    public static IUnitOfWork Saving(Func<CancellationToken, Task> save) => new MemoryUnitOfWork(save);

    /// <summary>
    /// The database-free double keeps <see cref="UnitOfWork"/>'s rules: saves need an active unit, nested units
    /// participate and flush, cancelled participants never run, and a caught participant failure (including a
    /// cancellation) still fails the outer unit before it saves.
    /// State is per async flow, so the shared <see cref="None"/> instance is safe across parallel tests.
    /// </summary>
    private sealed class MemoryUnitOfWork(Func<CancellationToken, Task>? save = null) : IUnitOfWork
    {
        private readonly AsyncLocal<Unit?> current = new();

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
        {
            if (current.Value is { } outer)
            {
                outer.RequireActive();
                try
                {
                    // As in UnitOfWork, a cancelled participant never runs and still fails the outer unit.
                    cancellationToken.ThrowIfCancellationRequested();
                    var nested = await operation();
                    await SaveChangesAsync(cancellationToken);
                    return nested;
                }
                catch (Exception error)
                {
                    outer.Failure ??= error;
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var unit = new Unit();
            current.Value = unit;
            try
            {
                var result = await operation();
                unit.RequireActive();
                await SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                unit.Completed = true;
                current.Value = null;
            }
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            var unit = current.Value ?? throw new InvalidOperationException("An active unit of work is required.");
            unit.RequireActive();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (save is not null) await save(cancellationToken);
            }
            catch (Exception error)
            {
                unit.Failure ??= error;
                throw;
            }
        }

        public bool IsActive => current.Value is { Completed: false };

        private sealed class Unit
        {
            public Exception? Failure { get; set; }
            public bool Completed { get; set; }

            public void RequireActive()
            {
                if (Completed) throw new InvalidOperationException("An active unit of work is required.");
                if (Failure is not null)
                    throw new InvalidOperationException("A failed participant requires the unit of work to roll back.", Failure);
            }
        }
    }
}
