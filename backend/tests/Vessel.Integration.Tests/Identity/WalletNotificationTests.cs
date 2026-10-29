using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Vessel.Application.Events;
using Vessel.Application.Notifications;

namespace Vessel.Tests.Identity;

public sealed class WalletNotificationTests
{
    [PostgresFact]
    public async Task One_Wallet_API_uses_each_sessions_owner_not_the_configured_Token_owner()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var identity = await IdentityDatabase.CreateAsync();
        var firstKey = EthECKey.GenerateKey();
        var secondKey = EthECKey.GenerateKey();
        var first = await identity.SeedAsync(firstKey);
        var second = await identity.SeedAsync(secondKey);
        var firstSession = await identity.SignInAsync(firstKey);
        var secondSession = await identity.SignInAsync(secondKey);
        var configured = Guid.NewGuid();
        var settings = identity.ConfigurationValues(configured);
        settings["ConnectionStrings:Vessel"] = journal.ConnectionString;
        await using var factory = new CoreApiFactory(configured, settings: settings,
            configure: services => services.AddSingleton<TimeProvider>(identity.Clock));
        using var firstClient = factory.CreateClient(new() { BaseAddress = new("https://vessel.test"), HandleCookies = false });
        using var secondClient = factory.CreateClient(new() { BaseAddress = new("https://vessel.test"), HandleCookies = false });
        foreach (var (client, token, user) in new[]
                 { (firstClient, firstSession.Token, first.User.Id), (secondClient, secondSession.Token, second.User.Id) })
        {
            client.DefaultRequestHeaders.Add("Cookie", "__Host-vessel-session=" + token);
            client.DefaultRequestHeaders.Add("X-Vessel-User", user.ToString());
            client.DefaultRequestHeaders.Add("X-Vessel-Request", "1");
            client.DefaultRequestHeaders.Add("Origin", "https://vessel.test");
            Assert.Equal("0.0.0", (await client.GetFromJsonAsync<NotificationHeadDto>("/api/notifications/head"))!.NextCursor);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/notifications/test", null)).StatusCode);
        }
        var firstEvent = Assert.Single((await firstClient.GetFromJsonAsync<VesselEventPage>("/api/events?after=0"))!.Events);
        var secondEvent = Assert.Single((await secondClient.GetFromJsonAsync<VesselEventPage>("/api/events?after=0"))!.Events);
        Assert.Equal(first.User.Id, firstEvent.OwnerId);
        Assert.Equal(second.User.Id, secondEvent.OwnerId);
        Assert.NotEqual(firstEvent.Id, secondEvent.Id);
        Assert.Equal(HttpStatusCode.OK, (await firstClient.PutAsJsonAsync("/api/notifications/preferences",
            new { enabled = false, minimumSeverity = "critical", categories = Array.Empty<string>(), expectedRevision = 0 })).StatusCode);
        var firstPreferences = (await firstClient.GetFromJsonAsync<NotificationPreferencesDto>("/api/notifications/preferences"))!;
        var secondPreferences = (await secondClient.GetFromJsonAsync<NotificationPreferencesDto>("/api/notifications/preferences"))!;
        Assert.Equal((false, "critical", 1L), (firstPreferences.Enabled, firstPreferences.MinimumSeverity, firstPreferences.Revision));
        Assert.Equal((true, "info", 0L), (secondPreferences.Enabled, secondPreferences.MinimumSeverity, secondPreferences.Revision));
        firstClient.DefaultRequestHeaders.Remove("X-Vessel-User");
        firstClient.DefaultRequestHeaders.Add("X-Vessel-User", second.User.Id.ToString());
        await IdentityHttpTest.Code(await firstClient.GetAsync("/api/notifications/head"), 401, "session-user-mismatch");
    }
}
