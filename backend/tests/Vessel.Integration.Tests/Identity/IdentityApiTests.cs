using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class IdentityApiTests
{

    [PostgresFact]
    public async Task Session_scheme_issues_the_owner_claims_and_the_session_claim()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var seeded = await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, seeded.User.Id);
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Path = "/api/system";
        context.Request.Headers.Cookie = "__Host-vessel-session=" + issued.Token;
        var result = await context.AuthenticateAsync(SessionAuthentication.SchemeName);
        Assert.True(result.Succeeded);
        Assert.Equal(SessionAuthentication.SchemeName, result.Ticket!.AuthenticationScheme);
        Assert.Equal(SessionAuthentication.SchemeName, result.Principal!.Identity!.AuthenticationType);
        Assert.Equal(seeded.User.DisplayName, result.Principal.Identity.Name);
        Assert.Equal(seeded.User.Id.ToString(), result.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal(issued.Session.Session.Id.ToString(), result.Principal.FindFirst(SessionAuthentication.SessionClaim)!.Value);
    }

    [PostgresFact]
    public async Task Sign_in_session_system_and_expected_sign_out_use_the_same_owner_claims()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var seeded = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, seeded.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        Assert.True(session.AdoptsLegacyPreferences);
        Assert.Equal(WalletSignatures.Checksum(key.GetPublicAddress()), session.Wallet.Address);
        var system = await client.GetFromJsonAsync<JsonElement>("/api/system");
        Assert.Equal(seeded.User.Id, system.GetProperty("owner").GetProperty("id").GetGuid());
        client.DefaultRequestHeaders.Remove("X-Vessel-User");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        await IdentityHttpTest.Code(await client.GetAsync("/api/system"), 401, "session-user-mismatch");
        client.DefaultRequestHeaders.Add("X-Vessel-User", Guid.NewGuid().ToString());
        await IdentityHttpTest.Code(await client.GetAsync("/api/system"), 401, "session-user-mismatch");
        client.DefaultRequestHeaders.Remove("X-Vessel-User");
        client.DefaultRequestHeaders.Add("X-Vessel-User", seeded.User.Id.ToString());
        var stale = await client.PostAsJsonAsync("/api/auth/sign-out", new { expectedSessionId = Guid.NewGuid() });
        await IdentityHttpTest.Code(stale, 409, "session-changed");
        Assert.False(stale.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        var signedOut = await client.PostAsJsonAsync("/api/auth/sign-out", new { expectedSessionId = session.Session.Id });
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Contains(signedOut.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("__Host-vessel-session=;", StringComparison.Ordinal));
        await IdentityHttpTest.Code(await client.GetAsync("/api/auth/session"), 401, "session-required");
    }

    [PostgresTheory]
    [InlineData("https://vessel.test", "__Host-vessel-signin", "__Host-vessel-session", true, false)]
    [InlineData("https://vessel.test", "__Host-vessel-signin", "__Host-vessel-session", true, true)]
    [InlineData("http://localhost", "vessel-signin", "vessel-session", false, false)]
    [InlineData("http://localhost", "vessel-signin", "vessel-session", false, true)]
    public async Task Cookies_have_origin_specific_names_attributes_lifetimes_and_reuse_binding(
        string origin, string bindingName, string sessionName, bool secure, bool remember)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory, origin);
        var first = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var cookie = first.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith(bindingName + "=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Contains("max-age=600", cookie);
        Assert.Equal(secure, cookie.Contains("; secure", StringComparison.Ordinal));
        Assert.DoesNotContain("domain=", cookie);
        var second = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        Assert.Equal(cookie, second.Headers.GetValues("Set-Cookie").Single());
        var challenge = (await first.Content.ReadFromJsonAsync<ChallengeDto>())!;
        var verify = await client.PostAsJsonAsync("/api/auth/verify", new { challengeId = challenge.ChallengeId,
            signature = IdentityDatabase.Sign(key, challenge.Message), remember });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var sessionCookie = verify.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(sessionName + "=", StringComparison.Ordinal));
        Assert.Equal(secure, sessionCookie.Contains("; secure", StringComparison.Ordinal));
        Assert.Equal(remember, sessionCookie.Contains("max-age=", StringComparison.Ordinal));
        if (remember) Assert.Contains("max-age=2592000", sessionCookie);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "null");
        client.DefaultRequestHeaders.Add("X-Vessel-User", owner.User.Id.ToString());
        await IdentityHttpTest.Code(await client.DeleteAsync("/api/unknown-write"), 403, "origin-rejected");
    }

    [PostgresFact]
    public async Task A_late_challenge_renews_the_same_binding_and_verifies_after_the_original_cookie_expired()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://vessel.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Origin", "https://vessel.test");
        client.DefaultRequestHeaders.Add("X-Vessel-Request", "1");
        var first = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        var binding = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(first.Headers.GetValues("Set-Cookie").Single());
        var originalExpiry = database.Clock.Now + binding.MaxAge!.Value;
        client.DefaultRequestHeaders.Add("Cookie", $"{binding.Name}={binding.Value}");
        database.Clock.Now = database.Clock.Now.AddMinutes(6);
        var retry = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        var renewed = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(retry.Headers.GetValues("Set-Cookie").Single());
        Assert.Equal(binding.Value, renewed.Value);
        Assert.Equal(TimeSpan.FromMinutes(10), renewed.MaxAge);
        var renewedExpiry = database.Clock.Now + renewed.MaxAge!.Value;
        var challenge = (await retry.Content.ReadFromJsonAsync<ChallengeDto>())!;
        database.Clock.Now = database.Clock.Now.AddMinutes(4.5);
        // Model the browser's Max-Age using the same fake clock as challenge verification.
        Assert.True(database.Clock.Now > originalExpiry && database.Clock.Now < renewedExpiry);
        var verified = await client.PostAsJsonAsync("/api/auth/verify", new { challengeId = challenge.ChallengeId,
            signature = IdentityDatabase.Sign(key, challenge.Message), remember = false });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal("signed-in", (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task New_user_verify_reissues_binding_cookie_and_failed_register_does_not_burn_it()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await using var factory = IdentityHttpTest.Factory(database, Guid.NewGuid(), registration: "Open");
        using var client = IdentityHttpTest.Client(factory);
        var challenge = await (await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() })).Content.ReadFromJsonAsync<ChallengeDto>();
        var response = await client.PostAsJsonAsync("/api/auth/verify", new { challengeId = challenge!.ChallengeId,
            signature = IdentityDatabase.Sign(key, challenge.Message), remember = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("__Host-vessel-signin=", StringComparison.Ordinal) && value.Contains("max-age=600", StringComparison.Ordinal));
        var verified = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("registration-required", verified.GetProperty("status").GetString());
        var registration = verified.GetProperty("registration").GetString();
        await IdentityHttpTest.Code(await client.PostAsJsonAsync("/api/auth/register", new { registration, displayName = "", remember = false }),
            400, "display-name-invalid");
        var registered = await client.PostAsJsonAsync("/api/auth/register", new { registration, displayName = "  Synthetic new user  ", remember = false });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var dto = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session");
        Assert.Equal("Synthetic new user", dto.GetProperty("user").GetProperty("displayName").GetString());
        Assert.False(dto.GetProperty("adoptsLegacyPreferences").GetBoolean());
        Assert.Contains(registered.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-vessel-session=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [PostgresTheory]
    [InlineData("link")]
    [InlineData("step-up")]
    public async Task Verify_rejects_and_consumes_challenges_for_other_purposes(string purpose)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var issued = await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() });
        var challenge = (await issued.Content.ReadFromJsonAsync<ChallengeDto>())!;
        await using var db = database.CreateDbContext();
        await db.Challenges.Where(row => row.Id == challenge.ChallengeId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Purpose, purpose));
        var request = new { challengeId = challenge.ChallengeId, signature = IdentityDatabase.Sign(key, challenge.Message), remember = false };
        await IdentityHttpTest.Code(await client.PostAsJsonAsync("/api/auth/verify", request), 400, "challenge-invalid");
        Assert.NotNull((await db.Challenges.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Empty(await db.Sessions.ToListAsync());
        await IdentityHttpTest.Code(await client.PostAsJsonAsync("/api/auth/verify", request), 400, "challenge-invalid");
    }

    [PostgresTheory]
    [InlineData("Origin", null, "origin-rejected")]
    [InlineData("Origin", "null", "origin-rejected")]
    [InlineData("Origin", "https://evil.test", "origin-rejected")]
    [InlineData("Origin", "https://vessel.test/", "origin-rejected")]
    [InlineData("Sec-Fetch-Site", "cross-site", "csrf-rejected")]
    [InlineData("X-Vessel-Request", null, "csrf-rejected")]
    public async Task Guard_refuses_missing_null_or_wrong_origins_cross_site_and_missing_public_header(string header, string? value, string code)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var factory = IdentityHttpTest.Factory(database, Guid.NewGuid());
        using var client = IdentityHttpTest.Client(factory);
        client.DefaultRequestHeaders.Remove(header);
        if (value is not null) client.DefaultRequestHeaders.Add(header, value);
        await IdentityHttpTest.Code(await client.PostAsJsonAsync("/api/auth/challenge", new { address = EthECKey.GenerateKey().GetPublicAddress() }),
            403, code);
    }

    [PostgresTheory]
    [InlineData("post", "/api/auth/sign-out")]
    [InlineData("delete", "/api/auth/sign-out")]
    [InlineData("PROPFIND", "/api/auth/sign-out")]
    [InlineData("post", "/api/accounts/11111111-1111-1111-1111-111111111111")]
    [InlineData("delete", "/api/accounts/11111111-1111-1111-1111-111111111111")]
    [InlineData("PROPFIND", "/api/accounts/11111111-1111-1111-1111-111111111111")]
    public async Task Guard_checks_lowercase_and_unusual_unsafe_methods_on_auth_and_journal_routes(string method, string path)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        HttpRequestMessage Request() => new(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { expectedSessionId = session.Session.Id })
        };
        client.DefaultRequestHeaders.Remove("Origin");
        using (var missingOrigin = Request())
            await IdentityHttpTest.Code(await client.SendAsync(missingOrigin), 403, "origin-rejected");
        client.DefaultRequestHeaders.Add("Origin", "https://vessel.test");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        using (var crossSite = Request())
            await IdentityHttpTest.Code(await client.SendAsync(crossSite), 403, "csrf-rejected");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Null((await db.Sessions.SingleAsync()).RevokedAt);
    }

    [PostgresTheory]
    [InlineData("post")]
    [InlineData("delete")]
    [InlineData("PROPFIND")]
    public async Task Public_auth_routes_guard_every_unsafe_method_even_without_a_session(string method)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var factory = IdentityHttpTest.Factory(database, Guid.NewGuid());
        using var client = IdentityHttpTest.Client(factory);
        client.DefaultRequestHeaders.Remove("Origin");
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/auth/challenge");
        await IdentityHttpTest.Code(await client.SendAsync(request), 403, "origin-rejected");
    }

    [PostgresFact]
    public async Task Public_routes_are_exempt_even_with_an_invalid_or_foreign_session_cookie()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var seed = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, seed.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(client, key);
        client.DefaultRequestHeaders.Remove("X-Vessel-User");
        client.DefaultRequestHeaders.Add("X-Vessel-User", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() })).StatusCode);
        await using var db = database.CreateDbContext();
        await db.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, database.Clock.Now));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/challenge", new { address = key.GetPublicAddress() })).StatusCode);
    }

    [PostgresFact]
    public async Task Guard_has_no_content_type_rule_and_allows_multipart_and_bodyless_DELETE()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var seed = await database.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, seed.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(client, key);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent([1, 2, 3]), "file", "synthetic.png");
        // Nonexistent authenticated paths reach the normal router, not a content-type/body guard.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/unknown-write", multipart)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/unknown-write")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        await IdentityHttpTest.Code(await client.DeleteAsync("/api/unknown-write"), 403, "origin-rejected");
    }

    [PostgresFact]
    public async Task Auth_bodies_are_bounded_including_streaming_and_malformed_JSON_has_a_code()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var factory = IdentityHttpTest.Factory(database, Guid.NewGuid());
        using var client = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.Code(await client.PostAsync("/api/auth/challenge", new StringContent("{")), 400, "request-invalid");
        await IdentityHttpTest.Code(await client.PostAsync("/api/auth/challenge", new StringContent(new string(' ', 16385))), 413, "request-too-large");
        await IdentityHttpTest.Code(await client.PostAsync("/api/auth/challenge", new ChunkedBody(new byte[17000])),
            413, "request-too-large");
    }

    [PostgresFact]
    public async Task Auth_429_preserves_code_and_RetryAfter_through_Response_Clear()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var factory = IdentityHttpTest.Factory(database, Guid.NewGuid());
        using var client = IdentityHttpTest.Client(factory);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/challenge", new { address = "invalid" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/auth/challenge", new { address = "invalid" });
        await IdentityHttpTest.Code(response, 429, "rate-limited");
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [PostgresFact]
    public async Task Readiness_checks_both_schemas_and_requires_an_active_operator_wallet()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var database = await IdentityDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var settings = database.ConfigurationValues(owner);
        settings["ConnectionStrings:Vessel"] = journal.ConnectionString;
        await using var factory = new CoreApiFactory(owner, connection: journal.ConnectionString, settings: settings);
        using var client = factory.CreateClient();
        var noWallet = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noWallet.StatusCode);
        Assert.Equal("""{"status":"unavailable","reason":"no-operator-wallet"}""", await noWallet.Content.ReadAsStringAsync());
        await database.SeedAsync(EthECKey.GenerateKey(), owner);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var db = database.CreateDbContext();
        await db.Users.ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "suspended"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [PostgresFact]
    public async Task Token_readiness_is_unavailable_when_only_the_identity_schema_is_missing()
    {
        await using var journal = await CoreDatabase.CreateAsync();
        await using var factory = new CoreApiFactory(Guid.NewGuid(), connection: journal.ConnectionString,
            settings: new() { ["Vessel:Identity:Schema"] = "missing_identity_" + Guid.NewGuid().ToString("N") });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"unavailable"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    private sealed class ChunkedBody(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
