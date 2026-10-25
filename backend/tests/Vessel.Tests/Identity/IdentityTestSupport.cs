using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Npgsql;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

internal sealed class IdentityClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class IdentityDatabase(string connection, IdentitySchema schema) : IDbContextFactory<IdentityDbContext>, IAsyncDisposable
{
    public string Connection => connection;
    public IdentitySchema Schema => schema;
    public IdentityClock Clock { get; } = new();
    public IInterceptor? Interceptor { get; set; }
    public IdentitySettings Settings { get; set; } = new() { Schema = schema.Name, Origins = ["https://vessel.test"] };
    public IdentityDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>();
        IdentityDbContext.Configure(options, connection, schema);
        if (Interceptor is not null) options.AddInterceptors(Interceptor);
        return new(options.Options, schema);
    }
    public IdentityService Service() => new(new(this), Settings, new WalletSignatures(), Clock);
    public SessionStore Sessions() => new(() => this, Settings, Clock);

    public static async Task<IdentityDatabase> CreateAsync()
    {
        var connection = Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES") ?? throw new InvalidOperationException("Disposable PostgreSQL is required.");
        var schema = new IdentitySchema("identity_test_" + Guid.NewGuid().ToString("N"));
        var database = new IdentityDatabase(new NpgsqlConnectionStringBuilder(connection)
            { Pooling = false, ApplicationName = schema.Name }.ConnectionString, schema);
        try
        {
            await using var db = database.CreateDbContext();
            await db.Database.MigrateAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    public async Task<(IdentityUser User, UserWallet Wallet)> SeedAsync(EthECKey key, Guid? userId = null, string role = "operator")
    {
        await using var db = CreateDbContext();
        var user = new IdentityUser { Id = userId ?? Guid.NewGuid(), Role = role, DisplayName = "Synthetic owner", CreatedAt = Clock.Now, UpdatedAt = Clock.Now };
        var wallet = new UserWallet { Id = Guid.NewGuid(), UserId = user.Id, Address = key.GetPublicAddress().ToLowerInvariant(), AddedAt = Clock.Now };
        db.Users.Add(user); db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return (user, wallet);
    }

    public async Task<IssuedSession> SignInAsync(EthECKey key, bool remember = false, string? previous = null)
    {
        var binding = IdentityTokens.Create();
        var challenge = await Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        var verified = await Service().VerifyAsync(new(challenge.ChallengeId, Sign(key, challenge.Message), remember),
            "https://vessel.test", binding, previous, "Mozilla Firefox/100 Linux", default);
        return verified.SignedIn!;
    }

    public Dictionary<string, string?> ConfigurationValues(Guid owner) => new()
    {
        ["Vessel:Auth:OwnerId"] = owner.ToString(),
        ["Vessel:Identity:Schema"] = schema.Name, ["Vessel:Identity:Origins:0"] = "https://vessel.test",
        ["Vessel:Identity:Origins:1"] = "http://localhost", ["ConnectionStrings:Vessel"] = connection
    };
    public IConfiguration Configuration(Guid owner) => new ConfigurationBuilder().AddInMemoryCollection(ConfigurationValues(owner)).Build();
    public static string Sign(EthECKey key, string message) => new EthereumMessageSigner().EncodeUTF8AndSign(message, key);
    public async ValueTask DisposeAsync()
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema.Name}\" CASCADE", db);
        await command.ExecuteNonQueryAsync();
    }
}

internal static class IdentityHttpTest
{
    public static CoreApiFactory Factory(IdentityDatabase database, Guid owner, string? registration = null,
        Action<IServiceCollection>? configure = null) =>
        new(owner, connection: database.Connection, settings: database.ConfigurationValues(owner), testAuthentication: false, configure: services =>
        {
            services.AddSingleton<TimeProvider>(database.Clock);
            // Exercise registration's complete cookie/transaction path before B2 opens the production gate.
            if (registration is not null)
                services.AddSingleton(database.Settings with { OwnerId = owner, Registration = registration, Development = true,
                    Origins = ["https://vessel.test", "http://localhost"] });
            configure?.Invoke(services);
        });

    public static HttpClient Client(CoreApiFactory factory, string origin = "https://vessel.test")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new(origin) });
        client.DefaultRequestHeaders.Add("Origin", origin);
        client.DefaultRequestHeaders.Add("X-Vessel-Request", "1");
        return client;
    }

    public static async Task<SessionDto> SignInAsync(HttpClient client, EthECKey key, bool remember = false)
    {
        var challengeResponse = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>())!;
        var response = await client.PostAsJsonAsync("/api/auth/verify", new
        {
            challengeId = challenge.ChallengeId, signature = IdentityDatabase.Sign(key, challenge.Message), remember
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var session = result.GetProperty("session").Deserialize<SessionDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        client.DefaultRequestHeaders.Remove("X-Vessel-User");
        client.DefaultRequestHeaders.Add("X-Vessel-User", session.User.Id.ToString());
        return session;
    }

    public static async Task Code(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
