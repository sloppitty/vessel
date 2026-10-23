using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Domain.Workspace;
using Vessel.Domain.Numerics;

namespace Vessel.Infrastructure.Venues.Pacifica;

public sealed partial class PacificaReader
{
    public Task<VenueOrderReadResult> ReadOrdersAsync(string sourceId, CancellationToken cancellationToken)
    {
        var source = Address(sourceId);
        return http.ReadBoundedAsync(async ct =>
        {
            var catalogue = await CatalogueAsync(ct, refresh: true);
            // History first, then a separate current read: historical "open" does not assert a resting order.
            var (history, more) = await Pages("/api/v1/orders/history", source, ct);
            var orders = new Dictionary<string, VenueOrder>(StringComparer.Ordinal);
            var skipped = 0;
            foreach (var row in Unique(history, "order_id"))
            {
                var order = ReadOrder(row, catalogue, current: false);
                if (order is null) skipped++;
                else orders.Add(order.OrderId, order);
            }
            using var current = await Get($"/api/v1/orders?account={source}", ct);
            foreach (var row in Unique(Rows(Data(current)).EnumerateArray(), "order_id"))
            {
                var order = ReadOrder(row, catalogue, current: true);
                if (order is null) { skipped++; continue; }
                if (orders.TryGetValue(order.OrderId, out var old))
                {
                    if (old.VenueContractId != order.VenueContractId || old.Side != order.Side ||
                        old.PlacedAtUtc != order.PlacedAtUtc) throw Json.Invalid();
                    if (old.StatusAtUtc > order.StatusAtUtc) continue;
                    if (old.StatusAtUtc == order.StatusAtUtc && old.Status != order.Status) throw Json.Invalid();
                }
                orders[order.OrderId] = order;
            }
            // TWAP/strategy parents and zero-size whole-position triggers need a separate attribution contract.
            // A partial current read never proves absence/cancellation/missing protection.
            return new VenueOrderReadResult(orders.Values.ToList(), time.GetUtcNow(),
                $"Pacifica: current ordinary/stop orders plus at most 1,000 historical orders{(more ? " (history capped)" : "")}. " +
                $"Omitted order records: {skipped}. Current book is partial: TWAP/strategy parents and unsupported whole-position triggers are not covered.",
                OpenOrdersComplete: false);
        }, cancellationToken);
    }

    private VenueOrder? ReadOrder(JsonElement row, Catalogue catalogue, bool current)
    {
        var symbol = Text(row, "symbol", 32);
        if (catalogue.Native(symbol) is not { } market) return null;
        var side = Text(row, "side", 8) switch { "bid" => ExecutionFacts.Buy, "ask" => ExecutionFacts.Sell, _ => throw Json.Invalid() };
        var type = Text(row, "order_type", 32);
        var triggered = type is "stop_limit" or "stop_market" or "take_profit_limit" or "stop_loss_limit" or "take_profit_market" or "stop_loss_market";
        if (!triggered && type is not ("limit" or "market")) return null;
        var amount = Nonnegative(row, current ? "initial_amount" : "amount");
        if (amount == 0) return null;
        var filled = Nonnegative(row, "filled_amount");
        var cancelled = current ? Nonnegative(row, "cancelled_amount") : 0;
        if (filled > amount) throw Json.Invalid();
        var remaining = Difference(amount, filled);
        if (cancelled > remaining) throw Json.Invalid();
        remaining = Difference(remaining, cancelled);
        var statusText = current ? "open" : Text(row, "order_status", 32);
        var status = statusText switch
        {
            "open" or "partially_filled" => VenueOrderStatus.Open,
            "filled" => VenueOrderStatus.Filled,
            "cancelled" => VenueOrderStatus.Canceled,
            "rejected" => VenueOrderStatus.Rejected,
            _ => VenueOrderStatus.Other
        };
        var stop = Json.OptionalAmount(row, "stop_price");
        if (triggered && stop is not > 0) return null;
        var placed = At(row, "created_at"); var updated = At(row, "updated_at");
        if (updated < placed) throw Json.Invalid();
        return new(Identity(Json.Property(row, "order_id")), market.ContractId, side, type,
            Nonnegative(row, current ? "price" : "initial_price"), triggered ? stop : null,
            Json.Boolean(Json.Property(row, "reduce_only")), false, amount,
            status == VenueOrderStatus.Open ? remaining : 0, placed, status, statusText, updated, symbol);
    }

    private static decimal Difference(decimal first, decimal second)
    {
        var difference = first - second;
        return ExactDecimal.Units(difference) == ExactDecimal.Units(first) - ExactDecimal.Units(second)
            ? difference : throw Json.Invalid();
    }
}
