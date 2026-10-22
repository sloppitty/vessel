using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

public sealed partial class HyperliquidPerpetualReader : IVenueFillHistoryReader
{
    /// <summary>userFillsByTime returns at most 2,000 fills a response.</summary>
    public int PageLimit => MaxFills;
    /// <summary>Only an account's latest 10,000 fills are served, whatever their age.</summary>
    public int Reach => 10_000;

    /// <summary>
    /// Phase 2, item 6b: <c>userFillsByTime</c> over an inclusive, millisecond-aligned past window, parsed exactly like
    /// <c>userFills</c>. Every raw row must carry a valid time inside the window: a page whose times are unknown proves
    /// nothing, so it fails rather than being paged past.
    /// </summary>
    public Task<VenueFillPage> ReadFillPageAsync(Account account, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (account.VenueId != Id || !account.IsEnabled)
            throw new VenueReadException("An enabled Hyperliquid account is required.");
        var source = EvmAddress.Require(account.SourceId);
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (fromUtc < DateTimeOffset.UnixEpoch || fromUtc > toUtc || toUtc > timeProvider.GetUtcNow() ||
            fromUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 || toUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new VenueReadException("A fill history page needs an inclusive, millisecond-aligned past window.");
        var start = fromUtc.ToUnixTimeMilliseconds();
        var end = toUtc.ToUnixTimeMilliseconds();
        return ReadBoundedAsync(async token =>
        {
            var contracts = (await CatalogueAsync(token, refresh: true)).All
                .Select(i => i.VenueContractId ?? i.ContractId).ToHashSet(StringComparer.Ordinal);
            using var document = await ReadJsonAsync(
                new { type = "userFillsByTime", user = source, startTime = start, endTime = end, aggregateByTime = false }, token);
            var fills = ReadFills(document.RootElement, contracts, end, out var omitted);
            DateTimeOffset? oldest = null, newest = null;
            var count = 0;
            foreach (var row in Array(document.RootElement).EnumerateArray())
            {
                var at = Timestamp(Property(row, "time"), end);
                var milliseconds = at.ToUnixTimeMilliseconds();
                if (milliseconds < start || milliseconds > end) throw new VenueReadException(InvalidResponse);
                if (oldest is null || at < oldest) oldest = at;
                if (newest is null || at > newest) newest = at;
                count++;
            }
            return new VenueFillPage(fills, count, oldest, newest, omitted);
        }, ct);
    }
}
