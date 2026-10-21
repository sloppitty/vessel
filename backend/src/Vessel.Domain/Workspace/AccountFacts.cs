using Vessel.Domain.Common;

namespace Vessel.Domain.Workspace;

/// <summary>
/// Vessel's own words for execution facts. Venue adapters translate into them, so matching, lifecycle and
/// results never read a venue's encoding. The venue's own wording stays beside them for display.
/// </summary>
public static class ExecutionFacts
{
    public const string Buy = "buy", Sell = "sell";

    /// <summary>What a fill did to the position: opened or increased it, reduced or closed it, or flipped it.</summary>
    public const string Open = "open", Close = "close", Flip = "flip", Unknown = "unknown";

    /// <summary>The fee is the venue's reported amount, or zero because the account tier trades fee-free.</summary>
    public const string FeeReported = "reported", FeeStandardAccountFree = "standard-account-free";

    /// <summary>Closed PnL before fees (the fee is reported separately) or with the fee already taken off.</summary>
    public const string PnlGross = "gross", PnlNetOfFee = "net-of-fee";

    public static bool IsSide(string? value) => value is Buy or Sell;
    public static bool IsEffect(string? value) => value is Open or Close or Flip or Unknown;
    public static bool IsFeeBasis(string? value) => value is FeeReported or FeeStandardAccountFree;
    public static bool IsPnlBasis(string? value) => value is PnlGross or PnlNetOfFee;

    /// <summary>The fill reduced, closed or flipped a position.</summary>
    public static bool Closes(string effect) => effect is Close or Flip;
}

// Only the latest coherent observation is retained. Unknown balances remain null.
public sealed class AccountSnapshot : IOwned
{
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string ValueScope { get; set; } = null!;
    public decimal? AccountValueUsd { get; set; }
    public decimal? WithdrawableUsd { get; set; }
    // Venue-reported available balance/free collateral, not a withdrawal guarantee.
    public decimal? AvailableMarginUsd { get; set; }
    public decimal? MarginUsedUsd { get; set; }
    public List<AccountPosition> Positions { get; set; } = [];
    public DateTimeOffset? StablecoinsObservedAtUtc { get; set; }
    public string? AccountMode { get; set; }
    public string? StablecoinScope { get; set; }
    public List<AccountStablecoin> Stablecoins { get; set; } = [];
}

public sealed class AccountStablecoin : IOwned
{
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public int TokenIndex { get; set; }
    public string TokenId { get; set; } = null!;
    public string Symbol { get; set; } = null!;
    public decimal Total { get; set; }
    public decimal Held { get; set; }
    public decimal Available { get; set; }
}

public sealed class AccountPosition : IOwned
{
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public string ContractId { get; set; } = null!;
    public string? VenueContractId { get; set; }
    public decimal SignedQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal UnrealizedPnlUsd { get; set; }
    public decimal MarginUsedUsd { get; set; }
    public int? Leverage { get; set; }
    /// <summary>The venue's reported liquidation price at this snapshot (Phase 2, item 8a); null when it reports none.</summary>
    public decimal? LiquidationPrice { get; set; }
    /// <summary><c>cross</c> or <c>isolated</c> as the venue reports it; null when it does not.</summary>
    public string? MarginMode { get; set; }
}

public sealed class ImportedFill : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>Vessel's instrument key.</summary>
    public string ContractId { get; set; } = null!;
    /// <summary>The venue's own identifier when it differs from the key, e.g. a numeric market ID.</summary>
    public string? VenueContractId { get; set; }
    public string SourceFillId { get; set; } = null!;
    /// <summary><see cref="ExecutionFacts.Buy"/> or <see cref="ExecutionFacts.Sell"/>.</summary>
    public string Side { get; set; } = null!;
    /// <summary>The venue's own wording, for display.</summary>
    public string Direction { get; set; } = null!;
    public string PositionEffect { get; set; } = ExecutionFacts.Unknown;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal Fee { get; set; }
    public string FeeToken { get; set; } = null!;
    public string FeeBasis { get; set; } = ExecutionFacts.FeeReported;
    public decimal ClosedPnlUsd { get; set; }
    public string PnlBasis { get; set; } = ExecutionFacts.PnlGross;
    public DateTimeOffset OccurredAtUtc { get; set; }
    /// <summary>The venue's order ID. A fill belongs to the order with the same account, contract and external ID.</summary>
    public string ExternalOrderId { get; set; } = null!;
    public string TransactionHash { get; set; } = null!;
    /// <summary>
    /// The position leverage the venue reported on this execution, when it reports one as a whole number (RISEx).
    /// Null when the venue does not report it; never derived from margin or notional.
    /// </summary>
    public int? Leverage { get; set; }
}

