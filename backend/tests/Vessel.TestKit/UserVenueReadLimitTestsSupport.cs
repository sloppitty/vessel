using Vessel.Application.MarketData;

namespace Vessel.Tests;

public partial class UserVenueReadLimitTestsSupport
{

    internal sealed class Candles : ICandleReader
    {
        public string VenueId => "hyperliquid";
        public Exception? Error { get; set; }
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs, CancellationToken ct) =>
            Error is { } error ? Task.FromException<IReadOnlyList<VenueCandle>>(error) : Task.FromResult<IReadOnlyList<VenueCandle>>([]);
    }
}
