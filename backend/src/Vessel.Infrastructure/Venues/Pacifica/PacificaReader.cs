using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Pacifica;

/// <summary>Public GETs only; no wallet SDK, agent key, signing, submission or browser-to-venue requests.</summary>
public sealed partial class PacificaReader(HttpClient client, TimeProvider time, PacificaCatalogueCache catalogueCache)
    : IPerpetualVenueReader, IVenueOrderReader, ICandleReader
{
    public const string Id = "pacifica";
    public string VenueId => Id;
    private const int PageSize = 100, MaxPages = 10, MaxRows = 10_000;
    private static readonly StrictJson Json = new("Pacifica returned an invalid or unsupported response.");
    private readonly BoundedJsonHttp http = new(client, time, Json, "Pacifica is unavailable. Try again later.", "Pacifica read timed out.");
    private static readonly IReadOnlyDictionary<string, long> Intervals = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["1m"] = 60_000, ["3m"] = 180_000, ["5m"] = 300_000, ["15m"] = 900_000, ["30m"] = 1_800_000,
        ["1h"] = 3_600_000, ["2h"] = 7_200_000, ["4h"] = 14_400_000, ["8h"] = 28_800_000,
        ["12h"] = 43_200_000, ["1d"] = 86_400_000, ["1w"] = 604_800_000
    };
    public static readonly VenueDescriptor Descriptor = new(Id, "Pacifica", "read-only", VenueSources.SolanaAddress,
        new(Sync: true, Instruments: true, Orders: true, Candles: true, MarketContext: false, Stream: false, StablecoinWallet: false,
            CurrentOrderBook: true),
        QuoteAsset: "USDC", TradeUrlTemplate: "https://app.pacifica.fi/trade/{venueContractId}", Intervals: [.. Intervals.Keys],
        PriceRule: PriceRules.TickSize,
        CandleNotice: "Pacifica trade candles. Manual refresh only; candles are observations, never fills.", Logo: VenueLogos.Pacifica);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex SymbolPattern();

    internal sealed record Catalogue(IReadOnlyList<VenueInstrument> Instruments, IReadOnlySet<string> Spot)
    {
        public VenueInstrument? Native(string id) => Instruments.FirstOrDefault(m => m.VenueContractId == id);
    }

    private async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var document = await http.SendAsync(request, ct);
        try
        {
            if (!Json.Boolean(Json.Property(document.RootElement, "success"))) throw Json.Invalid();
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static JsonElement Data(JsonDocument doc) => Json.Property(doc.RootElement, "data");
    private static JsonElement Rows(JsonElement rows, int max = MaxRows) =>
        Json.Array(rows).GetArrayLength() <= max ? rows : throw Json.Invalid();
    private static string Text(JsonElement row, string key, int max = 128) => Json.Text(Json.Property(row, key), max);
    private static decimal Number(JsonElement row, string key) => Json.Number(Json.Property(row, key));
    private static decimal Nonnegative(JsonElement row, string key) => Json.Nonnegative(Json.Property(row, key));
    private DateTimeOffset At(JsonElement row, string key) => Json.Timestamp(Json.Property(row, key), time.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds());
    private static string Address(string source) => AccountSource.IsSolanaAddress(source) ? source :
        throw new VenueReadException("Pacifica requires a case-sensitive base58 public address decoding to 32 bytes.");

    // Integer identifiers stay exact native strings, including IDs beyond JavaScript's safe-integer range.
    private static string Identity(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : Json.Text(value);
        return text.Length is > 0 and <= 128 && text.All(char.IsAsciiDigit) &&
            (text == "0" || text[0] != '0') ? text : throw Json.Invalid();
    }

    private Task<Catalogue> CatalogueAsync(CancellationToken ct, bool refresh = false) => catalogueCache.Get(
        token => http.ReadBoundedAsync(async bounded =>
        {
            using var document = await Get("/api/v1/info", bounded);
            return ReadCatalogue(Data(document));
        }, token), refresh, ct);

    internal static Catalogue ReadCatalogue(JsonElement rows)
    {
        var markets = new List<VenueInstrument>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var spot = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(rows).EnumerateArray())
        {
            var symbol = Text(row, "symbol", 32);
            if (!SymbolPattern().IsMatch(symbol) || !ids.Add(symbol)) throw Json.Invalid();
            var kind = Text(row, "instrument_type", 32);
            if (kind == "spot") { spot.Add(symbol); continue; }
            if (kind != "perpetual") throw Json.Invalid();
            // Only the explicitly verified thousand-unit aliases, never an arbitrary lowercase-k rule.
            if (symbol.StartsWith('k') && symbol is not ("kBONK" or "kPEPE")) continue;
            var canonical = symbol switch { "kBONK" => "1000BONK", "kPEPE" => "1000PEPE", _ => symbol };
            if (!keys.Add(canonical)) throw Json.Invalid();
            var tick = Json.Positive(Json.Property(row, "tick_size"));
            var lot = Json.Positive(Json.Property(row, "lot_size"));
            var decimals = 0;
            // The shared instrument contract represents decimal precision, not arbitrary lot increments.
            while (lot < 1 && decimals < 28) { lot *= 10; decimals++; }
            if (lot != 1) throw Json.Invalid();
            var leverage = Json.Integer(Json.Property(row, "max_leverage"));
            if (leverage is < 1 or > 1000) throw Json.Invalid();
            markets.Add(new(canonical, decimals, leverage, "USDC", tick, VenueContractId: symbol));
        }
        return new(markets, spot);
    }

    public async Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken cancellationToken) =>
        (await CatalogueAsync(cancellationToken)).Instruments;

    /// <summary>Bounded opaque-cursor reads, not a claim of lifetime completeness or sorted history.</summary>
    private async Task<(List<JsonElement> Rows, bool More)> Pages(string path, string source, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var doc = await Get($"{path}?account={Uri.EscapeDataString(source)}&limit={PageSize}" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), ct);
            result.AddRange(Rows(Data(doc), PageSize).EnumerateArray().Select(r => r.Clone()));
            var more = Json.Boolean(Json.Property(doc.RootElement, "has_more"));
            if (!more) return (result, false);
            var next = Text(doc.RootElement, "next_cursor", 512);
            if (Data(doc).GetArrayLength() == 0 || !cursors.Add(next)) throw Json.Invalid();
            cursor = next;
        }
        return (result, true);
    }

    private static List<JsonElement> Unique(IEnumerable<JsonElement> rows, string key)
    {
        var unique = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = Identity(Json.Property(row, key));
            if (unique.TryGetValue(id, out var previous))
            {
                // Corrections within a read are not last-row-wins.
                if (!JsonElement.DeepEquals(previous, row)) throw Json.Invalid();
            }
            else unique.Add(id, row);
        }
        return unique.Values.ToList();
    }

    public Task<PerpetualVenueReadResult> ReadAsync(string sourceId, CancellationToken cancellationToken)
    {
        var source = Address(sourceId);
        return http.ReadBoundedAsync(async ct =>
        {
            var catalogue = await CatalogueAsync(ct, refresh: true);
            using var settingsDoc = await Get($"/api/v1/account/settings?account={source}", ct);
            var settings = Data(settingsDoc);
            if (settings.ValueKind != JsonValueKind.Object) throw Json.Invalid();
            // pending_interest is borrower interest, not a lender indicator. Pacifica enables implicit lending by
            // default; only an explicit public opt-out establishes the supported no-lending account scope.
            if (!settings.TryGetProperty("auto_lend_disabled", out var disabled) || disabled.ValueKind != JsonValueKind.True)
                throw new VenueReadException("Pacifica automatic lending must be explicitly disabled at the venue before refreshing. No refresh was imported.")
                    { IsDeterministicSourceFailure = true };
            using var accountDoc = await Get($"/api/v1/account?account={source}", ct);
            var account = Data(accountDoc);
            // account_equity now includes raw spot market value. Never silently add non-stable valuation or lending.
            if (Number(account, "spot_market_value") != 0 || Number(account, "spot_collateral") != 0 ||
                Number(account, "pending_interest") != 0 ||
                Rows(Json.Property(account, "spot_balances")).EnumerateArray().Any(r => Number(r, "amount") != 0))
                throw new VenueReadException("Pacifica accounts with spot collateral, spot balances or accrued loan interest are not supported. No refresh was imported.");
            using var positions = await Get($"/api/v1/positions?account={source}", ct);
            var positionRows = Rows(Data(positions));
            var count = Json.Integer(Json.Property(account, "positions_count"));
            if (count < 0) throw Json.Invalid();
            // Completeness does not survive into the stored snapshot. Publishing [] with a transient incomplete
            // flag would look flat to Activity/sizing/Review, so unsupported or inconsistent positions abort the read.
            if (positionRows.GetArrayLength() != 0 || count != 0)
                throw new VenueReadException("Pacifica open positions cannot be projected safely. Refresh only when the account has no open positions. No refresh was imported.")
                    { IsDeterministicSourceFailure = true };
            At(account, "updated_at");
            var snapshot = new VenueSnapshot(time.GetUtcNow(), "pacifica-perpetual-account-equity",
                Number(account, "account_equity"), Json.OptionalAmount(account, "available_to_withdraw"),
                Nonnegative(account, "total_margin_used"), [],
                AvailableMarginUsd: Json.OptionalAmount(account, "available_to_spend"));
            var (rows, more) = await Pages("/api/v1/trades/history", source, ct);
            var coverage = new RawFillReadCoverage();
            using var raw = JsonDocument.Parse(JsonSerializer.Serialize(rows));
            coverage.Observe(raw.RootElement, row => At(row, "created_at"));
            var fills = new List<VenueFill>();
            var omitted = 0;
            foreach (var row in Unique(rows, "history_id"))
            {
                var symbol = Text(row, "symbol", 32);
                if (catalogue.Spot.Contains(symbol)) continue;
                if (catalogue.Native(symbol) is not { } market) { omitted++; continue; }
                var direction = Text(row, "side", 32);
                var side = direction switch
                {
                    "open_long" or "close_short" => ExecutionFacts.Buy,
                    "open_short" or "close_long" => ExecutionFacts.Sell,
                    _ => null
                };
                var cause = Text(row, "cause", 64);
                var eventType = Text(row, "event_type", 32);
                // No guessed effects for a future flip/correction/event encoding. Liquidations/settlements are not
                // ordinary order executions; they cannot be safely attributed by the shared order-link contract.
                if (side is null || cause != "normal" || eventType is not ("fulfill_maker" or "fulfill_taker") ||
                    Json.OptionalAmount(row, "fee") is not { } fee || Json.OptionalAmount(row, "pnl") is not { } pnl ||
                    row.TryGetProperty("spot_fee", out var spotFee) && spotFee.ValueKind != JsonValueKind.Null)
                { omitted++; continue; }
                var order = Identity(Json.Property(row, "order_id"));
                if (order == "0" || fee < 0) { omitted++; continue; } // rebate semantics are not documented
                fills.Add(new(Identity(Json.Property(row, "history_id")), market.ContractId, side, direction,
                    Json.Positive(Json.Property(row, "price")), Json.Positive(Json.Property(row, "amount")), fee, "USDC",
                    pnl, At(row, "created_at"), order, "", row.GetRawText(),
                    direction.StartsWith("open_", StringComparison.Ordinal) ? ExecutionFacts.Open : ExecutionFacts.Close,
                    PnlBasis: ExecutionFacts.PnlNetOfFee, VenueContractId: symbol));
            }
            var notice = "Pacifica: at most 1,000 recent raw executions per refresh; not complete lifetime history. " +
                "Reported trade PnL is net of trading fees; funding inclusion is unknown. " +
                $"Omitted executions: {omitted} unsupported or unverified records. " +
                "Refresh requires no open positions and explicit automatic-lending disablement. Leverage is not inferred. " +
                "Unverified contract denominations are omitted. Account equity excludes unsupported spot/lending accounts. " +
                "Funding settlements are not imported: payout sign is unverified.";
            return new PerpetualVenueReadResult(snapshot, catalogue.Instruments, fills, notice,
                PositionsComplete: true, FillHistoryComplete: false, FillHistoryTruncated: more,
                OldestRawFillAtUtc: coverage.OldestAtUtc, OmittedExecutions: omitted);
        }, cancellationToken);
    }
}

/// <summary>Public metadata only. Never caches authorization, account responses or executions.</summary>
public sealed class PacificaCatalogueCache(TimeProvider time)
{
    private readonly BoundedMarketDataCache<PacificaReader.Catalogue> entries = new(time, 1, 1, _ => 1);
    internal Task<PacificaReader.Catalogue> Get(Func<CancellationToken, Task<PacificaReader.Catalogue>> load,
        bool refresh, CancellationToken ct) => entries.GetAsync("markets", load, _ => TimeSpan.FromMinutes(1), ct, refresh);
}
