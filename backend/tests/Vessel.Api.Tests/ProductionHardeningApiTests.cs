using System.Net;

namespace Vessel.Tests;

public sealed class ProductionHardeningApiTests
{

    [Fact]
    public async Task Responses_carry_security_headers_and_api_data_is_not_cached()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/system");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Error_responses_keep_the_security_headers()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid(), new MemoryWorkspaceStore(Guid.NewGuid()));
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsync("/api/portfolios", new StringContent("{\"name\":", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Readiness_is_anonymous_and_reports_unavailable_without_a_database()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"unavailable"}""", await response.Content.ReadAsStringAsync());
    }
}
