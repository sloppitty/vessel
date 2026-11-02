using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.Venues;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

// Builder (HIP-3) perpetual DEXes, read through one allPerpMetas call. Their contracts are third-party listings, so one
// malformed market is skipped, never allowed to fail the catalogue. Only USDC-margined DEXes are in scope: sizing, fees and
// results assume USDC, and a DEX on another collateral stays out until they do not.
public sealed partial class HyperliquidPerpetualReader
{
    private const int UsdcTokenIndex = 0;
    private const int MaxKeywords = 8;

    [GeneratedRegex("^(?<dex>[a-z0-9]{1,16}):(?<coin>[A-Za-z0-9_-]{1,24})$")]
    private static partial Regex BuilderContract();

    private async Task<Catalogue> ReadBuildersAsync(CancellationToken token)
    {
        using var metas = await ReadJsonAsync(new { type = "allPerpMetas" }, token);
        using var dexes = await ReadJsonAsync(new { type = "perpDexs" }, token);
        var annotations = await ReadAnnotationsAsync(token);
        return ReadBuilders(metas.RootElement, dexes.RootElement, annotations);
    }

    // Labels improve the selector and nothing depends on them, so a failure leaves builder markets uncategorized.
    private async Task<Dictionary<string, MarketLabel>> ReadAnnotationsAsync(CancellationToken token)
    {
        try
        {
            using var document = await ReadJsonAsync(new { type = "perpConciseAnnotations" }, token);
            return ReadAnnotations(document.RootElement);
        }
        catch (Exception error) when (WalletFailure(error) && !token.IsCancellationRequested) { return []; }
    }

    internal sealed record MarketLabel(string? Category, string? DisplayName, IReadOnlyList<string>? Keywords);

    internal static Dictionary<string, MarketLabel> ReadAnnotations(JsonElement root)
    {
        var labels = new Dictionary<string, MarketLabel>(StringComparer.Ordinal);
        foreach (var row in Array(root).EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2 || row[0].ValueKind != JsonValueKind.String ||
                row[1].ValueKind != JsonValueKind.Object) continue;
            var keywords = row[1].TryGetProperty("keywords", out var words) && words.ValueKind == JsonValueKind.Array
                ? words.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.String).Select(w => w.GetString()!)
                    .Where(w => w.Length is > 0 and <= 32).Take(MaxKeywords).ToList() : null;
            labels[row[0].GetString()!] = new(MarketCategories.Normalize(Label(row[1], "category")), Label(row[1], "displayName"), keywords);
        }
        return labels;

        static string? Label(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 32 } text ? text : null;
    }

    internal static Catalogue ReadBuilders(JsonElement metas, JsonElement dexes, IReadOnlyDictionary<string, MarketLabel> labels)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dex in Array(dexes).EnumerateArray())
            if (dex.ValueKind == JsonValueKind.Object) names[Text(Property(dex, "name"))] = Text(Property(dex, "fullName"));

        List<VenueInstrument> all = [], selectable = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<string>();
        foreach (var meta in Array(metas).EnumerateArray())
        {
            if (!meta.TryGetProperty("collateralToken", out var collateral) || collateral.ValueKind != JsonValueKind.Number ||
                !collateral.TryGetInt32(out var token) || token != UsdcTokenIndex) continue;
            string? listed = null;
            foreach (var item in Array(Property(meta, "universe")).EnumerateArray())
            {
                if (ReadBuilderMarket(item, names, labels) is not { } market || !seen.Add(market.Instrument.ContractId)) continue;
                all.Add(market.Instrument);
                if (market.Delisted) continue;
                selectable.Add(market.Instrument);
                listed = market.Instrument.Source;
            }
            if (listed is not null) active.Add(listed);
        }
        return new(all, selectable, active);
    }

    private static (VenueInstrument Instrument, bool Delisted)? ReadBuilderMarket(JsonElement item,
        Dictionary<string, string> names, IReadOnlyDictionary<string, MarketLabel> labels)
    {
        try
        {
            var name = Text(Property(item, "name"));
            var match = BuilderContract().Match(name);
            var decimals = Integer(Property(item, "szDecimals"));
            var leverage = Integer(Property(item, "maxLeverage"));
            if (!match.Success || decimals is < 0 or > 28 || leverage <= 0) return null;
            var delisted = item.TryGetProperty("isDelisted", out var flag) && flag.ValueKind == JsonValueKind.True;
            var dex = match.Groups["dex"].Value;
            var label = labels.GetValueOrDefault(name);
            return (new VenueInstrument(name, decimals, leverage, PrimaryQuoteAsset,
                Category: label?.Category ?? MarketCategories.Other, MaintenanceMarginFraction: 1m / (2 * leverage),
                MaxPriceDecimals: Math.Max(0, MaxPerpetualPriceDecimals - decimals), Source: dex,
                SourceName: names.GetValueOrDefault(dex, dex), DisplayName: label?.DisplayName ?? match.Groups["coin"].Value,
                Keywords: label?.Keywords, IsolatedOnly: IsIsolatedOnly(item)), delisted);
        }
        catch (VenueReadException) { return null; }
    }
}
