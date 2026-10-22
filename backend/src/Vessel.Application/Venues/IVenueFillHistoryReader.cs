using Vessel.Domain.Accounts;

namespace Vessel.Application.Venues;

/// <summary>
/// Phase 2, item 6b: one page of an account's executions in an inclusive, millisecond-aligned time window, for a backfill
/// the owner started. Pages are taken from the window's start; the caller pages from the newest raw time.
/// </summary>
public interface IVenueFillHistoryReader
{
    string VenueId { get; }
    /// <summary>The most raw rows one page returns; a page this full may have left rows at its newest time.</summary>
    int PageLimit { get; }
    /// <summary>How many of the account's most recent executions the venue still serves; older ones are beyond its reach.</summary>
    int Reach { get; }
    Task<VenueFillPage> ReadFillPageAsync(Account account, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

/// <param name="Fills">The in-scope executions of the page, normalized like any import.</param>
/// <param name="RawCount">Every row the venue returned, out-of-scope ones included.</param>
/// <param name="OldestRawAtUtc">The oldest raw row's time; null without rows.</param>
/// <param name="NewestRawAtUtc">The newest raw row's time; null without rows.</param>
/// <param name="OmittedExecutions">In-scope rows left out because a fact could not be verified.</param>
public sealed record VenueFillPage(IReadOnlyList<VenueFill> Fills, int RawCount, DateTimeOffset? OldestRawAtUtc,
    DateTimeOffset? NewestRawAtUtc, int OmittedExecutions);
