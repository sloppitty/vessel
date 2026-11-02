using System.Text.Json;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

public sealed partial class HyperliquidPerpetualReader : IMarketContextReader
{
    private const int MaxUniverse = 10_000;

    public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken cancellationToken) =>
        ReadBoundedAsync<IReadOnlyList<VenueMarketContext>>(async token =>
        {
            using var response = await ReadJsonAsync(new { type = "metaAndAssetCtxs" }, token);
            var contexts = ReadMarketContexts(response.RootElement);
            foreach (var dex in builderMarkets ? (await CatalogueAsync(token)).Dexes : [])
            {
                try
                {
                    using var builder = await ReadJsonAsync(new { type = "metaAndAssetCtxs", dex }, token);
                    contexts.AddRange(ReadMarketContexts(builder.RootElement, dex));
                }
                catch (Exception error) when (WalletFailure(error) && !token.IsCancellationRequested)
                {
                    // A market with no context shows none; one builder DEX never hides the primary marks.
                }
            }
            return contexts;
        }, cancellationToken);

    private static List<VenueMarketContext> ReadMarketContexts(JsonElement root, string? dex = null)
    {
        if (Array(root).GetArrayLength() != 2)
            throw new VenueReadException(InvalidResponse);
        var universe = Array(Property(root[0], "universe"));
        var contexts = Array(root[1]);
        if (universe.GetArrayLength() > MaxUniverse || universe.GetArrayLength() != contexts.GetArrayLength())
            throw new VenueReadException(InvalidResponse);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<VenueMarketContext>(universe.GetArrayLength());
        for (var i = 0; i < universe.GetArrayLength(); i++)
        {
            var name = Text(Property(universe[i], "name"));
            if (dex is null ? !IsPrimaryContract(name) : !name.StartsWith(dex + ":", StringComparison.Ordinal))
                continue;
            var canonical = HyperliquidInstruments.Canonical(name);
            if (!names.Add(canonical) || HyperliquidInstruments.Native(canonical) != name)
                throw new VenueReadException(InvalidResponse);
            result.Add(ReadMarketContext(name, contexts[i], numbers: false));
        }
        return result;
    }

    // Shared by REST and streaming reads; midPx and premium may be null.
    private static VenueMarketContext ReadMarketContext(string name, JsonElement ctx, bool numbers)
    {
        var funding = Signed(Property(ctx, "funding"), numbers);
        var premium = Property(ctx, "premium");
        var mid = Property(ctx, "midPx");
        return new(HyperliquidInstruments.Canonical(name), Price(Property(ctx, "markPx"), numbers).Text, Price(Property(ctx, "oraclePx"), numbers).Text,
            mid.ValueKind == JsonValueKind.Null ? null : Price(mid, numbers).Text,
            Price(Property(ctx, "prevDayPx"), numbers).Text, Price(Property(ctx, "dayNtlVlm"), numbers).Text,
            Price(Property(ctx, "openInterest"), numbers).Text, funding,
            premium.ValueKind == JsonValueKind.Null ? null : Signed(premium, numbers));
    }

    // Keeps the exact text after proving it is a finite decimal that may be negative.
    private static string Signed(JsonElement element, bool numbers = false)
    {
        var text = DecimalText(element, numbers);
        ParseDecimal(text);
        return text;
    }
}
