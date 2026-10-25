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
    /// <summary>
    /// Phase 2, item 9c: how often the monitor worker reads the funding of an account with an Open venue Play or kept
    /// execution history, behind protection checks, with the same windows, bounds and cursor as a Refresh.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    public void Validate()
    {
        // A venue read and the stored result both hold at most 31 days.
        if (Lookback <= TimeSpan.Zero || Lookback > TimeSpan.FromDays(31) || Window <= TimeSpan.Zero || Window > Lookback ||
            MinimumWindow < TimeSpan.FromMinutes(1) || MinimumWindow > Window || MaxReads is < 1 or > 100 ||
            Recheck < TimeSpan.Zero || Recheck > Lookback || TimeBudget < TimeSpan.FromSeconds(1) || TimeBudget > TimeSpan.FromSeconds(60))
            throw new InvalidOperationException($"{Section}: Lookback up to 31 days, Window up to Lookback, " +
                "MinimumWindow from 1 minute up to Window, MaxReads 1 to 100, Recheck 0 up to Lookback, TimeBudget 1 to 60 seconds.");
        // Slow, and short enough that the next read still continues the stored coverage within the lookback.
        if (Interval < TimeSpan.FromHours(1) || Interval > TimeSpan.FromDays(7) || Interval > Lookback)
            throw new InvalidOperationException($"{Section}:Interval must be between 1 hour and 7 days, and at most Lookback.");
    }
}

/// <summary>A Refresh's funding read: one result to store, and the venue's retry time when a rate limit stopped it.</summary>
public sealed record FundingWindowRead(VenueFundingReadResult Result, DateTimeOffset? RetryAtUtc = null);

/// <summary>
/// The account's stored funding state that steers a Refresh: its contiguous verified coverage, which is the cursor, and
/// its last attempted window: the widest the last read completed, whose span the next one starts from, or, when the
/// last read was incomplete, the last one tried, whose half it starts from.
/// </summary>
public sealed record FundingCursor(DateTimeOffset? CoveredFromUtc, DateTimeOffset? CoveredToUtc, DateTimeOffset? LastAttemptFromUtc,
    DateTimeOffset? LastAttemptToUtc, bool LastReadComplete = true);

/// <summary>
/// Reads settled funding in bounded windows (D-23), so a venue whose page budget cannot hold the whole lookback (Lighter's
/// five pages against 720 hourly rows a market) is still covered, window by window and Refresh by Refresh.
/// <list type="bullet">
/// <item>The account's stored contiguous coverage is the cursor. Uncovered spans come first: the span after it oldest
/// first, then the span before it newest first, so every completed window extends that one interval.</item>
/// <item>Only then, with the reads left, the last <c>Recheck</c> of the stored coverage is read again, so a settlement
/// the venue reports late is kept and a changed one still fails closed; rechecks never starve new coverage (Sol round 1
/// on #44).</item>
/// <item>A window halves only when the venue says its page budget truncated it, and doubles after a full-size success,
/// never to a size that just failed. The first window starts from the stored last attempt: the widest window the last
/// Refresh completed, or half the last one it tried when it completed none. A size that fits is not relearned from
/// scratch every Refresh (Sol round 1 on #44).</item>
/// <item>Any other incompleteness, or a failure, ends the read; facts from completed windows are kept even then.</item>
/// </list>
/// Missing funding is never zero.
/// </summary>
public static class FundingWindows
{
    private const int MaxPayments = 10_000;

    private enum Outcome { Done, Narrower, Stop }

