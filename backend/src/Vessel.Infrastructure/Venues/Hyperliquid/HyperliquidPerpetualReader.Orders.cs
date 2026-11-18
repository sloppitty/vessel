using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Infrastructure.Venues.Hyperliquid;

// frontendOpenOrders is the current book; historicalOrders holds the latest 2,000 status updates,
// several per order. Shapes observed live on October 3, 2026.
public sealed partial class HyperliquidPerpetualReader : IVenueOrderReader
{
    private const int MaxOrders = 2000;
    private const string OrdersNotice =
        "Primary perpetual orders only. Order history is the latest 2,000 status updates; older orders are not available.";
    private const string BuilderOrdersNotice =
        "Perpetual orders. Order history is the latest 2,000 status updates; older orders are not available.";

    public Task<VenueOrderReadResult> ReadOrdersAsync(string sourceId, CancellationToken cancellationToken)
    {
        var publicAddress = EvmAddress.Require(sourceId);
        var latestTimestamp = Math.Min(253402300799999L, timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + 300000);
        return ReadBoundedAsync(async token =>
        {
            var catalogue = await CatalogueAsync(token, refresh: true);
            var contracts = catalogue.All.Select(i => i.VenueContractId ?? i.ContractId).ToHashSet(StringComparer.Ordinal);
            using var history = await ReadJsonAsync(new { type = "historicalOrders", user = publicAddress }, token);
            using var open = await ReadJsonAsync(new { type = "frontendOpenOrders", user = publicAddress, dex = "" }, token);
            // Keyed by (canonical contract, oid): the same venue ID on another contract is another order.
            var orders = new Dictionary<(string ContractId, string OrderId), VenueOrder>();
            var updates = Array(history.RootElement);
            if (updates.GetArrayLength() > MaxOrders) throw new VenueReadException(InvalidResponse);
            foreach (var update in updates.EnumerateArray())
            {
                var raw = Text(Property(update, "status"), 64);
                var order = ReadOrder(Property(update, "order"), contracts, latestTimestamp, raw, Normalize(raw),
                    Timestamp(Property(update, "statusTimestamp"), latestTimestamp));
                if (order is not null && (!orders.TryGetValue((order.ContractId, order.OrderId), out var known) || order.StatusAtUtc >= known.StatusAtUtc))
                    orders[(order.ContractId, order.OrderId)] = order;
            }
            // Unlike history, the open book is per DEX.
            var books = new List<JsonDocument> { open };
            try
            {
                foreach (var dex in catalogue.Dexes)
                    books.Add(await ReadJsonAsync(new { type = "frontendOpenOrders", user = publicAddress, dex }, token));
                // Whole-read conditions. #95: an order with attached children names its own contract instead.
                var openOrdersComplete = !catalogue.BuildersUnavailable;
                var incomplete = new List<VenueIncompleteBook>();
                var openIds = new HashSet<(string ContractId, string OrderId)>();
                var childIds = new List<(string ContractId, string OrderId)>();
                var observed = timeProvider.GetUtcNow();
                foreach (var document in books)
                {
                    var book = Array(document.RootElement);
                    if (book.GetArrayLength() > MaxOrders) throw new VenueReadException(InvalidResponse);
                    // #95: attached children count toward the cap; whether the venue counts them is not documented.
                    var rows = book.GetArrayLength();
                    foreach (var item in book.EnumerateArray())
                    {
                        // The open book is the current truth and overrides an older history entry.
                        var order = ReadOrder(item, contracts, latestTimestamp, "open", VenueOrderStatus.Open, observed);
                        if (order is not null)
                        {
                            if (!openIds.Add((order.ContractId, order.OrderId))) openOrdersComplete = false;
                            orders[(order.ContractId, order.OrderId)] = order;
                        }
                        else if (IsPerpetualContract(Text(Property(item, "coin")))) openOrdersComplete = false;
                        // Nested children are not projected by ReadOrder. Never claim their absence. #95: a read parent
                        // whose every child is a well-formed order on the parent's own market leaves only its contract
                        // unknown; an unread parent, a malformed list or any child that fails that leaves the whole book.
                        if (item.TryGetProperty("children", out var children) &&
                            (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > 0))
                        {
                            var attached = order is not null && children.ValueKind == JsonValueKind.Array
                                ? children.EnumerateArray()
                                    .Select(child => AttachedOn(child, order.VenueContractId ?? order.ContractId, contracts, latestTimestamp, observed))
                                    .ToList()
                                : null;
                            if (attached is not null && attached.All(child => child is not null))
                            {
                                childIds.AddRange(attached.Select(child => (child!.ContractId, child.OrderId)));
                                rows += attached.Count;
                                incomplete.Add(new(order!.ContractId, order.VenueContractId, VenueIncompleteBook.AttachedTpsl));
                            }
                            else openOrdersComplete = false;
                        }
                    }
                    openOrdersComplete &= rows < MaxOrders;
                }
                // #95: a child shares the active-order identity space. Repeating another child, its parent or any
                // top-level order, wherever it appears in the response, is a whole-book condition.
                foreach (var id in childIds)
                    if (!openIds.Add(id)) openOrdersComplete = false;
                return new VenueOrderReadResult(orders.Values.OrderByDescending(o => o.PlacedAtUtc).ToList(), observed,
                    catalogue.Dexes.Count > 0 ? BuilderOrdersNotice : OrdersNotice, OpenOrdersComplete: openOrdersComplete && incomplete.Count == 0)
                {
                    IncompleteContracts = openOrdersComplete ? incomplete.Distinct().ToArray() : null
                };
            }
            finally
            {
                foreach (var document in books.Skip(1)) document.Dispose();
            }
        }, cancellationToken);
    }

