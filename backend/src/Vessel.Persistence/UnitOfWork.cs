using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Persistence.Configurations;

namespace Vessel.Persistence;

/// <summary>
/// The scoped write boundary. The outer <see cref="ExecuteAsync{T}"/> owns the transaction; nested calls and the
/// store lock wrappers participate in it. The first participant failure is remembered and the unit rolls back even
/// when the caller catches it. Each flow is tracked by an <see cref="AsyncLocal{T}"/> participation, so a stale,
/// dangling or parallel callback is rejected instead of writing through a finished or shared transaction.
/// </summary>
public sealed class UnitOfWork(VesselDbContext db, IEnumerable<IUnitOfWorkParticipant>? participants = null,
    ILogger<UnitOfWork>? logger = null) : IUnitOfWork
{
    public const string PlayConflict = "This Play changed elsewhere. Reload it before saving again.";
    public const string ExecutionConflict = "This Play changed elsewhere. Reload it and check the venue again.";
    /// <summary>Exception data key holding a rollback error that followed, and must not replace, the original failure.</summary>
    public const string RollbackErrorKey = "Vessel.UnitOfWork.RollbackError";

    private readonly IUnitOfWorkParticipant[] participants = participants?.ToArray() ?? [];
    private static readonly AsyncLocal<UnitOfWork?> boundary = new();
    private readonly AsyncLocal<Participation?> current = new();
    private Participation? active;
    private Exception? failure;
    // A dangling participant or an unknown connection state: this scope can never start another unit.
    private bool unusable;
    private VesselDbContext Context => db;

    // Infrastructure writers join this exact boundary without a constructor cycle through participants.
    // An EF transaction alone conveys no authority, and copied ExecutionContexts retain closed callbacks.
    internal static UnitOfWork ForCurrentContext(VesselDbContext context)
    {
        var work = boundary.Value;
        if (work is null || !ReferenceEquals(work.Context, context))
            throw new InvalidOperationException("The current unit of work does not own this context.");
        // Child admission/overlap is checked by ParticipateAsync so an overlapping attempt
        // poisons its own live transaction, but a closed callback cannot poison a later one.
        work.RequireActiveParticipation();
        return work;
    }

    public Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, IsolationLevel.ReadCommitted, cancellationToken);

    /// <summary>One repeatable-read snapshot for related reads; it must be the outermost unit, so its isolation holds.</summary>
    public Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        if (IsActive) throw new InvalidOperationException("A read snapshot must be the outermost unit of work.");
        return RunAsync(read, IsolationLevel.RepeatableRead, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, IsolationLevel isolation, CancellationToken cancellationToken)
    {
        if (unusable)
            throw new InvalidOperationException("The failed transaction requires a new scope.");
        if (current.Value is { } inherited && (inherited.Completing || !Belongs(inherited)))
            throw new InvalidOperationException("This participant belongs to a completed unit of work.");
        var root = new Participation(null);
        if (Interlocked.CompareExchange(ref active, root, null) is not null)
            // Nested units flush before returning, so the caller reads their writes and conflicts surface here.
            return await ParticipateAsync(async () =>
            {
                var result = await operation();
                await SaveChangesAsync(cancellationToken);
                return result;
            }, cancellationToken);
        current.Value = root;
        var previousBoundary = boundary.Value;
        boundary.Value = this;
        failure = null;
        IDbContextTransaction? transaction = null;
        var committed = false;
        try
        {
            if (db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("The unit of work must own the database transaction.");
            cancellationToken.ThrowIfCancellationRequested();
            // Facts revisions share a row per owner; stricter writer snapshots can fail on unrelated fact writes, so
            // only read snapshots ask for repeatable read.
            transaction = await db.Database.BeginTransactionAsync(isolation, cancellationToken);
            var result = await operation();
            RequireAwaited(root);
            await SaveChangesAsync(cancellationToken);
            RequireActive();
            foreach (var participant in participants)
            {
                // Central SaveChanges remains available here without recursively invoking participants.
                await ParticipateAsync(async () =>
                {
                    await participant.BeforeCommitAsync(cancellationToken);
                    return true;
                }, cancellationToken);
                RequireAwaited(root);
                RequireActive();
            }
            // Closes admission; it fails if a participant slipped in while the final save or a hook was running.
            if (!root.StartCompletion()) throw Dangling();
            // A participant rejected during the final save or a hook records its failure after the check above.
            if (failure is not null)
                throw new InvalidOperationException("A failed participant requires the unit of work to roll back.", failure);
            cancellationToken.ThrowIfCancellationRequested();
            // Past this point the outcome is decided; a late cancellation must not abandon the commit halfway.
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
            // The rollback catch cannot observe postcommit failures. Durable recovery owns failed sends.
            foreach (var participant in participants)
            {
                try { await participant.AfterCommitAsync(CancellationToken.None); }
                catch { Warn("A postcommit participant failed; the committed operation remains successful."); }
            }
            return result;
        }
        catch (Exception error) when (!committed)
        {
            // Stop inherited callbacks before rollback yields, not only after disposal.
            unusable |= root.Close();
            await RollbackAsync(transaction, error);
            throw;
        }
        finally
        {
            try
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }
            catch (Exception error)
            {
                unusable = true;
                logger?.LogError(error, "Disposing a unit-of-work transaction failed.");
            }
            finally
            {
                unusable |= root.Close();
                current.Value = null;
                boundary.Value = previousBoundary;
                active = null;
                failure = null;
            }
        }
    }

    private void Warn(string message)
    {
        // Never log exception text, connection strings or event bodies, or turn logging failure into a retry.
        try { logger?.LogWarning("{UnitOfWorkNotice}", message); }
        catch { }
    }

    // Lock wrappers and store writes participate but cannot start, commit or roll back a transaction.
    internal async Task<T> ParticipateAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        Participation? parent = null;
        Participation? self = null;
        try
        {
            RequireActive();
            cancellationToken.ThrowIfCancellationRequested();
            parent = current.Value!;
            if (!parent.StartChild())
                throw new InvalidOperationException("Concurrent use of a scoped unit of work is not supported.");
            self = new Participation(active);
            current.Value = self;
            var result = await operation();
            RequireAwaited(self);
            RequireActive();
            if (!self.StartCompletion()) throw Dangling();
            return result;
        }
        catch (Exception error)
        {
            if (self?.ChildRunning == true) unusable = true;
            // A stale continuation cannot poison a later unit it was never admitted to.
            if (active is { Completing: false } && (current.Value is null || Belongs(current.Value)))
                failure ??= error;
            throw;
        }
        finally
        {
            if (self is not null)
            {
                // ExecutionContext copies retain this object after the callback returns.
                // Close it before releasing the parent so those copies cannot re-enter.
                unusable |= self.Close();
                current.Value = parent;
                parent!.FinishChild();
            }
            if (unusable) db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Runs an operation and saves its tracked effects inside a savepoint of the active unit. On failure the effects
    /// roll back to the savepoint, tracked entities and participants' buffered state are discarded and the unit stays
    /// usable, so the caller still holds its row locks and can durably record the failure in the same transaction.
    /// Call it from the operation itself, before any other buffered participant state: that state is discarded too.
    /// A dangling callback or a failed savepoint rollback still makes the whole unit roll back.
    /// </summary>
    internal async Task<Exception?> IsolateAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        RequireActive();
        const string savepoint = "vessel_isolated";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            await ParticipateAsync(async () =>
            {
                await operation();
                // Saved here, not by the final save: a failing effect belongs to the isolated operation.
                await SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);
            return null;
        }
        catch (Exception error) when (!unusable)
        {
            try { await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None); }
            catch (Exception rollback)
            {
                // The connection state is unknown: report the original failure, with the rollback error beside it.
                unusable = true;
                error.Data[RollbackErrorKey] = rollback;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
            db.ChangeTracker.Clear();
            foreach (var participant in participants) await participant.RollbackAsync(CancellationToken.None);
            // The poisoning failure was confined to the savepoint that has just been undone.
            failure = null;
            return error;
        }
    }

    private void RequireActiveParticipation()
    {
        var root = active;
        if (unusable || root is null || current.Value is not { } participant || participant.Completing ||
            participant != root && participant.Root != root || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An active unit of work is required.");
        if (failure is not null)
            throw new InvalidOperationException("A failed participant requires the unit of work to roll back.", failure);
        if (root.Completing)
            throw new InvalidOperationException("The unit of work is completing; new participants are not allowed.");
    }

    internal void RequireActive()
    {
        RequireActiveParticipation();
        if (current.Value!.ChildRunning)
            throw new InvalidOperationException("Concurrent use of a scoped unit of work is not supported.");
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => SaveChangesAsync(PlayConflict, cancellationToken);

    // Any running unit on this scope counts, as does a transaction the unit does not own: an EF transaction,
    // an explicitly enlisted one or an ambient System.Transactions scope.
    public bool IsActive => Volatile.Read(ref active) is not null || db.Database.CurrentTransaction is not null ||
        System.Transactions.TransactionsDatabaseFacadeExtensions.GetEnlistedTransaction(db.Database) is not null ||
        System.Transactions.Transaction.Current is not null;

    /// <summary>Flushes inside the active unit; <paramref name="conflict"/> words a Play version conflict for this area.</summary>
    internal Task SaveChangesAsync(string conflict, CancellationToken cancellationToken) => ParticipateAsync(async () =>
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            failure ??= error;
            throw new ProblemException(409, conflict);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_accounts_owner_venue_source" })
        {
            failure ??= error;
            throw new ProblemException(409, "An account for this venue and source already exists. Manage or re-enable that account.");
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PlayRuleIndexes.Version or PlayRuleIndexes.Active })
        {
            // Phase 2, item 3: another change of the if-then rule committed first.
            failure ??= error;
            throw new ProblemException(409, Vessel.Application.Plays.PlayRules.Changed);
        }
        return true;
    }, cancellationToken);

    private bool Belongs(Participation participant) => active is { } root && (participant == root || participant.Root == root);

    private async Task RollbackAsync(IDbContextTransaction? transaction, Exception original)
    {
        try
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            // The connection state is unknown. Report the original failure and keep the rollback error beside it.
            unusable = true;
            original.Data[RollbackErrorKey] = error;
            logger?.LogError(error, "Rolling back a failed unit of work failed; the original error is reported.");
        }
        finally
        {
            db.ChangeTracker.Clear();
            foreach (var participant in participants)
            {
                try { await participant.RollbackAsync(CancellationToken.None); }
                catch { Warn("A unit-of-work rollback participant could not clear its state."); unusable = true; }
            }
        }
    }

    private void RequireAwaited(Participation participant)
    {
        // A dangling callback may still use this context. It must never join another transaction.
        if (participant.ChildRunning) throw Dangling();
    }

    private InvalidOperationException Dangling()
    {
        unusable = true;
        return new("Unit-of-work participants must be awaited. A new scope is required.");
    }

    // 0 idle, 1 running a child participant, 2 completing or closed.
    private sealed class Participation(Participation? root)
    {
        private int state;
        public Participation? Root { get; } = root;
        public bool Completing => Volatile.Read(ref state) == 2;
        public bool ChildRunning => Volatile.Read(ref state) == 1;
        public bool StartChild() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
        public void FinishChild() => Interlocked.CompareExchange(ref state, 0, 1);
        public bool StartCompletion() => Interlocked.CompareExchange(ref state, 2, 0) == 0;
        /// <summary>Closes the participation; true when a child was still running (a dangling callback).</summary>
        public bool Close() => Interlocked.Exchange(ref state, 2) == 1;
    }
}
