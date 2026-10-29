
namespace Vessel.Tests;

public partial class MonitorDiscrepancyRound5TestsSupport
{
    internal const int Lifetime = 7 * 24 * 60 * 60;
    internal static DiscrepancyFixture Untracked()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        return f;
    }
}
