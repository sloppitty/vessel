using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Nethereum.Signer;
using Npgsql;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

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
