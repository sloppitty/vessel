using System.Text.Json;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;

namespace Vessel.Tests;
internal static class ConditionKindsTestSupport
{

    /// <summary>The fixture with the kind's condition present, at evidence version 0.</summary>
    internal static DiscrepancyFixture Arrange(string kind)
    {
        var f = new DiscrepancyFixture();
        Evidence(f, kind, 0);
        return f;
    }

    /// <summary>
    /// Sets the kind's condition with evidence version <paramref name="version"/>; each version has other quantities, so
    /// another fingerprint. For the protection kinds the Play's open quantity (and the same venue position) is the
    /// version's <see cref="Value"/>, with no resting stop or with a stop of 2; for the position kinds the venue position is.
    /// </summary>
    internal static void Evidence(DiscrepancyFixture f, string kind, int version)
    {
        switch (kind)
        {
            case "protection.missing":
                f.VenueOrders.RemoveAll(x => x.OrderId == "stop");
                Quantity(f, Value(kind, version));
                break;
            case "protection.excess":
                Quantity(f, Value(kind, version));
                f.Stop(o => o with { RemainingSize = 2, ReduceOnly = false });
                break;
            default: f.Position = Value(kind, version); break;
        }
    }

    /// <summary>Clears the kind's condition, so the next fresh observation proves it absent.</summary>
    internal static void Clear(DiscrepancyFixture f, string kind)
    {
        var quantity = 1 + f.Store.Fills.Where(Scaled).Sum(x => x.Quantity);
        switch (kind)
        {
            case "protection.missing":
                f.VenueOrders.Add(new("stop", "BTC", "sell", "Stop Market", 90, 90, true, false, quantity, quantity, f.Clock.Now.AddMinutes(-15),
                    VenueOrderStatus.Open, "open", f.Clock.Now));
                break;
            case "protection.excess": f.Stop(o => o with { RemainingSize = quantity, ReduceOnly = true }); break;
            default: f.Position = 1; break;
        }
    }

    /// <summary>The quantity shown for evidence version <paramref name="version"/>.</summary>
    internal static string Latest(string kind, int version) => ExactAmount.From(Value(kind, version)).ToString();
    internal static string? Shown(string kind, JsonElement details) =>
        details.GetProperty(kind.StartsWith("position.", StringComparison.Ordinal) ? "venueQuantity" : "quantity").GetString();
    internal static string Severity(string kind) => kind == "protection.missing" ? "critical" : "warning";

    internal static decimal Value(string kind, int version) => kind switch
    {
        "position.short" => .5m + .1m * version,
        "position.excess" => 2 + .5m * version,
        _ => 1 + .2m * version
    };

    /// <summary>
    /// The Play's open quantity, and the same venue position, through entry fills on its linked entry order. A larger
    /// quantity only adds a fill, so persisted fixtures can follow it.
    /// </summary>
    internal static void Quantity(DiscrepancyFixture f, decimal quantity)
    {
        var current = 1 + f.Store.Fills.Where(Scaled).Sum(x => x.Quantity);
        if (quantity < current) { f.Store.Fills.RemoveAll(Scaled); current = 1; }
        if (quantity > current) f.Fill("scale-" + f.Store.Fills.Count, "entry", quantity - current, f.Clock.Now.AddSeconds(-1));
        f.Position = quantity;
    }

    internal static bool Scaled(Vessel.Domain.Workspace.ImportedFill fill) => fill.SourceFillId.StartsWith("scale-", StringComparison.Ordinal);

    /// <summary>
    /// Evidence the kind cannot decide: a history gap for every kind, or an incomplete read of what it rests on. An
    /// incomplete book still shows an oversized resting order (positive evidence), so the excess uses incomplete facts.
    /// </summary>
    internal static void Unknown(DiscrepancyFixture f, string kind, string interruption, bool on)
    {
        if (interruption == "history-gap") { f.HistoryGap = on; return; }
        switch (kind)
        {
            case "protection.missing": f.OrdersComplete = !on; break;
            case "protection.excess": f.Store.Complete = !on; break;
            default: f.PositionsComplete = !on; break;
        }
    }
}
