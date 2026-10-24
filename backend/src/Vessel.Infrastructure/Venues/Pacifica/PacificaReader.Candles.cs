using System.Globalization;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;

namespace Vessel.Infrastructure.Venues.Pacifica;

public sealed partial class PacificaReader
{
    public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs,
        CancellationToken cancellationToken) => http.ReadBoundedAsync<IReadOnlyList<VenueCandle>>(async ct =>
    {
        if (!Intervals.TryGetValue(interval, out var step) || fromMs < 0 || toMs < fromMs) throw Json.Invalid();
        var catalogue = await CatalogueAsync(ct);
        var market = catalogue.Native(contractId) ?? catalogue.Instruments.FirstOrDefault(i => i.ContractId == contractId)
            ?? throw new VenueReadException("Pacifica instrument is unavailable.");
        return await ReadNativeCandlesAsync(market, interval, fromMs, toMs, ct);
    }, cancellationToken);

    public Task<IReadOnlyList<VenueCandle>> ReadNativeCandlesAsync(VenueInstrument market, string interval, long fromMs, long toMs,
        CancellationToken cancellationToken) => http.ReadBoundedAsync<IReadOnlyList<VenueCandle>>(async ct =>
    {
        if (!Intervals.TryGetValue(interval, out var step) || fromMs < 0 || toMs < fromMs ||
            toMs > time.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds() ||
            market.VenueContractId is not { } native || !SymbolPattern().IsMatch(native)) throw Json.Invalid();
        using var doc = await Get($"/api/v1/kline?symbol={Uri.EscapeDataString(native)}&interval={interval}" +
            $"&start_time={fromMs.ToString(CultureInfo.InvariantCulture)}&end_time={toMs.ToString(CultureInfo.InvariantCulture)}&limit=4000", ct);
        var candles = new SortedDictionary<long, VenueCandle>();
        foreach (var row in Rows(Data(doc), 4000).EnumerateArray())
        {
            if (Text(row, "s", 32) != native || Text(row, "i", 8) != interval) throw Json.Invalid();
            var openAt = At(row, "t").ToUnixTimeMilliseconds();
            var closeAt = Json.Timestamp(Json.Property(row, "T"), time.GetUtcNow().AddMilliseconds(step).ToUnixTimeMilliseconds()).ToUnixTimeMilliseconds();
            if (openAt > toMs || closeAt != openAt + step) throw Json.Invalid();
            string Price(string key) { var text = Text(row, key); if (Json.ParseDecimal(text) < 0) throw Json.Invalid(); return text; }
            var open = Price("o"); var high = Price("h"); var low = Price("l"); var close = Price("c");
            var highValue = Json.ParseDecimal(high); var lowValue = Json.ParseDecimal(low);
            if (highValue < lowValue || Json.ParseDecimal(open) < lowValue || Json.ParseDecimal(open) > highValue ||
                Json.ParseDecimal(close) < lowValue || Json.ParseDecimal(close) > highValue) throw Json.Invalid();
            var trades = Json.Integer(Json.Property(row, "n"));
            if (trades < 0) throw Json.Invalid();
            // An unaligned start can include the candle spanning that start. The shared window filters by open time.
            if (openAt < fromMs) continue;
            var candle = new VenueCandle(openAt, closeAt - 1, open, high, low, close, Price("v"), trades);
            if (candles.TryGetValue(openAt, out var old) && old != candle) throw Json.Invalid();
            candles[openAt] = candle;
        }
        return candles.Values.ToList();
    }, cancellationToken);
}
