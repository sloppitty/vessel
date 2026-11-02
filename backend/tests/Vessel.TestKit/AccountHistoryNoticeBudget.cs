using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

internal static class AccountHistoryNoticeBudget
{
    public static string LighterCappedOrders => Capped(LighterExchange.Mainnet);

    public static string Capped(LighterExchange exchange) => exchange.OrdersNotice + LighterReader.OrdersPageLimitNotice;

    public static string LighterPublic(string credentialNotice, string terminalNotice, int missingPnl = 500, int fees = 500, int selfTrades = 500,
        int forced = 500) =>
        Public(LighterExchange.Mainnet, credentialNotice, terminalNotice, missingPnl, fees, selfTrades, forced);

    public static string Public(LighterExchange exchange, string credentialNotice, string terminalNotice,
        int missingPnl = 500, int fees = 500, int selfTrades = 500, int forced = 500) =>
        string.Join(" ", new[] { exchange.HistoryNotice, credentialNotice, terminalNotice,
            LighterReader.OmissionNotice(missingPnl, fees, forced, forced, forced, selfTrades) }
            .Where(text => !string.IsNullOrEmpty(text)));

    public static IEnumerable<(string Venue, string Public, string Orders)> CombinedCases()
    {
        // A refused bind clears the token, so it cannot also cause an authenticated-page retry.
        // A failed page clears the cursor, so unavailable history and the page cap cannot coexist.
        // Counters share at most 500 rows; use the maximum three-digit width for each conservatively.
        // Every Lighter exchange is checked: a longer exchange name must still fit.
        foreach (var exchange in LighterExchange.All)
        {
            foreach (var credential in new[] { "", LighterReader.CredentialRefusedNotice, LighterReader.HistoryRetryNotice })
            foreach (var terminal in new[] { "", exchange.PageLimitNotice, LighterReader.HistoryUnavailableNotice })
                yield return (exchange.VenueId, Public(exchange, credential, terminal), Capped(exchange));
            yield return (exchange.VenueId, string.Join(" ", exchange.HistoryNotice, LighterReader.NonzeroRateNotice), Capped(exchange));
        }
        yield return ("hyperliquid", Literal(typeof(HyperliquidPerpetualReader), "HistoryNotice"), Literal(typeof(HyperliquidPerpetualReader), "OrdersNotice"));
        yield return ("risex", Literal(typeof(RisexReader), "HistoryNotice"), Literal(typeof(RisexReader), "OrdersNotice"));
    }

    private static string Literal(Type type, string name) => (string)type.GetField(name,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
}
