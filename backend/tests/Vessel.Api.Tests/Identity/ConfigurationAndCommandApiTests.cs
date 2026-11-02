using Vessel.Api;

namespace Vessel.Tests.Identity;

public sealed class ConfigurationAndCommandApiTests : ConfigurationAndCommandTestsSupport
{

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
}
