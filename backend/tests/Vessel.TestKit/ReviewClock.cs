
namespace Vessel.Tests;

internal sealed class ReviewClock : TimeProvider
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}
