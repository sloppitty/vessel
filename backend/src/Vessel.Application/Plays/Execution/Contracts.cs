using System.Text.Json.Serialization;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;
using Vessel.Application.Venues;
using Vessel.Application.Monitoring;
using Vessel.Application.Common;

namespace Vessel.Application.Plays.Execution;

/// <summary>Links an order to a plan level: role entry with its entry, stop or target with its entry and level, or exit.</summary>
/// <summary>The owner says a venue order is not this Play's (phase 1, 1.6d).</summary>
public sealed record RejectOrderRequest([property: JsonRequired] string OrderId);

public sealed record LinkOrderRequest(
    [property: JsonRequired] string OrderId,
    [property: JsonRequired] string Role,
    string? EntryId = null,
    string? LevelId = null);

/// <summary>
/// Rebinds a linked stop or target to the observed venue order at its acting price (<c>level.drift</c>). The Play
/// must still be at <paramref name="ExpectedVersion"/> and the order at <paramref name="ObservedPrice"/>; the change
/// is one plan revision with <paramref name="Reason"/>, plus the link to <paramref name="OrderId"/> when it is another order.
/// </summary>
public sealed record RebindLevelRequest(
    [property: JsonRequired] long ExpectedVersion,
    [property: JsonRequired] string OrderId,
    [property: JsonRequired] string ObservedPrice,
    [property: JsonRequired] string Reason);

public sealed record ExecutionOrderDto(string OrderId, string Side, string OrderType, string LimitPrice, string? TriggerPrice,
    bool ReduceOnly, bool IsPositionTpsl, string OriginalSize, string RemainingSize, DateTimeOffset PlacedAtUtc,
    string Status, string VenueStatus, DateTimeOffset StatusAtUtc);

public sealed record ExecutionFillDto(string SourceFillId, string Direction, string Price, string Quantity, string Fee,
    string FeeToken, string ClosedPnlUsd, DateTimeOffset OccurredAtUtc, string Side, string PositionEffect, string PnlBasis);

public sealed record OrderLinkDto(Guid Id, string Role, string? EntryId, string? LevelId, string State, string Source,
    ExecutionOrderDto? Order, string FilledQuantity, IReadOnlyList<ExecutionFillDto> Fills)
{
    /// <summary>
    /// On a suggestion only: whether its order is also suggested to another Planned, Paused or Open Play of the account, so
    /// the copy may say "another play" (phase 1, 1.6d). Null on linked orders.
    /// </summary>
    public bool? OtherPlay { get; init; }
}

public sealed record EntryProgressDto(string EntryId, string FilledQuantity, string? AverageFillPrice, int RestingOrders)
{
    /// <summary>
    /// The entry as planned, from the plan revision in force at its first fill (<see cref="PlanRevision"/>, null for a Draft
    /// or a retrospective Play); before any fill, the current plan. Null when the plan cannot give it.
    /// </summary>
    public string? PlannedPrice { get; init; }
    public string? PlannedQuantity { get; init; }
    public int? PlanRevision { get; init; }
}

public sealed record FeeTotalDto(string Token, string Amount);

/// <summary>
/// Quantities are base units summed from linked fills; closed PnL and fees are venue-reported.
/// <paramref name="ClosedPnlBasis"/> is gross (fees separate), net-of-fee (fees already taken off) or mixed.
/// </summary>
public sealed record ExecutionTotalsDto(string EnteredQuantity, string ExitedQuantity, string OpenQuantity,
    string ClosedPnlUsd, string ClosedPnlBasis, IReadOnlyList<FeeTotalDto> Fees)
{
    /// <summary>
    /// Venue closed PnL less listed stablecoin fees on gross-basis fills, over the fills counted here (contradicting fills
    /// are left out), so far while the play is open. Review and sizing score this basis only for a closed play whose fills
    /// settle it, each with its own eligibility rules; retrospective plays are not in sizing. Null when a fee is
    /// unverified or in another token.
    /// </summary>
    public string? VenueNetUsd { get; init; }
    /// <summary>The Play-local result beside it; known once the play is closed and its fills settle it.</summary>
    public PlayLocalResultDto? Local { get; init; }
}

/// <summary><paramref name="Issues"/> say why linked fills cannot settle the play; their quantities stay out of the totals when they contradict it.</summary>
public sealed record PlayExecutionDto(Guid PlayId, string Status, bool Tracked, string? Reason, DateTimeOffset? CheckedAtUtc,
    ExecutionTotalsDto Totals, IReadOnlyList<EntryProgressDto> Entries, IReadOnlyList<OrderLinkDto> Links,
    IReadOnlyList<OrderLinkDto> Suggestions, IReadOnlyList<ExecutionOrderDto> UnlinkedOrders, string Notice, IReadOnlyList<string> Issues)
{
    public AccountMonitorDto? Monitor { get; init; }
    public IReadOnlyList<MonitorDiscrepancyDto> Discrepancies { get; init; } = [];
    /// <summary>Linked exit and entry orders watched after the Play ended: watching, resolved or a persistent unverifiable.</summary>
    public IReadOnlyList<MonitorWatchDto> Watches { get; init; } = [];
    /// <summary>Times, averages and estimated R by Review's rules, over the facts Review reads.</summary>
    public PlayReceiptDto? Receipt { get; init; }
}

