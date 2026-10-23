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
    string Status, string VenueStatus, DateTimeOffset StatusAtUtc)
{
    /// <summary>
    /// For an order offered to link: the roles the link endpoint would accept on this Play, by its side, mechanics and
    /// retained fills (<see cref="ExecutionIntegrity.RoleConflict"/>). Absent on linked orders.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Roles { get; init; }
}

public sealed record ExecutionFillDto(string SourceFillId, string Direction, string Price, string Quantity, string Fee,
    string FeeToken, string ClosedPnlUsd, DateTimeOffset OccurredAtUtc, string Side, string PositionEffect, string PnlBasis);

/// <summary>
/// One retained link of a Play in any state, for the Play bundle (Phase 2, item 5c): a removed or rejected link stays in
/// the record with when it was made and last changed, and its venue order ID.
/// </summary>
public sealed record LinkRecordDto(Guid Id, string Role, string? EntryId, string? LevelId, string State, string Source, string? OrderId,
    string? ContractId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

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
    /// <summary>
    /// Compatible orders on the Play's contract placed in the lookback before it was created and not linked to any Play
    /// (Phase 2, item 4). The owner may link one; the automatic matcher never does.
    /// </summary>
    public IReadOnlyList<ExecutionOrderDto> EarlierOrders { get; init; } = [];
    /// <summary>The account's current venue position on the Play's contract, with its reported risk context (Phase 2, item 8a).</summary>
    public VenueRiskDto? VenuePosition { get; init; }
    /// <summary>The first risk context the venue reported at or after the Play's first entry fill; null when none was kept.</summary>
    public VenueRiskDto? RiskAtFirstFill { get; init; }
    /// <summary>
    /// Settled funding beside the Play (Phase 2, item 9b), shown only with proven sole exposure. Absent for a manual
    /// instrument and before the first linked entry fill. Never added to the venue or Play-local result.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayFundingDto? Funding { get; init; }
}

/// <summary>
/// Settled funding beside a Play (Phase 2, item 9b). <see cref="State"/> is one of:
/// <list type="bullet">
/// <item><c>sole</c>: the Play alone held the position over its holding window (<see cref="FromUtc"/> to <see cref="ToUtc"/>),
/// with the exact nominal sum of the settlements in it, their asset when they share one, and their count;</item>
/// <item><c>shared</c>: shared, not allocated, because other exposure was on the position;</item>
/// <item><c>incomplete</c>: coverage incomplete, because a condition of the proof is not shown;</item>
/// <item><c>unavailable</c>: the venue's funding is not read, or the account is disabled.</item>
/// </list>
/// Every state but <c>sole</c> has a <see cref="Reason"/> code and no amount, count or window. The amount is never added to
/// the venue or Play-local result, and no execution P&amp;L funding basis moves from unknown.
/// </summary>
public sealed record PlayFundingDto(string State, string? Reason, string? AmountUsd, string? Asset, int? PaymentCount,
    DateTimeOffset? FromUtc, DateTimeOffset? ToUtc);

/// <summary>
/// A span of an account's execution history (Phase 2, item 9b reads the item 6 ledger). As a covered span, a null start is
/// verified complete history from the account's start; as an open gap, a null end is unbounded on that side.
/// </summary>
public readonly record struct LedgerSpan(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc);

/// <summary>
/// What the funding proof asks the store for (Phase 2, item 9b): one account and Play, the canonical contracts of the Play's
/// partition and its native market (null for a canonical-only one, where every market of the contract counts), and the
/// earliest time evidence can matter for the window and the snapshot anchor.
/// </summary>
public sealed record FundingEvidenceRequest(Guid AccountId, Guid PlayId, IReadOnlyCollection<string> ContractIds, string? VenueContractId,
    DateTimeOffset FromUtc);

/// <summary>An order on the instrument with known missing executions, by the shared rule, and the life its executions lie in.</summary>
public sealed record MissingExecutionOrder(string ContractId, string? VenueContractId, DateTimeOffset PlacedAtUtc, DateTimeOffset? EndedAtUtc);

/// <summary>
/// The evidence the funding proof reads besides Activity's aggregate (Phase 2, item 9b), in one repeatable-read snapshot:
/// the owner's Activity facts revision at its start and end, the Play's linked orders, the coverage spans the ledger proves
/// continuous, the open history gaps, and the venue-forced executions and orders with known missing executions on the
/// instrument. Rows before <see cref="FundingEvidenceRequest.FromUtc"/> are read only when a complete ledger record could
/// make them matter; none is ever truncated.
/// </summary>
public sealed record FundingEvidence(long Revision, long RevisionAfter, IReadOnlyList<(OrderLinkRole Role, ImportedOrder Order)> Links,
    IReadOnlyList<LedgerSpan> Covered, IReadOnlyList<LedgerSpan> OpenGaps,
    IReadOnlyList<Vessel.Application.Activity.ActivityOmission> ForcedEvents, IReadOnlyList<MissingExecutionOrder> MissingExecutions);

/// <summary>
/// A position's venue-reported risk context (Phase 2, item 8a): signed quantity and entry as reported, the venue's
/// liquidation price and margin mode (null when not reported) and when Vessel read them. The venue's figures, never the
/// plan's isolated estimate; a cross-margin liquidation price moves with the whole account.
/// </summary>
public sealed record VenueRiskDto(string SignedQuantity, string EntryPrice, string? LiquidationPrice, string? MarginMode, DateTimeOffset ObservedAtUtc);

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
    /// <summary>
    /// The enabled account's current snapshot position in the Play's <paramref name="direction"/> (1 long, −1 short) on its
    /// native market, with the snapshot's time; null when flat, opposite, ambiguous or unknown.
    /// </summary>
    Task<VenueRiskDto?> PositionAsync(Guid accountId, string contractId, string? venueContractId, int direction, CancellationToken ct) =>
        Task.FromResult<VenueRiskDto?>(null);
    /// <summary>
    /// The first kept risk observation of the Play's own position: its direction and native market, from its first entry
    /// fill to its last exit when it has closed; null when none or when the market cannot be told apart.
    /// </summary>
    Task<VenueRiskDto?> RiskSinceAsync(Guid accountId, string contractId, string? venueContractId, int direction, DateTimeOffset since,
        DateTimeOffset? until, CancellationToken ct) =>
        Task.FromResult<VenueRiskDto?>(null);
    /// <summary>
    /// Phase 2, item 9b: the funding proof's evidence, read-only, in one snapshot (<see cref="FundingEvidence"/>). Null when
    /// the store keeps no ledger, which proves nothing.
    /// </summary>
    Task<FundingEvidence?> FundingEvidenceAsync(FundingEvidenceRequest request, CancellationToken ct) => Task.FromResult<FundingEvidence?>(null);
    void Add(PlayOrderLink link);
    void Add(PlayStatusChange change);
    /// <summary>
    /// Removes the monitor's watches of a Play's linked orders, so the Play's next end starts new ones from that end even
    /// when the clock stepped back or two ends share a time (1.3a, a return to Open).
    /// </summary>
    Task ForgetWatchesAsync(Guid playId, CancellationToken ct);
    /// <summary>Persists through the active unit of work, without committing.</summary>
    Task SaveAsync(CancellationToken ct);
    /// <summary>Requires an active unit of work; serializes reconciliation across requests and processes until it completes.</summary>
    Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct);
}
