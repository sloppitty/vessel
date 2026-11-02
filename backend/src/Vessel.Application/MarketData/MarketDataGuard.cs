using Vessel.Application.Common;
using Vessel.Application.Venues;
using System.Text.RegularExpressions;
using Vessel.Application.Workspace;

namespace Vessel.Application.MarketData;

// Shared, ordered request checks for the public market data use cases.
// Order matters for clients: account (404), disabled/manual (409), instrument and interval (400).
internal static partial class MarketDataGuard
{
    public static readonly IReadOnlyDictionary<string, long> IntervalMs = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["1m"] = 60_000L, ["3m"] = 3 * 60_000L, ["5m"] = 5 * 60_000L, ["15m"] = 15 * 60_000L,
        ["30m"] = 30 * 60_000L, ["1h"] = 3_600_000L, ["2h"] = 2 * 3_600_000L, ["4h"] = 4 * 3_600_000L,
        ["8h"] = 8 * 3_600_000L, ["12h"] = 12 * 3_600_000L, ["1d"] = 86_400_000L, ["3d"] = 3 * 86_400_000L,
        ["1w"] = 7 * 86_400_000L, ["1M"] = 31 * 86_400_000L
    };

    // A native key may carry one source prefix, as in xyz:GOLD.
    [GeneratedRegex(@"^(?=.{1,32}$)[A-Za-z0-9_-]+(?::[A-Za-z0-9_-]+)?$")]
    private static partial Regex InstrumentPattern();

    public static async Task<MarketDataAccount> AccountAsync(IWorkspaceStore store, Guid accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var account = await store.MarketDataAccountAsync(accountId, ct) ?? throw new ProblemException(404, "Account not found.");
        if (!account.IsEnabled)
            throw new ProblemException(409, "Enable the account before reading market data.");
        if (account.VenueId == VenueDescriptor.ManualId)
            throw new ProblemException(409, "No market data provider for manual accounts.");
        return account;
    }

    public static string Instrument(string? instrument) =>
        instrument is not null && InstrumentPattern().IsMatch(instrument)
            ? instrument
            : throw new ProblemException(400, "Instrument must be 1 to 32 letters, digits, hyphens or underscores, with at most one colon.");

    /// <summary>An interval the venue serves natively. Others are refused rather than approximated.</summary>
    public static (string Interval, long Step) Interval(string? interval, VenueDescriptor? venue)
    {
        var supported = IntervalMs.Keys.Where(known => venue?.CandleIntervals.Contains(known) == true).ToList();
        return interval is not null && supported.Contains(interval) && IntervalMs.TryGetValue(interval, out var step)
            ? (interval, step)
            : throw new ProblemException(400, supported.Count == 0
                ? "This venue has no candle intervals."
                : $"Interval must be one of {string.Join(", ", supported)}.");
    }
}
