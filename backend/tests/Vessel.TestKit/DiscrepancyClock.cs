
namespace Vessel.Tests;
internal sealed class DiscrepancyClock : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}
