using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Overview;

namespace Vessel.Tests;

public sealed class OverviewSummaryApiTests : OverviewSummaryTestsSupport
{

    [Fact]
    public async Task Endpoint_requires_authentication_and_validates_parameters_without_caching()
    {
        await using var factory = new CoreApiFactory(owner, store, configure: services => services.AddSingleton<IActivityStore>(activityStore));
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/overview/summary")).StatusCode);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/overview/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(await response.Content.ReadFromJsonAsync<OverviewSummaryDto>());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/overview/summary?groupBy=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/overview/summary?scope=portfolio:{Guid.NewGuid()}")).StatusCode);
    }
}
