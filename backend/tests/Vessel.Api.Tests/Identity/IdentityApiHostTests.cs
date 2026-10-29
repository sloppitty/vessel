using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class IdentityApiHostTests
{
    [Fact]
    public async Task Config_does_not_reveal_origins_and_only_reports_the_query_hint_and_gate()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid(), testAuthentication: false, settings: new()
        {
            ["Vessel:Identity:Origins:0"] = "https://vessel.test", ["Vessel:Identity:Registration"] = "Open"
        });
        using var client = factory.CreateClient();
        var without = await client.GetFromJsonAsync<JsonElement>("/api/auth/config");
        Assert.False(without.GetProperty("originAccepted").GetBoolean());
        Assert.Equal("open", without.GetProperty("registration").GetString());
        var accepted = await client.GetFromJsonAsync<JsonElement>("/api/auth/config?origin=https%3A%2F%2Fvessel.test");
        Assert.True(accepted.GetProperty("originAccepted").GetBoolean());
        Assert.Equal(["registration", "originAccepted"], accepted.EnumerateObject().Select(property => property.Name));
        var rejected = await client.GetFromJsonAsync<JsonElement>("/api/auth/config?origin=https%3A%2F%2Fevil.test");
        Assert.False(rejected.GetProperty("originAccepted").GetBoolean());
        using var scope = factory.Services.CreateScope();
        var schemes = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();
        // The removed Token sign-in's bearer scheme is gone; a bearer header is just an unauthenticated request.
        Assert.Null(await schemes.GetSchemeAsync("VesselBearer"));
        Assert.Equal([SessionAuthentication.SchemeName], (await schemes.GetAllSchemesAsync()).Select(scheme => scheme.Name));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-only-retired-bearer-secret");
        await IdentityHttpTest.Code(await client.GetAsync("/api/system"), 401, "session-required");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-vessel-session=" + IdentityTokens.Create());
        await IdentityHttpTest.Code(await client.GetAsync("/api/system"), 503, "identity-unavailable");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/config")).StatusCode);
    }
}
