namespace Vessel.Tests;

// Value-only tests need the timestamp, not TestKit's TimeProvider fake.
internal static class ReviewClock
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
}
