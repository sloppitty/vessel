using System.Globalization;
using System.Text.Json;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

public sealed partial class HyperliquidPerpetualReader
{
    // https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint (candleSnapshot):
    // only the most recent 5,000 candles are available; o/h/l/c/v are decimal strings.
    private const int MaxCandles = 5000;

    public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(
        string contractId, string interval, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        if (!IsPrimaryContract(contractId) || fromMs < 0 || toMs < fromMs)
            throw new VenueReadException("A primary perpetual contract and valid window are required.") { IsDeterministicSourceFailure = true };
        var venueContractId = HyperliquidInstruments.Native(contractId);
        return ReadBoundedAsync<IReadOnlyList<VenueCandle>>(async token =>
        {
            using var response = await ReadJsonAsync(new
            {
                type = "candleSnapshot",
                req = new { coin = venueContractId, interval, startTime = fromMs, endTime = toMs }
            }, token);
            return ReadCandles(response.RootElement, venueContractId, interval);
        }, cancellationToken);
    }

    private static List<VenueCandle> ReadCandles(JsonElement root, string contractId, string interval)
    {
        var array = Array(root);
        if (array.GetArrayLength() > MaxCandles)
            throw new VenueReadException(InvalidResponse) { IsDeterministicSourceFailure = true };
        var candles = new List<VenueCandle>();
        foreach (var item in array.EnumerateArray())
        {
            if (Text(Property(item, "s")) != contractId || Text(Property(item, "i")) != interval)
                throw new VenueReadException(InvalidResponse) { IsDeterministicSourceFailure = true };
            candles.Add(ReadCandle(item, numbers: false));
        }
        return candles;
    }

    // Shared by REST and streaming reads. Streams may send decimals as JSON numbers (numbers: true);
    // their raw token text is kept, never a binary floating-point conversion.
    private static VenueCandle ReadCandle(JsonElement item, bool numbers)
    {
        var open = Price(Property(item, "o"), numbers); var high = Price(Property(item, "h"), numbers);
        var low = Price(Property(item, "l"), numbers); var close = Price(Property(item, "c"), numbers);
        var volume = Price(Property(item, "v"), numbers);
        var trades = Property(item, "n");
        var openTime = Property(item, "t"); var closeTime = Property(item, "T");
        if (trades.ValueKind != JsonValueKind.Number || !trades.TryGetInt32(out var count) || count < 0 ||
            openTime.ValueKind != JsonValueKind.Number || !openTime.TryGetInt64(out var t) || t < 0 ||
            closeTime.ValueKind != JsonValueKind.Number || !closeTime.TryGetInt64(out var closedAt) || closedAt < t ||
            high.Value < Math.Max(Math.Max(open.Value, close.Value), low.Value) ||
            low.Value > Math.Min(open.Value, close.Value))
            throw new VenueReadException(InvalidResponse) { IsDeterministicSourceFailure = true };
        return new(t, closedAt, open.Text, high.Text, low.Text, close.Text, volume.Text, count);
    }

    // Keeps the venue's exact text after proving it is a finite non-negative decimal.
    private static (string Text, decimal Value) Price(JsonElement element, bool numbers = false)
    {
        var text = DecimalText(element, numbers);
        var value = ParseDecimal(text);
        if (value < 0)
            throw new VenueReadException(InvalidResponse) { IsDeterministicSourceFailure = true };
        return (text, value);
    }

    private static string DecimalText(JsonElement element, bool numbers) =>
        numbers && element.ValueKind == JsonValueKind.Number ? element.GetRawText() : Text(element);
}
