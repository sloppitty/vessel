namespace Vessel.Application.Ownership;

/// <summary>
/// Owns a database write boundary on the scoped owner context. Nested calls participate and flush their changes
/// before returning, so the caller reads them and conflicts surface at the nested call; only the outer call commits.
/// Calls within a scope must be sequential and awaited; parallel or fire-and-forget work is unsupported.
/// </summary>
/// <remarks>
/// A failure inside a participant (a nested call, a store write or a store lock wrapper) poisons the whole unit:
/// it cannot be caught and continued. Later participants and the commit are refused and the unit rolls back, even
/// when the caller handled the exception. Keep participating calls out of catch-and-continue <c>try</c> blocks:
/// do the reads and writes a recovery path needs before the guarded external call, or after it outside the
/// <c>catch</c>. Only failures of non-participating work (for example a venue HTTP read) can be handled and recorded.
/// </remarks>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken);

    /// <summary>
    /// Runs reads against one consistent database snapshot, as the outermost unit (Phase 2, item 5c: a Play bundle whose
    /// parts must agree). It writes nothing; close it before any object-storage read. Stores without snapshots run it as
    /// an ordinary unit.
    /// </summary>
    Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken) => ExecuteAsync(read, cancellationToken);

    /// <summary>Persists intermediate changes inside the active boundary, without committing.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True while a unit of work or database transaction is running in this scope. Operations with side effects
    /// outside the database (object storage) refuse to start then: a later rollback could not undo them.
    /// </summary>
    bool IsActive { get; }

    /// <summary>Refuses, before any side effect, to run <paramref name="operation"/> inside an active unit.</summary>
    void RequireOutermost(string operation)
    {
        if (IsActive)
            throw new InvalidOperationException(
                $"{operation} writes object storage outside the database and cannot run inside a unit of work or transaction.");
    }
}
