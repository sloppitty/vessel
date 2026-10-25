using Vessel.Domain.Workspace;
using Vessel.Domain.Accounts;

namespace Vessel.Application.Venues;

// External adapters supply read-only, normalized perpetual facts. These types
// carry no journal thesis, play assignment, persistence or venue SDK dependency.
public interface IPerpetualVenueReader
{
    string VenueId { get; }
    Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken cancellationToken);
    Task<PerpetualVenueReadResult> ReadAsync(string sourceId, CancellationToken cancellationToken);
    Task<PerpetualVenueReadResult> ReadAsync(Account account, CancellationToken cancellationToken) =>
        ReadAsync(account.SourceId ?? throw new VenueReadException("Account source is unavailable."), cancellationToken);
}

/// <summary>
/// A selectable perpetual. <paramref name="ContractId"/> is Vessel's instrument key (the canonical asset, e.g. BTC);
/// <paramref name="VenueContractId"/> is the venue's own identifier when it differs (e.g. a numeric market ID).
/// Prices are quoted and margined in <paramref name="QuoteAsset"/>, e.g. BTC/USDC. <paramref name="PriceStep"/> is the
/// tick when the venue has one; <paramref name="Category"/> labels non-crypto markets (stocks, commodities, indices).
/// <paramref name="MaintenanceMarginFraction"/> is the maintenance margin as a fraction of notional (0.0125 at 40×
/// on Hyperliquid) when the venue states it; liquidation estimates otherwise assume one.
/// <paramref name="MaxPriceDecimals"/> caps a significant-figure venue's price decimals (Phase 2, item 14b, F-11):
/// Hyperliquid perpetuals take at most five significant figures, integers whatever their length, and at most
/// <c>6 − szDecimals</c> decimals. Null when the venue states no cap.
/// </summary>
public sealed record VenueInstrument(string ContractId, int QuantityDecimals, int MaxLeverage, string QuoteAsset,
    decimal? PriceStep = null, string? Category = null, string? VenueContractId = null, decimal? MaintenanceMarginFraction = null,
    int? MaxPriceDecimals = null);

/// <summary>
/// One open position as the venue reports it. <paramref name="LiquidationPrice"/> and <paramref name="MarginMode"/>
/// (<c>cross</c> or <c>isolated</c>) are the venue's own figures when it states them (Phase 2, item 8a); null is not
/// reported, never estimated. A reported price of zero means no liquidation price and is null too.
/// </summary>
public sealed record VenuePosition(
    string ContractId,
    decimal SignedQuantity,
    decimal EntryPrice,
    decimal UnrealizedPnlUsd,
    decimal MarginUsedUsd,
    int? Leverage,
    string? VenueContractId = null,
    decimal? LiquidationPrice = null,
    string? MarginMode = null);

public static class MarginModes
{
    public const string Cross = "cross", Isolated = "isolated";
}

/// <param name="AccountMode">
/// The venue's account mode as this read observed it, independently of whether its wallet could be read (Phase 2, item
/// 14b, Sol round 1): it decides how the ledgers combine. Null when the venue has none.
/// </param>
public sealed record VenueSnapshot(
    DateTimeOffset ObservedAtUtc,
    string ValueScope,
    decimal AccountValueUsd,
    decimal? WithdrawableUsd,
    decimal MarginUsedUsd,
    IReadOnlyList<VenuePosition> Positions,
    VenueStablecoinWallet? StablecoinWallet = null,
    decimal? AvailableMarginUsd = null,
    string? AccountMode = null);

public sealed record VenueStablecoinBalance(
    string Symbol, int TokenIndex, string TokenId,
    decimal Total, decimal Held, decimal Available);

public sealed record VenueStablecoinWallet(
    DateTimeOffset ObservedAtUtc, string AccountMode, string Scope,
    IReadOnlyList<VenueStablecoinBalance> Balances);

/// <summary>
/// One execution. <paramref name="Side"/> and <paramref name="PositionEffect"/> use <see cref="ExecutionFacts"/>;
/// <paramref name="Direction"/> keeps the venue's wording. <paramref name="PnlBasis"/> says whether
/// <paramref name="ClosedPnlUsd"/> already has the fee taken off.
/// </summary>
public sealed record VenueFill(
    string SourceFillId,
    string ContractId,
    string Side,
    string Direction,
    decimal Price,
    decimal Quantity,
    decimal Fee,
    string FeeToken,
    decimal ClosedPnlUsd,
    DateTimeOffset OccurredAtUtc,
    string OrderId,
    string TransactionHash,
    string RawJson,
    string PositionEffect = ExecutionFacts.Unknown,
    string FeeBasis = ExecutionFacts.FeeReported,
    string PnlBasis = ExecutionFacts.PnlGross,
    // Set by venues whose own identifier differs from Vessel's instrument key.
    string? VenueContractId = null,
    // The position leverage the venue reports on the execution (RISEx), whole numbers only; null when not reported.
    int? Leverage = null);

