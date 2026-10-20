using Vessel.Application.Venues;
using Vessel.Application.Credentials;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Lighter;

/// <summary>
/// One Lighter exchange. Lighter on Robinhood Chain is a separate exchange (own accounts, indexes, market IDs, API
/// keys) speaking the same protocol, so both are configured instances of one module. Hosts come from
/// <c>Vessel:Venues:&lt;HostSection&gt;</c>. Every user-facing message names <see cref="Name"/>.
/// </summary>
public sealed class LighterExchange
{
    // Declared before the instances: static initializers run in order and the constructor reads it.
    internal static readonly IReadOnlyDictionary<string, long> Intervals = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["1m"] = 60_000, ["5m"] = 300_000, ["15m"] = 900_000, ["30m"] = 1_800_000,
        ["1h"] = 3_600_000, ["4h"] = 14_400_000, ["12h"] = 43_200_000, ["1d"] = 86_400_000
    };
    private static readonly string[] Counts = ["zero", "one", "two", "three", "four", "five"];

    public LighterExchange(string venueId, string name, string hostSection, string collateral, string credentialSetupUrl,
        string? tradeUrlTemplate, string logo, int tradePages, string snapshotScope, string? noticeName = null)
    {
        if (tradePages is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(tradePages));
        VenueId = venueId; Name = name; HostSection = hostSection; Collateral = collateral;
        CredentialSetupUrl = credentialSetupUrl; TradeUrlTemplate = tradeUrlTemplate; Logo = logo;
        TradePages = tradePages; SnapshotScope = snapshotScope; NoticeName = noticeName ?? name;
        Json = new($"{name} returned an invalid or unsupported response.");
        Descriptor = new(VenueId, Name, "read-only", VenueSources.AccountIndex,
            new VenueCapabilities(Sync: true, Instruments: true, Orders: true, Candles: true,
                MarketContext: false, Stream: false, StablecoinWallet: false, AccountDiscovery: true, ReadOnlyCredential: true,
                CurrentOrderBook: true),
            QuoteAsset: Collateral, TradeUrlTemplate: TradeUrlTemplate, Intervals: [.. Intervals.Keys], PriceRule: PriceRules.TickSize,
            CandleNotice: CandleNotice, CredentialSetupUrl: CredentialSetupUrl, Logo: Logo);
    }

    public string VenueId { get; }
    public string Name { get; }
    /// <summary>Section under <c>Vessel:Venues</c> holding this exchange's host.</summary>
    public string HostSection { get; }
    /// <summary>Collateral and quote stablecoin, Lighter asset ID 3.</summary>
    public string Collateral { get; }
    public string CredentialSetupUrl { get; }
    public string? TradeUrlTemplate { get; }
    public string Logo { get; }
    /// <summary>Execution-history page budget (100 per page), respecting the exchange's <c>trades</c> weight.</summary>
    public int TradePages { get; }
    public string SnapshotScope { get; }
    /// <summary>
    /// Short name for the notices stored in the 1,000-character HistoryNotice column, where public and order coverage
    /// are combined. Interactive messages use <see cref="Name"/>.
    /// </summary>
    public string NoticeName { get; }
    public VenueDescriptor Descriptor { get; }

    /// <summary>Lighter's asset ID for the exchange's collateral stablecoin (USDC on mainnet, USDG on Robinhood).</summary>
    public const string CollateralAssetId = "3";

    /// <summary>The original Lighter venue. Its ID, host, names and notices must not change.</summary>
    public static readonly LighterExchange Mainnet = new("lighter", "Lighter", "Lighter", "USDC",
        "https://app.lighter.xyz/read-only-tokens", null, VenueLogos.Lighter, 5, "lighter-perps-account");

    /// <summary>
    /// Lighter on Robinhood Chain. Its <c>trades</c> weight is 600 against mainnet's 200, so it reads at most two
    /// pages (1,200 weight, close to mainnet's 1,000). The web app's <c>/read-only-tokens</c> and
    /// <c>/trade/:symbol</c> routes were confirmed in its router on 2026-10-14.
    /// </summary>
    public static readonly LighterExchange Robinhood = new("lighter-rh", "Lighter · Robinhood", "LighterRh", "USDG",
        "https://robinhoodchain.lighter.xyz/read-only-tokens", "https://robinhoodchain.lighter.xyz/trade/{instrument}",
        VenueLogos.LighterRh, 2, "lighter-rh-perps-account", "Lighter");

    public static readonly IReadOnlyList<LighterExchange> All = [Mainnet, Robinhood];

    /// <summary>Named HTTP client; one per exchange so base addresses and handlers never mix.</summary>
    public string ClientName => VenueId;
    /// <summary>The purpose the credential services derive for this venue; tokens for the two exchanges are bound separately.</summary>
    public string CredentialPurpose => VenueCredentialPurpose.ReadToken(VenueId);

    internal StrictJson Json { get; }

    internal int MaxTrades => TradePages * 100;

    private string PageCount => Counts[TradePages];

    // Stored in the account's 1,000-character HistoryNotice column; the coverage table in
    // docs/architecture/lighter-state.md carries the detail. A test keeps every sentence combined below that limit.
    internal string HistoryNotice =>
        $"Incomplete {NoticeName} history: at most the latest {MaxTrades} perpetual executions, no backfill. " +
        "Only fee-free executions with known realized PnL are imported; liquidation, deleverage, settlement and self-trade records are not. " +
        $"{Collateral} perpetual equity only, no spot wallets. Margin is the initial-margin requirement. Reported availability is not guaranteed withdrawal capacity.";
    internal string PageLimitNotice => $"The {PageCount}-page history limit was reached; older executions remain unimported.";
    internal string Unavailable => $"{Name} is unavailable or refused the read. Try again or verify the read-only token.";
    internal string TimedOut => $"{Name} read timed out.";
    internal string TokenRefused => $"{Name} did not accept this read-only token.";
    internal string PublicRefused => $"{Name} refused the public read.";
    internal string IndexRequired => $"A nonnegative {Name} account index within int64 is required.";
    internal string AccountRequired => $"An enabled {Name} account is required.";
    internal string CollateralUnsupported => $"{Name} non-{Collateral} collateral valuation is not supported. No partial snapshot was imported.";
    internal string BinaryUnsupported => $"{Name} binary positions are not supported. No partial snapshot was imported.";
    internal string PositionMarketUnsupported => $"A {Name} position uses an unsupported market.";
    internal string IntervalRequired => $"A native {Name} interval and a valid candle window are required.";
    internal string InactiveInstrument => $"The instrument is not an active {Name} perpetual.";
    internal string MarketRequired => $"A native {Name} market and a valid candle window are required.";
    internal string AddressRequired => $"A valid {Name} L1 address is required.";
    internal string TokenFormat => $"Enter a {Name} read-only token in ro:index:single|all:expiry:hex format.";
    internal string DiscoveryPages => $"{Name} account discovery exceeded five pages. No partial account list was accepted.";
    internal string NamePages => $"{Name} account names exceeded five pages. No partial names were accepted.";
    internal string TokenOtherAccount => $"The read-only token belongs to a different {Name} account.";
    internal string TokenScope => $"The read-only token scope does not match this {Name} account and its master.";
    internal string TokenRequired => $"Add a valid read-only token to this {Name} account to track orders.";
    internal string OrdersNotice =>
        $"{NoticeName} perpetual orders: max 1,000 active, 500 recent inactive, five pages; no backfill. " +
        "Zero-size, flagged and TWAP omitted; position-tied/grouped execution unsupported.";
    internal string CandleNotice =>
        $"{Name} trade candles, at most 500 per manual refresh. Earlier candles and trade counts are unavailable. Prices are observations, never fills.";

}
