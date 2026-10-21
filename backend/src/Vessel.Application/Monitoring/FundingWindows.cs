using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Monitoring;

/// <summary><c>Vessel:Funding</c> settings for reading settled funding in resumable windows (Phase 2, item 9a).</summary>
public sealed class FundingOptions
{
    public const string Section = "Vessel:Funding";

    /// <summary>How far back a Refresh tries to have funding covered.</summary>
    public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(30);
    /// <summary>The first window's length. It halves whenever a venue's page budget cannot hold a window.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(30);
    /// <summary>A window this short that still does not fit ends the Refresh's funding read.</summary>
    public TimeSpan MinimumWindow { get; set; } = TimeSpan.FromHours(1);
    /// <summary>At most this many venue reads per Refresh, so one account cannot spend a venue's shared budget.</summary>
    public int MaxReads { get; set; } = 8;
    /// <summary>How much before the stored coverage's end each Refresh reads again, so a settlement reported late is kept.</summary>
    public TimeSpan Recheck { get; set; } = TimeSpan.FromDays(1);
    /// <summary>
    /// All of a Refresh's funding windows together, so the browser's Refresh keeps its time: the single read this
    /// replaces had the venue clients' 20-second deadline.
    /// </summary>
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromSeconds(20);

    public void Validate()
    {
        // A venue read and the stored result both hold at most 31 days.
        if (Lookback <= TimeSpan.Zero || Lookback > TimeSpan.FromDays(31) || Window <= TimeSpan.Zero || Window > Lookback ||
            MinimumWindow < TimeSpan.FromMinutes(1) || MinimumWindow > Window || MaxReads is < 1 or > 100 ||
            Recheck < TimeSpan.Zero || Recheck > Lookback || TimeBudget < TimeSpan.FromSeconds(1) || TimeBudget > TimeSpan.FromSeconds(60))
            throw new InvalidOperationException($"{Section}: Lookback up to 31 days, Window up to Lookback, " +
                "MinimumWindow from 1 minute up to Window, MaxReads 1 to 100, Recheck 0 up to Lookback, TimeBudget 1 to 60 seconds.");
    }
}

/// <summary>A Refresh's funding read: one result to store, and the venue's retry time when a rate limit stopped it.</summary>
public sealed record FundingWindowRead(VenueFundingReadResult Result, DateTimeOffset? RetryAtUtc = null);

/// <summary>
/// Reads settled funding in bounded windows (D-23), so a venue whose page budget cannot hold the whole lookback
/// (Lighter's five pages against 720 hourly rows a market) is still covered, window by window and Refresh by Refresh.
/// The account's stored contiguous coverage is the cursor: the span after it is read oldest first and the span
/// before it newest first, so every completed window extends that one interval. Only the last <c>Recheck</c> of the
/// stored coverage is read again, so a settlement the venue reports late is still kept and a changed one still fails
/// closed. A window is halved only when the venue says its page budget truncated it; any other incompleteness, or a
/// failure, ends the read. Facts from completed windows are kept even then. Missing funding is never zero.
/// </summary>
public static class FundingWindows
{
    private const int MaxPayments = 10_000;

