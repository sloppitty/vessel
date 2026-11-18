using System.Data.Common;

namespace Vessel.Application.Common;

/// <summary>
/// A statement that hit PostgreSQL's <c>lock_timeout</c> (55P03, lock not available) or <c>statement_timeout</c>
/// (57014). Found through the base <see cref="DbException"/>, so use cases need no provider reference. It is the
/// database being busy, never a venue failure: a request answers "try again", a worker retries on its next run.
/// </summary>
public static class DatabaseBusy
{
    public const string Message = "The database is busy. Try again in a moment.";

    public static bool Is(Exception error)
    {
        // A cancellation the caller asked for is reported as cancellation, even though the server says 57014.
        if (error is OperationCanceledException) return false;
        for (var current = error; current is not null; current = current.InnerException)
            if (current is DbException { SqlState: "55P03" or "57014" }) return true;
        return false;
    }
}
