using System.Net;

namespace Vessel.Tests;

public sealed class NotificationApiHostTests
{
    [Theory]
    [InlineData("GET", "/api/notifications")]
    [InlineData("GET", "/api/notifications/head")]
    [InlineData("POST", "/api/notifications/claim")]
    [InlineData("POST", "/api/notifications/read")]
    [InlineData("GET", "/api/notifications/preferences")]
    [InlineData("POST", "/api/notifications/test")]
    public async Task Notification_routes_require_auth_before_resolving_the_database(string method, string route)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route))).StatusCode);
    }
}
