using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Vessel.Persistence.Events;

internal static class VesselEventSql
{
    internal static DbCommand TransactionCommand(VesselDbContext db, string sql, params (string Name, object? Value)[] parameters)
    {
        // Recheck after awaits: an unawaited callback may resume after rollback closed its lease.
        RequireTransaction(db);
        return Command(db, sql, parameters);
    }

    internal static DbCommand Command(VesselDbContext db, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        return command;
    }

    internal static async Task<int> ExecuteAsync(VesselDbContext db, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = TransactionCommand(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<object?> ScalarAsync(VesselDbContext db, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = TransactionCommand(db, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// A wait that is designed to block (the retention key, a receipt another dispatcher holds): no lock limit for this
    /// statement only. The statement limit still bounds it, and the session value returns afterwards.
    /// </summary>
    internal static async Task<object?> BlockingScalarAsync(VesselDbContext db, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await ExecuteAsync(db, DatabaseLimits.LiftLockLimit, ct);
        var value = await ScalarAsync(db, sql, ct, parameters);
        await ExecuteAsync(db, DatabaseLimits.RestoreLockLimit, ct);
        return value;
    }

    /// <summary>Retention sweeps and bulk deletes: no statement limit for the rest of this transaction.</summary>
    internal static Task LiftStatementLimitAsync(VesselDbContext db, CancellationToken ct) =>
        ExecuteAsync(db, "SET LOCAL statement_timeout = 0", ct);

    internal static void RequireTransaction(VesselDbContext db)
    {
        UnitOfWork.ForCurrentContext(db).RequireActive();
        if (db.CurrentOwnerId == Guid.Empty) throw new UnauthorizedAccessException("Missing event owner.");
    }

    // ForCurrentContext already requires an active participation; ParticipateAsync rechecks it after admission.
    internal static Task<T> ParticipateAsync<T>(VesselDbContext db, Func<Task<T>> operation, CancellationToken ct) =>
        UnitOfWork.ForCurrentContext(db).ParticipateAsync(async () =>
        {
            if (db.CurrentOwnerId == Guid.Empty) throw new UnauthorizedAccessException("Missing event owner.");
            return await operation();
        }, ct);

    internal static Task ParticipateAsync(VesselDbContext db, Func<Task> operation, CancellationToken ct) =>
        ParticipateAsync(db, async () => { await operation(); return true; }, ct);

    /// <summary>Per-owner retention key: dispatch takes it shared, floor publication exclusively.</summary>
    internal static long RetentionLock(Guid owner) => BitConverter.ToInt64(owner.ToByteArray(), 0) ^ 0x5645_5353_454C_5254;
}
