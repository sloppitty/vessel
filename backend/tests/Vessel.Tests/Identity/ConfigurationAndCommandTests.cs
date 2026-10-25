using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Nethereum.Signer;
using Vessel.Api;
using Vessel.Application.Ownership;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class ConfigurationAndCommandTests
{
    private static IConfiguration Config(Dictionary<string, string?>? overrides = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString(),
            ["Vessel:Identity:Origins:0"] = "https://vessel.test"
        }.Concat(overrides ?? []).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value)).Build();

    [Theory]
    [InlineData("Vessel:Auth:Mode", "wallet")]
    [InlineData("Vessel:Auth:Mode", "Token")]
    [InlineData("Vessel:Auth:Token", "synthetic-must-never-echo")]
    [InlineData("Vessel:Auth:OwnerId", null)]
    [InlineData("Vessel:Auth:OwnerId", "11111111-1111-1111-1111-111111111111")]
    [InlineData("Vessel:Identity:Origins:0", "http://localhost")]
    [InlineData("Vessel:Identity:Origins:0", "https://vessel.test/path")]
    [InlineData("Vessel:Identity:Origins:0", null)]
    [InlineData("Vessel:Identity:Schema", "unsafe-schema")]
    [InlineData("Vessel:Identity:Registration", "unknown")]
    [InlineData("Vessel:Identity:Session:IdleHours", "25")]
    [InlineData("Vessel:Identity:Session:RememberIdleDays", "31")]
    [InlineData("Vessel:Identity:MaxSessionsPerUser", "201")]
    [InlineData("Vessel:Identity:MaxPendingRegistrations", "0")]
    [InlineData("Vessel:Identity:CookieName", "not-host-prefixed")]
    [InlineData("Vessel:Identity:SignInCookieName", "__Host-vessel-session")]
    [InlineData("Vessel:Events:MaxStreams", "0")]
    public void Invalid_or_removed_token_settings_are_rejected_without_values(string key, string? value)
    {
        var error = Assert.ThrowsAny<Exception>(() => IdentitySettings.Read(Config(new() { [key] = value }), false));
        Assert.DoesNotContain("synthetic-must-never-echo", error.Message);
    }

    [Fact]
    public void Origins_are_normalized_and_registration_is_open_to_policy_and_default_owner_needs_the_override()
    {
        var settings = IdentitySettings.Read(Config(new() { ["Vessel:Identity:Origins:0"] = "https://VESSEL.test:443/" }), false);
        Assert.Equal("https://vessel.test", Assert.Single(settings.Origins));
        Assert.Equal("Invitation", settings.Registration);
        Assert.True(IdentityRegistrationGate.MultiOwnerReady);
        // An explicit Wallet mode from an earlier opt-in stays accepted; Development keeps the legacy default owner.
        Assert.Equal(JournalOwner.Default, IdentitySettings.Read(Config(new()
            { ["Vessel:Auth:Mode"] = "Wallet", ["Vessel:Auth:OwnerId"] = null }), true).OwnerId);
        _ = IdentitySettings.Read(Config(new()
        {
            ["Vessel:Auth:OwnerId"] = JournalOwner.Default.ToString(), ["Vessel:Identity:AllowLegacyDefaultOwner"] = "true"
        }), false);
    }

    [Theory]
    [InlineData("Development", "Vessel:Auth:Token", "synthetic-must-never-echo")]
    [InlineData("Production", "Vessel:Auth:Token", "synthetic-must-never-echo")]
    [InlineData("Staging", "Vessel:Auth:Token", "synthetic-must-never-echo")]
    [InlineData("Development", "Vessel:Auth:Mode", "Token")]
    [InlineData("Production", "Vessel:Auth:Mode", "Token")]
    public async Task Hosts_refuse_the_removed_token_settings_in_every_environment(string environment, string key, string value)
    {
        await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: "Host=unused", environment: environment,
            settings: new() { [key] = value });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(key, error.Message);
        if (key == "Vessel:Auth:Token") Assert.DoesNotContain(value, error.Message);
    }

    [Theory]
    [InlineData("Vessel:Identity:Origins:0", "http://localhost", "Vessel:Identity:Origins")]
    [InlineData("Vessel:Identity:CookieName", "vessel-session", "Vessel:Identity:CookieName")]
    [InlineData("Vessel:Identity:SignInCookieName", "vessel-signin", "Vessel:Identity:SignInCookieName")]
    [InlineData("Vessel:Identity:SignInCookieName", "__Host-vessel-session", "Vessel:Identity:SignInCookieName")]
    public async Task Production_wallet_validation_and_host_enforce_HTTPS_origins_and_host_cookie_names(
        string key, string value, string expectedSetting)
    {
        var values = new Dictionary<string, string?>
        {
            ["Vessel:Identity:Origins:0"] = "https://vessel.test", ["ConnectionStrings:Vessel"] = "Host=unused"
        };
        ProductionConfiguration.Validate(Config(values));
        values[key] = value;
        var validation = Assert.Throws<InvalidOperationException>(() => ProductionConfiguration.Validate(Config(values)));
        Assert.Contains(expectedSetting, validation.Message);
        await using var factory = new CoreApiFactory(Guid.NewGuid(), environment: "Production", settings: values);
        var startup = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(expectedSetting, startup.Message);
    }

    [PostgresTheory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Nondevelopment_hosts_use_secure_configured_cookies(string environment)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var values = database.ConfigurationValues(owner.User.Id);
        values.Remove("Vessel:Identity:Origins:1");
        values["Vessel:Identity:CookieName"] = "__Host-test-session";
        values["Vessel:Identity:SignInCookieName"] = "__Host-test-signin";
        await using var factory = new CoreApiFactory(owner.User.Id, environment: environment, settings: values, testAuthentication: false);
        using var client = IdentityHttpTest.Client(factory);
        var binding = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        Assert.Equal(HttpStatusCode.OK, binding.StatusCode);
        var bindingCookie = binding.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-test-signin=", bindingCookie);
        Assert.Contains("; secure", bindingCookie);
        Assert.Contains("; httponly", bindingCookie);
        Assert.Contains("; samesite=strict", bindingCookie);
        Assert.Contains("; path=/", bindingCookie);
        Assert.DoesNotContain("domain=", bindingCookie);
        var challenge = (await binding.Content.ReadFromJsonAsync<ChallengeDto>())!;
        var verified = await client.PostAsJsonAsync("/api/auth/verify", new { challengeId = challenge.ChallengeId,
            signature = IdentityDatabase.Sign(key, challenge.Message), remember = false });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var sessionCookie = verified.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("__Host-test-session=", StringComparison.Ordinal));
        Assert.Contains("; secure", sessionCookie);
        Assert.Contains("; httponly", sessionCookie);
        Assert.Contains("; samesite=strict", sessionCookie);
        Assert.Contains("; path=/", sessionCookie);
        Assert.DoesNotContain("domain=", sessionCookie);
        var session = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(owner.User.Id, session.GetProperty("session").GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        var settings = factory.Services.GetRequiredService<IdentitySettings>();
        Assert.False(settings.Development);
        Assert.Equal("__Host-test-session", settings.CookieName);
        Assert.Equal("__Host-test-signin", settings.SignInCookieName);
    }

    [Fact]
    public async Task Identity_commands_refuse_the_removed_token_settings_before_touching_a_database()
    {
        foreach (var (key, value) in new[] { ("Vessel:Auth:Mode", "Token"), ("Vessel:Auth:Token", "synthetic-must-never-echo") })
        {
            var output = new StringWriter();
            var config = Config(new() { [key] = value, ["ConnectionStrings:Vessel"] = null });
            Assert.Equal(1, await IdentityCommands.RunAsync(["ensure-owner"], config, false, output));
            Assert.Contains(key, output.ToString());
            Assert.DoesNotContain("synthetic-must-never-echo", output.ToString());
            Assert.Equal(2, await IdentityCommands.RunAsync(["unknown"], config, false, output));
        }
    }

    [Fact]
    public async Task Command_line_configuration_is_effective_and_malformed_command_options_are_usage_errors()
    {
        var output = new StringWriter();
        Assert.Equal(1, await IdentityCommands.RunAsync(["ensure-owner", "--Vessel:Auth:Mode=Token"], output));
        Assert.Contains("Vessel:Auth:Mode", output.ToString());
        Assert.Equal(2, await IdentityCommands.RunAsync(["bind-wallet", "--user"], output));
        Assert.Equal(2, await IdentityCommands.RunAsync(["ensure-owner", "--Vessel:Auth:Mode"], output));
    }

    [PostgresFact]
    public async Task Bootstrap_bind_and_invite_are_idempotent_safe_and_return_documented_exit_codes()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var config = database.Configuration(owner);
        var output = new StringWriter();
        Assert.Equal(0, await IdentityCommands.RunAsync(["ensure-owner"], config, true, output));
        Assert.Equal(0, await IdentityCommands.RunAsync(["ensure-owner"], config, true, output));
        await using var db = database.CreateDbContext();
        var user = await db.Users.SingleAsync();
        Assert.Equal(owner, user.Id);
        Assert.Equal("operator", user.Role);
        Assert.Equal("active", user.Status);
        var key = EthECKey.GenerateKey();
        string[] bind = ["bind-wallet", "--user", owner.ToString(), "--address", key.GetPublicAddress(), "--label", "Recovery"];
        Assert.Equal(0, await IdentityCommands.RunAsync(bind, config, true, output));
        Assert.Equal(0, await IdentityCommands.RunAsync(bind, config, true, output));
        Assert.Single(await db.Wallets.ToListAsync());
        Assert.Equal(3, await IdentityCommands.RunAsync(
            ["bind-wallet", "--user", Guid.NewGuid().ToString(), "--address", key.GetPublicAddress()], config, true, output));
        Assert.Equal(2, await IdentityCommands.RunAsync(["bind-wallet", "--user", "not-a-guid", "--address", key.GetPublicAddress()], config, true, output));
        var invitation = new StringWriter();
        Assert.Equal(0, await IdentityCommands.RunAsync(["invite", "--note", "Synthetic", "--expires", "2d", "--max-uses", "2"], config, true, invitation));
        var code = invitation.ToString().Trim();
        Assert.StartsWith("VSL-", code);
        var stored = await db.Invitations.SingleAsync();
        Assert.Equal(IdentityTokens.Hash(InvitationCodes.Normalize(code)), stored.CodeHash);
        Assert.Equal(2, stored.MaxUses);
        Assert.Equal(TimeSpan.FromDays(2), stored.ExpiresAt - stored.CreatedAt);
        Assert.Equal(2, await IdentityCommands.RunAsync(["invite", "--expires", "-1d"], config, true, output));
        var legacy = database.ConfigurationValues(JournalOwner.Default);
        legacy["Vessel:Identity:AllowLegacyDefaultOwner"] = "true";
        legacy["Vessel:Identity:Origins:1"] = null;
        Assert.Equal(0, await IdentityCommands.RunAsync(["ensure-owner"],
            new ConfigurationBuilder().AddInMemoryCollection(legacy).Build(), false, output));
        Assert.True(await db.Users.AnyAsync(x => x.Id == JournalOwner.Default));
        Assert.Contains("AllowLegacyDefaultOwner", output.ToString());
    }

    [Fact]
    public async Task Default_GUID_is_refused_outside_Development_without_the_override()
    {
        var output = new StringWriter();
        Assert.Equal(1, await IdentityCommands.RunAsync(["ensure-owner"], Config(new()
            { ["Vessel:Auth:OwnerId"] = JournalOwner.Default.ToString() }), false, output));
        Assert.Contains("Vessel:Auth:OwnerId", output.ToString());
    }

    [Fact]
    public void Migration_script_keeps_argument_forwarding_only_on_the_journal_bundle()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "deploy", "migrate"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var script = File.ReadAllText(Path.Combine(directory.FullName, "deploy", "migrate"));
        var expected = new[] { "Vessel.Api.dll check-config", "/app/migrate-identity-ef\n", "/app/migrate-ef \"$@\"", "Vessel.Api.dll identity ensure-owner", "Vessel.Api.dll provision-messaging" };
        var previous = -1;
        foreach (var step in expected) { var index = script.IndexOf(step, StringComparison.Ordinal); Assert.True(index > previous); previous = index; }
        Assert.Contains("set -eu", script);
    }
}
