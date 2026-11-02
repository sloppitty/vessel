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
                var openOrdersComplete = !catalogue.BuildersUnavailable;
                var openIds = new HashSet<(string ContractId, string OrderId)>();
                var observed = timeProvider.GetUtcNow();
                foreach (var document in books)
                {
                    var book = Array(document.RootElement);
                    if (book.GetArrayLength() > MaxOrders) throw new VenueReadException(InvalidResponse);
                    openOrdersComplete &= book.GetArrayLength() < MaxOrders;
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
                        // Nested children are not projected by ReadOrder. Never claim their absence.
                        if (item.TryGetProperty("children", out var children) &&
                            (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > 0))
                            openOrdersComplete = false;
                    }
                }
                return new VenueOrderReadResult(orders.Values.OrderByDescending(o => o.PlacedAtUtc).ToList(), observed,
                    catalogue.Dexes.Count > 0 ? BuilderOrdersNotice : OrdersNotice, OpenOrdersComplete: openOrdersComplete);
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
