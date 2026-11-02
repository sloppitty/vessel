
namespace Vessel.Tests;

public partial class VenueRateLimitTestsSupport
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
