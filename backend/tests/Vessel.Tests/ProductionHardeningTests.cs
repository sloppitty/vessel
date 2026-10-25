using System.Net;
using Microsoft.Extensions.Configuration;
using Vessel.Api;

namespace Vessel.Tests;

public sealed class ProductionHardeningTests
{
    private const string Owner = "22222222-2222-4222-8222-222222222222";

    private static IConfiguration Config(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Vessel:Auth:OwnerId"] = Owner, ["Vessel:Identity:Origins:0"] = "https://diary.example.test",
            ["ConnectionStrings:Vessel"] = "Host=db"
        };
        foreach (var (key, value) in overrides) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Production_accepts_a_complete_wallet_configuration() => ProductionConfiguration.Validate(Config());

    [Theory]
    [InlineData("Vessel:Auth:OwnerId", "not-a-guid", "Vessel:Auth:OwnerId")]
    [InlineData("Vessel:Auth:OwnerId", null, "Vessel:Auth:OwnerId")]
    [InlineData("Vessel:Identity:Origins:0", null, "Vessel:Identity:Origins")]
    [InlineData("Vessel:Identity:Origins:0", "http://diary.example.test", "Vessel:Identity:Origins")]
    [InlineData("Vessel:Auth:Token", "a-generated-secret-of-the-removed-token-mode", "Vessel:Auth:Token")]
    [InlineData("Vessel:Auth:Mode", "Token", "Vessel:Auth:Mode")]
    [InlineData("ConnectionStrings:Vessel", null, "ConnectionStrings__Vessel")]
    public void Production_refuses_incomplete_wallet_settings_and_removed_token_settings_without_echoing_values(
        string key, string? value, string named)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProductionConfiguration.Validate(Config((key, value))));
        Assert.Contains(named, error.Message);
        if (value is not null && key != "Vessel:Auth:Mode") Assert.DoesNotContain(value, error.Message);
    }

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

    [PostgresFact]
    public async Task Readiness_reports_ready_once_the_database_is_migrated()
    {
        await using var database = await CoreDatabase.CreateAsync();
        await using var identity = await Identity.IdentityDatabase.CreateAsync();
        await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: database.ConnectionString,
            settings: new() { ["Vessel:Identity:Schema"] = identity.Schema.Name });
        using var client = factory.CreateClient();
        // Nobody could sign in yet: an active operator needs a bound wallet (identity bind-wallet).
        using (var unbound = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unbound.StatusCode);
            Assert.Equal("""{"status":"unavailable","reason":"no-operator-wallet"}""", await unbound.Content.ReadAsStringAsync());
        }
        await identity.SeedAsync(Nethereum.Signer.EthECKey.GenerateKey());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
