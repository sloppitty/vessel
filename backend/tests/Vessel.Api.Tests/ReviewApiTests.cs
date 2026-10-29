using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Review;

namespace Vessel.Tests;

public sealed class ReviewApiTests : ReviewTestsSupport
{

    [Fact]
    public async Task Endpoint_requires_auth_validates_filters_and_serializes_exact_decimal_strings()
    {
        f.Add(.1234567890123456789m);
        await using var factory = new CoreApiFactory(f.Owner, configure: services =>
        {
            services.AddSingleton<IReviewStore>(f);
            services.AddSingleton<TimeProvider>(new ReviewClock());
        });
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/review")).StatusCode);
        using var client = factory.AuthorizedClient();
        // The drill-downs validate the report's filters too, positions included.
        foreach (var route in new[] { "/api/review?", "/api/review/exclusions?", "/api/review/process?lane=unanswered&" })
        {
            foreach (var query in new[] { "period=bad", "accountId=bad", "offset=-1", "portfolio=bad", "positions=bad", "positions=auto&minPlays=-1" })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + query)).StatusCode);
            (await client.GetAsync(route + "positions=exclude&minPlays=1000")).EnsureSuccessStatusCode();
        }
        var response = await client.GetAsync("/api/review");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var report = await response.Content.ReadFromJsonAsync<ReviewDto>();
        Assert.Equal("0.1234567890123456789", report!.Metrics.Net);
    }
}
