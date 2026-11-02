
namespace Vessel.Tests;

public partial class MonitorDiscrepancyRound6TestsSupport
{
    internal static DiscrepancyFixture Untracked()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        f.Account.Configure(null, "0x" + Guid.NewGuid().ToString("N") + "00000000", null);
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        return f;
    }
}
