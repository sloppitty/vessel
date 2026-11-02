using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

/// <summary>
/// Bounded, read-only primary perpetual DEX adapter. Composition supplies an
/// HttpClient with BaseAddress https://api.hyperliquid.xyz/ (and no signing credentials).
/// </summary>
public sealed partial class HyperliquidPerpetualReader(HttpClient httpClient, TimeProvider timeProvider,
    HyperliquidCatalogueCache? catalogueCache = null)
    : IPerpetualVenueReader, ICandleReader, IVenueFundingReader
{
    // Official schemas and bounds, checked October 1, 2026:
    // https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint
    // https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint/perpetuals
    // https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api/info-endpoint/spot
    // https://github.com/hyperliquid-dex/hyperliquid-python-sdk/blob/master/hyperliquid/info.py
    // https://github.com/hyperliquid-dex/hyperliquid-python-sdk/blob/master/hyperliquid/utils/types.py
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxFills = 2000;
    private const string InvalidResponse = "Hyperliquid returned an invalid or unsupported response.";
    private const string HistoryNotice =
        "Incomplete history: only primary perpetual DEX contracts recognized by current metadata are included. " +
        "userFills returns at most the latest 2,000 fills across markets before filtering; older fills, spot, " +
        "other DEXs and unrecognized contracts are excluded. This is not complete lifetime history. " +
        "Account value is primary perpetual margin only, not total spot/unified account equity.";
    // Curated identities checked October 1, 2026. Indices come from metadata;
    // names and isCanonical alone are not token identity or stablecoin approval.
    private static readonly Dictionary<string, string> Stablecoins = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["0x6d1e7cde53ba9467b783cb7c530ce054"] = "USDC",
        ["0x2e6d84f2d7ca82e6581e03523e4389f7"] = "USDE",
        ["0x25faedc3f054130dbb4e4203aca63567"] = "USDT0",
        ["0x54e00a5988577cb0b0c9ab0cb6ef7f4b"] = "USDH"
    };

    public const string Id = "hyperliquid";

    public static readonly VenueDescriptor Descriptor = new(Id, "Hyperliquid", "read-only", VenueSources.EvmAddress,
        new VenueCapabilities(Sync: true, Instruments: true, Orders: true, Candles: true, MarketContext: true, Stream: true, StablecoinWallet: true,
            CurrentOrderBook: true, PaperExecution: true),
        QuoteAsset: PrimaryQuoteAsset, TradeUrlTemplate: "https://app.hyperliquid.xyz/trade/{venueContractId}",
        Intervals: ["1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "8h", "12h", "1d", "3d", "1w", "1M"],
        PriceRule: PriceRules.SignificantFigures,
        CandleNotice: "Hyperliquid exposes only the latest 5,000 candles per interval. Prices are trade candles, not fills.",
        MarketContextNotice: "Venue market context for the primary perpetual DEX. Funding is the current hourly rate; open interest is in base units. Not a fill or valuation.",
        Logo: VenueLogos.Hyperliquid);

    public string VenueId => Id;

    // Strict parsing and bounded reads are shared with other venues; Hyperliquid keeps its own messages.
    private static readonly StrictJson Json = new(InvalidResponse);
    private const string Unavailable = "Hyperliquid is unavailable. Try again later.";
    // One per reader: the reader owns the injected HttpClient for its own (scoped) lifetime.
    private readonly BoundedJsonHttp Http = new(httpClient, timeProvider, Json, Unavailable, "Hyperliquid read timed out.", MaxResponseBytes);

    private Task<T> ReadBoundedAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        Http.ReadBoundedAsync(read, cancellationToken);

    private Task<JsonDocument> ReadJsonAsync(object payload, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/info") { Content = JsonContent.Create(payload) };
        return SendAndDispose(request, cancellationToken);
    }

    private async Task<JsonDocument> SendAndDispose(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request) return await Http.SendAsync(request, cancellationToken);
    }

    private static BigInteger DecimalUnits(decimal value) => StrictJson.DecimalUnits(value);
    private static JsonElement Property(JsonElement element, string name) => Json.Property(element, name);
    private static JsonElement Array(JsonElement element) => Json.Array(element);
    private static string Text(JsonElement element, int maxLength = 128) => Json.Text(element, maxLength);
    private static int Integer(JsonElement element) => Json.Integer(element);
    private static DateTimeOffset Timestamp(JsonElement element, long latestTimestamp) => Json.Timestamp(element, latestTimestamp);
    private static decimal Positive(JsonElement element) => Json.Positive(element);
    private static decimal Nonnegative(JsonElement element) => Json.Nonnegative(element);
    private static decimal Number(JsonElement element) => Json.Number(element);
    private static decimal ParseDecimal(string text) => Json.ParseDecimal(text);
    private static void RejectDuplicateProperties(JsonElement element) => Json.RejectDuplicateProperties(element);

    private readonly HyperliquidCatalogueCache catalogues = catalogueCache ?? new(timeProvider);
    internal sealed record Catalogue(IReadOnlyList<VenueInstrument> All, IReadOnlyList<VenueInstrument> Selectable);

    // Imports pass refresh so a market listed within the cache lifetime is not filtered out of their facts.
    private Task<Catalogue> CatalogueAsync(CancellationToken ct, bool refresh = false) => catalogues.GetAsync(token =>
        ReadBoundedAsync(async bounded =>
        {
            using var meta = await ReadJsonAsync(new { type = "meta", dex = "" }, bounded);
            return new Catalogue(ReadInstruments(meta.RootElement), ReadInstruments(meta.RootElement, selectableOnly: true));
        }, token), ct, refresh);

    public async Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken cancellationToken) =>
        (await CatalogueAsync(cancellationToken)).Selectable;

    public async Task<PerpetualVenueReadResult> ReadAsync(string sourceId, CancellationToken cancellationToken)
    {
        var publicAddress = EvmAddress.Require(sourceId);

        var latestTimestamp = Math.Min(253402300799999L, timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + 300000);

        return await ReadBoundedAsync(async token =>
        {
            var instruments = (await CatalogueAsync(token, refresh: true)).All;
            var contracts = instruments.Select(instrument => instrument.VenueContractId ?? instrument.ContractId)
                .ToHashSet(StringComparer.Ordinal);

            using var state = await ReadJsonAsync(
                new { type = "clearinghouseState", user = publicAddress, dex = "" }, token);
            var snapshot = ReadSnapshot(state.RootElement, contracts, latestTimestamp, out var positionsComplete);

            using var fills = await ReadJsonAsync(
                new { type = "userFills", user = publicAddress, aggregateByTime = false }, token);
            var executions = ReadFills(fills.RootElement, contracts, latestTimestamp, out var omitted);
            var fillCoverage = new RawFillReadCoverage();
            fillCoverage.Observe(fills.RootElement, row => Timestamp(Property(row, "time"), latestTimestamp));

            using var abstraction = await ReadJsonAsync(new { type = "userAbstraction", user = publicAddress }, token);
            var accountMode = ReadAccountMode(abstraction.RootElement);
            VenueStablecoinWallet? wallet;
            var notice = HistoryNotice;
            try
            {
                using var spotMeta = await ReadJsonAsync(new { type = "spotMeta" }, token);
                var stablecoins = ReadStablecoinMetadata(spotMeta.RootElement);
                using var spotState = await ReadJsonAsync(new { type = "spotClearinghouseState", user = publicAddress }, token);
                wallet = ReadStablecoinWallet(spotState.RootElement, stablecoins, accountMode, timeProvider.GetUtcNow());
            }
            catch (Exception error) when (WalletFailure(error) && SeparateLedgers(accountMode) && !token.IsCancellationRequested)
            {
                // Phase 2, item 14b (F-14): with separate perps and spot ledgers the wallet is not the perps balance, so its
                // failure degrades the wallet alone. The last observed wallet stays, with its own time, and the account's
                // balance is unknown until a wallet is read with the perps again. A rate limit or the read's deadline
                // still stops the whole read so the venue's backoff holds.
                wallet = null;
                notice += " " + WalletUnavailableNotice;
            }
            return new PerpetualVenueReadResult(snapshot with { StablecoinWallet = wallet, AccountMode = accountMode }, instruments,
                executions, notice,
                PositionsComplete: positionsComplete, FillHistoryTruncated: fillCoverage.Count == MaxFills,
                OldestRawFillAtUtc: fillCoverage.OldestAtUtc, OmittedExecutions: omitted);
        }, cancellationToken);
    }

    /// <summary>Primary perpetual DEX contracts are quoted and margined in USDC.</summary>
    private const string PrimaryQuoteAsset = "USDC";
    /// <summary>Hyperliquid perpetual prices take at most this many decimals minus the contract's <c>szDecimals</c>.</summary>
    private const int MaxPerpetualPriceDecimals = 6;

    private static List<VenueInstrument> ReadInstruments(JsonElement meta, bool selectableOnly = false)
    {
        var result = new List<VenueInstrument>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Array(Property(meta, "universe")).EnumerateArray())
        {
            var name = Text(Property(item, "name"));
            if (!IsPrimaryContract(name))
                continue;
            var decimals = Integer(Property(item, "szDecimals"));
            var leverage = Integer(Property(item, "maxLeverage"));
            var canonical = HyperliquidInstruments.Canonical(name);
            if (decimals is < 0 or > 28 || leverage <= 0 || !names.Add(canonical) ||
                HyperliquidInstruments.Native(canonical) != name)
                throw new VenueReadException(InvalidResponse);
            if (item.TryGetProperty("isDelisted", out var delisted) &&
                delisted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new VenueReadException(InvalidResponse);
            // Delisted contracts still identify historical positions and fills during refresh.
            if (!selectableOnly || delisted.ValueKind != JsonValueKind.True)
                // Hyperliquid's maintenance margin is half the initial margin at the contract's maximum leverage. Perpetual
                // prices take at most 6 − szDecimals decimals besides five significant figures (Phase 2, item 14b, F-11).
                result.Add(new(canonical, decimals, leverage, PrimaryQuoteAsset,
                    VenueContractId: canonical == name ? null : name, MaintenanceMarginFraction: 1m / (2 * leverage),
                    MaxPriceDecimals: Math.Max(0, MaxPerpetualPriceDecimals - decimals)));
        }
        return result;
    }

    private static VenueSnapshot ReadSnapshot(JsonElement state, HashSet<string> contracts, long latestTimestamp,
        out bool positionsComplete)
    {
        var summary = Property(state, "marginSummary");
        positionsComplete = true; // clearinghouseState has the full current primary-DEX position list, not history.
        var positions = new List<VenuePosition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Array(Property(state, "assetPositions")).EnumerateArray())
        {
            var position = Property(item, "position");
            var name = Text(Property(position, "coin"));
            if (!contracts.Contains(name))
            {
                if (IsPrimaryContract(name)) positionsComplete = false;
                continue;
            }
            var quantity = Number(Property(position, "szi"));
            if (quantity == 0)
                continue;
            if (Text(Property(item, "type")) != "oneWay" || !names.Add(name))
                throw new VenueReadException(InvalidResponse);

            int? leverage = null;
            string? mode = null;
            if (position.TryGetProperty("leverage", out var leverageElement) && leverageElement.ValueKind != JsonValueKind.Null)
            {
                leverage = Integer(Property(leverageElement, "value"));
                if (leverage <= 0)
                    throw new VenueReadException(InvalidResponse);
                // The venue's own mode; another value is not one Vessel knows, so it stays unreported.
                if (leverageElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                    mode = type.GetString() switch { "cross" => MarginModes.Cross, "isolated" => MarginModes.Isolated, _ => null };
            }
            // liquidationPx is null when the venue reports none (for example a fully collateralised position).
            decimal? liquidation = position.TryGetProperty("liquidationPx", out var liquidationElement) &&
                liquidationElement.ValueKind != JsonValueKind.Null ? Nonnegative(liquidationElement) : null;
            positions.Add(new(HyperliquidInstruments.Canonical(name), quantity, Positive(Property(position, "entryPx")),
                Number(Property(position, "unrealizedPnl")), Nonnegative(Property(position, "marginUsed")), leverage,
                VenueContractId: HyperliquidInstruments.Canonical(name) == name ? null : name,
                LiquidationPrice: liquidation is > 0 ? liquidation : null, MarginMode: mode));
        }
        return new(
            Timestamp(Property(state, "time"), latestTimestamp),
            "primary-perpetual-dex",
            Number(Property(summary, "accountValue")),
            Nonnegative(Property(state, "withdrawable")),
            Nonnegative(Property(summary, "totalMarginUsed")),
            positions);
    }

    /// <param name="omitted">
    /// Primary-perpetual fills on a contract the catalogue does not list. Spot (<c>@…</c>, <c>A/B</c>) and HIP-3
    /// (<c>dex:…</c>) fills are proven out of scope; an unrecognized primary contract is an in-scope fact Vessel left out.
    /// </param>
    internal static List<VenueFill> ReadFills(JsonElement root, HashSet<string> contracts, long latestTimestamp, out int omitted)
    {
        omitted = 0;
        var array = Array(root);
        if (array.GetArrayLength() > MaxFills)
            throw new VenueReadException(InvalidResponse);
        var fills = new List<VenueFill>();
        foreach (var fill in array.EnumerateArray())
        {
            var name = Text(Property(fill, "coin"));
            if (!contracts.Contains(name))
            {
                if (IsPrimaryContract(name)) omitted++;
                continue;
            }
            var side = Text(Property(fill, "side"));
            if (side is not ("A" or "B"))
                throw new VenueReadException(InvalidResponse);
            var direction = Text(Property(fill, "dir"));
            fills.Add(new(
                Identity(Property(fill, "tid")), HyperliquidInstruments.Canonical(name), SideOf(side),
                direction, Positive(Property(fill, "px")), Positive(Property(fill, "sz")),
                Number(Property(fill, "fee")), Text(Property(fill, "feeToken"), FeeTokenLength),
                Number(Property(fill, "closedPnl")), Timestamp(Property(fill, "time"), latestTimestamp),
                Identity(Property(fill, "oid")), Text(Property(fill, "hash")), fill.GetRawText(),
                // closedPnl excludes the fee, which is reported separately.
                EffectOf(direction), ExecutionFacts.FeeReported, ExecutionFacts.PnlGross,
                VenueContractId: HyperliquidInstruments.Canonical(name) == name ? null : name));
        }
        // Preserve venue side/direction as facts; they do not identify a journal play.
        return fills.OrderByDescending(fill => fill.OccurredAtUtc).ToList();
    }

    /// <summary>Hyperliquid's B (bid) buys and A (ask) sells.</summary>
    public static string SideOf(string side) => side == "B" ? ExecutionFacts.Buy : ExecutionFacts.Sell;

    /// <summary>
    /// From Hyperliquid's <c>dir</c>: "Open Long", "Close Short", "Long &gt; Short" (a flip). Anything else, such as
    /// a liquidation or settlement wording, stays unknown rather than guessed.
    /// </summary>
    public static string EffectOf(string direction) => direction switch
    {
        "Open Long" or "Open Short" => ExecutionFacts.Open,
        "Close Long" or "Close Short" => ExecutionFacts.Close,
        "Long > Short" or "Short > Long" => ExecutionFacts.Flip,
        _ => ExecutionFacts.Unknown
    };

    internal const string WalletUnavailableNotice =
        "The stablecoin wallet could not be refreshed; its last observed balances and their time are kept, and the " +
        "account balance is unknown until it is read again.";

    /// <summary>
    /// A wallet read that failed on its own: an invalid or failed venue response, a dropped connection or a malformed
    /// body (Sol round 1 on 14b). Never a rate limit, cancellation or the read's deadline.
    /// </summary>
    private static bool WalletFailure(Exception error) => error switch
    {
        VenueRateLimitException => false,
        VenueReadException or HttpRequestException or IOException or JsonException => true,
        _ => false,
    };

    /// <summary>
    /// Modes whose perps and spot ledgers are separate. Unified and portfolio-margin accounts hold every balance in the
    /// wallet, so they still need it to refresh.
    /// </summary>
    private static bool SeparateLedgers(string accountMode) => accountMode is not ("unifiedAccount" or "portfolioMargin");

    internal static bool IsPrimaryContract(string name) =>
        !name.StartsWith('@') && !name.Contains('/') && !name.Contains(':');

    private static string ReadAccountMode(JsonElement root)
    {
        var mode = Text(root);
        if (mode is not ("default" or "disabled" or "dexAbstraction" or "unifiedAccount" or "portfolioMargin"))
            throw new VenueReadException(InvalidResponse);
        return mode;
    }

    private static Dictionary<int, (string Symbol, string TokenId)> ReadStablecoinMetadata(JsonElement root)
    {
        var stablecoins = new Dictionary<int, (string Symbol, string TokenId)>();
        var indices = new HashSet<int>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Array(Property(root, "tokens")).EnumerateArray())
        {
            var index = Integer(Property(token, "index"));
            var name = Text(Property(token, "name"));
            var id = Text(Property(token, "tokenId"));
            if (index < 0 || id.Length != 34 || !id.StartsWith("0x", StringComparison.Ordinal) ||
                !id.Skip(2).All(char.IsAsciiHexDigit))
                throw new VenueReadException(InvalidResponse);
            id = id.ToLowerInvariant();
            if (!indices.Add(index) || !identities.Add(id))
                throw new VenueReadException(InvalidResponse);
            if (!Stablecoins.TryGetValue(id, out var symbol))
                continue;
            if (name != symbol)
                throw new VenueReadException(InvalidResponse);
            stablecoins.Add(index, (symbol, id));
        }
        // An empty/unrecognized catalog cannot establish supported wallet zeros.
        if (stablecoins.Count == 0)
            throw new VenueReadException(InvalidResponse);
        return stablecoins;
    }

    private static VenueStablecoinWallet ReadStablecoinWallet(
        JsonElement state, Dictionary<int, (string Symbol, string TokenId)> metadata,
        string accountMode, DateTimeOffset observedAtUtc)
    {
        var observed = new Dictionary<int, VenueStablecoinBalance>();
        var indices = new HashSet<int>();
        foreach (var balance in Array(Property(state, "balances")).EnumerateArray())
        {
            var index = Integer(Property(balance, "token"));
            var name = Text(Property(balance, "coin"));
            if (index < 0 || !indices.Add(index))
                throw new VenueReadException(InvalidResponse);
            if (!metadata.TryGetValue(index, out var token))
                continue;
            if (name != token.Symbol)
                throw new VenueReadException(InvalidResponse);
            var total = Nonnegative(Property(balance, "total"));
            var held = Nonnegative(Property(balance, "hold"));
            if (held > total)
                throw new VenueReadException(InvalidResponse);
            var available = total - held;
            // Decimal subtraction can round even when both inputs are exact.
            // Check the difference at a common scale without binary floats.
            if (DecimalUnits(available) != DecimalUnits(total) - DecimalUnits(held))
                throw new VenueReadException(InvalidResponse);
            observed.Add(index, new(name, index, token.TokenId, total, held, available));
        }
        // Only a successfully observed sparse balance array establishes zero
        // for absent metadata-approved tokens. Missing/failed responses throw.
        var balances = metadata.OrderBy(token => token.Key).Select(token =>
            observed.TryGetValue(token.Key, out var balance)
                ? balance
                : new VenueStablecoinBalance(token.Value.Symbol, token.Key, token.Value.TokenId, 0m, 0m, 0m)).ToList();
        // The spot endpoint has no timestamp: this is local read observation time.
        // Never add this ledger to perp margin equity; unified modes overlap.
        // Available means total minus spot order holds, not collateral or a withdrawal guarantee.
        return new(observedAtUtc, accountMode, "hypercore-spot-stablecoins", balances);
    }

    // Matches the persisted fee-token column, so an oversized value fails as a venue read.
    private const int FeeTokenLength = 64;

    private static string Identity(JsonElement element)
    {
        var value = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => Text(element),
            _ => throw new VenueReadException(InvalidResponse)
        };
        if (value.Length > 128 || !value.All(char.IsAsciiDigit))
            throw new VenueReadException(InvalidResponse);
        // No double/decimal conversion: identities can exceed JavaScript's safe integer range.
        var normalized = value.TrimStart('0');
        return normalized.Length == 0 ? "0" : normalized;
    }
}

/// <summary>Full validated primary metadata, including retired identities; shared across adapter instances.</summary>
public sealed class HyperliquidCatalogueCache(TimeProvider time)
{
    private readonly BoundedMarketDataCache<HyperliquidPerpetualReader.Catalogue> entries = new(time, 1, 1, _ => 1);
    internal Task<HyperliquidPerpetualReader.Catalogue> GetAsync(
        Func<CancellationToken, Task<HyperliquidPerpetualReader.Catalogue>> load, CancellationToken ct, bool refresh = false) =>
        entries.GetAsync("primary", load, _ => TimeSpan.FromSeconds(60), ct, refresh);
}
