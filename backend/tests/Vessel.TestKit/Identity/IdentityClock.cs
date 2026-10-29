
namespace Vessel.Tests.Identity;

internal sealed class IdentityClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public override DateTimeOffset GetUtcNow() => Now;
}
