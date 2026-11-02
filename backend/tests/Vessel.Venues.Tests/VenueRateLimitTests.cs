using System.Net;
using System.Net.Http.Headers;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Venues.Common;

namespace Vessel.Tests;

public sealed class VenueRateLimitTests : VenueRateLimitTestsSupport
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http429_preserves_delta_and_date_retry_deadlines_without_response_secrets(bool httpDate)
    {
        using var http = new HttpClient(new Limited(httpDate)) { BaseAddress = new("https://synthetic.invalid") };
        var reader = new BoundedJsonHttp(http, new Clock(), new StrictJson("Invalid synthetic read."), "Unavailable.", "Timed out.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/read");
        var error = await Assert.ThrowsAsync<VenueRateLimitException>(() =>
            reader.ReadBoundedAsync(ct => reader.SendAsync(request, ct), default));
        Assert.Equal(Now.AddSeconds(600), error.RetryAtUtc);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid deadline")]
    public async Task Missing_or_invalid_retry_header_uses_the_sixty_second_fallback(string? value)
    {
        using var http = new HttpClient(new MissingRetry(value)) { BaseAddress = new("https://synthetic.invalid") };
        var reader = new BoundedJsonHttp(http, new Clock(), new StrictJson("Invalid synthetic read."), "Unavailable.", "Timed out.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/read");
        var error = await Assert.ThrowsAsync<VenueRateLimitException>(() => reader.SendAsync(request, default));
        Assert.Equal(Now.AddSeconds(60), error.RetryAtUtc);
    }

    [Theory]
    [InlineData("315360000")]
    [InlineData("Fri, 31 Dec 9999 23:59:59 GMT")]
    public async Task Review_M1_retry_after_is_clamped_to_one_hour(string value)
    {
        using var http = new HttpClient(new MissingRetry(value)) { BaseAddress = new("https://synthetic.invalid") };
        var reader = new BoundedJsonHttp(http, new Clock(), new StrictJson("Invalid synthetic read."), "Unavailable.", "Timed out.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/read");
        var error = await Assert.ThrowsAsync<VenueRateLimitException>(() => reader.SendAsync(request, default));
        Assert.Equal(Now.AddHours(1), error.RetryAtUtc);
    }
    private sealed class MissingRetry(string? value) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (value is not null) response.Headers.TryAddWithoutValidation("Retry-After", value);
            return Task.FromResult(response);
        }
    }
    private sealed class Limited(bool httpDate) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("SECRET") };
            response.Headers.RetryAfter = httpDate ? new RetryConditionHeaderValue(Now.AddSeconds(600)) :
                new RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return Task.FromResult(response);
        }
    }
}