/// <summary>
/// A venue-reported position leverage seen by a refresh, kept after the latest snapshot replaces it so a closed
/// position's leverage is not lost. Recorded only when a position appears or its quantity, entry price or leverage
/// changes. An observation, never a correction of fills or snapshots.
/// </summary>
public sealed class LeverageObservation : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public string ContractId { get; set; } = null!;
    public string? VenueContractId { get; set; }
    /// <summary>The snapshot's observation time (when Vessel read the venue), not an execution time.</summary>
    public DateTimeOffset ObservedAtUtc { get; set; }
    public decimal SignedQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public int Leverage { get; set; }
}

/// <summary>
/// A position's venue-reported risk context (Phase 2, item 8a): its liquidation price and margin mode as the venue reported
/// them, kept when the position appears, when its quantity, entry price or margin mode changes, or when its market
/// receives new executions, so a later review still sees them. The current snapshot always holds the latest figures; a
/// cross-margin liquidation price that only drifts with the account is not kept again. An observation, never an estimate.
/// </summary>
public sealed class RiskObservation : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    public string ContractId { get; set; } = null!;
    public string? VenueContractId { get; set; }
    /// <summary>The snapshot's observation time (when Vessel read the venue), not an execution time.</summary>
    public DateTimeOffset ObservedAtUtc { get; set; }
    public decimal SignedQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal? LiquidationPrice { get; set; }
    public string? MarginMode { get; set; }
}

/// <summary>
/// A retained fill that the venue later reported with different facts (Phase 2, item 14c, F-12). The first stored copy is
/// kept and never overwritten. This records only which venue fields differed, never either payload, so Account detail can
/// say so. There is one row per retained fill: later differing reports widen the field set and add to the count.
/// </summary>
public sealed class FillImportConflict : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>The retained fill whose first copy is kept.</summary>
    public Guid FillId { get; set; }
    public string ContractId { get; set; } = null!;
    public string SourceFillId { get; set; } = null!;
    /// <summary>The venue field names that differed, comma-separated in <c>FillConflictFields.All</c> order.</summary>
    public string Fields { get; set; } = null!;
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    /// <summary>Refreshes that reported a differing copy.</summary>
    public int Reports { get; set; }
}

/// <summary>
/// The venue facts compared when a retained fill ID is reported again. Vessel's own readings (canonical label, direction
/// text, position effect, fee and P&amp;L bases) are left out: they follow Vessel's rules, not the venue's report. The
/// venue contract and leverage count only when both copies state one; a value newly reported where none was is not a
/// change.
/// </summary>
public static class FillConflictFields
{
    public const string Side = "side", Price = "price", Quantity = "quantity", Fee = "fee", FeeToken = "feeToken",
        ClosedPnl = "closedPnl", Time = "time", OrderId = "orderId", TransactionHash = "transactionHash",
        VenueContract = "venueContract", Leverage = "leverage";

    public static readonly IReadOnlyList<string> All =
        [Side, Price, Quantity, Fee, FeeToken, ClosedPnl, Time, OrderId, TransactionHash, VenueContract, Leverage];
}

// The latest known state of a venue order that could belong to a Play. A fact, not trading intent.
public sealed class ImportedOrder : IOwned
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>Vessel's instrument key.</summary>
    public string ContractId { get; set; } = null!;
    /// <summary>The venue's own identifier when it differs from the key, e.g. a numeric market ID.</summary>
    public string? VenueContractId { get; set; }
    /// <summary>The venue's order ID; unique per account and contract.</summary>
    public string ExternalOrderId { get; set; } = null!;
    /// <summary><see cref="ExecutionFacts.Buy"/> or <see cref="ExecutionFacts.Sell"/>.</summary>
    public string Side { get; set; } = null!;
    public string OrderType { get; set; } = null!;
    public decimal LimitPrice { get; set; }
    public decimal? TriggerPrice { get; set; }
    public bool ReduceOnly { get; set; }
    public bool IsPositionTpsl { get; set; }
    public decimal OriginalSize { get; set; }
    public decimal RemainingSize { get; set; }
    public DateTimeOffset PlacedAtUtc { get; set; }
    /// <summary>open, filled, triggered, canceled, rejected or other.</summary>
    public string Status { get; set; } = null!;
    public string VenueStatus { get; set; } = null!;
    public DateTimeOffset StatusAtUtc { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
}
