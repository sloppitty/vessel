using System.Globalization;
using System.Text.Json;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Common;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Infrastructure.Venues.Lighter;

public sealed partial class LighterReader
{
    // Mainnet wording for existing callers; each exchange words its own.
    internal static string OrdersNotice => LighterExchange.Mainnet.OrdersNotice;
    internal const string OrdersPageLimitNotice = " The five-page history limit was reached.";

    public Task<VenueOrderReadResult> ReadOrdersAsync(string sourceId, CancellationToken cancellationToken)
    {
        Index(sourceId, venue);
        throw new VenueReadException(venue.TokenRequired);
    }

    public async Task<VenueOrderReadResult> ReadOrdersAsync(Account account, CancellationToken cancellationToken)
    {
        var source = Source(account);
        var attempt = await credentials.ReadAttemptAsync(account.Id, cancellationToken)
            ?? throw new VenueReadException(venue.TokenRequired);
        var credential = attempt.Token;
        try
        {
            return await BoundedAsync(async ct =>
        {
            await BindAsync(source, credential, ct);
            var catalogue = await CatalogueAsync(ct, refresh: true);
            var observed = timeProvider.GetUtcNow();
            // Keyed by (canonical contract, order index): the same venue ID on another market is another order.
            var orders = new Dictionary<(string ContractId, string OrderId), VenueOrder>();
            bool openOrdersComplete;
            IReadOnlyList<VenueIncompleteBook>? incompleteContracts;
            using (var active = await GetAsync("accountActiveOrders?account_index=" + source + "&market_type=perp", ct, credential))
            {
                var skipped = new List<UnreadOrder>();
                var current = ReadOrders(active.RootElement, catalogue, source, observed, 1000, skipped);
                var rows = Rows(json, active.RootElement, "orders", 1000);
                foreach (var order in current)
                    if (!orders.TryAdd((order.ContractId, order.OrderId), order)) throw json.Invalid();
                // #95: an unsupported order narrows to its market only when it passed every check a read order passes
                // (exact ID, an active status, no repeat among all active rows). Any doubt leaves the whole book unknown.
                var seen = orders.Keys.ToHashSet();
                var attributed = true;
                foreach (var unread in skipped)
                    attributed &= unread is { Book: not null, Order: { Status: VenueOrderStatus.Open } order } &&
                        seen.Add((order.ContractId, order.OrderId));
                // Active orders cover all perp markets without a market_id filter. A cursor, cap,
                // omitted unsupported order or unknown active status prevents negative evidence.
                // #95: an attributed unsupported order on a known perp market prevents it only for that market.
                var noNextPage = !active.RootElement.TryGetProperty("next_cursor", out var next) ||
                    next.ValueKind == JsonValueKind.String && next.GetString() == "";
                var whole = rows.GetArrayLength() < 1000 && noNextPage && attributed &&
                    current.Count + skipped.Count == rows.GetArrayLength() && current.All(o => o.Status == VenueOrderStatus.Open);
                openOrdersComplete = whole && skipped.Count == 0;
                incompleteContracts = whole ? skipped.Select(x => x.Book!).Distinct().ToArray() : null;
            }
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < MaxPages; page++)
            {
                using var inactive = await GetAsync("accountInactiveOrders?account_index=" + source + "&market_type=perp&limit=100" +
                    (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), ct, credential);
                foreach (var order in ReadOrders(inactive.RootElement, catalogue, source, observed, PageSize))
                    // A fill/cancel can move an order between the two reads. Keep the later observation.
                    if (!orders.TryGetValue((order.ContractId, order.OrderId), out var previous) || order.StatusAtUtc >= previous.StatusAtUtc)
                        orders[(order.ContractId, order.OrderId)] = order;
                cursor = Cursor(json, inactive.RootElement);
                if (cursor is null) break;
                if (!cursors.Add(cursor)) throw json.Invalid();
            }
            return new VenueOrderReadResult(orders.Values.ToArray(), observed, venue.OrdersNotice +
                (cursor is null ? "" : OrdersPageLimitNotice), OpenOrdersComplete: openOrdersComplete)
                { IncompleteContracts = incompleteContracts };
            }, cancellationToken);
        }
        catch (VenueCredentialRejectedException)
        {
            await credentials.MarkRefusedAsync(account.Id, attempt.Revision, cancellationToken);
            throw;
        }
    }

    private static DateTimeOffset Seconds(StrictJson json, JsonElement row, string name, DateTimeOffset latest)
    {
        var value = json.Property(row, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) ||
            seconds < 0 || seconds > latest.ToUnixTimeSeconds()) throw json.Invalid();
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    // https://apidocs.lighter.xyz/reference/accountactiveorders.md and /reference/accountinactiveorders.md.
    // Authenticated happy-path payloads remain fixture-tested until the owner supplies a read-only token.
    /// <param name="skipped">#95: collects each perp order left out because it cannot be modelled, with its market.</param>
    internal static IReadOnlyList<VenueOrder> ReadOrders(JsonElement root, Catalogue catalogue, string source,
        DateTimeOffset observed, int limit, List<UnreadOrder>? skipped = null)
    {
        var json = catalogue.Exchange.Json;
        var orders = new List<VenueOrder>();
        foreach (var row in Rows(json, root, "orders", limit).EnumerateArray())
        {
            if (Identity(json, json.Property(row, "owner_account_index")) != source) throw json.Invalid();
            var marketId = Identity(json, json.Property(row, "market_index"));
            if (catalogue.SpotIds.Contains(marketId)) continue;
            var market = catalogue.ById(marketId) ?? throw json.Invalid();
            var type = Text(json, row, "type");
            if (type is not ("limit" or "market" or "stop-loss" or "stop-loss-limit" or "take-profit" or
                "take-profit-limit" or "twap" or "twap-sub" or "liquidation")) throw json.Invalid();
            var initial = Nonnegative(json, row, "initial_base_amount");
            var remaining = Nonnegative(json, row, "remaining_base_amount");
            if (remaining > initial) throw json.Invalid();
            var unread = type is "twap" or "twap-sub" ? VenueIncompleteBook.Twap : initial == 0 ? VenueIncompleteBook.Unsized : null;
            bool flagged;
            // Flags are checked on every row, whatever its type or size; malformed flags still fail a modelled row.
            try { flagged = row.TryGetProperty("order_flags", out var flags) && json.Integer(flags) != 0; }
            catch (VenueReadException) when (unread is not null) { flagged = true; }
            if (unread is null && !flagged)
            {
                orders.Add(ReadOrder(json, row, market.Key, marketId, type, initial, remaining, observed));
                continue;
            }
            if (skipped is null) continue;
            // #95: validated like a read order before it may narrow the book to its market. A row that fails, or any
            // flags (whose scope is not documented), leaves the whole book unknown.
            VenueOrder? read;
            try { read = ReadOrder(json, row, market.Key, marketId, type, initial, remaining, observed); }
            catch (VenueReadException) { read = null; }
            skipped.Add(new(read, flagged ? null : new(market.Key, marketId, unread!)));
        }
        return orders;
    }

    private static VenueOrder ReadOrder(StrictJson json, JsonElement row, string contractId, string marketId, string type,
        decimal initial, decimal remaining, DateTimeOffset observed)
    {
        var id = ExactId(json, row, "order_index", "order_id");
        var isAsk = json.Boolean(json.Property(row, "is_ask"));
        var reduceOnly = json.Boolean(json.Property(row, "reduce_only"));
        var trigger = type.StartsWith("stop-loss", StringComparison.Ordinal) || type.StartsWith("take-profit", StringComparison.Ordinal);
        var price = Nonnegative(json, row, "price");
        var triggerPrice = Nonnegative(json, row, "trigger_price");
        if (trigger && triggerPrice == 0 || !trigger && triggerPrice != 0) throw json.Invalid();
        var status = Text(json, row, "status");
        if (status.Any(c => c is not (>= 'a' and <= 'z') && c != '-')) throw json.Invalid();
        var normalized = status switch
        {
            "open" or "in-progress" or "pending" => VenueOrderStatus.Open,
            "filled" => VenueOrderStatus.Filled,
            "canceled" => VenueOrderStatus.Canceled,
            _ when status.StartsWith("canceled-", StringComparison.Ordinal) => VenueOrderStatus.Canceled,
            _ => VenueOrderStatus.Other
        };
        var at = Seconds(json, row, "timestamp", observed.AddMinutes(5));
        // created_at/updated_at units have no official description. Do not guess based on magnitude.
        // timestamp is the venue order time; the local observation is a known upper bound for status time.
        return new(id, contractId, isAsk ? ExecutionFacts.Sell : ExecutionFacts.Buy, type,
            price, trigger ? triggerPrice : null, reduceOnly, false, initial, remaining, at,
            normalized, status, observed, marketId);
    }

    /// <summary>
    /// #95: an active perp order left out because it cannot be modelled. <paramref name="Order"/> is null when the row
    /// failed a check a read order must pass; <paramref name="Book"/> is null when the reason is not market-local.
    /// </summary>
    internal sealed record UnreadOrder(VenueOrder? Order, VenueIncompleteBook? Book);
}
