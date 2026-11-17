using Vessel.Domain.Accounts;

namespace Vessel.Application.Venues;

/// <summary>Reads an account's orders: current open orders and recent order history, read-only.</summary>
public interface IVenueOrderReader
{
    string VenueId { get; }
    Task<VenueOrderReadResult> ReadOrdersAsync(string sourceId, CancellationToken cancellationToken);
    Task<VenueOrderReadResult> ReadOrdersAsync(Account account, CancellationToken cancellationToken) =>
        ReadOrdersAsync(account.SourceId ?? throw new VenueReadException("Account source is unavailable."), cancellationToken);
}

/// <summary>Normalized order lifecycle; <see cref="VenueOrder.VenueStatus"/> keeps the venue's own word.</summary>
public enum VenueOrderStatus { Open, Filled, Triggered, Canceled, Rejected, Other }

/// <summary>
/// One order at the venue. <see cref="Side"/> is <c>buy</c> or <c>sell</c>. Prices and sizes are exact. A trigger order (stop or take profit) acts at
/// <see cref="TriggerPrice"/>; a plain limit order rests at <see cref="LimitPrice"/>.
/// </summary>
public sealed record VenueOrder(
    string OrderId,
    string ContractId,
    string Side,
    string OrderType,
    decimal LimitPrice,
    decimal? TriggerPrice,
    bool ReduceOnly,
    bool IsPositionTpsl,
    decimal OriginalSize,
    decimal RemainingSize,
    DateTimeOffset PlacedAtUtc,
    VenueOrderStatus Status,
    string VenueStatus,
    DateTimeOffset StatusAtUtc,
    string? VenueContractId = null)
{
    /// <summary>The price the order is meant to act at.</summary>
    public decimal ActingPrice => TriggerPrice ?? LimitPrice;
}

/// <summary>Current orders together with potentially bounded historical status records.</summary>
/// <param name="OpenOrdersComplete">
/// True only when every current open order in the adapter's supported perpetual scope was read without
/// truncation or unsupported omissions. This does not assert complete order history or an atomic observation.
/// False means unknown or incomplete; absence must not establish cancellation or missing protection.
/// Even when true, absence does not establish a terminal status, and Orders can retain historical open statuses.
/// </param>
public sealed record VenueOrderReadResult(IReadOnlyList<VenueOrder> Orders, DateTimeOffset ObservedAtUtc, string Notice,
    bool OpenOrdersComplete = false)
{
    /// <summary>
    /// #95: set only when the whole book was read (no cap, page, read failure, unattributable order or other whole-read
    /// condition) except for these contracts, each holding an open order the adapter could attribute to its market but
    /// not read. <see cref="OpenOrdersComplete"/> is then false, so a reader of the whole book stays safe. Null means no
    /// contract is known to be complete unless <see cref="OpenOrdersComplete"/> is true.
    /// </summary>
    public IReadOnlyList<VenueIncompleteBook>? IncompleteContracts { get; init; }

    /// <summary>
    /// Whether every current open order on this contract was read. The match is deliberately wide: the canonical contract
    /// or the native market naming it makes it incomplete, so an unreadable order never leaves its own market decided.
    /// </summary>
    public bool OpenOrdersCompleteFor(string contractId, string? venueContractId) =>
        OpenOrdersComplete || IncompleteContracts is not null && IncompleteBookFor(contractId, venueContractId) is null;

    /// <summary>The first unreadable order kind on this contract, or null when none is named.</summary>
    public VenueIncompleteBook? IncompleteBookFor(string contractId, string? venueContractId) =>
        IncompleteContracts?.FirstOrDefault(x => x.ContractId == contractId || x.Native == (venueContractId ?? contractId));
}

/// <summary>
/// #95: one contract whose current open orders include one the adapter could attribute to it but not read, and the kind of
/// that order (one of <see cref="VenueIncompleteBook"/>'s kinds).
/// </summary>
public sealed record VenueIncompleteBook(string ContractId, string? VenueContractId, string Kind)
{
    /// <summary>A Hyperliquid order with attached take-profit/stop-loss children, which are not read.</summary>
    public const string AttachedTpsl = "attached-tpsl";
    /// <summary>A Lighter TWAP order or one of its slices.</summary>
    public const string Twap = "twap";
    /// <summary>A Lighter order with flags the adapter does not model.</summary>
    public const string Flagged = "flagged";
    /// <summary>A Lighter order with no size, such as one sized by the position when it acts.</summary>
    public const string Unsized = "unsized";

    public string Native => VenueContractId ?? ContractId;
}
