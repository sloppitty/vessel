using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Infrastructure.Venues.Lighter;
using Vessel.Infrastructure.Venues.Risex;

namespace Vessel.Tests;

internal sealed class AccountHistoryOrders(string venueId = "hyperliquid") : IVenueOrderReader
{
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
    public string VenueId => venueId;
    public int Reads { get; private set; }
    public Account? Account { get; private set; }
    public Exception? Failure { get; set; }
    public Action? AfterRead { get; set; }
    public Func<CancellationToken, Task>? WaitForRead { get; set; }
    public VenueOrderReadResult Result { get; set; } = new(
        [Order("entry", "buy", false), Order("exit", "sell", true)], At.AddDays(1), "Bounded recent order history only; no full-history backfill.");
    private static VenueOrder Order(string id, string side, bool reduceOnly) =>
        new(id, "BTC", side, "limit", reduceOnly ? 110 : 100, null, reduceOnly, false, 1, 0,
            At.AddSeconds(reduceOnly ? 59 : -1), VenueOrderStatus.Filled, "filled", At.AddSeconds(reduceOnly ? 60 : 0), "BTC");
    public Task<VenueOrderReadResult> ReadOrdersAsync(string source, CancellationToken ct) => throw new NotSupportedException("Use account-aware reads.");
    public async Task<VenueOrderReadResult> ReadOrdersAsync(Account account, CancellationToken ct)
    {
        Reads++; Account = account; ct.ThrowIfCancellationRequested(); AfterRead?.Invoke();
        if (WaitForRead is not null) await WaitForRead(ct);
        if (Failure is not null) throw Failure;
        return Result;
    }
    internal static VenueDescriptor Descriptor(string venue) => venue switch
    {
        "lighter" => LighterReader.Descriptor,
        "lighter-rh" => LighterExchange.Robinhood.Descriptor,
        "risex" => RisexReader.Descriptor,
        _ => HyperliquidPerpetualReader.Descriptor
    };
    internal static VenueRegistry Registry(FixtureReader reader, AccountHistoryOrders orders) =>
        new([VenueDescriptor.Manual, Descriptor(reader.VenueId)], [reader], [orders], [], [], [], [], []);
    internal static FixtureReader PublicReader(string venue = "hyperliquid")
    {
        var reader = new FixtureReader { VenueId = venue };
        reader.Result = reader.Result with
        {
            Snapshot = new(At.AddDays(1), "synthetic-perpetuals", 1000, 900, 0, []),
            Fills =
            [
                new("entry-fill", "BTC", "buy", "Open Long", 100, 1, 0, "USDC", 0, At, "entry", "", "{}", ExecutionFacts.Open, VenueContractId: "BTC"),
                new("exit-fill", "BTC", "sell", "Close Long", 110, 1, 0, "USDC", 10, At.AddSeconds(60), "exit", "", "{}", ExecutionFacts.Close, VenueContractId: "BTC")
            ],
            HistoryNotice = "Bounded recent public fills only."
        };
        return reader;
    }
}
