using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Extended;

/// <summary>
/// Public market data only. The official SDK exposes API-key-only cancellation/leverage writes without Stark signing;
/// no verifiable read-only scope was found. Do not add account reads or credentials without resolving that policy.
/// See docs/architecture/extended-integration.md (primary sources checked October 23, 2026).
/// </summary>
public sealed partial class ExtendedReader(HttpClient httpClient, TimeProvider timeProvider, ExtendedCatalogueCache cache)
    : IPerpetualVenueReader, ICandleReader
{
    public const string Id = "extended";
    private const int MaxCandles = 500;
    private static readonly StrictJson Json = new("Extended returned an invalid or unsupported public response.");
    private readonly BoundedJsonHttp http = new(httpClient, timeProvider, Json,
        "Extended public data is unavailable. Try again later.", "Extended public data read timed out.");

    // The official SDK's CandleInterval, not an assumed list of common intervals.
    private static readonly IReadOnlyDictionary<string, (string Native, long Ms)> Intervals =
        new Dictionary<string, (string, long)>(StringComparer.Ordinal)
        {
            ["1m"] = ("PT1M", 60_000), ["5m"] = ("PT5M", 300_000), ["15m"] = ("PT15M", 900_000),
            ["30m"] = ("PT30M", 1_800_000), ["1h"] = ("PT1H", 3_600_000), ["2h"] = ("PT2H", 7_200_000),
            ["4h"] = ("PT4H", 14_400_000), ["1d"] = ("P1D", 86_400_000)
        };

    public static readonly VenueDescriptor Descriptor = new(Id, "Extended", "candidate", VenueSources.None,
        VenueCapabilities.None with { Instruments = true, Candles = true }, QuoteAsset: "USD",
        Intervals: [.. Intervals.Keys], PriceRule: PriceRules.TickSize,
        CandleNotice: "Extended public trade candles only, at most 500 per read; refresh manually. Prices are not fills. " +
            "Account integration is blocked: a verifiably read-only credential is not established.");

    public string VenueId => Id;

    public Task<PerpetualVenueReadResult> ReadAsync(string sourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new VenueReadException("Extended account integration is unavailable: verifiably read-only credentials are not established.");
    }

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalAsset();

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        // Public requests never read the vault, authenticate, sign or call /user/*.
        return await http.SendAsync(request, ct);
    }

    private static JsonElement Data(JsonDocument document)
    {
        var root = document.RootElement;
        if (Json.Text(Json.Property(root, "status")) != "OK" ||
            root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind != JsonValueKind.Null)
            throw Json.Invalid(); // These public endpoints are unpaginated; never silently accept a partial catalogue.
        return Json.Array(Json.Property(root, "data"));
    }

    public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken cancellationToken) =>
        cache.GetAsync(ct => http.ReadBoundedAsync<IReadOnlyList<VenueInstrument>>(async token =>
        {
            using var document = await GetAsync("/api/v1/info/markets", token);
            var data = Data(document);
            if (data.GetArrayLength() > 2000) throw Json.Invalid();
            var instruments = new List<VenueInstrument>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.EnumerateArray())
            {
                if (Json.Text(Json.Property(row, "type")) != "PERPETUAL" ||
                    Json.Text(Json.Property(row, "collateralAssetName")) != "USD" ||
                    !Json.Boolean(Json.Property(row, "active")) ||
                    Json.Text(Json.Property(row, "status")) != "ACTIVE") continue;
                var asset = Json.Text(Json.Property(row, "assetName"), 64);
                var native = Json.Text(Json.Property(row, "name"), 64);
                // Do not guess multiplier aliases from a leading k, nor collapse different trading schedules.
                // Unsupported identities stay out until their canonical mapping is verified.
                if (!CanonicalAsset().IsMatch(asset) || native != asset + "-USD") continue;
                if (!keys.Add(asset)) throw Json.Invalid();
                var config = Json.Property(row, "tradingConfig");
                var decimals = Json.Integer(Json.Property(row, "assetPrecision"));
                var leverage = Json.Positive(Json.Property(config, "maxLeverage"));
                if (decimals is < 0 or > 28 || leverage != decimal.Truncate(leverage) || leverage > 1000)
                    throw Json.Invalid();
                var category = row.TryGetProperty("category", out var reported) ? Json.Text(reported, 32) : null;
                instruments.Add(new(asset, decimals, (int)leverage, "USD",
                    Json.Positive(Json.Property(config, "minPriceChange")),
                    category == "Crypto" ? null : category, native));
            }
            return instruments;
        }, ct), cancellationToken);

    public async Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs,
        long toMs, CancellationToken cancellationToken)
    {
        ValidateWindow(interval, fromMs, toMs);
        var market = (await ReadInstrumentsAsync(cancellationToken)).FirstOrDefault(m => m.ContractId == contractId)
            ?? throw new VenueReadException("An active supported Extended perpetual is required.");
        return await ReadNativeCandlesAsync(market, interval, fromMs, toMs, cancellationToken);
    }

    public Task<IReadOnlyList<VenueCandle>> ReadNativeCandlesAsync(VenueInstrument market, string interval, long fromMs,
        long toMs, CancellationToken cancellationToken)
    {
        ValidateWindow(interval, fromMs, toMs);
        if (!CanonicalAsset().IsMatch(market.ContractId) || market.VenueContractId != market.ContractId + "-USD" ||
            market.QuoteAsset != "USD")
            throw new VenueReadException("A supported native Extended perpetual is required.");
        var (nativeInterval, step) = Intervals[interval];
        var limit = Math.Min(MaxCandles, (toMs - fromMs) / step + 1);
        return http.ReadBoundedAsync<IReadOnlyList<VenueCandle>>(async ct =>
        {
            using var document = await GetAsync(FormattableString.Invariant(
                $"/api/v1/info/candles/{market.VenueContractId}/trades?interval={nativeInterval}&limit={limit}&endTime={toMs}"), ct);
            var data = Data(document);
            if (data.GetArrayLength() > limit) throw Json.Invalid();
            var candles = new List<VenueCandle>();
            long previous = long.MaxValue;
            foreach (var row in data.EnumerateArray())
            {
                var at = Json.Timestamp(Json.Property(row, "T"), toMs).ToUnixTimeMilliseconds();
                if (at >= previous || at % step != 0) throw Json.Invalid();
                previous = at;
                (string Text, decimal Value) Amount(string name, bool zero = false)
                {
                    var text = Json.Text(Json.Property(row, name));
                    var value = Json.ParseDecimal(text);
                    if (value < 0 || !zero && value == 0) throw Json.Invalid();
                    return (text, value);
                }
                var open = Amount("o"); var high = Amount("h"); var low = Amount("l");
                var close = Amount("c"); var volume = Amount("v", zero: true);
                if (low.Value > high.Value || open.Value < low.Value || open.Value > high.Value ||
                    close.Value < low.Value || close.Value > high.Value) throw Json.Invalid();
                if (at >= fromMs)
                    candles.Add(new(at, checked(at + step - 1), open.Text, high.Text, low.Text, close.Text, volume.Text, null));
            }
            candles.Reverse();
            return candles;
        }, cancellationToken);
    }

    private void ValidateWindow(string interval, long fromMs, long toMs)
    {
        if (!Intervals.ContainsKey(interval) || fromMs < 0 || toMs <= fromMs ||
            toMs > timeProvider.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds())
            throw new VenueReadException("A native Extended interval and bounded candle window are required.");
    }
}

/// <summary>Public metadata only, with the same bounded single-flight cache as the other venue catalogues.</summary>
public sealed class ExtendedCatalogueCache(TimeProvider time)
{
    private readonly BoundedMarketDataCache<IReadOnlyList<VenueInstrument>> entries = new(time, 1, 1, _ => 1);
    internal Task<IReadOnlyList<VenueInstrument>> GetAsync(Func<CancellationToken, Task<IReadOnlyList<VenueInstrument>>> load,
        CancellationToken ct) => entries.GetAsync("markets", load, _ => TimeSpan.FromMinutes(1), ct);
}
