using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Api;
using Vessel.Application.Venues;

namespace Vessel.Tests;

public sealed class EndpointTransportTests
{
    [Fact]
    public async Task Every_credential_route_carries_the_marker_and_the_credential_body_limit()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        var endpoints = Endpoints(factory);
        var credential = endpoints.Where(e => e.RoutePattern.RawText!.Contains("credential", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal(
            ["DELETE /api/accounts/{id:guid}/credential", "GET /api/accounts/{id:guid}/credential",
             "POST /api/venues/{venueId}/accounts/credential", "PUT /api/accounts/{id:guid}/credential"],
            credential.Select(Name).Order(StringComparer.Ordinal));
        Assert.All(credential, endpoint =>
        {
            Assert.NotNull(endpoint.Metadata.GetMetadata<CredentialEndpoint>());
            // Only the post-authorization middleware may lower the server limit.
            Assert.Null(endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
            var limit = Assert.IsType<RequestBodyLimit>(endpoint.Metadata.GetMetadata<RequestBodyLimit>());
            Assert.Equal((4096L, "Credential request is too large."), (limit.MaxBytes, limit.TooLargeMessage));
        });
        // The marker never leaks onto routes that carry no token.
        Assert.DoesNotContain(endpoints.Except(credential), e => e.Metadata.GetMetadata<CredentialEndpoint>() is not null);
    }

    [Fact]
    public async Task Diary_writes_carry_their_body_limits()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        var limits = Endpoints(factory)
            .Where(e => e.Metadata.GetMetadata<RequestBodyLimit>() is not null && e.Metadata.GetMetadata<CredentialEndpoint>() is null)
            .ToDictionary(Name, e => e.Metadata.GetMetadata<RequestBodyLimit>()!);

        Assert.Equal(
            ["POST /api/plays/retrospective", "POST /api/plays/{id:guid}/manual-executions",
             "POST /api/plays/{id:guid}/manual-executions/corrections", "PUT /api/plays/{id:guid}/manual-executions/{executionId:guid}"],
            limits.Keys.Order(StringComparer.Ordinal));
        Assert.All(limits, pair => Assert.Equal("Diary request is too large.", pair.Value.TooLargeMessage));
        Assert.All(Endpoints(factory), endpoint => Assert.Null(endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()));
        Assert.Equal(131_072, limits["POST /api/plays/retrospective"].MaxBytes);
        Assert.Equal(16_384, limits["POST /api/plays/{id:guid}/manual-executions"].MaxBytes);
        Assert.Equal(16_384, limits["PUT /api/plays/{id:guid}/manual-executions/{executionId:guid}"].MaxBytes);
        // A correction batch holds up to 200 full rows and one reason (phase 1, 1.3b).
        Assert.Equal(262_144, limits["POST /api/plays/{id:guid}/manual-executions/corrections"].MaxBytes);
    }

    [Fact]
    public async Task Only_the_public_auth_and_health_routes_are_anonymous()
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        var endpoints = Endpoints(factory);
        Assert.Equal(
            ["GET /api/auth/config", "GET /health/live", "GET /health/ready",
             "POST /api/auth/challenge", "POST /api/auth/pairings/claim", "POST /api/auth/pairings/complete",
             "POST /api/auth/register", "POST /api/auth/verify"],
            endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                .Select(Name).Order(StringComparer.Ordinal));
        Assert.All(endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal) &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null),
            endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
    }

    [Theory]
    [InlineData("POST", "/api/venues/index-fixture/accounts/credential")]
    [InlineData("PUT", "/api/accounts/11111111-1111-1111-1111-111111111111/credential")]
    public async Task Chunked_credential_bodies_are_bounded_during_binding(string method, string path)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid());
        // TestServer does not enforce the server's chunked request-body limit.
        factory.UseKestrel(0);
        using var client = factory.AuthorizedClient();
        var body = Encoding.UTF8.GetBytes(new string(' ', 4096) + "{}");
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new ChunkedContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chunked_unauthorized_request_does_not_break_an_immediate_authenticated_retry()
    {
        var owner = Guid.NewGuid();
        var discovery = new SecurityDiscovery([new("123", "Main", "main")]);
        var clock = new ManualTime(SecurityFixture.Now);
        await using var factory = new CoreApiFactory(owner, new MemoryWorkspaceStore(owner), configure: services =>
        {
            services.AddSingleton<IVenueRegistry>(SecurityFixture.Registry(discovery));
            services.AddSingleton<Vessel.Application.Monitoring.IServerStateStore>(new MonitorServerState());
            services.AddSingleton<TimeProvider>(clock);
        });
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        const string path = "/api/venues/index-fixture/accounts/credential";
        for (var i = 0; i < 50; i++)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ChunkedContent(Encoding.UTF8.GetBytes(new string(' ', 4096) + "{}"))
            })
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.TransferEncodingChunked = true;
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                await response.Content.ReadAsStringAsync();
            }
            // No pause or new client: the rejected body must not poison a pooled connection.
            using var retry = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    address = SecurityFixture.Address, token = SecurityFixture.Token
                }), Encoding.UTF8, "application/json")
            };
            factory.SignIn(retry);
            using var accepted = await client.SendAsync(retry);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.True(accepted.Headers.CacheControl?.NoStore);
            await accepted.Content.ReadAsStringAsync();
            // Keep this transport stress test inside the real ten-attempts-per-minute quota.
            clock.Advance(TimeSpan.FromSeconds(6));
        }
    }

    private static List<RouteEndpoint> Endpoints(CoreApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private static string Name(RouteEndpoint endpoint) =>
        $"{Assert.Single(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)} {endpoint.RoutePattern.RawText!.TrimEnd('/')}";

    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
