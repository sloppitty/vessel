using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Infrastructure.Credentials;

namespace Vessel.Tests;

public sealed class AccountSecurityApiTests
{
    [Theory]
    [InlineData("GET", "/api/venues/index-fixture/accounts?address=0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("GET", "/api/accounts/11111111-1111-1111-1111-111111111111/credential")]
    [InlineData("PUT", "/api/accounts/11111111-1111-1111-1111-111111111111/credential")]
    [InlineData("DELETE", "/api/accounts/11111111-1111-1111-1111-111111111111/credential")]
    public async Task Routes_require_authentication(string method, string path)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid()); using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Credential_routes_are_write_only_and_owner_scoped_and_disabled_reads_are_blocked()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var foreign = SecurityFixture.Account(Guid.NewGuid()); store.Accounts.Add(foreign);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        await using var factory = Factory(owner, store, credentials, vault); using var client = factory.AuthorizedClient();
        using var saved = await client.PutAsJsonAsync($"/api/accounts/{account.Id}/credential", new { token = SecurityFixture.Token });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); Assert.True(saved.Headers.CacheControl!.NoStore);
        var body = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecurityFixture.Token, body); Assert.DoesNotContain("nonce", body); Assert.DoesNotContain("ciphertext", body);
        var envelope = await saved.Content.ReadFromJsonAsync<AccountCredentialDto>(); Assert.Equal("valid", envelope!.Credential!.Status);
        using var metadata = await client.GetAsync($"/api/accounts/{account.Id}/credential");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode); Assert.True(metadata.Headers.CacheControl!.NoStore);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, $"/api/accounts/{foreign.Id}/credential")
            { Content = JsonContent.Create(new { token = SecurityFixture.Token }) });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        }
        account.UpdateSettings(account.Name, null, false);
        using var disabled = await client.PutAsJsonAsync($"/api/accounts/{account.Id}/credential", new { token = SecurityFixture.Token });
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<Vessel.Application.Common.ProblemException>(() =>
            new AccountCredentialReader(store, credentials, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.None).ReadAsync(account.Id, default))).StatusCode);
        using var removed = await client.DeleteAsync($"/api/accounts/{account.Id}/credential");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode); Assert.Null(credentials.Stored);
    }

    [Fact]
    public async Task No_key_allows_metadata_and_removal_but_not_save()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = new AesGcmCredentialVault(new());
        await using var factory = Factory(owner, store, credentials, vault); using var client = factory.AuthorizedClient();
        var metadata = await client.GetFromJsonAsync<AccountCredentialDto>($"/api/accounts/{account.Id}/credential");
        Assert.False(metadata!.StorageConfigured); Assert.Null(metadata.Credential);
        using var saved = await client.PutAsJsonAsync($"/api/accounts/{account.Id}/credential", new { token = SecurityFixture.Token });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, saved.StatusCode); Assert.True(saved.Headers.CacheControl!.NoStore);
        Assert.Contains("Credential storage is not configured", await saved.Content.ReadAsStringAsync());
        using var removed = await client.DeleteAsync($"/api/accounts/{account.Id}/credential"); Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("Credential")]
    [InlineData("credential/")]
    public async Task Rejected_tokens_malformed_and_oversized_bodies_never_leak_to_responses_or_logs(string suffix)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault(); var logs = new SecurityLogs();
        await using var factory = Factory(owner, store, credentials, vault,
            new SecurityVerifier { Error = new InvalidOperationException(SecurityFixture.Token) }, logs);
        using var client = factory.AuthorizedClient(); var path = $"/api/accounts/{account.Id}/{suffix}";
        foreach (var (json, expected) in new[]
        {
            (JsonSerializer.Serialize(new { token = SecurityFixture.Token }), HttpStatusCode.BadRequest),
            ("{}", HttpStatusCode.BadRequest), ("{\"token\":null}", HttpStatusCode.BadRequest),
            (JsonSerializer.Serialize(new { token = new string('x', 513) }), HttpStatusCode.BadRequest),
            (JsonSerializer.Serialize(new { token = new string('x', 4200) }), HttpStatusCode.RequestEntityTooLarge),
            ("{\"token\":\"" + SecurityFixture.Token, HttpStatusCode.BadRequest)
        })
        {
            using var response = await client.PutAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(expected, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.DoesNotContain(SecurityFixture.Token, await response.Content.ReadAsStringAsync());
        }
        Assert.Null(credentials.Stored);
        Assert.DoesNotContain(SecurityFixture.Token, string.Join('\n', logs.Messages));
    }

    [Fact]
    public async Task Source_and_discovery_json_never_turn_int64_into_number()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        using var vault = SecurityFixture.Vault();
        await using var factory = Factory(owner, store, new(), vault); using var client = factory.AuthorizedClient();
        using var created = await client.PostAsJsonAsync("/api/accounts", new { name = "Lighter main", venueId = SecurityFixture.Venue.Id, sourceId = "9223372036854775807" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var parsed = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.String, parsed.RootElement.GetProperty("sourceId").ValueKind);
        Assert.Equal("9223372036854775807", parsed.RootElement.GetProperty("sourceId").GetString());
        using var invalid = await client.PostAsJsonAsync("/api/accounts", new { name = "Lighter main", venueId = SecurityFixture.Venue.Id, sourceId = 123 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        store.Accounts.Single().UpdateSettings("Disabled", null, false);
        using var discovered = await client.GetAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts?address={SecurityFixture.Address}");
        Assert.Equal(HttpStatusCode.OK, discovered.StatusCode);
        using var discovery = JsonDocument.Parse(await discovered.Content.ReadAsStringAsync());
        var row = discovery.RootElement.GetProperty("accounts")[0];
        Assert.Equal("9223372036854775807", row.GetProperty("sourceId").GetString()); Assert.False(row.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(store.Accounts.Single().Id, row.GetProperty("existingAccountId").GetGuid());
    }

    [Fact]
    public async Task Production_registry_activation_has_no_credential_dependency_cycle()
    {
        var owner = Guid.NewGuid();
        await using var factory = new CoreApiFactory(owner);
        using var client = factory.AuthorizedClient();
        using var response = await client.GetAsync("/api/system");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var lighter = json.RootElement.GetProperty("venues").EnumerateArray().Single(v => v.GetProperty("id").GetString() == "lighter");
        Assert.True(lighter.GetProperty("capabilities").GetProperty("readOnlyCredential").GetBoolean());
        Assert.True(lighter.GetProperty("capabilities").GetProperty("accountDiscovery").GetBoolean());
    }

    [Fact]
    public async Task Name_preview_is_no_store_and_does_not_require_vault_or_import_accounts()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var credentials = new SecurityCredentialStore(); using var vault = new AesGcmCredentialVault(new());
        var discovery = new SecurityDiscovery([new("9223372036854775807", "Swing ETH", "main", CollateralUsd: "42.000", AvailableBalanceUsd: "0")]);
        await using var factory = Factory(owner, store, credentials, vault, discovery: discovery);
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsJsonAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts/credential",
            new { address = SecurityFixture.Address, token = SecurityFixture.Token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(SecurityFixture.Token, discovery.ReceivedToken);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Swing ETH", body); Assert.Contains("\"collateralUsd\":\"42.000\"", body);
        Assert.DoesNotContain(SecurityFixture.Token, body);
        Assert.Null(credentials.Stored); Assert.Empty(store.Accounts);
    }

    [Fact]
    public async Task Name_preview_with_an_unusable_token_is_the_owners_error_not_a_venue_outage()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        using var vault = new AesGcmCredentialVault(new());
        await using var factory = Factory(owner, store, new(), vault,
            discovery: new SecurityDiscovery([]) { Error = new VenueCredentialRejectedException() });
        using var client = factory.AuthorizedClient();
        using var response = await client.PostAsJsonAsync($"/api/venues/{SecurityFixture.Venue.Id}/accounts/credential",
            new { address = SecurityFixture.Address, token = "not-read-only" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not valid for this wallet", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Name_preview_refusals_and_bad_bodies_never_echo_tokens_to_responses_or_logs()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        using var vault = new AesGcmCredentialVault(new()); var logs = new SecurityLogs();
        await using var factory = Factory(owner, store, new(), vault, logs: logs,
            discovery: new SecurityDiscovery([]) { Error = new InvalidOperationException(SecurityFixture.Token) });
        using var client = factory.AuthorizedClient();
        var path = $"/api/venues/{SecurityFixture.Venue.Id}/accounts/credential";
        foreach (var (body, expected) in new[]
        {
            (JsonSerializer.Serialize(new { address = SecurityFixture.Address, token = SecurityFixture.Token }), HttpStatusCode.BadGateway),
            (JsonSerializer.Serialize(new { address = SecurityFixture.Address, token = new string('x', 513) }), HttpStatusCode.BadRequest),
            (JsonSerializer.Serialize(new { address = SecurityFixture.Address, token = new string('x', 4200) }), HttpStatusCode.RequestEntityTooLarge),
            ("{\"token\":\"" + SecurityFixture.Token, HttpStatusCode.BadRequest)
        })
        {
            using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.DoesNotContain(SecurityFixture.Token, await response.Content.ReadAsStringAsync());
        }
        Assert.DoesNotContain(logs.Messages, message => message.Contains(SecurityFixture.Token, StringComparison.Ordinal));
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostAsJsonAsync(path, new { address = SecurityFixture.Address, token = SecurityFixture.Token });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    private static CoreApiFactory Factory(Guid owner, MemoryWorkspaceStore accounts, SecurityCredentialStore credentials, ICredentialVault vault,
        SecurityVerifier? verifier = null, SecurityLogs? logs = null, SecurityDiscovery? discovery = null) => new(owner, accounts, configure: services =>
    {
        services.AddSingleton<IVenueRegistry>(SecurityFixture.Registry(verifier ?? new(),
            discovery ?? new SecurityDiscovery([new("9223372036854775807", "Main", "main")])));
        services.AddSingleton<Vessel.Application.Monitoring.IServerStateStore>(new MonitorServerState());
        services.AddSingleton<ICredentialVault>(vault); services.AddSingleton<IAccountCredentialStore>(credentials);
        services.AddSingleton(SecurityFixture.Clock);
        if (logs is not null) services.AddLogging(b => b.AddProvider(logs));
    });

    private sealed class SecurityLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}
