using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

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

    public static Task Code(HttpResponseMessage response, int status, string code) => IdentityResponse.Code(response, status, code);
}