    public static async Task<FundingWindowRead> ReadAsync(IVenueFundingReader reader, Account account, DateTimeOffset to,
        FundingCursor? cursor, FundingOptions options, Func<CancellationToken, Task> beforeRead, TimeProvider time, CancellationToken ct)
    {
        // Running out of time cancels the read in flight, like a venue deadline: kept progress or an incomplete Refresh.
        using var budget = new CancellationTokenSource(options.TimeBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        to = Floor(to);
        var floor = Ceiling(to - options.Lookback);
        // Stored coverage is the cursor only where it reaches into the lookback; otherwise reading starts afresh at the
        // newest boundary, and the store keeps the newer interval (it never bridges an unread gap).
        var known = cursor is { CoveredFromUtc: { } from, CoveredToUtc: { } until } && until >= floor && from <= to
            ? (From: from, To: until) : ((DateTimeOffset From, DateTimeOffset To)?)null;
        var low = known is { } k ? Max(Ceiling(k.From), floor) : to;
        var high = known is { } h ? Min(Floor(h.To), to) : to;
        var payments = new Dictionary<string, VenueFundingPayment>(StringComparer.Ordinal);
        var size = Floor(options.Window);
        if (cursor is { LastAttemptFromUtc: { } attemptFrom, LastAttemptToUtc: { } attemptTo } && attemptTo > attemptFrom)
            size = Clamp(Floor(cursor.LastReadComplete ? attemptTo - attemptFrom : (attemptTo - attemptFrom) / 2),
                options.MinimumWindow, options.Window);
        TimeSpan? failed = null;
        (DateTimeOffset From, DateTimeOffset To)? widest = null;
        var reads = 0;
        var completed = 0;
        VenueFundingReadResult? last = null, incomplete = null;
        string? stop = null;
        DateTimeOffset? retryAt = null;

        async Task<Outcome> ReadWindow(DateTimeOffset start, DateTimeOffset end)
        {
            if (reads == options.MaxReads) { stop = "the per-Refresh read limit was reached"; return Outcome.Stop; }
            if (budget.IsCancellationRequested) { stop = "the Refresh's time for funding ran out"; return Outcome.Stop; }
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
                return Outcome.Stop;
            }
            catch (Exception error) when (completed > 0 && (error is VenueReadException or HttpRequestException ||
                error is OperationCanceledException && !ct.IsCancellationRequested))
            {
                stop = budget.IsCancellationRequested ? "the Refresh's time for funding ran out" : "a later window could not be read";
                return Outcome.Stop;
            }
            if (!result.Complete)
            {
                incomplete = result;
                // Only a page budget that ran out is helped by a narrower window.
                if (!result.Truncated || end - start <= options.MinimumWindow) { stop = "a window was incomplete"; return Outcome.Stop; }
                failed = failed is { } f && f < end - start ? f : end - start;
                size = Floor((end - start) / 2);
                return Outcome.Narrower;
            }
            if (payments.Count + result.Payments.Count > MaxPayments) { stop = "the per-Refresh payment limit was reached"; return Outcome.Stop; }
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
            if (widest is not { } w || end - start > w.To - w.From) widest = (start, end);
            // Grow back after a full-size success (a window cut at a boundary proves nothing larger), but never to a
            // size that has just failed.
            var grown = size * 2 > options.Window ? Floor(options.Window) : Floor(size * 2);
            if (end - start >= size && (failed is not { } ceiling || grown < ceiling)) size = grown;
            return Outcome.Done;
        }

        // Uncovered spans first: after the stored coverage, oldest first, then before it, newest first.
        while (stop is null && high < to)
        {
            var end = Min(high + size, to);
            if (await ReadWindow(high, end) == Outcome.Done) high = end;
        }
        while (stop is null && low > floor)
        {
            var start = Max(low - size, floor);
            if (await ReadWindow(start, low) == Outcome.Done) low = start;
        }
        // Then, with the reads left, the stored coverage's last Recheck again, for settlements reported late.
        if (known is { } recheck && options.Recheck > TimeSpan.Zero)
        {
            var end = Min(Floor(recheck.To), high);
            var at = Max(Ceiling(recheck.To - options.Recheck), low);
            while (stop is null && at < end)
            {
                var next = Min(at + size, end);
                if (await ReadWindow(at, next) == Outcome.Done) at = next;
            }
        }

        if (completed == 0 && incomplete is not null)
            // Nothing new was verified: keep the venue's own incomplete result, so its facts are kept and the stored
            // coverage stays unchanged. Its span, the last window tried, is where the next Refresh starts.
            return new(incomplete);
        var whole = low <= floor && high >= to;
        var notice = (last?.Notice ?? "Settled funding as the venue reports it; positive received, negative paid.") + (whole ? "" :
            $" Funding is covered from {low.UtcDateTime:yyyy-MM-dd HH:mm} to {high.UtcDateTime:yyyy-MM-dd HH:mm} UTC of the last " +
            $"{options.Lookback.TotalDays:0.##} days; {stop ?? "the rest was not read"}, so refresh again to read the rest. Missing funding is not zero.");
        if (low == high && completed == 0)
            // No coverage existed and nothing could be kept: an empty, incomplete attempt over the lookback. It keeps the
            // last window, so the next Refresh starts from half of it, not the lookback.
            return new(new([], floor, to, false, notice, FundingFacts.Unknown)
                { WindowFromUtc = cursor?.LastAttemptFromUtc, WindowToUtc = cursor?.LastAttemptToUtc }, retryAt);
        return new(new(payments.Values.OrderBy(p => p.OccurredAtUtc).ThenBy(p => p.SourceId, StringComparer.Ordinal).ToList(),
            low, high, true, notice, last?.PnlBasis ?? FundingFacts.Unknown) { WindowFromUtc = widest?.From, WindowToUtc = widest?.To }, retryAt);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
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
