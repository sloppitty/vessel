using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;
internal static class StopConfirmationTestSupport
{

    /// <summary>Adds an entry fill and the same venue position, and resizes the linked stop.</summary>
    internal static void ScaleIn(DiscrepancyFixture f, decimal added, decimal stop)
    {
        f.Fill("entry-fill-" + f.Store.Fills.Count, "entry", added, f.Clock.Now.AddSeconds(-1));
        f.Position += added;
        f.Stop(o => o with { RemainingSize = stop });
    }
}
