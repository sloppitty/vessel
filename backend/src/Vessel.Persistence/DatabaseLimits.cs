using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Vessel.Persistence;

/// <summary>
/// The session limits of request and worker connections: <c>lock_timeout</c> and <c>statement_timeout</c>, sent as
/// startup options on every connection of the host's one data source. They survive the pool's reset, so a
/// <c>SET</c> one borrower makes never reaches the next. Offline commands and the migration bundles connect without them.
/// Work that is long on purpose lifts a limit for its own transaction or session only (see the lift helpers).
/// </summary>
public sealed record DatabaseLimits(TimeSpan LockTimeout, TimeSpan StatementTimeout)
{
    public const string Section = "Vessel:Database";
    public static readonly DatabaseLimits Default = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(25));
    private static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    /// <summary>Reads and validates the settings. Messages name settings, never connection details.</summary>
    public static DatabaseLimits Read(IConfiguration configuration)
    {
        TimeSpan Setting(string name, TimeSpan fallback)
        {
            var text = configuration[$"{Section}:{name}"];
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) || value < TimeSpan.FromSeconds(1) ||
                value > Longest || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
                throw new InvalidOperationException($"{Section}:{name} must be a time span from 00:00:01 to 01:00:00 in whole milliseconds.");
            return value;
        }
        var limits = new DatabaseLimits(Setting("LockTimeout", Default.LockTimeout), Setting("StatementTimeout", Default.StatementTimeout));
        if (limits.LockTimeout > limits.StatementTimeout)
            throw new InvalidOperationException($"{Section}:LockTimeout must not be longer than {Section}:StatementTimeout.");
        // The server must report the timeout before the client gives up on the command, or the clear error is lost.
        if (configuration.GetConnectionString("Vessel") is { Length: > 0 } connection && CommandTimeout(connection) is { } client &&
            limits.StatementTimeout >= client)
            throw new InvalidOperationException($"{Section}:StatementTimeout must be shorter than the connection's Command Timeout.");
        return limits;
    }

    private static TimeSpan? CommandTimeout(string connection)
    {
        try
        {
            var seconds = new NpgsqlConnectionStringBuilder(connection).CommandTimeout;
            return seconds == 0 ? null : TimeSpan.FromSeconds(seconds);
        }
        catch (ArgumentException)
        {
            // An unreadable connection string fails where it is used, with its own safe message.
            return null;
        }
    }

    /// <summary>Request and worker hosts: these limits join the connection's startup options.</summary>
    internal void Apply(NpgsqlConnectionStringBuilder connection)
    {
        var options = $"-c lock_timeout={LockTimeout.TotalMilliseconds:0} -c statement_timeout={StatementTimeout.TotalMilliseconds:0}";
        var existing = connection.Options;
        connection.Options = string.IsNullOrWhiteSpace(existing) ? options : existing + " " + options;
    }

    /// <summary>
    /// Lifts the statement limit for the rest of the current transaction: retention sweeps, history backfill pages and
    /// bulk deletes. The client's command timeout still bounds each statement, as it did before these limits.
    /// </summary>
    internal static Task LiftStatementLimitAsync(DatabaseFacade database, CancellationToken ct)
    {
        if (database.CurrentTransaction is null) throw new InvalidOperationException("Lifting a limit needs the unit's transaction.");
        return database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = 0", ct);
    }

    /// <summary>
    /// Lifts the statement limit for a streamed read outside any transaction, such as an export: the connection stays open
    /// until the context is disposed, and the pool's reset on return restores the startup limits.
    /// </summary>
    internal static async Task LiftStatementLimitForSessionAsync(DatabaseFacade database, CancellationToken ct)
    {
        await database.OpenConnectionAsync(ct);
        await database.ExecuteSqlRawAsync(database.CurrentTransaction is null ? "SET statement_timeout = 0" : "SET LOCAL statement_timeout = 0", ct);
    }

    /// <summary>For a wait that is designed to block (it waits for other work to finish): no lock limit for that statement.</summary>
    internal const string LiftLockLimit = "SET LOCAL lock_timeout = 0";

    /// <summary>Back to the session's startup value after the blocking wait.</summary>
    internal const string RestoreLockLimit = "SET LOCAL lock_timeout TO DEFAULT";

    /// <summary>Runs one designed-to-block wait inside the current transaction without the lock limit; the statement limit stays.</summary>
    internal static async Task BlockingWaitAsync(DatabaseFacade database, FormattableString wait, CancellationToken ct)
    {
        if (database.CurrentTransaction is null) throw new InvalidOperationException("A blocking wait needs the unit's transaction.");
        await database.ExecuteSqlRawAsync(LiftLockLimit, ct);
        await database.ExecuteSqlInterpolatedAsync(wait, ct);
        await database.ExecuteSqlRawAsync(RestoreLockLimit, ct);
    }
}