    public static async Task<FundingWindowRead> ReadAsync(IVenueFundingReader reader, Account account, DateTimeOffset to,
        (DateTimeOffset From, DateTimeOffset To)? covered, FundingOptions options, Func<CancellationToken, Task> beforeRead,
        TimeProvider time, CancellationToken ct)
    {
        // Running out of time cancels the read in flight, like a venue deadline: kept progress or an incomplete Refresh.
        using var budget = new CancellationTokenSource(options.TimeBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        to = Floor(to);
        var floor = Ceiling(to - options.Lookback);
        // Stored coverage is the cursor only where it reaches into the lookback; otherwise reading starts afresh at the
        // newest boundary, and the store keeps the newer interval (it never bridges an unread gap).
        var known = covered is { } stored && stored.To >= floor && stored.From <= to ? stored : ((DateTimeOffset, DateTimeOffset)?)null;
        var low = known is { } k ? Max(Ceiling(k.Item1), floor) : to;
        var high = known is { } h ? Max(Min(Floor(h.Item2 - options.Recheck), to), low) : to;
        var payments = new Dictionary<string, VenueFundingPayment>(StringComparer.Ordinal);
        var size = Floor(options.Window);
        var reads = 0;
        var completed = 0;
        VenueFundingReadResult? last = null, incomplete = null;
        string? stop = null;
        DateTimeOffset? retryAt = null;

        // Forward from the stored interval to the newest boundary, then back from it to the lookback's start.
        foreach (var forward in new[] { true, false })
        {
            while (stop is null && (forward ? high < to : low > floor))
            {
                if (reads == options.MaxReads) { stop = "the per-Refresh read limit was reached"; break; }
                if (budget.IsCancellationRequested) { stop = "the Refresh's time for funding ran out"; break; }
                var (start, end) = forward ? (high, Min(high + size, to)) : (Max(low - size, floor), low);
                VenueFundingReadResult result;
                try
                {
                    await beforeRead(linked.Token);
                    reads++;
                    result = await reader.ReadFundingAsync(account, start, end, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    VenueFundingChecks.Validate(result, start, end);
                }
                catch (VenueRateLimitException limited) when (completed > 0)
                {
                    retryAt = limited.RetryAtUtc;
                    stop = "the venue asked Vessel to wait";
                    break;
                }
                catch (Exception error) when (completed > 0 && (error is VenueReadException or HttpRequestException ||
                    error is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    stop = budget.IsCancellationRequested ? "the Refresh's time for funding ran out" : "a later window could not be read";
                    break;
                }
                if (!result.Complete)
                {
                    incomplete = result;
                    // Only a page budget that ran out is helped by a narrower window, newest-adjacent half first.
                    if (!result.Truncated || end - start <= options.MinimumWindow)
                    {
                        stop = "a window was incomplete";
                        break;
                    }
                    size = Floor((end - start) / 2);
                    continue;
                }
                if (payments.Count + result.Payments.Count > MaxPayments)
                {
                    stop = "the per-Refresh payment limit was reached";
                    break;
                }
                foreach (var payment in result.Payments)
                {
                    // Adjacent windows share their inclusive boundary; the same settlement must read the same.
                    if (payments.TryGetValue(payment.SourceId, out var seen))
                    {
                        if (seen with { RawJson = payment.RawJson } != payment)
                            throw new VenueReadException("The venue returned conflicting funding identities.");
                    }
                    else payments.Add(payment.SourceId, payment);
                }
                last = result;
                completed++;
                if (forward) high = end; else low = start;
            }
        }

        if (completed == 0 && incomplete is not null)
            // Nothing new was verified: keep the venue's own incomplete result, so its facts are kept and the stored
            // coverage stays unchanged.
            return new(incomplete);
        var whole = low <= floor && high >= to;
        var notice = (last?.Notice ?? "Settled funding as the venue reports it; positive received, negative paid.") + (whole ? "" :
            $" Funding is covered from {low.UtcDateTime:yyyy-MM-dd HH:mm} to {high.UtcDateTime:yyyy-MM-dd HH:mm} UTC of the last " +
            $"{options.Lookback.TotalDays:0.##} days; {stop ?? "the rest was not read"}, so refresh again to read the rest. Missing funding is not zero.");
        if (low == high && completed == 0)
            // No coverage existed and nothing could be kept: an empty, incomplete attempt over the lookback.
            return new(new([], floor, to, false, notice, FundingFacts.Unknown), retryAt);
        return new(new(payments.Values.OrderBy(p => p.OccurredAtUtc).ThenBy(p => p.SourceId, StringComparer.Ordinal).ToList(),
            low, high, true, notice, last?.PnlBasis ?? FundingFacts.Unknown), retryAt);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    // Venue windows are millisecond-aligned. Stored bounds round inward (start up, end down), so windows always overlap them.
    private static DateTimeOffset Floor(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    private static DateTimeOffset Ceiling(DateTimeOffset value)
    {
        var floor = Floor(value);
        return floor < value ? floor.AddMilliseconds(1) : floor;
    }
    private static TimeSpan Floor(TimeSpan value) => TimeSpan.FromMilliseconds(Math.Floor(value.TotalMilliseconds));
}