// Application owns this bounded port; EF details stay in Persistence. Reads are owner-filtered.
public interface IPlayExecutionStore
{
    Task<Account?> AccountAsync(Guid id, CancellationToken ct);
    /// <summary>
    /// Planned, paused and open Plays of the account, tracked for status changes with their committed values and locked
    /// until the transaction ends, so owner status and plan changes take turns with linking. <paramref name="settling"/>
    /// adds Unresolved Plays, which take no new links but whose existing linked orders still settle them.
    /// </summary>
    Task<List<Play>> ActivePlaysAsync(Guid accountId, CancellationToken ct, bool settling = false);
    /// <summary>Every retained order of the account, read-only, by placement time then external order ID.</summary>
    Task<List<ImportedOrder>> OrdersAsync(Guid accountId, CancellationToken ct);

    /// <summary>
    /// Read-only orders with one of the internal <paramref name="orderIds"/>, plus orders on a contract of <paramref name="placedSince"/>
    /// placed at or after its time, in the order <see cref="OrdersAsync(Guid, CancellationToken)"/> returns them.
    /// </summary>
    async Task<List<ImportedOrder>> OrdersAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds,
        IReadOnlyDictionary<string, DateTimeOffset> placedSince, CancellationToken ct) =>
        (await OrdersAsync(accountId, ct)).Where(o => IncludesOrder(o, orderIds, placedSince)).ToList();

    /// <summary>The selection rule of the targeted order read; persistence may narrow in SQL first but must end here.</summary>
    static bool IncludesOrder(ImportedOrder order, IReadOnlyCollection<Guid> orderIds, IReadOnlyDictionary<string, DateTimeOffset> placedSince) =>
        orderIds.Contains(order.Id) || placedSince.TryGetValue(order.ContractId, out var since) && order.PlacedAtUtc >= since;

    /// <summary>The read-only order with the venue's <paramref name="externalOrderId"/> on one contract of the account, or null.</summary>
    async Task<ImportedOrder?> OrderAsync(Guid accountId, string contractId, string externalOrderId, CancellationToken ct) =>
        (await OrdersAsync(accountId, ct)).FirstOrDefault(o => o.ContractId == contractId && o.ExternalOrderId == externalOrderId);

    /// <summary>
    /// Inserts new orders and updates the status of known ones, keyed by contract and external order ID. A known order
    /// whose native venue contract differs from the incoming one fails closed with a <see cref="VenueReadException"/>.
    /// </summary>
    Task UpsertOrdersAsync(Account account, IReadOnlyList<VenueOrder> orders, DateTimeOffset observedAt, CancellationToken ct);
    /// <summary>Every link of the account in any state, tracked for changes, by creation time then ID.</summary>
    Task<List<PlayOrderLink>> LinksAsync(Guid accountId, CancellationToken ct);

    /// <summary>
    /// Links in any state, tracked for changes, on one of the internal <paramref name="orderIds"/> (for any Play) or belonging
    /// to one of <paramref name="playIds"/>, in the order <see cref="LinksAsync(Guid, CancellationToken)"/> returns them.
    /// </summary>
    async Task<List<PlayOrderLink>> LinksAsync(Guid accountId, IReadOnlyCollection<Guid> orderIds, IReadOnlyCollection<Guid> playIds,
        CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => orderIds.Contains(l.ImportedOrderId) || playIds.Contains(l.PlayId)).ToList();

    /// <summary>Links of one Play in any state, read-only, in creation order.</summary>
    async Task<List<PlayOrderLink>> PlayLinksAsync(Guid accountId, Guid playId, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => l.PlayId == playId).ToList();

    /// <summary>One link of a Play in any state, tracked for changes, or null.</summary>
    async Task<PlayOrderLink?> LinkAsync(Guid accountId, Guid playId, Guid linkId, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).FirstOrDefault(l => l.Id == linkId && l.PlayId == playId);

    /// <summary>
    /// Of <paramref name="orderIds"/>, those also suggested to another Planned, Paused or Open Play of the account than
    /// <paramref name="playId"/> (phase 1, 1.6d).
    /// </summary>
    async Task<HashSet<Guid>> SuggestedElsewhereAsync(Guid accountId, Guid playId, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        if (orderIds.Count == 0) return [];
        var others = (await ActivePlaysAsync(accountId, ct)).Where(p => p.Id != playId).Select(p => p.Id).ToHashSet();
        if (others.Count == 0) return [];
        return (await LinksAsync(accountId, orderIds, [], ct)).Where(l => l.State == OrderLinkState.Suggested && others.Contains(l.PlayId))
            .Select(l => l.ImportedOrderId).ToHashSet();
    }

    /// <summary>Internal IDs of orders linked to any Play of the account, in any status.</summary>
    async Task<HashSet<Guid>> LinkedOrderIdsAsync(Guid accountId, CancellationToken ct) =>
        (await LinksAsync(accountId, ct)).Where(l => l.State == OrderLinkState.Linked).Select(l => l.ImportedOrderId).ToHashSet();
    /// <summary>Read-only fills of the account belonging to one of <paramref name="orders"/>, by time then ID.</summary>
    Task<List<ImportedFill>> FillsAsync(Guid accountId, IReadOnlyCollection<OrderKey> orders, CancellationToken ct);
    void Add(PlayOrderLink link);
    void Add(PlayStatusChange change);
    /// <summary>Persists through the active unit of work, without committing.</summary>
    Task SaveAsync(CancellationToken ct);
    /// <summary>Requires an active unit of work; serializes reconciliation across requests and processes until it completes.</summary>
    Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct);
}
