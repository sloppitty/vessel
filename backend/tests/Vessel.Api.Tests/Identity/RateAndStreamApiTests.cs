using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Events;
using Vessel.Application.Ownership;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

[Collection("Identity streams")]
public sealed class StreamRevalidationApiTests : StreamRevalidationTestsSupport
{
    [Fact]
    public async Task Streams_revalidate_the_wallet_session_that_opened_them()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid(), testAuthentication: false);
        using var scope = factory.Services.CreateScope();
        var validity = scope.ServiceProvider.GetRequiredService<IPrincipalValidity>();
        Assert.IsType<SessionPrincipal>(validity);
        // No session authenticated this scope, so nothing may keep streaming.
        Assert.False(await validity.StillValidAsync(default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task Both_event_caps_preserve_RetryAfter_and_code_at_the_client(int maximum)
    {
        var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner, settings: new() { ["Vessel:Events:MaxStreams"] = maximum.ToString() },
            configure: services => services.AddScoped<IVesselEventReader>(_ => new EmptyReader()));
        using var client = factory.AuthorizedClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var streams = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < (maximum == 1 ? 1 : 4); i++)
            {
                var stream = await client.GetAsync("/api/events/stream?after=0", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
                streams.Add(stream);
            }
            HttpResponseMessage rejected;
            if (maximum == 1)
            {
                await using var second = new CoreApiFactory(Guid.NewGuid(), settings: new() { ["Vessel:Events:MaxStreams"] = "1" },
                    configure: services => services.AddScoped<IVesselEventReader>(_ => new EmptyReader()));
                using var other = second.AuthorizedClient();
                rejected = await other.GetAsync("/api/events/stream?after=0", stop.Token);
            }
            else rejected = await client.GetAsync("/api/events/stream?after=0", stop.Token);
            using (rejected)
            {
                await IdentityHttpTest.Code(rejected, 429, "rate-limited");
                Assert.Equal(TimeSpan.FromSeconds(5), rejected.Headers.RetryAfter?.Delta);
            }
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var stream in streams) stream.Dispose();
        }
    }
}
