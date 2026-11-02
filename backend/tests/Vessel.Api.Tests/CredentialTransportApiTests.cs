using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;

namespace Vessel.Tests;

public sealed class CredentialTransportApiTests(CredentialTransportApiTests.RejectionHost host)
    : IClassFixture<CredentialTransportApiTests.RejectionHost>
{
    /// <summary>
    /// The legacy-rejection matrix's shared Production host from a remote peer. Every case is refused before any
    /// read or write (401 without a session, 403/413 with one).
    /// </summary>
    public sealed class RejectionHost : IAsyncDisposable
    {
        internal CoreApiFactory Factory { get; } = new(Guid.NewGuid(), connection: "Host=unused.invalid;Database=synthetic",
            environment: "Production",
            configure: services => services.AddSingleton<IStartupFilter>(new Peer(IPAddress.Parse("192.0.2.10"))));

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    [Theory]
    [InlineData("PUT", "/api/accounts/not-a-guid/credential", "http", 2, 403, "Credential entry requires HTTPS outside localhost.")]
    [InlineData("POST", "/api/accounts/11111111-1111-1111-1111-111111111111/credential", "http", 2, 403, "Credential entry requires HTTPS outside localhost.")]
    [InlineData("PATCH", "/api/unknown/CREDENTIAL/", "http", 4099, 403, "Credential entry requires HTTPS outside localhost.")]
    [InlineData("PUT", "/api/unknown/credential", "https", 4099, 413, "Credential request is too large.")]
    [InlineData("GET", "/api/unknown/credential", "http", 4099, 413, "Credential request is too large.")]
    [InlineData("PUT", "/unknown/credential", "https", 4099, 413, "Credential request is too large.")]
    [InlineData("POST", "/api/plays/not-a-guid/manual-executions", "https", 16385, 413, "Diary request is too large.")]
    [InlineData("DELETE", "/api/plays/retrospective/", "https", 131073, 413, "Diary request is too large.")]
    public async Task Unmatched_and_wrong_method_requests_preserve_legacy_transport_rejections(
        string method, string path, string scheme, int bodyBytes, int status, string detail)
    {
        using var client = host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"{scheme}://vessel.example.invalid"), AllowAutoRedirect = false
        });
        foreach (var authorized in new[] { false, true })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = new StringContent(new string(' ', bodyBytes), Encoding.UTF8, "application/json")
            };
            if (authorized) host.Factory.SignIn(request);
            using var response = await client.SendAsync(request);
            Assert.Equal(authorized ? status : 401, (int)response.StatusCode);
            if (!authorized) continue;
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Empty(response.Content.Headers.ContentEncoding);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(detail, problem.GetProperty("detail").GetString());
        }
    }

    [Theory]
    // Production plaintext from another machine, with or without a spoofed forwarded scheme.
    [InlineData("Production", "http://vessel.example.invalid", "192.0.2.10", null, null, false)]
    [InlineData("Production", "http://vessel.example.invalid", "192.0.2.10", "https", null, false)]
    [InlineData("Production", "http://localhost", "192.0.2.10", "https", null, false)]
    // Same-host plaintext proxy for a LAN origin keeps the LAN host or forwards the LAN client.
    [InlineData("Production", "http://10.1.0.219:5180", "127.0.0.1", null, null, false)]
    [InlineData("Production", "http://localhost", "127.0.0.1", "http", "192.0.2.10", false)]
    // Loopback development, HTTPS, and a same-host TLS-terminating proxy.
    [InlineData("Production", "http://localhost", "127.0.0.1", null, null, true)]
    [InlineData("Production", "http://[::1]", "::1", null, null, true)]
    [InlineData("Production", "https://vessel.example.invalid", "192.0.2.10", null, null, true)]
    [InlineData("Production", "http://vessel.example.invalid", "127.0.0.1", "https", "192.0.2.10", true)]
    // Only the explicitly named Development environment gets the LAN exception.
    [InlineData("Development", "http://10.1.0.219:5180", "127.0.0.1", null, null, true)]
    [InlineData("Development", "http://vessel.example.invalid", "192.0.2.10", null, null, true)]
    [InlineData("Development", "http://localhost", "127.0.0.1", "http", "192.0.2.10", true)]
    [InlineData("Staging", "http://vessel.example.invalid", "192.0.2.10", null, null, false)]
    [InlineData("Testing", "http://vessel.example.invalid", "192.0.2.10", null, null, false)]
    [InlineData("Production", "http://vessel.example.invalid", "172.30.86.10", "https", "198.51.100.7", true, "172.30.86.10")]
    [InlineData("Production", "http://vessel.example.invalid", "172.30.86.10", "http", "198.51.100.7", false, "172.30.86.10")]
    [InlineData("Production", "http://vessel.example.invalid", "192.0.2.10", "https", "198.51.100.7", false, "172.30.86.10")]
    [InlineData("Production", "http://vessel.example.invalid", "172.30.86.1", "https", "198.51.100.7", false, "172.30.86.10")]
    [InlineData("Production", "http://vessel.example.invalid", "172.30.86.11", "https", "198.51.100.7", false, "172.30.86.10")]
    [InlineData("Production", "http://vessel.example.invalid", "127.0.0.1", "https", "198.51.100.7", false, "172.30.86.10")]
    public async Task Token_transport_exception_is_development_only(string environment, string origin, string peer, string? forwardedProto, string? forwardedFor, bool allowed, string? knownProxy = null)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        var verifier = new CountingVerifier(); var discovery = new SecurityDiscovery([new("123", "Main", "main")]);
        await using var factory = new CoreApiFactory(owner, store, connection: "Host=unused.invalid;Database=synthetic",
            environment: environment,
            settings: knownProxy is null ? null : new() { ["Vessel:ForwardedHeaders:KnownProxies:0"] = knownProxy },
            configure: services =>
        {
            services.AddSingleton<Vessel.Application.Monitoring.IServerStateStore>(new MonitorServerState());
            services.AddSingleton<IVenueRegistry>(SecurityFixture.Registry(verifier, discovery)); services.AddSingleton<ICredentialVault>(vault);
            services.AddSingleton<IAccountCredentialStore>(credentials); services.AddSingleton(SecurityFixture.Clock);
            services.AddSingleton<IStartupFilter>(new Peer(IPAddress.Parse(peer)));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(origin), AllowAutoRedirect = false });
        // A browser cannot request the Development environment through a header.
        client.DefaultRequestHeaders.Add("ASPNETCORE_ENVIRONMENT", "Development");
        using (var unauthorized = await client.PutAsJsonAsync($"/api/accounts/{account.Id}/credential", new { token = SecurityFixture.Token }))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        TestAuthentication.SignIn(client.DefaultRequestHeaders, owner);
        if (forwardedProto is not null) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        using var save = await client.PutAsJsonAsync($"/api/accounts/{account.Id}/credential", new { token = SecurityFixture.Token });
        using var preview = await client.PostAsJsonAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts/credential",
            new { address = SecurityFixture.Address, token = SecurityFixture.Token });
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal(1, verifier.Calls); Assert.Equal(SecurityFixture.Token, discovery.ReceivedToken);
            Assert.True(save.Headers.CacheControl!.NoStore);
            Assert.True(preview.Headers.CacheControl!.NoStore);
            Assert.DoesNotContain(SecurityFixture.Token, await save.Content.ReadAsStringAsync());
            Assert.DoesNotContain(SecurityFixture.Token, await preview.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
            Assert.True(save.Headers.CacheControl!.NoStore);
            Assert.Equal(0, verifier.Calls); Assert.Null(discovery.ReceivedToken); Assert.Null(credentials.Stored);
            Assert.DoesNotContain(SecurityFixture.Token, await save.Content.ReadAsStringAsync());
            // Metadata reads and removal carry no token and stay available.
            using var metadata = await client.GetAsync($"/api/accounts/{account.Id}/credential");
            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            using var removed = await client.DeleteAsync($"/api/accounts/{account.Id}/credential");
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }
    }

    private sealed class Peer(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proceed) => { context.Connection.RemoteIpAddress = address; return proceed(); });
            next(app);
        };
    }

    private sealed class CountingVerifier : IVenueCredentialVerifier
    {
        public int Calls { get; private set; }
        public string VenueId => SecurityFixture.Venue.Id;
        public Task<VerifiedVenueCredential> VerifyAsync(string sourceId, string token, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new VerifiedVenueCredential("single", SecurityFixture.Now.AddDays(60)));
        }
    }
}
