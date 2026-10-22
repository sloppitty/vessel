using System.Numerics;
using Vessel.Application.Common;
using Vessel.Application.Sizing;
using Vessel.Application.Venues;
using Vessel.Application.Credentials;
using Vessel.Application.Ownership;
using Vessel.Application.Monitoring;
using Vessel.Domain.Accounts;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Application.Plays.Execution;

/// <summary>
/// Links venue orders to Play levels and moves Plays between Planned, Open and Closed from linked fills.
/// Unambiguous orders link automatically; ambiguous ones become suggestions for the owner. Read-only at the venue.
/// </summary>
public sealed class PlayExecutionService(IPlayStore plays, IPlayExecutionStore store,
    IVenueRegistry venues, TimeProvider time, IUnitOfWork unitOfWork, IAccountCredentialReader? credentials = null,
    PlayExecutionOptions? executionOptions = null) : IMonitorImporter
{
    private const int MaxUnlinkedOrders = 20;
    /// <summary>Earlier orders checked for compatibility, newest first, bounding the fills read for them.</summary>
    private const int MaxEarlierScan = 200;
    private static readonly OrderLinkRole[] OfferedRoles = [OrderLinkRole.Entry, OrderLinkRole.Stop, OrderLinkRole.Target, OrderLinkRole.Exit];
    private static readonly PlayExecutionOptions DefaultOptions = new();
    private const string Notice = "Orders and fills come from the venue and link to this play by order ID. " +
        "Price touches are never treated as fills. Quantities are base units from linked fills; closed PnL and fees are as the venue reports them.";

    /// <summary>Retain an already-read order observation inside the caller's fenced account transaction. No linking or network reads.</summary>
    public Task RetainAsync(Account account, VenueOrderReadResult orders, CancellationToken ct) =>
        store.WithAccountLockAsync(account.Id, async () =>
        {
            if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts cannot import actual orders.");
            await store.UpsertOrdersAsync(account, orders.Orders, orders.ObservedAtUtc, ct);
            return true;
        }, ct);

    /// <summary>Reuse execution matching and integrity checks with imported facts, without another venue/catalogue read.</summary>
    public Task ReconcileImportedAsync(Account account, VenueOrderReadResult orders,
        IReadOnlyList<VenueInstrument> instruments, CancellationToken ct) =>
        store.WithAccountLockAsync(account.Id, async () =>
        {
            if (account.TradingMode != "real") throw new ProblemException(409, "Paper accounts cannot reconcile actual orders.");
            // A tick-size venue's tick, or a significant-figure venue's decimal cap (Phase 2, item 14b, F-11).
            var tickVenue = venues.Descriptor(account.VenueId)?.PriceRule == PriceRules.TickSize;
            var ticks = instruments.Select(i => (i.ContractId, Step: tickVenue ? i.PriceStep : PriceRules.DecimalStep(i.MaxPriceDecimals)))
                .Where(i => i.Step is > 0).GroupBy(i => i.ContractId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Step!.Value, StringComparer.Ordinal);
            await ReconcileAsync(account, orders, ticks, ct);
            return true;
        }, ct);

    Task IMonitorImporter.ReconcileAsync(Account account, VenueOrderReadResult orders,
        IReadOnlyList<VenueInstrument> instruments, CancellationToken ct) =>
        ReconcileImportedAsync(account, orders, instruments, ct);

    public async Task<PlayExecutionDto> GetAsync(Guid playId, CancellationToken ct)
    {
        var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
        if (play.TradingMode != "real") throw new ProblemException(409, "Paper Plays use explicit simulated executions, not actual order linking.");
        var account = await store.AccountAsync(play.AccountId, ct);
        var mine = (await store.PlayLinksAsync(play.AccountId, play.Id, ct)).Where(l => l.State is not (OrderLinkState.Dismissed or OrderLinkState.Rejected)).ToList();
        // Linked to any Play of the account, also closed ones: such an order is never offered again.
        var linkedOrders = await store.LinkedOrderIdsAsync(play.AccountId, ct);
        var mineOrders = mine.Select(l => l.ImportedOrderId).ToHashSet();
        // This Play's linked and suggested orders (on any contract), plus its contract's orders placed since a bounded lookback
        // before it was created: the earlier ones are only offered to the owner (Phase 2, item 4), never matched.
        var earliest = play.CreatedAtUtc - (executionOptions ?? DefaultOptions).EarlierOrderLookback;
        var loaded = await store.OrdersAsync(play.AccountId, mineOrders, play.ContractId is null
            ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal) { [play.ContractId] = earliest }, ct);
        var orders = loaded.ToDictionary(o => o.Id);
        var mineLoaded = mineOrders.Where(orders.ContainsKey).Select(id => orders[id]).ToList();
        var fills = FillsByOrder(mineLoaded, await store.FillsAsync(play.AccountId, mineLoaded.Select(OrderKey.Of).ToList(), ct));
        var linked = mine.Where(l => l.State == OrderLinkState.Linked)
            .OrderBy(l => l.Role).ThenBy(l => l.EntryId).ThenBy(l => l.LevelId).ThenBy(l => External(orders, l)).ToList();
        // An Unresolved Play is never offered orders (1.3a); it takes a link only after a return to Open.
        var suggestions = play.Status == PlayStatus.Unresolved ? [] :
            mine.Where(l => l.State == OrderLinkState.Suggested && !linkedOrders.Contains(l.ImportedOrderId)).ToList();
        // Cancelled orders that never filled cannot be part of the play, so only live or filled ones are offered.
        var offered = play.ContractId is null ? [] : loaded
            .Where(o => o.ContractId == play.ContractId && o.PlacedAtUtc >= earliest && !linkedOrders.Contains(o.Id) &&
                o.Status is "open" or "filled" or "triggered").OrderByDescending(o => o.PlacedAtUtc).ToList();
        var plan = PlayDocuments.Read(play.Plan);
        // Each offered order states the roles the link endpoint would accept (Sol round 1 on #43), so the panel never offers
        // one that a retained fill contradicts. Sol round 1 on #37: an earlier order is offered only when some role accepts
        // it, checked before the cap, so unusable orders cannot crowd out the fast entry. Its price is not required to
        // match: a market entry rarely fills at the planned price, and the owner decides.
        var after = offered.Where(o => o.PlacedAtUtc >= play.CreatedAtUtc).Take(MaxUnlinkedOrders).ToList();
        var before = offered.Where(o => o.PlacedAtUtc < play.CreatedAtUtc).Take(MaxEarlierScan).ToList();
        var candidates = after.Concat(before).ToList();
        var candidateFills = (candidates.Count == 0 ? [] : await store.FillsAsync(play.AccountId, candidates.Select(OrderKey.Of).ToList(), ct))
            .ToLookup(OrderKey.Of);
        List<string> Roles(ImportedOrder o, IEnumerable<OrderLinkRole> roles) =>
            roles.Where(role => ExecutionIntegrity.RoleConflict(role, plan, o, candidateFills[OrderKey.Of(o)]) is null).Select(Name).ToList();
        var unlinked = after.Select(o => OrderDto(o) with { Roles = Roles(o, OfferedRoles) }).ToList();
        var earlier = before.Select(o => (Order: o, Roles: Roles(o, OfferedRoles))).Where(x => x.Roles.Any(role => role != "exit"))
            .Take(MaxUnlinkedOrders).Select(x => OrderDto(x.Order) with { Roles = x.Roles }).ToList();

        var revisions = await plays.RevisionsAsync(play.Id, ct);
        var settlement = Settle(plan, linked, orders, fills, play.Origin == PlayOrigin.Retrospective);
        var entered = settlement.Entered;
        var exited = settlement.Exited;
        var open = entered - exited;
        var all = settlement.Counted.Select(f => f.Fill).ToList();
        // The scored venue basis and the Play-local basis side by side. The local result reads the same facts sizing and
        // Review read: every linked fill and the orders known to be missing executions.
        var venueNet = PlayOutcomeCalculator.VenueNet(all);
        var local = PlayLocalResult.From(new ClosedPlayFacts(play, [], settlement.All, settlement.Incomplete),
            venues.Descriptor(play.VenueId)?.QuoteAsset);
        var totals = new ExecutionTotalsDto(entered.ToString(), exited.ToString(), open.Units.Sign > 0 ? open.ToString() : "0",
            all.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.ClosedPnlUsd)).ToString(), PnlBasis(all),
            all.GroupBy(f => f.FeeToken).OrderBy(g => g.Key).Select(g => new FeeTotalDto(g.Key,
                g.Aggregate(default(ExactAmount), (sum, f) => sum + ExactAmount.From(f.Fee)).ToString())).ToList())
        {
            VenueNetUsd = venueNet.FeesComplete ? venueNet.Net.ToString() : null,
            Local = local.ToDto(venueNet.FeesComplete ? venueNet.Net : null),
        };
        var entries = plan.Entries.Select(entry =>
        {
            var own = linked.Where(l => l.Role == OrderLinkRole.Entry && l.EntryId == entry.Id).ToList();
            var filled = own.SelectMany(l => fills[l.ImportedOrderId]).Where(f => !ExecutionIntegrity.Contradicts(OrderLinkRole.Entry, settlement.EntrySide, f)).ToList();
            var quantity = ExecutionIntegrity.Quantity(filled);
            var notional = filled.Aggregate(BigInteger.Zero, (sum, f) => sum + ExactAmount.Product(f.Price, f.Quantity));
            var planned = PlayReceipts.Planned(play, revisions, entry.Id, filled.Count == 0 ? null : filled.Min(f => f.OccurredAtUtc));
            return new EntryProgressDto(entry.Id, quantity.ToString(), quantity.Units.Sign > 0 ? ExactAmount.Average(notional, quantity) : null,
                own.Count(l => orders.TryGetValue(l.ImportedOrderId, out var o) && o.Status == "open"))
            {
                PlannedPrice = planned.Price, PlannedQuantity = planned.Quantity, PlanRevision = planned.Revision,
            };
        }).ToList();
        // A suggestion says "another play" only when another active Play is offered the same order (1.6d).
        var elsewhere = await store.SuggestedElsewhereAsync(play.AccountId, play.Id, suggestions.Select(l => l.ImportedOrderId).ToHashSet(), ct);
        OrderLinkDto Link(PlayOrderLink link) => new(link.Id, Name(link.Role), link.EntryId, link.LevelId, Name(link.State), Name(link.Source),
            orders.TryGetValue(link.ImportedOrderId, out var order) ? OrderDto(order) : null, ExecutionIntegrity.Quantity(fills[link.ImportedOrderId]).ToString(),
            fills[link.ImportedOrderId].OrderBy(f => f.OccurredAtUtc).Select(FillDto).ToList());
        var reason = UntrackedReason(play, account, account is null ? null : venues.Descriptor(account.VenueId));
        if (reason is null && account is { IsEnabled: true } && play.Status is not (PlayStatus.Closed or PlayStatus.Cancelled))
            reason = await CredentialReasonAsync(account, ct);
        var risk = RiskScope(plan, linked, fills, settlement.EntrySide, open.Units.Sign <= 0 && exited.Units.Sign > 0);
        return new PlayExecutionDto(play.Id, PlayService.StatusName(play.Status), reason is null, reason, account?.LastSyncedAtUtc,
            totals, entries, linked.Select(Link).ToList(),
            suggestions.Select(l => Link(l) with { OtherPlay = elsewhere.Contains(l.ImportedOrderId) }).ToList(), unlinked, Notice, settlement.Problems)
        {
            Receipt = PlayReceipts.Build(new ClosedPlayFacts(play, revisions, settlement.All, settlement.Incomplete), time.GetUtcNow()),
            EarlierOrders = earlier,
            VenuePosition = play.ContractId is null || risk is not { } at ? null
                : await store.PositionAsync(play.AccountId, play.ContractId, at.Native, at.Direction, ct),
            RiskAtFirstFill = play.ContractId is not null && risk is { First: { } first } own
                ? await store.RiskSinceAsync(play.AccountId, play.ContractId, own.Native, own.Direction, first, own.Until, ct) : null,
        };
    }

    /// <summary>The owner links an order (or confirms a suggestion). It must not belong to another level already.</summary>
    public async Task<PlayExecutionDto> LinkAsync(Guid playId, LinkOrderRequest request, CancellationToken ct)
    {
        var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
        if (play.TradingMode != "real") throw new ProblemException(409, "Paper Plays use explicit simulated executions, not actual order linking.");
        if (play.Status == PlayStatus.Unresolved) throw new ProblemException(409, "Return this Play to Open before linking another order.");
        if (!ExecutionMatcher.IsActive(play)) throw new ProblemException(409, NotLinkable);
        var role = request.Role switch
        {
            "entry" => OrderLinkRole.Entry, "stop" => OrderLinkRole.Stop, "target" => OrderLinkRole.Target, "exit" => OrderLinkRole.Exit,
            _ => throw new ProblemException(400, "Role must be entry, stop, target or exit.")
        };
        await unitOfWork.ExecuteAsync(() => store.WithAccountLockAsync(play.AccountId, async () =>
        {
            await RequireActiveAsync(play, NotLinkable, ct);
            // The committed plan decides, not the one this request read first.
            var plan = PlayDocuments.Read(play.Plan);
            var entry = role == OrderLinkRole.Exit ? null : plan.Entries.FirstOrDefault(e => e.Id == request.EntryId)
                ?? throw new ProblemException(400, "Choose an entry of this play.");
            var levelId = role switch
            {
                OrderLinkRole.Stop => entry!.Stops.FirstOrDefault(s => s.Id == request.LevelId)?.Id ?? throw new ProblemException(400, "Choose a stop of this entry."),
                OrderLinkRole.Target => entry!.Targets.FirstOrDefault(t => t.Id == request.LevelId)?.Id ?? throw new ProblemException(400, "Choose a target of this entry."),
                _ => null
            };
            // The venue ID resolves within the Play's account and contract; another contract's order is not this one.
            var order = (play.ContractId is null ? null : await store.OrderAsync(play.AccountId, play.ContractId, request.OrderId, ct))
                ?? throw new ProblemException(404, "This order is not known for the play's instrument. Check the venue first.");
            // Every link on this order, for any Play and in any state.
            var links = await store.LinksAsync(play.AccountId, [order.Id], [], ct);
            if (links.Any(l => l.ImportedOrderId == order.Id && l.State == OrderLinkState.Linked))
                throw new ProblemException(409, "This order is already linked. Unlink it first.");
            if (ExecutionIntegrity.RoleConflict(role, plan, order, await store.FillsAsync(play.AccountId, [OrderKey.Of(order)], ct)) is { } conflict)
                throw new ProblemException(409, conflict);
            var now = time.GetUtcNow();
            var key = PlayOrderLink.Key(role, entry?.Id, levelId);
            var existing = links.FirstOrDefault(l => l.PlayId == play.Id && l.ImportedOrderId == order.Id && l.LevelKey == key &&
                l.State != OrderLinkState.Rejected);
            if (existing is not null) existing.Confirm(now);
            else store.Add(new PlayOrderLink(play, order, role, entry?.Id, levelId, OrderLinkState.Linked, OrderLinkSource.Owner, now));
            // Other suggestions for this order stay hidden while it is linked and return if it is unlinked.
            await store.SaveAsync(ct);
            await ApplyTransitionsAsync(play.AccountId, [play], ct);
            await store.SaveAsync(ct);
            return true;
        }, ct), ct);
        return await GetAsync(playId, ct);
    }

    /// <summary>
    /// The owner says an order is not this Play's (phase 1, 1.6d): in one step under the account lock, every suggestion of
    /// it to this Play is declined and one <see cref="OrderLinkState.Rejected"/> record is kept, so no reconciliation offers
    /// it to this Play again, at a level of any plan revision or as an unplanned exit. Other Plays keep their suggestions.
    /// </summary>
    public async Task<PlayExecutionDto> RejectOrderAsync(Guid playId, RejectOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OrderId)) throw new ProblemException(400, "Choose an order.");
        var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
        if (play.TradingMode != "real") throw new ProblemException(409, "Paper Plays use explicit simulated executions, not actual order linking.");
        await unitOfWork.ExecuteAsync(() => store.WithAccountLockAsync(play.AccountId, async () =>
        {
            // A Play's instrument is fixed once planned; the rejection does not read its plan, so a revision cannot escape it.
            var order = (play.ContractId is null ? null : await store.OrderAsync(play.AccountId, play.ContractId, request.OrderId, ct))
                ?? throw new ProblemException(404, "This order is not known for the play's instrument. Check the venue first.");
            var links = (await store.LinksAsync(play.AccountId, [order.Id], [], ct)).Where(l => l.PlayId == play.Id).ToList();
            if (links.Any(l => l.State == OrderLinkState.Linked))
                throw new ProblemException(409, "This order is linked to this play. Unlink it instead.");
            var now = time.GetUtcNow();
            foreach (var link in links.Where(l => l.State == OrderLinkState.Suggested)) link.Dismiss(now);
            if (!links.Any(l => l.State == OrderLinkState.Rejected))
                store.Add(new PlayOrderLink(play, order, OrderLinkRole.Exit, null, null, OrderLinkState.Rejected, OrderLinkSource.Owner, now));
            await store.SaveAsync(ct);
            return true;
        }, ct), ct);
        return await GetAsync(playId, ct);
    }

    /// <summary>
    /// Removes a link or declines a suggestion. It is never proposed for that level again. Closed, Cancelled and Unresolved
    /// are final, so their links stay: removing one would leave a final status its fills no longer support.
    /// </summary>
    public async Task<PlayExecutionDto> UnlinkAsync(Guid playId, Guid linkId, CancellationToken ct)
    {
        var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
        if (play.TradingMode != "real") throw new ProblemException(409, "Paper Plays use explicit simulated executions, not actual order linking.");
        await unitOfWork.ExecuteAsync(() => store.WithAccountLockAsync(play.AccountId, async () =>
        {
            var link = await store.LinkAsync(play.AccountId, play.Id, linkId, ct) is { State: not (OrderLinkState.Dismissed or OrderLinkState.Rejected) } found
                ? found : throw new ProblemException(404, "Link not found.");
            if (link.State == OrderLinkState.Linked) await RequireActiveAsync(play, "Closed, cancelled and unresolved plays keep their linked orders.", ct);
            link.Dismiss(time.GetUtcNow());
            await store.SaveAsync(ct);
            return true;
        }, ct), ct);
        return await GetAsync(playId, ct);
    }

    /// <summary>
    /// Answers <c>level.drift</c>: the linked stop or target takes the observed order's acting price as a price level, in
    /// one plan revision with the owner's reason. When the observed order is another order, it replaces the link: the old
    /// link is dismissed and the order is linked to the level. Under the account and Play locks, the Play must still be at
    /// the expected version and the retained order resting at the observed price; otherwise nothing changes (409).
    /// Nothing is sent to the venue, and an order's price is never treated as a fill.
    /// </summary>
    public Task<PlayDto> RebindAsync(Guid playId, Guid linkId, RebindLevelRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ProblemException(400, "Say why the level moves. The reason is kept with the revision.");
        if (!ExactDecimal.IsPlanText(request.ObservedPrice) || !ExactDecimal.TryParseVenue(request.ObservedPrice, out var observed) || observed <= 0)
            throw new ProblemException(400, $"Observed price: {PlayDocuments.PlainDecimalRule}");
        if (string.IsNullOrEmpty(request.OrderId)) throw new ProblemException(400, "Name the observed order.");
        return unitOfWork.ExecuteAsync(() => plays.WithPlayLockAsync(playId, async () =>
        {
            var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
            if (play.TradingMode != "real") throw new ProblemException(409, "Paper Plays use explicit simulated executions, not actual order linking.");
            if (play.Version != request.ExpectedVersion) throw new ProblemException(409, StalePlay);
            if (!ExecutionMatcher.IsActive(play) || play.Origin == PlayOrigin.Retrospective || play.ContractId is null)
                throw new ProblemException(409, "Only planned, paused or open plays rebind their levels.");
            var link = await store.LinkAsync(play.AccountId, play.Id, linkId, ct) is { State: OrderLinkState.Linked, Role: OrderLinkRole.Stop or OrderLinkRole.Target } found
                ? found : throw new ProblemException(404, "Linked stop or target not found.");
            // The venue ID resolves within the Play's account and contract; another contract's order is not this one.
            var order = await store.OrderAsync(play.AccountId, play.ContractId, request.OrderId, ct)
                ?? throw new ProblemException(404, "This order is not known for the play's instrument. Check the venue first.");
            if (order.Status != "open") throw new ProblemException(409, "This order no longer rests at the venue. Check again before rebinding.");
            // A trigger order acts at its trigger price, a limit order at its limit price, as the matcher compares them.
            var acting = order.TriggerPrice ?? order.LimitPrice;
            if (acting != observed) throw new ProblemException(409, "The order's price changed since it was observed. Check again before rebinding.");
            var plan = PlayDocuments.Read(play.Plan);
            var entry = plan.Entries.FirstOrDefault(e => e.Id == link.EntryId);
            var level = (link.Role == OrderLinkRole.Stop ? entry?.Stops : entry?.Targets)?.FirstOrDefault(l => l.Id == link.LevelId)
                ?? throw new ProblemException(409, "The linked level is no longer in the plan. Review the link first.");
            // As in a save: another order linked to this level still references its price.
            if ((await plays.LinkedPlanReferencesAsync(play.Id, ct)).FirstOrDefault(r => r.Link.Id != link.Id && r.Link.LevelKey == link.LevelKey) is { } other)
                throw new ProblemException(409, $"Order {other.ExternalOrderId} still references this plan level. Unlink it before changing or removing the level.");
            var replacing = order.Id != link.ImportedOrderId;
            if (replacing)
            {
                if ((await store.LinksAsync(play.AccountId, [order.Id], [], ct)).Any(l => l.State == OrderLinkState.Linked))
                    throw new ProblemException(409, "This order is already linked. Unlink it first.");
                // Dismissing a link with fills would drop executed exits from the Play.
                var previous = (await store.OrdersAsync(play.AccountId, [link.ImportedOrderId], NoContracts, ct)).FirstOrDefault();
                if (previous is not null && (await store.FillsAsync(play.AccountId, [OrderKey.Of(previous)], ct)).Count > 0)
                    throw new ProblemException(409, $"Order {previous.ExternalOrderId} has fills that count for this play, so its link cannot be replaced.");
                if (ExecutionIntegrity.RoleConflict(link.Role, plan, order, await store.FillsAsync(play.AccountId, [OrderKey.Of(order)], ct)) is { } conflict)
                    throw new ProblemException(409, conflict);
            }
            var rebound = level with { Unit = "price", Value = ExactDecimal.Format(acting) };
            var next = plan with
            {
                Entries = plan.Entries.Select(e => e.Id != entry!.Id ? e : link.Role == OrderLinkRole.Stop
                    ? e with { Stops = e.Stops.Select(s => s.Id == level.Id ? rebound : s).ToList() }
                    : e with { Targets = e.Targets.Select(t => t.Id == level.Id ? rebound : t).ToList() }).ToList()
            };
            var now = time.GetUtcNow();
            // The same plan rules as a save: a stop of a filled entry may sit at or beyond it, everything else keeps its side.
            var revision = play.ChangePlan(PlayDocuments.Serialize(PlayDocuments.Normalize(next, plan, await PlayService.ExecutedEntriesAsync(plays, play, ct))),
                request.Reason, now) ?? throw new ProblemException(409, "This level already sits at the order's price.");
            plays.Add(revision);
            if (replacing)
            {
                link.Dismiss(now);
                store.Add(new PlayOrderLink(play, order, link.Role, link.EntryId, link.LevelId, OrderLinkState.Linked, OrderLinkSource.Owner, now));
                // As with an owner link, fills the new order already has count toward the Play's status at once.
                await store.SaveAsync(ct);
                await ApplyTransitionsAsync(play.AccountId, [play], ct);
            }
            return PlayService.ToDto(play);
        }, ct), ct);
    }

    /// <summary>The earliest linked entry fill that does not contradict the entry side, or null before any.</summary>
    /// <summary>
    /// Which venue position is the Play's (Sol round 1 on #41): its direction, the one native market its linked fills name
    /// (null when they name none), its first entry fill and, once flat, its last fill. Null when the fills name several
    /// native markets, so attribution stays unknown.
    /// </summary>
    private static (int Direction, string? Native, DateTimeOffset? First, DateTimeOffset? Until)? RiskScope(PlayPlanDocument plan,
        IEnumerable<PlayOrderLink> linked, ILookup<Guid, ImportedFill> fills, string entrySide, bool closed)
    {
        var own = linked.SelectMany(l => fills[l.ImportedOrderId]).ToList();
        var natives = own.Select(f => f.VenueContractId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (natives.Count > 1) return null;
        return (plan.Direction == "long" ? 1 : -1, natives.SingleOrDefault(), FirstEntryFill(linked, fills, entrySide),
            closed && own.Count > 0 ? own.Max(f => f.OccurredAtUtc) : null);
    }

    private static DateTimeOffset? FirstEntryFill(IEnumerable<PlayOrderLink> linked, ILookup<Guid, ImportedFill> fills, string entrySide) =>
        linked.Where(l => l.Role == OrderLinkRole.Entry).SelectMany(l => fills[l.ImportedOrderId])
            .Where(f => !ExecutionIntegrity.Contradicts(OrderLinkRole.Entry, entrySide, f)).Select(f => (DateTimeOffset?)f.OccurredAtUtc).Min();

    private const string StalePlay = "This Play changed elsewhere. Reload it before saving again.";

    /// <summary>
    /// The owner declares an Open venue Play unresolved: its executions can never settle it (phase 1, 1.3a, decision 4).
    /// Under the account and Play locks that imports hold, the Play must still be Open at the expected version and have
    /// imported executions; the declaration keeps a snapshot of its execution facts. Its result stays null. Nothing is
    /// sent to the venue, and nothing here reads account flatness or the monitor.
    /// </summary>
    public Task<PlayDto> DeclareUnresolvedAsync(Guid playId, DeclareUnresolvedRequest request, CancellationToken ct)
    {
        var reason = PlayUnresolved.ParseReason(request.Reason) ?? throw new ProblemException(400, PlayUnresolved.ReasonRule);
        if (string.IsNullOrWhiteSpace(request.Note)) throw new ProblemException(400, "Say what happened. The note is kept with the declaration.");
        return unitOfWork.ExecuteAsync(() => plays.WithPlayLockAsync(playId, async () =>
        {
            var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
            if (play.TradingMode != "real")
                throw new ProblemException(409, "A paper Play's simulation holds what it cannot settle; it is never declared unresolved.");
            if (play.Version != request.ExpectedVersion) throw new ProblemException(409, StalePlay);
            if (play.Status != PlayStatus.Open) throw new ProblemException(409, "Only an open Play can be declared unresolved.");
            var evidence = await EvidenceAsync(play, ct)
                ?? throw new ProblemException(409, "Only a Play with imported executions can be declared unresolved.");
            plays.Add(play.DeclareUnresolved(reason, request.Note, PlayUnresolved.Serialize(evidence), time.GetUtcNow()));
            return PlayService.ToDto(play);
        }, ct), ct);
    }

    /// <summary>
    /// The owner returns an Unresolved Play to Open, with a reason. It can take new links again, and a completed review is
    /// needed again once it ends; text and answers stay.
    /// </summary>
    public Task<PlayDto> ReturnToOpenAsync(Guid playId, ReturnToOpenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Note)) throw new ProblemException(400, "Say why the Play returns to open. The reason is kept with the change.");
        return unitOfWork.ExecuteAsync(() => plays.WithPlayLockAsync(playId, async () =>
        {
            var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
            if (play.Version != request.ExpectedVersion) throw new ProblemException(409, StalePlay);
            if (play.Status != PlayStatus.Unresolved) throw new ProblemException(409, "Only an unresolved Play returns to open.");
            // The return keeps the reason of the declaration it undoes, bound here under the Play lock.
            var undone = PlayUnresolved.Current(await plays.StatusChangesAsync(play.Id, ct), play.EndedAtUtc)?.UnresolvedReason;
            var (change, cleared) = play.ReturnToOpen(request.Note, time.GetUtcNow(), undone);
            plays.Add(change);
            if (cleared is not null) plays.Add(cleared);
            // Its linked orders are watched again from its next end, never under the declaration it leaves.
            await store.ForgetWatchesAsync(play.Id, ct);
            return PlayService.ToDto(play);
        }, ct), ct);
    }

    /// <summary>
    /// The Play's execution facts as the execution view reads them: entered, exited, the signed difference, its linked
    /// orders with their filled quantity, and the integrity problems. Null when it has no imported execution.
    /// </summary>
    private async Task<UnresolvedEvidenceDto?> EvidenceAsync(Play play, CancellationToken ct)
    {
        var links = (await store.LinksAsync(play.AccountId, new HashSet<Guid>(), new HashSet<Guid> { play.Id }, ct))
            .Where(l => l.PlayId == play.Id && l.State == OrderLinkState.Linked).ToList();
        var orders = (await store.OrdersAsync(play.AccountId, links.Select(l => l.ImportedOrderId).ToHashSet(), NoContracts, ct)).ToDictionary(o => o.Id);
        var fills = FillsByOrder(orders.Values, await store.FillsAsync(play.AccountId, orders.Values.Select(OrderKey.Of).ToList(), ct));
        var settlement = Settle(PlayDocuments.Read(play.Plan), links, orders, fills);
        if (settlement.All.Count == 0) return null;
        return new(settlement.Entered.ToString(), settlement.Exited.ToString(), (settlement.Entered - settlement.Exited).ToString(), links.Count,
            links.OrderBy(l => l.Role).ThenBy(l => l.EntryId).ThenBy(l => l.LevelId).ThenBy(l => External(orders, l), StringComparer.Ordinal)
                .Take(PlayUnresolved.MaxEvidenceOrders).Select(l => new UnresolvedOrderDto(External(orders, l), Name(l.Role), l.EntryId, l.LevelId,
                    orders.TryGetValue(l.ImportedOrderId, out var order) ? order.Status : "unknown",
                    ExecutionIntegrity.Quantity(fills[l.ImportedOrderId]).ToString())).ToList(),
            settlement.Problems.Take(PlayUnresolved.MaxEvidenceProblems).ToList());
    }

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> NoContracts = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

    private const string NotLinkable = "Only planned, paused or open plays link orders.";

    /// <summary>
    /// The Play may have closed, been cancelled or revised since this request read it. Membership in the committed
    /// active set decides, under the account lock that fill-driven changes hold; it also reloads the Play and holds it
    /// until the transaction ends, so the owner's status and plan changes wait rather than commit in between.
    /// </summary>
    private async Task RequireActiveAsync(Play play, string message, CancellationToken ct)
    {
        if (!(await store.ActivePlaysAsync(play.AccountId, ct)).Any(p => p.Id == play.Id)) throw new ProblemException(409, message);
    }

    private async Task ReconcileAsync(Account account, VenueOrderReadResult read, IReadOnlyDictionary<string, decimal> ticks, CancellationToken ct)
    {
        // Unresolved Plays settle from their existing linked orders but never take new links or suggestions.
        var active = (await store.ActivePlaysAsync(account.Id, ct, settling: true))
            .Where(p => p.InstrumentSource == InstrumentSource.Venue && p.ContractId is not null).ToList();
        if (active.Count == 0) return;
        var since = active.GroupBy(p => p.ContractId!).ToDictionary(g => g.Key, g => g.Min(p => p.CreatedAtUtc), StringComparer.Ordinal);
        await store.UpsertOrdersAsync(account, read.Orders.Where(o => o.Status != VenueOrderStatus.Rejected &&
            since.TryGetValue(o.ContractId, out var from) && o.PlacedAtUtc >= from).ToList(), read.ObservedAtUtc, ct);

        // An entry fill opens a play, which can make its exit orders matchable, so repeat while statuses move.
        for (var pass = 0; pass < 3; pass++)
        {
            var before = active.Select(p => p.Status).ToList();
            await MatchAsync(account, active.Where(ExecutionMatcher.IsActive).ToList(), ticks, ct);
            await ApplyTransitionsAsync(account.Id, active, ct);
            await store.SaveAsync(ct);
            if (before.SequenceEqual(active.Select(p => p.Status))) break;
        }
    }

    private async Task MatchAsync(Account account, List<Play> active, IReadOnlyDictionary<string, decimal> ticks, CancellationToken ct)
    {
        // An order can only be a candidate of an active venue Play on its contract placed at or after the Play was created
        // (ExecutionMatcher.Candidates), so other orders are not read. The matcher still applies each Play's own rule.
        var since = active.Where(p => p.InstrumentSource == InstrumentSource.Venue && p.ContractId is not null)
            .GroupBy(p => p.ContractId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(p => p.CreatedAtUtc), StringComparer.Ordinal);
        var orders = await store.OrdersAsync(account.Id, new HashSet<Guid>(), since, ct);
        var candidateIds = orders.Select(o => o.Id).ToHashSet();
        var activeIds = active.Select(p => p.Id).ToHashSet();
        // Links on the candidate orders for any Play (one linked to a closed Play still counts as linked), plus the active Plays' own.
        var links = await store.LinksAsync(account.Id, candidateIds, activeIds, ct);
        // A linked entry may predate its Play; its retained order's fills still date the first entry fill.
        var earlier = links.Where(l => activeIds.Contains(l.PlayId) && l.State == OrderLinkState.Linked && l.Role == OrderLinkRole.Entry &&
            !candidateIds.Contains(l.ImportedOrderId)).Select(l => l.ImportedOrderId).ToHashSet();
        var retained = earlier.Count == 0 ? [] : await store.OrdersAsync(account.Id, earlier, NoContracts, ct);
        var known = orders.Concat(retained).ToList();
        var fills = FillsByOrder(known, await store.FillsAsync(account.Id, known.Select(OrderKey.Of).ToList(), ct));
        var candidatesPlays = active.Select(p => (Play: p, Plan: PlayDocuments.Read(p.Plan))).ToList();
        var firstEntryFill = candidatesPlays.ToDictionary(p => p.Play.Id, p => links
            .Where(l => l.PlayId == p.Play.Id && l.State == OrderLinkState.Linked && l.Role == OrderLinkRole.Entry)
            .SelectMany(l => fills[l.ImportedOrderId]).Where(f => !ExecutionIntegrity.Contradicts(OrderLinkRole.Entry, ExecutionIntegrity.EntrySide(p.Plan), f))
            .Select(f => (DateTimeOffset?)f.OccurredAtUtc).Min());
        var index = new LinkIndex(links);
        var now = time.GetUtcNow();
        foreach (var order in orders.OrderBy(o => o.PlacedAtUtc))
        {
            if (order.Status == "rejected") continue;
            if (index.IsLinked(order.Id)) continue;
            // A cancelled order that never filled cannot affect a play.
            if (order.Status == "canceled" && !fills[order.Id].Any()) continue;
            var closes = fills[order.Id].Any(f => ExecutionFacts.Closes(f.PositionEffect));
            var candidates = ExecutionMatcher.Candidates(order, candidatesPlays,
                (play, key) => index.IsDismissed(play.Id, order.Id, key),
                closes, play => firstEntryFill.GetValueOrDefault(play.Id),
                ticks.TryGetValue(order.ContractId, out var tick) ? tick : null, fills[order.Id].ToList());
            if (candidates.Count == 1)
            {
                var (play, level) = (candidates[0].Play, candidates[0].Level);
                var existing = index.Existing(play.Id, order.Id, level.Key);
                if (existing is not null)
                {
                    existing.LinkAutomatically(now);
                    index.MarkLinked(existing);
                }
                else
                {
                    var link = new PlayOrderLink(play, order, level.Role, level.EntryId, level.LevelId, OrderLinkState.Linked, OrderLinkSource.Automatic, now);
                    store.Add(link);
                    index.Add(link);
                }
                continue;
            }
            foreach (var candidate in candidates)
            {
                if (index.Existing(candidate.Play.Id, order.Id, candidate.Level.Key) is not null) continue;
                var link = new PlayOrderLink(candidate.Play, order, candidate.Level.Role, candidate.Level.EntryId, candidate.Level.LevelId,
                    OrderLinkState.Suggested, OrderLinkSource.Automatic, now);
                store.Add(link);
                index.Add(link);
            }
        }
        await store.SaveAsync(ct);
    }

    /// <summary>
    /// Hash lookups over the loaded links, kept in step as links are added or become linked. Rows are not unique per
    /// (Play, order, level): <see cref="Existing"/> is the first in load order, and a level is dismissed if any of its rows is.
    /// </summary>
    private sealed class LinkIndex
    {
        private readonly HashSet<Guid> linked = [];
        private readonly Dictionary<(Guid PlayId, Guid ImportedOrderId, string LevelKey), PlayOrderLink> first = [];
        private readonly HashSet<(Guid PlayId, Guid ImportedOrderId, string LevelKey)> dismissed = [];
        private readonly HashSet<(Guid PlayId, Guid ImportedOrderId)> rejected = [];

        public LinkIndex(IEnumerable<PlayOrderLink> links)
        {
            foreach (var link in links) Add(link);
        }

        public void Add(PlayOrderLink link)
        {
            var key = (link.PlayId, link.ImportedOrderId, link.LevelKey);
            first.TryAdd(key, link);
            if (link.State == OrderLinkState.Linked) linked.Add(link.ImportedOrderId);
            if (link.State == OrderLinkState.Dismissed) dismissed.Add(key);
            if (link.State == OrderLinkState.Rejected) rejected.Add((link.PlayId, link.ImportedOrderId));
        }

        public void MarkLinked(PlayOrderLink link) => linked.Add(link.ImportedOrderId);
        public bool IsLinked(Guid orderId) => linked.Contains(orderId);
        /// <summary>Declined for that level, or rejected for the whole Play.</summary>
        public bool IsDismissed(Guid playId, Guid orderId, string levelKey) =>
            dismissed.Contains((playId, orderId, levelKey)) || rejected.Contains((playId, orderId));
        public PlayOrderLink? Existing(Guid playId, Guid orderId, string levelKey) => first.GetValueOrDefault((playId, orderId, levelKey));
    }

    /// <summary>
    /// Planned or paused → Open on the first linked entry fill; Open or Unresolved → Closed once linked exits equal the
    /// entries and nothing is known missing, contradictory or out of order. Closed is final, so it waits until the fills
    /// settle the play. An Unresolved Play closes by the same rule and keeps its declaration in history.
    /// </summary>
    private async Task ApplyTransitionsAsync(Guid accountId, IReadOnlyList<Play> targets, CancellationToken ct)
    {
        // Only the targets' linked orders and their fills settle them.
        var links = (await store.LinksAsync(accountId, new HashSet<Guid>(), targets.Select(p => p.Id).ToHashSet(), ct))
            .Where(l => l.State == OrderLinkState.Linked).ToList();
        var linkedIds = links.Select(l => l.ImportedOrderId).ToHashSet();
        var orders = (await store.OrdersAsync(accountId, linkedIds, NoContracts, ct)).ToDictionary(o => o.Id);
        var fills = FillsByOrder(orders.Values, await store.FillsAsync(accountId, orders.Values.Select(OrderKey.Of).ToList(), ct));
        var now = time.GetUtcNow();
        foreach (var play in targets)
        {
            var own = links.Where(l => l.PlayId == play.Id).ToList();
            var settlement = Settle(PlayDocuments.Read(play.Plan), own, orders, fills);
            var entryFills = settlement.Counted.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill).OrderBy(f => f.OccurredAtUtc).ToList();
            var entered = settlement.Entered;
            if (play.Status is PlayStatus.Planned or PlayStatus.Paused && entered.Units.Sign > 0)
            {
                var first = entryFills[0];
                store.Add(play.MarkOpen(
                    $"Entry fill of {ExactDecimal.Format(first.Quantity)} at {ExactDecimal.Format(first.Price)} on order {first.ExternalOrderId}.", now));
            }
            var resting = own.Any(l => l.Role == OrderLinkRole.Entry && orders.TryGetValue(l.ImportedOrderId, out var o) && o.Status == "open");
            var settled = entered.Units.Sign > 0 && settlement.Exited == entered && settlement.Problems.Count == 0 && !resting;
            if (play.Status == PlayStatus.Open && settled)
                store.Add(play.MarkClosed($"Linked exits covered the {entered} entered.", now));
            else if (play.Status == PlayStatus.Unresolved && settled)
            {
                var (change, cleared) = play.SettleUnresolved($"Linked exits covered the {entered} entered.", now);
                store.Add(change);
                if (cleared is not null) plays.Add(cleared);
            }
        }
    }

    /// <summary>
    /// The linked fills that count for a Play, with the reasons they cannot settle it yet. <see cref="All"/> keeps every
    /// linked fill and <see cref="Incomplete"/> the orders known to be missing executions, as sizing and Review read them.
    /// </summary>
    private sealed record Settlement(string EntrySide, List<LinkedFill> Counted, List<string> Problems)
    {
        public List<LinkedFill> All { get; init; } = [];
        public List<string> Incomplete { get; init; } = [];
        public ExactAmount Entered => ExecutionIntegrity.Quantity(Counted.Where(f => f.Role == OrderLinkRole.Entry).Select(f => f.Fill));
        public ExactAmount Exited => ExecutionIntegrity.Quantity(Counted.Where(f => f.Role != OrderLinkRole.Entry).Select(f => f.Fill));
    }

    /// <summary>Fills that contradict their role stay visible on their links but count toward neither side.</summary>
    private static Settlement Settle(PlayPlanDocument plan, IReadOnlyCollection<PlayOrderLink> linked,
        Dictionary<Guid, ImportedOrder> orders, ILookup<Guid, ImportedFill> fills, bool retrospective = false)
    {
        var side = ExecutionIntegrity.EntrySide(plan);
        var all = linked.SelectMany(l => fills[l.ImportedOrderId].Select(f => new LinkedFill(l.Role, f))).ToList();
        var incomplete = linked.Where(l => orders.TryGetValue(l.ImportedOrderId, out var order) &&
            ExecutionIntegrity.MissingExecutions(order, fills[l.ImportedOrderId])).Select(l => orders[l.ImportedOrderId].ExternalOrderId).ToList();
        var problems = ExecutionIntegrity.Problems(side, all, incomplete);
        if (!retrospective)
        {
            var levels = ExecutionMatcher.Levels(plan).Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(linked.Where(l => l.Role != OrderLinkRole.Exit && !levels.Contains(l.LevelKey))
                .Select(l => $"Order {External(orders, l)} references a plan entry or level that no longer exists; review its association."));
        }
        return new(side, all.Where(f => !ExecutionIntegrity.Contradicts(f.Role, side, f.Fill)).ToList(), problems)
        {
            All = all, Incomplete = incomplete
        };
    }

    /// <summary>Each order's fills by its internal ID, through the shared <see cref="OrderKey"/> rule.</summary>
    private static ILookup<Guid, ImportedFill> FillsByOrder(IEnumerable<ImportedOrder> orders, IEnumerable<ImportedFill> fills)
    {
        var byKey = fills.ToLookup(OrderKey.Of);
        return orders.DistinctBy(o => o.Id).SelectMany(o => byKey[OrderKey.Of(o)].Select(f => (o.Id, Fill: f))).ToLookup(x => x.Id, x => x.Fill);
    }

    /// <summary>The venue's order ID of a link, for ordering and the owner's messages; empty if the order was not loaded.</summary>
    private static string External(Dictionary<Guid, ImportedOrder> orders, PlayOrderLink link) =>
        orders.TryGetValue(link.ImportedOrderId, out var order) ? order.ExternalOrderId : "";

    /// <summary>Why the play cannot be tracked at the venue, or null when it can.</summary>
    public static string? UntrackedReason(Play play, Account? account, VenueDescriptor? venue) =>
        play.Status == PlayStatus.Draft ? "Plan the play to start tracking venue orders."
        : play.Status is PlayStatus.Closed or PlayStatus.Cancelled ? null
        : play.InstrumentSource != InstrumentSource.Venue || play.ContractId is null ? "Manual instruments are not tracked at a venue."
        : account is null || venue is not { Capabilities.Orders: true } ? "Orders are only tracked at venues that provide them."
        : !account.IsEnabled ? "Enable the account to track its orders."
        : account.SourceId is null ? "The account needs a source identity to track its orders."
        : null;

    /// <summary>The status path checks credential metadata only, without the account lock or decryption.</summary>
    private async Task<string?> CredentialReasonAsync(Account account, CancellationToken ct) =>
        venues.Descriptor(account.VenueId)?.Capabilities.ReadOnlyCredential == true &&
        (credentials is null || !await credentials.IsUsableAsync(account, ct))
            ? "Add or renew a read-only token to track orders." : null;

    /// <summary>The basis shared by every fill, "mixed" if they differ, or gross when there are none.</summary>
    private static string PnlBasis(List<ImportedFill> fills) =>
        fills.Select(f => f.PnlBasis).Distinct().ToList() is [var only] ? only : fills.Count == 0 ? ExecutionFacts.PnlGross : "mixed";

    private static string Name<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();

    /// <summary>Every retained link of a real Play, in any state, in creation order (Phase 2, item 5c).</summary>
    public async Task<IReadOnlyList<LinkRecordDto>> LinkRecordAsync(Guid playId, CancellationToken ct)
    {
        var play = await plays.FindAsync(playId, ct) ?? throw new ProblemException(404, "Play not found.");
        if (play.TradingMode != "real") throw new ProblemException(404, "Play not found.");
        var links = await store.PlayLinksAsync(play.AccountId, play.Id, ct);
        var orders = (await store.OrdersAsync(play.AccountId, links.Select(l => l.ImportedOrderId).ToHashSet(),
            new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal), ct)).ToDictionary(o => o.Id);
        return links.OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).Select(l => new LinkRecordDto(l.Id, Name(l.Role), l.EntryId, l.LevelId,
            Name(l.State), Name(l.Source), orders.GetValueOrDefault(l.ImportedOrderId)?.ExternalOrderId,
            orders.GetValueOrDefault(l.ImportedOrderId)?.ContractId, l.CreatedAtUtc, l.UpdatedAtUtc)).ToList();
    }

    private static ExecutionOrderDto OrderDto(ImportedOrder o) => new(o.ExternalOrderId, o.Side, o.OrderType, ExactDecimal.Format(o.LimitPrice),
        ExactDecimal.Format(o.TriggerPrice), o.ReduceOnly, o.IsPositionTpsl, ExactDecimal.Format(o.OriginalSize),
        ExactDecimal.Format(o.RemainingSize), o.PlacedAtUtc, o.Status, o.VenueStatus, o.StatusAtUtc);

    private static ExecutionFillDto FillDto(ImportedFill f) => new(f.SourceFillId, f.Direction, ExactDecimal.Format(f.Price),
        ExactDecimal.Format(f.Quantity), ExactDecimal.Format(f.Fee), f.FeeToken, ExactDecimal.Format(f.ClosedPnlUsd), f.OccurredAtUtc, f.Side,
        f.PositionEffect, f.PnlBasis);
}
