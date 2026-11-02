using System.Net;

namespace Vessel.Tests;

public sealed class VesselEventApiTests
{
    [Theory]
    [InlineData("/api/events")]
    [InlineData("/api/events/stream")]
    public async Task Event_reads_require_auth_before_resolving_database(string route)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
    }
}