    private static VenueOrder? ReadOrder(JsonElement order, HashSet<string> contracts, long latestTimestamp,
        string venueStatus, VenueOrderStatus status, DateTimeOffset statusAt)
    {
        var coin = Text(Property(order, "coin"));
        if (!contracts.Contains(coin)) return null;
        var side = Text(Property(order, "side"));
        if (side is not ("A" or "B")) throw new VenueReadException(InvalidResponse);
        var isTrigger = Property(order, "isTrigger");
        if (isTrigger.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new VenueReadException(InvalidResponse);
        var trigger = isTrigger.GetBoolean() ? Positive(Property(order, "triggerPx")) : (decimal?)null;
        return new VenueOrder(Identity(Property(order, "oid")), HyperliquidInstruments.Canonical(coin), SideOf(side), Text(Property(order, "orderType"), 32),
            Nonnegative(Property(order, "limitPx")), trigger, Flag(order, "reduceOnly"), Flag(order, "isPositionTpsl"),
            Nonnegative(Property(order, "origSz")), Nonnegative(Property(order, "sz")),
            Timestamp(Property(order, "timestamp"), latestTimestamp), status, venueStatus, statusAt,
            VenueContractId: HyperliquidInstruments.Canonical(coin) == coin ? null : coin);
    }

    /// <summary>
    /// #95: the attached child read as an order, when it is one Vessel could read, without children of its own, on the
    /// parent's native market; otherwise null. Only then (and with a unique identity) may it narrow the book's gap to the
    /// parent's contract.
    /// </summary>
    private static VenueOrder? AttachedOn(JsonElement child, string coin, HashSet<string> contracts, long latestTimestamp,
        DateTimeOffset observed)
    {
        if (child.ValueKind != JsonValueKind.Object || !child.TryGetProperty("coin", out var childCoin) ||
            childCoin.ValueKind != JsonValueKind.String || childCoin.GetString() != coin) return null;
        if (child.TryGetProperty("children", out var nested) &&
            (nested.ValueKind != JsonValueKind.Array || nested.GetArrayLength() > 0)) return null;
        try { return ReadOrder(child, contracts, latestTimestamp, "open", VenueOrderStatus.Open, observed); }
        catch (VenueReadException) { return null; }
    }

    private static bool Flag(JsonElement order, string name) =>
        order.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static VenueOrderStatus Normalize(string status) => status switch
    {
        "open" => VenueOrderStatus.Open,
        "filled" => VenueOrderStatus.Filled,
        "triggered" => VenueOrderStatus.Triggered,
        "canceled" or "scheduledCancel" => VenueOrderStatus.Canceled,
        _ when status.EndsWith("Canceled", StringComparison.Ordinal) => VenueOrderStatus.Canceled,
        "rejected" => VenueOrderStatus.Rejected,
        _ when status.EndsWith("Rejected", StringComparison.Ordinal) => VenueOrderStatus.Rejected,
        _ => VenueOrderStatus.Other
    };
}
