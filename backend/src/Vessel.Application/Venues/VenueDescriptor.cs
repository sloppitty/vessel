using Vessel.Domain.Accounts;

namespace Vessel.Application.Venues;

/// <summary>How an account at a venue is identified. Adapters validate and normalize it.</summary>
public static class VenueSources
{
    public const string None = "none";
    public const string EvmAddress = "evm-address";
    public const string AccountIndex = "account-index";
}

/// <summary>How a venue rounds prices: Hyperliquid's five significant figures, or each instrument's tick.</summary>
public static class PriceRules
{
    public const string SignificantFigures = "significant-figures";
    public const string TickSize = "tick-size";

    /// <summary>
    /// The smallest price difference the venue distinguishes on an instrument, for matching: its tick, otherwise one unit
    /// of its last allowed decimal (Phase 2, item 14b). Null when it states neither, so the fifth significant figure decides.
    /// </summary>
    public static decimal? MinimumStep(VenueInstrument instrument) =>
        instrument.PriceStep is > 0 ? instrument.PriceStep : DecimalStep(instrument.MaxPriceDecimals);

    /// <summary>One unit of the last allowed decimal (0.00001 for five decimals); null without a cap.</summary>
    public static decimal? DecimalStep(int? decimals) =>
        decimals is >= 0 and <= 28 ? new decimal(1, 0, 0, false, (byte)decimals.Value) : null;
}

/// <summary>
/// Venue logo keys. The browser maps each to a bundled same-origin asset (frontend <c>venue-logos.ts</c>, kept in step
/// like portfolio icons) and falls back to a generic icon. Registration rejects any other key.
/// </summary>
public static class VenueLogos
{
    public const string Hyperliquid = "hyperliquid";
    public const string Lighter = "lighter";
    public const string LighterRh = "lighter-rh";
    public const string Risex = "risex";
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All = [Hyperliquid, Lighter, LighterRh, Risex, Manual];

    public static bool IsValid(string? logo) => logo is null || All.Contains(logo, StringComparer.Ordinal);
}

/// <summary>What Vessel can do at a venue. Use cases and the browser check these instead of venue names.</summary>
/// <param name="CurrentOrderBook">
/// The order reader returns the venue's current open order book: open rows observed with the read and a meaningful
/// <see cref="VenueOrderReadResult.OpenOrdersComplete"/>. False means order history only, so a stop is never seen
/// resting and its absence proves nothing: protection coverage is unsupported ("current book not read"), the
/// book-dependent checks (missing, short and cancelled stops, excess, orphaned exits and entries, level drift) do not
/// run, and the history-only read alone does not degrade a check. Defaults to false, so a venue opts in.
/// </param>
/// <param name="FillHistory">
/// The reader serves executions by time window (<see cref="IVenueFillHistoryReader"/>), so the owner can backfill a
/// history gap (Phase 2, item 6b). Defaults to false.
/// </param>
public sealed record VenueCapabilities(bool Sync, bool Instruments, bool Orders, bool Candles, bool MarketContext, bool Stream, bool StablecoinWallet,
    bool AccountDiscovery = false, bool ReadOnlyCredential = false, bool ManualExecutions = false, bool CurrentOrderBook = false,
    bool PaperExecution = false, bool FillHistory = false)
{
    public static readonly VenueCapabilities None = new(false, false, false, false, false, false, false);
}

/// <summary>
/// One venue as Vessel sees it. <paramref name="TradeUrlTemplate"/> uses <c>{instrument}</c> for a canonical key
/// or <c>{venueContractId}</c> for the venue's own identifier.
/// <paramref name="Intervals"/> are the candle intervals the venue serves natively; others are refused, not approximated.
/// The notices replace the generic candle and market-context notices for this venue.
/// <paramref name="Logo"/> is an optional <see cref="VenueLogos"/> key.
/// Adapters are looked up by <paramref name="Id"/> through <see cref="IVenueRegistry"/>.
/// </summary>
public sealed record VenueDescriptor(
    string Id, string Name, string Status, string Source, VenueCapabilities Capabilities,
    string? QuoteAsset = null, string? TradeUrlTemplate = null, IReadOnlyList<string>? Intervals = null,
    string PriceRule = PriceRules.SignificantFigures, string? CandleNotice = null, string? MarketContextNotice = null,
    string? CredentialSetupUrl = null, string? Logo = null)
{
    public IReadOnlyList<string> CandleIntervals => Intervals ?? [];

    public const string ManualId = ManualVenue.Id;

    /// <summary>Manual accounts hold owner-entered values: no venue adapter, no source identity.</summary>
    public static readonly VenueDescriptor Manual = new(ManualId, "Manual", "manual", VenueSources.None,
        VenueCapabilities.None with { ManualExecutions = true }, Logo: VenueLogos.Manual);
}