/// <summary>Rejects adapter output that does not use Vessel's execution vocabulary, before anything is stored.</summary>
public static class VenueFactChecks
{
    public static bool Valid(VenueFill fill) => ExecutionFacts.IsSide(fill.Side) && ExecutionFacts.IsEffect(fill.PositionEffect) &&
        ExecutionFacts.IsFeeBasis(fill.FeeBasis) && ExecutionFacts.IsPnlBasis(fill.PnlBasis) &&
        (fill.FeeBasis != ExecutionFacts.FeeStandardAccountFree || fill.Fee == 0) && fill.Leverage is null or (>= 1 and <= 1000);

    public static bool Valid(VenueOrder order) => ExecutionFacts.IsSide(order.Side);
}

/// <summary>A current snapshot and imported executions within the adapter's supported perpetual scope.</summary>
/// <param name="PositionsComplete">
/// True only when the read establishes every current position within the snapshot's supported venue scope.
/// It does not cover other DEXs, wallets or unsupported products. False means unknown or incomplete;
/// an absent position must not be interpreted as flat. Independent responses are not an atomic snapshot.
/// </param>
/// <param name="FillHistoryComplete">
/// True only for verified complete execution history within the supported scope, without truncation or omitted facts.
/// Bounded recent fills do not establish lifetime completeness or a gap-free time window, even below their cap.
/// False means unknown or incomplete; an absent fill must not be interpreted as no execution.
/// </param>
/// <param name="FillHistoryTruncated">
/// True when a raw response/page cap was reached or another page remains unread, before any fact filtering.
/// False when the bounded raw read finished without a known limit; this is not lifetime completeness.
/// Null means unavailable or partial, so whether a limit was reached is unknown.
/// </param>
/// <param name="OldestRawFillAtUtc">
/// Oldest validated execution timestamp across all raw rows read, before contract, fee or PnL filtering.
/// Null for no rows, an unavailable/partial read, or any raw row without a valid timestamp.
/// This is an observed boundary, not a covered-window start or proof of gap-free history.
/// </param>
/// <param name="OmittedExecutions">
/// In-scope executions the adapter read but deliberately left out because a required fact was missing or could not be
/// verified (for example unreported realized PnL or an unverified fee). These are known missing facts, separate from
/// lifetime completeness and raw truncation; zero means none were knowingly omitted, not that history is complete.
/// </param>
/// <summary>
/// Phase 2, item 7: an in-scope execution the adapter read but left out, for its durable marker. <paramref name="Kind"/>
/// is one of <see cref="ExecutionOmission.Kinds"/>. Time and market are null when the venue gives none.
/// </summary>
public sealed record VenueOmission(string SourceId, string Kind, string Reason, string? ContractId, string? VenueContractId,
    string? OrderId, DateTimeOffset? OccurredAtUtc);

/// <summary>Phase 2, item 7: a venue-forced execution, never a fill. Unreported or unverifiable money is null.</summary>
public sealed record VenueForcedEvent(string SourceId, string Kind, string ContractId, string? VenueContractId, string Side,
    decimal Quantity, decimal? Price, DateTimeOffset OccurredAtUtc, string? OrderId, decimal? Fee, decimal? RealizedPnl);

public sealed record PerpetualVenueReadResult(
    VenueSnapshot Snapshot,
    IReadOnlyList<VenueInstrument> Instruments,
    IReadOnlyList<VenueFill> Fills,
    string HistoryNotice,
    bool PositionsComplete = false,
    bool FillHistoryComplete = false,
    bool? FillHistoryTruncated = null,
    DateTimeOffset? OldestRawFillAtUtc = null,
    int OmittedExecutions = 0)
{
    public VenueFundingReadResult? Funding { get; init; }
    /// <summary>Phase 2, item 7: every row counted in <see cref="OmittedExecutions"/>, for durable markers.</summary>
    public IReadOnlyList<VenueOmission> Omissions { get; init; } = [];
    /// <summary>Phase 2, item 7: venue-forced executions, each also an omission.</summary>
    public IReadOnlyList<VenueForcedEvent> ForcedEvents { get; init; } = [];
}

public class VenueReadException(string message) : Exception(message)
{
    /// <summary>Proven malformed source data or an explicit request rejection, never an inferred outage category.</summary>
    public bool IsDeterministicSourceFailure { get; init; }
}

/// <summary>A safe rate-limit category; the deadline comes from Retry-After, never the response body.</summary>
public sealed class VenueRateLimitException(DateTimeOffset retryAtUtc) : VenueReadException("The venue rate limit requires a later retry.")
{
    /// <summary>The longest venue deadline Vessel honours; a longer or far-future Retry-After is clamped to it.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);
    public DateTimeOffset RetryAtUtc { get; } = retryAtUtc;

    public static DateTimeOffset Clamp(DateTimeOffset retryAtUtc, DateTimeOffset now) =>
        retryAtUtc < now ? now : retryAtUtc - now > MaxDelay ? now + MaxDelay : retryAtUtc;
}

/// <summary>The owner's credential input is unusable (format, expiry or wrong wallet); not a venue outage.</summary>
public sealed class VenueCredentialRejectedException() : Exception("The read-only token is not valid for this wallet.");
