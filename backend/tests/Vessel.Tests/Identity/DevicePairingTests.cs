using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

/// <summary>Device pairing (device-pairing-plan.md): every state transition and failure over the real HTTP stack.</summary>
public sealed class DevicePairingTests
{
    private const string PhoneAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>A signed-in desktop over a disposable identity schema, with the injected clock shared by sessions and pairings.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required IdentityDatabase Db { get; init; }
        public required CoreApiFactory Factory { get; init; }
        public required HttpClient Desktop { get; init; }
        public required EthECKey Key { get; init; }
        public required (IdentityUser User, UserWallet Wallet) Owner { get; init; }
        public required SessionDto Session { get; init; }

        public static async Task<Rig> CreateAsync(string role = "operator")
        {
            var db = await IdentityDatabase.CreateAsync();
            var key = EthECKey.GenerateKey();
            var owner = await db.SeedAsync(key, role: role);
            var factory = IdentityHttpTest.Factory(db, owner.User.Id);
            var desktop = IdentityHttpTest.Client(factory);
            var session = await IdentityHttpTest.SignInAsync(desktop, key);
            return new() { Db = db, Factory = factory, Desktop = desktop, Key = key, Owner = owner, Session = session };
        }

        public HttpClient Phone(string origin = "https://vessel.test")
        {
            var client = IdentityHttpTest.Client(Factory, origin);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", PhoneAgent);
            return client;
        }

        /// <summary>A second desktop session of the same user.</summary>
        public async Task<HttpClient> AnotherDesktopAsync()
        {
            var client = IdentityHttpTest.Client(Factory);
            await IdentityHttpTest.SignInAsync(client, Key);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            Desktop.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private static async Task GrantAsync(HttpClient client, EthECKey signer)
    {
        var challenge = await client.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "device.pair", address = signer.GetPublicAddress() });
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        var body = (await challenge.Content.ReadFromJsonAsync<ChallengeDto>())!;
        Assert.Contains("Confirm connecting a new device to your Vessel journal. This is not a transaction.", body.Message);
        var stepUp = await client.PostAsJsonAsync("/api/auth/step-up", new { challengeId = body.ChallengeId, signature = IdentityDatabase.Sign(signer, body.Message) });
        Assert.Equal(HttpStatusCode.NoContent, stepUp.StatusCode);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient desktop, EthECKey signer)
    {
        await GrantAsync(desktop, signer);
        var response = await desktop.PostAsync("/api/auth/pairings", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> ClaimAsync(HttpClient phone, string secret) =>
        phone.PostAsJsonAsync("/api/auth/pairings/claim", new { secret });

    private static async Task<JsonElement> ClaimedAsync(HttpClient phone, string secret)
    {
        var response = await ClaimAsync(phone, secret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient phone, bool remember = false) =>
        phone.PostAsJsonAsync("/api/auth/pairings/complete", new { remember });

    /// <summary>The ProblemDetails body of a refusal, read once (<see cref="IdentityHttpTest.Code"/> consumes it).</summary>
    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static async Task<string> StatusAsync(HttpClient desktop, Guid id)
    {
        var response = await desktop.GetAsync($"/api/auth/pairings/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient desktop, Guid id, string code) =>
        desktop.PostAsJsonAsync($"/api/auth/pairings/{id}/approve", new { code });

    private static async Task<string> CompleteStatusAsync(HttpClient phone)
    {
        var response = await CompleteAsync(phone);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }

    [PostgresFact]
    public async Task A_phone_pairs_through_the_QR_secret_and_receives_its_own_session()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        var secret = created.GetProperty("secret").GetString()!;
        Assert.Equal(43, secret.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", created.GetProperty("typedCode").GetString()!);
        Assert.Equal(rig.Db.Clock.Now + Window, created.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("pending", await StatusAsync(rig.Desktop, id));

        using var phone = rig.Phone();
        var claimed = await ClaimedAsync(phone, secret);
        Assert.Matches("^[0-9]{6}$", claimed.GetProperty("code").GetString()!);
        Assert.Equal(rig.Db.Clock.Now + Window, claimed.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("Synthetic owner", claimed.GetProperty("journal").GetProperty("displayName").GetString());
        Assert.Equal(rig.Key.GetPublicAddress(), claimed.GetProperty("journal").GetProperty("wallet").GetString());
        var status = await (await rig.Desktop.GetAsync($"/api/auth/pairings/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claimed", status.GetProperty("status").GetString());
        Assert.Equal("Safari on iOS", status.GetProperty("device").GetString());
        Assert.Equal("waiting", await CompleteStatusAsync(phone));

        // The secret was spent by the claim: it cannot claim a second time (contesting is the second scan).
        var approve = await ApproveAsync(rig.Desktop, id, claimed.GetProperty("code").GetString()!);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Equal("approved", await StatusAsync(rig.Desktop, id));

        rig.Db.Clock.Now += TimeSpan.FromSeconds(30);
        var complete = await CompleteAsync(phone, remember: true);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.True(complete.Headers.CacheControl?.NoStore);
        var cookies = string.Join("\n", complete.Headers.GetValues("Set-Cookie"));
        Assert.Contains("vessel-session=", cookies);
        Assert.Matches(@"vessel-signin=; expires=", cookies);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("signed-in", body.GetProperty("status").GetString());
        var session = body.GetProperty("session").Deserialize<SessionDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(rig.Owner.User.Id, session.User.Id);
        Assert.Equal(rig.Owner.Wallet.Id, session.Wallet.Id);
        Assert.NotEqual(rig.Session.Session.Id, session.Session.Id);

        // The phone's cookie is a normal full session of the same user.
        var current = await phone.GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(session.Session.Id, (await current.Content.ReadFromJsonAsync<SessionDto>())!.Session.Id);
        await using var db = rig.Db.CreateDbContext();
        var row = await db.Sessions.SingleAsync(x => x.Id == session.Session.Id);
        Assert.Equal(("pairing", true, "Safari on iOS", rig.Owner.Wallet.Id), (row.Source, row.Remember, row.UserAgentSummary, row.WalletId));
        Assert.Equal("wallet", (await db.Sessions.SingleAsync(x => x.Id == rig.Session.Session.Id)).Source);
        Assert.Equal("completed", await StatusAsync(rig.Desktop, id));

        // The sessions list labels the source and the desktop's own row stays "wallet".
        var rows = (await rig.Desktop.GetFromJsonAsync<SessionRowDto[]>("/api/auth/sessions"))!;
        Assert.Equal(["pairing", "wallet"], rows.Select(x => x.Source).Order().ToArray());
        // A completed pairing cannot be replayed: the binding cookie is gone, and the same one would match a used pairing.
        await IdentityHttpTest.Code(await CompleteAsync(phone), 400, "pairing-invalid");
        // The step-up grant was spent by the creation.
        var again = await ProblemAsync(await rig.Desktop.PostAsync("/api/auth/pairings", null), 403, "step-up-required");
        Assert.Equal("device.pair", again.GetProperty("action").GetString());
    }

    [PostgresTheory]
    [InlineData(true, 10, 30)]
    [InlineData(false, 0, 0)]
    public async Task The_paired_session_uses_the_same_lifetimes_as_a_wallet_session(bool remember, int idleDays, int absoluteDays)
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        using var phone = rig.Phone();
        var claimed = await ClaimedAsync(phone, created.GetProperty("secret").GetString()!);
        Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, created.GetProperty("id").GetGuid(), claimed.GetProperty("code").GetString()!)).StatusCode);
        var body = await (await CompleteAsync(phone, remember)).Content.ReadFromJsonAsync<JsonElement>();
        var session = body.GetProperty("session").Deserialize<SessionDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Session;
        Assert.Equal(remember, session.Remember);
        Assert.Equal(remember ? TimeSpan.FromDays(idleDays) : TimeSpan.FromHours(12), session.IdleExpiresAt - session.CreatedAt);
        Assert.Equal(remember ? TimeSpan.FromDays(absoluteDays) : TimeSpan.FromHours(24), session.AbsoluteExpiresAt - session.CreatedAt);
    }

    [Fact]
    public void Remembered_sessions_default_to_ten_days_idle_and_thirty_absolute()
    {
        var settings = new IdentitySettings();
        Assert.Equal(TimeSpan.FromDays(10), settings.IdleLifetime(true));
        Assert.Equal(TimeSpan.FromDays(30), settings.AbsoluteLifetime(true));
        var read = IdentitySettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Identity:Origins:0"] = "https://vessel.test", ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString()
        }).Build(), development: false);
        Assert.Equal((10, 30), (read.RememberIdleDays, read.RememberAbsoluteDays));
    }

    [PostgresFact]
    public async Task A_phone_pairs_with_the_typed_code_and_the_desktop_approves_with_spaces_in_the_code()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        // Lower case, no dash and stray spaces are all read as the displayed code.
        var typed = created.GetProperty("typedCode").GetString()!.ToLowerInvariant().Replace("-", " ");
        using var phone = rig.Phone();
        var response = await phone.PostAsJsonAsync("/api/auth/pairings/claim", new { typedCode = $" {typed} " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var code = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, id, $"{code[..3]} {code[3..]}")).StatusCode);
        Assert.Equal("signed-in", await CompleteStatusAsync(phone));
    }

    [Theory]
    [InlineData("7KQ2M-HX9PD", "7KQ2MHX9PD")]
    [InlineData("7kq2m hx9pd", "7KQ2MHX9PD")]
    [InlineData("  7kq2m-hx9pd\t", "7KQ2MHX9PD")]
    [InlineData("OIL0O-1l1il", "0110011111")]
    [InlineData("7KQ2M-HX9P", null)]
    [InlineData("7KQ2M-HX9PDD", null)]
    [InlineData("7KQ2U-HX9PD", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Typed_codes_normalize_like_invitations(string? input, string? expected) =>
        Assert.Equal(expected, PairingCodes.NormalizeTyped(input));

    [PostgresFact]
    public async Task Typed_code_normalization_reaches_the_stored_hash_over_HTTP()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        await using (var db = rig.Db.CreateDbContext())
        {
            var pairing = await db.Pairings.SingleAsync();
            pairing.TypedCodeHash = IdentityTokens.Hash("0110011111");
            await db.SaveChangesAsync();
        }
        using var phone = rig.Phone();
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync("/api/auth/pairings/claim", new { typedCode = "OIL0O-1l1il" })).StatusCode);
        Assert.Equal("claimed", await StatusAsync(rig.Desktop, created.GetProperty("id").GetGuid()));
    }

    [PostgresFact]
    public async Task Claims_name_exactly_one_credential_and_unknown_or_malformed_ones_are_all_pairing_invalid()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        using var phone = rig.Phone();
        var secret = created.GetProperty("secret").GetString()!;
        await IdentityHttpTest.Code(await phone.PostAsJsonAsync("/api/auth/pairings/claim", new { }), 400, "request-invalid");
        await IdentityHttpTest.Code(await phone.PostAsJsonAsync("/api/auth/pairings/claim",
            new { secret, typedCode = created.GetProperty("typedCode").GetString() }), 400, "request-invalid");
        foreach (var body in new object[] { new { secret = IdentityTokens.Create() }, new { secret = "short" }, new { typedCode = "ZZZZZ-ZZZZZ" },
            new { typedCode = "nonsense" }, new { typedCode = secret } })
            await IdentityHttpTest.Code(await phone.PostAsJsonAsync("/api/auth/pairings/claim", body), 400, "pairing-invalid");
        // None of that touched the real pairing.
        Assert.Equal("pending", await StatusAsync(rig.Desktop, created.GetProperty("id").GetGuid()));
    }

    [PostgresFact]
    public async Task A_claim_from_another_origin_is_refused_and_the_pairing_stays_claimable()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var secret = created.GetProperty("secret").GetString()!;
        using var other = rig.Phone("http://localhost");
        await IdentityHttpTest.Code(await ClaimAsync(other, secret), 400, "pairing-invalid");
        await IdentityHttpTest.Code(await other.PostAsJsonAsync("/api/auth/pairings/claim", new { typedCode = created.GetProperty("typedCode").GetString() }), 400, "pairing-invalid");
        Assert.Equal("pending", await StatusAsync(rig.Desktop, created.GetProperty("id").GetGuid()));
        using var phone = rig.Phone();
        await ClaimedAsync(phone, secret);
    }

    [PostgresFact]
    public async Task Public_pairing_routes_follow_the_sign_in_request_guard()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        Assert.True(IdentityHttp.PublicRoute("/api/auth/pairings/claim"));
        Assert.True(IdentityHttp.PublicRoute("/api/auth/pairings/complete/"));
        Assert.False(IdentityHttp.PublicRoute("/api/auth/pairings"));
        Assert.False(IdentityHttp.PublicRoute($"/api/auth/pairings/{Guid.NewGuid()}/approve"));
        foreach (var path in new[] { "/api/auth/pairings/claim", "/api/auth/pairings/complete" })
        {
            var body = path.EndsWith("claim") ? (object)new { secret = created.GetProperty("secret").GetString() } : new { remember = false };
            using var noMarker = new HttpClient(rig.Factory.Server.CreateHandler()) { BaseAddress = new("https://vessel.test") };
            noMarker.DefaultRequestHeaders.Add("Origin", "https://vessel.test");
            await IdentityHttpTest.Code(await noMarker.PostAsJsonAsync(path, body), 403, "csrf-rejected");
            using var badOrigin = rig.Phone("https://vessel.test");
            badOrigin.DefaultRequestHeaders.Remove("Origin");
            badOrigin.DefaultRequestHeaders.Add("Origin", "https://evil.test");
            await IdentityHttpTest.Code(await badOrigin.PostAsJsonAsync(path, body), 403, "origin-rejected");
            using var crossSite = rig.Phone();
            crossSite.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
            await IdentityHttpTest.Code(await crossSite.PostAsJsonAsync(path, body), 403, "csrf-rejected");
            using var big = rig.Phone();
            var huge = await big.PostAsync(path, new StringContent(new string(' ', 20_000), System.Text.Encoding.UTF8, "application/json"));
            await IdentityHttpTest.Code(huge, 413, "request-too-large");
        }
        Assert.Equal("pending", await StatusAsync(rig.Desktop, created.GetProperty("id").GetGuid()));
    }

    [PostgresFact]
    public async Task Complete_needs_the_claiming_browsers_binding_cookie()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        using var phone = rig.Phone();
        // No binding cookie yet, and nothing claimed.
        await IdentityHttpTest.Code(await CompleteAsync(phone), 400, "pairing-invalid");
        var claimed = await ClaimedAsync(phone, created.GetProperty("secret").GetString()!);
        Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, id, claimed.GetProperty("code").GetString()!)).StatusCode);
        // Another browser has a binding of its own (from its own claim attempt) and matches no claimed pairing.
        using var thief = rig.Phone();
        await IdentityHttpTest.Code(await thief.PostAsJsonAsync("/api/auth/pairings/claim", new { secret = IdentityTokens.Create() }), 400, "pairing-invalid");
        await IdentityHttpTest.Code(await CompleteAsync(thief), 400, "pairing-invalid");
        await using (var db = rig.Db.CreateDbContext()) Assert.Equal("approved", (await db.Pairings.SingleAsync()).Status);
        Assert.Equal("signed-in", await CompleteStatusAsync(phone));
    }

    [PostgresFact]
    public async Task A_second_claim_contests_the_pairing_and_neither_phone_gets_in()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        var secret = created.GetProperty("secret").GetString()!;
        using var first = rig.Phone();
        var claimed = await ClaimedAsync(first, secret);
        using var second = rig.Phone();
        await IdentityHttpTest.Code(await ClaimAsync(second, secret), 409, "pairing-contested");
        Assert.Equal("contested", await StatusAsync(rig.Desktop, id));
        Assert.Equal("contested", await CompleteStatusAsync(first));
        await IdentityHttpTest.Code(await CompleteAsync(second), 400, "pairing-invalid");
        await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, claimed.GetProperty("code").GetString()!), 400, "pairing-invalid");
        await IdentityHttpTest.Code(await ClaimAsync(second, secret), 400, "pairing-invalid");
        await using var db = rig.Db.CreateDbContext();
        Assert.Empty(await db.Sessions.Where(x => x.Source == "pairing").ToListAsync());
    }

    [PostgresFact]
    public async Task A_typed_code_never_contests_a_claimed_pairing()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        using var phone = rig.Phone();
        await ClaimedAsync(phone, created.GetProperty("secret").GetString()!);
        using var guesser = rig.Phone();
        await IdentityHttpTest.Code(await guesser.PostAsJsonAsync("/api/auth/pairings/claim",
            new { typedCode = created.GetProperty("typedCode").GetString() }), 400, "pairing-invalid");
        Assert.Equal("claimed", await StatusAsync(rig.Desktop, created.GetProperty("id").GetGuid()));
        Assert.Equal("waiting", await CompleteStatusAsync(phone));
    }

    [PostgresFact]
    public async Task Three_wrong_confirmation_codes_cancel_the_pairing()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        using var phone = rig.Phone();
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        var wrong = code == "000000" ? "111111" : "000000";
        foreach (var left in new[] { 2, 1, 0 })
        {
            var problem = await ProblemAsync(await ApproveAsync(rig.Desktop, id, wrong), 400, "pairing-code-invalid");
            Assert.Equal(left, problem.GetProperty("attemptsLeft").GetInt32());
        }
        Assert.Equal("cancelled", await StatusAsync(rig.Desktop, id));
        // Even the right code is too late, and the phone is told.
        await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, code), 400, "pairing-invalid");
        Assert.Equal("cancelled", await CompleteStatusAsync(phone));
        await using var db = rig.Db.CreateDbContext();
        Assert.Equal(3, (await db.Pairings.SingleAsync()).CodeAttempts);
    }

    [PostgresFact]
    public async Task A_malformed_confirmation_code_is_a_bad_request_that_does_not_count_as_an_attempt()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        using var phone = rig.Phone();
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        foreach (var bad in new[] { "12345", "1234567", "12345a", "" })
            await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, bad), 400, "request-invalid");
        await IdentityHttpTest.Code(await rig.Desktop.PostAsJsonAsync($"/api/auth/pairings/{id}/approve", new { }), 400, "request-invalid");
        Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, id, code)).StatusCode);
        await using var db = rig.Db.CreateDbContext();
        Assert.Equal(0, (await db.Pairings.SingleAsync()).CodeAttempts);
    }

    [PostgresFact]
    public async Task Approval_only_applies_to_a_claimed_pairing()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, "123456"), 400, "pairing-invalid");
        using var phone = rig.Phone();
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, id, code)).StatusCode);
        // Approved already: a second approval, right or wrong, is invalid and changes nothing.
        await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, code), 400, "pairing-invalid");
        await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, "123456"), 400, "pairing-invalid");
        Assert.Equal("approved", await StatusAsync(rig.Desktop, id));
    }

    [PostgresTheory]
    [InlineData("pending")]
    [InlineData("claimed")]
    [InlineData("approved")]
    public async Task Each_state_expires_two_minutes_after_it_began(string state)
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        var secret = created.GetProperty("secret").GetString()!;
        using var phone = rig.Phone();
        var code = "";
        if (state != "pending")
        {
            rig.Db.Clock.Now += TimeSpan.FromSeconds(90);
            code = (await ClaimedAsync(phone, secret)).GetProperty("code").GetString()!;
        }
        if (state == "approved")
        {
            rig.Db.Clock.Now += TimeSpan.FromSeconds(90);
            Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, id, code)).StatusCode);
        }
        // One second before the deadline the state still works; the deadline itself has passed.
        rig.Db.Clock.Now += Window - TimeSpan.FromSeconds(1);
        var stillOpen = await rig.Desktop.GetFromJsonAsync<JsonElement>($"/api/auth/pairings/{id}");
        Assert.Equal(state, stillOpen.GetProperty("status").GetString());
        Assert.Equal(rig.Db.Clock.Now + TimeSpan.FromSeconds(1), stillOpen.GetProperty("expiresAt").GetDateTimeOffset());
        rig.Db.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal("expired", await StatusAsync(rig.Desktop, id));
        switch (state)
        {
            case "pending":
                await IdentityHttpTest.Code(await ClaimAsync(phone, secret), 400, "pairing-expired");
                await IdentityHttpTest.Code(await phone.PostAsJsonAsync("/api/auth/pairings/claim",
                    new { typedCode = created.GetProperty("typedCode").GetString() }), 400, "pairing-expired");
                break;
            case "claimed":
                await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, code), 400, "pairing-expired");
                Assert.Equal("expired", await CompleteStatusAsync(phone));
                // A late second scan is expired, not a contest.
                using (var late = rig.Phone()) await IdentityHttpTest.Code(await ClaimAsync(late, secret), 400, "pairing-expired");
                break;
            default:
                Assert.Equal("expired", await CompleteStatusAsync(phone));
                break;
        }
        await using var db = rig.Db.CreateDbContext();
        Assert.Empty(await db.Sessions.Where(x => x.Source == "pairing").ToListAsync());
    }

    [PostgresFact]
    public async Task Creating_needs_a_live_device_pair_grant_and_cancels_the_users_earlier_pairings()
    {
        await using var rig = await Rig.CreateAsync();
        var none = await ProblemAsync(await rig.Desktop.PostAsync("/api/auth/pairings", null), 403, "step-up-required");
        Assert.Equal("device.pair", none.GetProperty("action").GetString());
        // A grant for another action does not serve.
        var challenge = await rig.Desktop.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "wallet.link", address = rig.Key.GetPublicAddress() });
        var link = (await challenge.Content.ReadFromJsonAsync<ChallengeDto>())!;
        await rig.Desktop.PostAsJsonAsync("/api/auth/step-up", new { challengeId = link.ChallengeId, signature = IdentityDatabase.Sign(rig.Key, link.Message) });
        await IdentityHttpTest.Code(await rig.Desktop.PostAsync("/api/auth/pairings", null), 403, "step-up-required");
        // An expired grant does not serve.
        await GrantAsync(rig.Desktop, rig.Key);
        rig.Db.Clock.Now += TimeSpan.FromMinutes(5);
        await IdentityHttpTest.Code(await rig.Desktop.PostAsync("/api/auth/pairings", null), 403, "step-up-required");
        // device.pair takes no target.
        await IdentityHttpTest.Code(await rig.Desktop.PostAsJsonAsync("/api/auth/step-up/challenge",
            new { action = "device.pair", address = rig.Key.GetPublicAddress(), target = Guid.NewGuid() }), 400, "request-invalid");

        var first = await CreateAsync(rig.Desktop, rig.Key);
        using var phone = rig.Phone();
        await ClaimedAsync(phone, first.GetProperty("secret").GetString()!);
        var second = await CreateAsync(rig.Desktop, rig.Key);
        Assert.Equal("cancelled", await StatusAsync(rig.Desktop, first.GetProperty("id").GetGuid()));
        Assert.Equal("cancelled", await CompleteStatusAsync(phone));
        Assert.Equal("pending", await StatusAsync(rig.Desktop, second.GetProperty("id").GetGuid()));
        await using var db = rig.Db.CreateDbContext();
        var row = await db.Pairings.SingleAsync(x => x.Id == second.GetProperty("id").GetGuid());
        Assert.Equal((rig.Owner.User.Id, rig.Session.Session.Id, rig.Owner.Wallet.Id, "https://vessel.test"),
            (row.UserId, row.CreatedSessionId, row.SignerWalletId, row.Origin));
    }

    [PostgresFact]
    public async Task Only_hashes_of_the_secrets_are_stored()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        using var phone = rig.Phone();
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        await using var db = rig.Db.CreateDbContext();
        var row = await db.Pairings.SingleAsync();
        Assert.Equal(IdentityTokens.Hash(created.GetProperty("secret").GetString()!), row.SecretHash);
        Assert.Equal(IdentityTokens.Hash(created.GetProperty("typedCode").GetString()!.Replace("-", "")), row.TypedCodeHash);
        Assert.NotEqual(IdentityTokens.Hash(code), row.CodeHash);
        Assert.Equal(32, row.CodeHash!.Length);
        Assert.Equal(32, row.BindingHash!.Length);
        Assert.Equal("Safari on iOS", row.DeviceSummary);
    }

    [PostgresFact]
    public async Task Status_approve_and_cancel_answer_404_to_any_other_session()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        using var phone = rig.Phone();
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;

        var sibling = await rig.AnotherDesktopAsync();
        var strangerKey = EthECKey.GenerateKey();
        await rig.Db.SeedAsync(strangerKey, role: "member");
        var stranger = IdentityHttpTest.Client(rig.Factory);
        await IdentityHttpTest.SignInAsync(stranger, strangerKey);
        foreach (var other in new[] { sibling, stranger })
        {
            var unknown = Guid.NewGuid();
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Get, "{0}", null), (HttpMethod.Post, "{0}/approve", new { code }), (HttpMethod.Delete, "{0}", null)
            })
            {
                var foreignResponse = await other.SendAsync(Request(method, $"/api/auth/pairings/{string.Format(path, id)}", body));
                var unknownResponse = await other.SendAsync(Request(method, $"/api/auth/pairings/{string.Format(path, unknown)}", body));
                Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
                var a = await foreignResponse.Content.ReadFromJsonAsync<JsonElement>();
                var b = await unknownResponse.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
                Assert.Equal("not-found", a.GetProperty("code").GetString());
                foreach (var field in new[] { "status", "title", "detail", "code" }) Assert.Equal(b.GetProperty(field).ToString(), a.GetProperty(field).ToString());
            }
        }
        // Nothing changed for the creating session.
        Assert.Equal("claimed", await StatusAsync(rig.Desktop, id));
        Assert.Equal(HttpStatusCode.NoContent, (await ApproveAsync(rig.Desktop, id, code)).StatusCode);
        sibling.Dispose();
        stranger.Dispose();

        static HttpRequestMessage Request(HttpMethod method, string path, object? body) =>
            new(method, path) { Content = body is null ? null : JsonContent.Create(body) };
    }

    [PostgresFact]
    public async Task Cancel_is_idempotent_for_unfinished_pairings_and_tells_the_phone()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var id = created.GetProperty("id").GetGuid();
        using var phone = rig.Phone();
        await ClaimedAsync(phone, created.GetProperty("secret").GetString()!);
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Desktop.DeleteAsync($"/api/auth/pairings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Desktop.DeleteAsync($"/api/auth/pairings/{id}")).StatusCode);
        Assert.Equal("cancelled", await StatusAsync(rig.Desktop, id));
        Assert.Equal("cancelled", await CompleteStatusAsync(phone));
        await IdentityHttpTest.Code(await ClaimAsync(rig.Phone(), created.GetProperty("secret").GetString()!), 400, "pairing-invalid");
        // A finished (completed) pairing is not turned into a cancelled one.
        var next = await CreateAsync(rig.Desktop, rig.Key);
        var nextId = next.GetProperty("id").GetGuid();
        using var second = rig.Phone();
        var code = (await ClaimedAsync(second, next.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        await ApproveAsync(rig.Desktop, nextId, code);
        Assert.Equal("signed-in", await CompleteStatusAsync(second));
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Desktop.DeleteAsync($"/api/auth/pairings/{nextId}")).StatusCode);
        Assert.Equal("completed", await StatusAsync(rig.Desktop, nextId));
    }

    [PostgresFact]
    public async Task Completing_replaces_a_session_cookie_the_phone_already_sent()
    {
        await using var rig = await Rig.CreateAsync();
        var otherKey = EthECKey.GenerateKey();
        await rig.Db.SeedAsync(otherKey, role: "member");
        using var phone = rig.Phone();
        var existing = await IdentityHttpTest.SignInAsync(phone, otherKey);
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        await ApproveAsync(rig.Desktop, created.GetProperty("id").GetGuid(), code);
        Assert.Equal("signed-in", await CompleteStatusAsync(phone));
        await using var db = rig.Db.CreateDbContext();
        var old = await db.Sessions.SingleAsync(x => x.Id == existing.Session.Id);
        Assert.Equal("replaced", old.RevokeReason);
        var current = await phone.GetFromJsonAsync<SessionDto>("/api/auth/session");
        Assert.Equal(rig.Owner.User.Id, current!.User.Id);
    }

    [PostgresFact]
    public async Task Removing_the_signer_wallet_cancels_the_open_pairing_and_the_phone_is_told()
    {
        await using var rig = await Rig.CreateAsync();
        var second = EthECKey.GenerateKey();
        var secondWallet = new UserWallet { Id = Guid.NewGuid(), UserId = rig.Owner.User.Id, Address = second.GetPublicAddress().ToLowerInvariant(), AddedAt = rig.Db.Clock.Now };
        await using (var db = rig.Db.CreateDbContext()) { db.Wallets.Add(secondWallet); await db.SaveChangesAsync(); }
        // The second wallet signs the device.pair step-up, so it is the pairing's signer.
        var created = await CreateAsync(rig.Desktop, second);
        var id = created.GetProperty("id").GetGuid();
        using var phone = rig.Phone();
        var claimed = await ClaimedAsync(phone, created.GetProperty("secret").GetString()!);
        Assert.Equal(second.GetPublicAddress(), claimed.GetProperty("journal").GetProperty("wallet").GetString());
        var challenge = await rig.Desktop.PostAsJsonAsync("/api/auth/step-up/challenge",
            new { action = "wallet.remove", target = secondWallet.Id, address = rig.Key.GetPublicAddress() });
        var remove = (await challenge.Content.ReadFromJsonAsync<ChallengeDto>())!;
        await rig.Desktop.PostAsJsonAsync("/api/auth/step-up", new { challengeId = remove.ChallengeId, signature = IdentityDatabase.Sign(rig.Key, remove.Message) });
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Desktop.DeleteAsync($"/api/auth/wallets/{secondWallet.Id}")).StatusCode);
        Assert.Equal("cancelled", await StatusAsync(rig.Desktop, id));
        Assert.Equal("cancelled", await CompleteStatusAsync(phone));
        await using var verify = rig.Db.CreateDbContext();
        var row = await verify.Pairings.SingleAsync();
        Assert.Equal(("cancelled", (Guid?)null), (row.Status, row.SignerWalletId));
        await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, id, claimed.GetProperty("code").GetString()!), 400, "pairing-invalid");
    }

    [PostgresFact]
    public async Task Removing_any_wallet_cancels_all_the_users_open_pairings()
    {
        await using var rig = await Rig.CreateAsync();
        var second = EthECKey.GenerateKey();
        var secondWallet = new UserWallet { Id = Guid.NewGuid(), UserId = rig.Owner.User.Id, Address = second.GetPublicAddress().ToLowerInvariant(), AddedAt = rig.Db.Clock.Now };
        await using (var db = rig.Db.CreateDbContext()) { db.Wallets.Add(secondWallet); await db.SaveChangesAsync(); }
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var challenge = await rig.Desktop.PostAsJsonAsync("/api/auth/step-up/challenge",
            new { action = "wallet.remove", target = secondWallet.Id, address = rig.Key.GetPublicAddress() });
        var remove = (await challenge.Content.ReadFromJsonAsync<ChallengeDto>())!;
        await rig.Desktop.PostAsJsonAsync("/api/auth/step-up", new { challengeId = remove.ChallengeId, signature = IdentityDatabase.Sign(rig.Key, remove.Message) });
        await rig.Desktop.DeleteAsync($"/api/auth/wallets/{secondWallet.Id}");
        Assert.Equal("cancelled", await StatusAsync(rig.Desktop, created.GetProperty("id").GetGuid()));
    }

    [PostgresFact]
    public async Task Suspension_revoke_sessions_and_unbind_wallet_cancel_open_pairings()
    {
        await using var rig = await Rig.CreateAsync(role: "member");
        var second = EthECKey.GenerateKey();
        var secondWallet = new UserWallet { Id = Guid.NewGuid(), UserId = rig.Owner.User.Id, Address = second.GetPublicAddress().ToLowerInvariant(), AddedAt = rig.Db.Clock.Now };
        await using (var db = rig.Db.CreateDbContext()) { db.Wallets.Add(secondWallet); await db.SaveChangesAsync(); }
        var config = rig.Db.Configuration(rig.Owner.User.Id);
        var user = rig.Owner.User.Id.ToString();

        async Task<(Guid Id, HttpClient Phone)> OpenClaimedAsync()
        {
            var desktop = await rig.AnotherDesktopAsync();
            var created = await CreateAsync(desktop, rig.Key);
            var phone = rig.Phone();
            await ClaimedAsync(phone, created.GetProperty("secret").GetString()!);
            return (created.GetProperty("id").GetGuid(), phone);
        }
        async Task<string> Row(Guid id)
        {
            await using var db = rig.Db.CreateDbContext();
            return (await db.Pairings.AsNoTracking().SingleAsync(x => x.Id == id)).Status;
        }

        foreach (var args in new[]
        {
            new[] { "revoke-sessions", "--user", user }, ["revoke-sessions", "--all"], ["suspend", "--user", user],
            ["unbind-wallet", "--wallet", secondWallet.Id.ToString()]
        })
        {
            var (id, phone) = await OpenClaimedAsync();
            Assert.Equal("claimed", await Row(id));
            Assert.Equal(0, await IdentityCommands.RunAsync(args, config, true, new StringWriter()));
            Assert.Equal("cancelled", await Row(id));
            Assert.Equal("cancelled", await CompleteStatusAsync(phone));
            phone.Dispose();
            if (args[0] == "suspend") Assert.Equal(0, await IdentityCommands.RunAsync(["restore", "--user", user], config, true, new StringWriter()));
        }
    }

    [PostgresFact]
    public async Task A_suspended_user_cannot_be_paired_in_and_the_restore_reset_deletes_all_pairings()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        using var phone = rig.Phone();
        var code = (await ClaimedAsync(phone, created.GetProperty("secret").GetString()!)).GetProperty("code").GetString()!;
        await ApproveAsync(rig.Desktop, created.GetProperty("id").GetGuid(), code);
        // Suspended directly (not through the command) between approval and completion: nobody signs in.
        await using (var db = rig.Db.CreateDbContext())
            await db.Users.ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "suspended"));
        Assert.Equal("cancelled", await CompleteStatusAsync(phone));
        await using (var db = rig.Db.CreateDbContext())
        {
            Assert.Equal("cancelled", (await db.Pairings.SingleAsync()).Status);
            Assert.Empty(await db.Sessions.Where(x => x.Source == "pairing").ToListAsync());
            await db.Users.ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "active"));
        }
        await CreateAsync(rig.Desktop, rig.Key);
        Assert.Equal(0, await IdentityCommands.RunAsync(["reset-after-restore", "--keep-active", rig.Owner.User.Id.ToString()],
            rig.Db.Configuration(rig.Owner.User.Id), true, new StringWriter()));
        await using var verify = rig.Db.CreateDbContext();
        Assert.Empty(await verify.Pairings.ToListAsync());
    }

    [PostgresFact]
    public async Task The_oldest_unfinished_pairings_are_evicted_at_the_global_cap()
    {
        await using var rig = await Rig.CreateAsync();
        var otherKey = EthECKey.GenerateKey();
        var other = await rig.Db.SeedAsync(otherKey, role: "member");
        var otherSession = await rig.Db.SignInAsync(otherKey);
        var start = rig.Db.Clock.Now.AddSeconds(-60);
        var rows = Enumerable.Range(0, DevicePairingCap).Select(i => new DevicePairing
        {
            Id = Guid.NewGuid(), UserId = other.User.Id, CreatedSessionId = otherSession.Session.Session.Id, SignerWalletId = other.Wallet.Id,
            SecretHash = RandomNumberGenerator.GetBytes(32), TypedCodeHash = RandomNumberGenerator.GetBytes(32), Origin = "https://vessel.test",
            CreatedAt = start.AddMilliseconds(i), ExpiresAt = rig.Db.Clock.Now.AddMinutes(1)
        }).ToList();
        await using (var db = rig.Db.CreateDbContext()) { db.Pairings.AddRange(rows); await db.SaveChangesAsync(); }
        var created = await CreateAsync(rig.Desktop, rig.Key);
        await using var verify = rig.Db.CreateDbContext();
        var open = await verify.Pairings.Where(x => x.Status != "cancelled" && x.Status != "expired").ToListAsync();
        Assert.Equal(DevicePairingCap, open.Count);
        Assert.Contains(open, x => x.Id == created.GetProperty("id").GetGuid());
        Assert.Equal("cancelled", (await verify.Pairings.SingleAsync(x => x.Id == rows[0].Id)).Status);
        Assert.Equal("pending", (await verify.Pairings.SingleAsync(x => x.Id == rows[1].Id)).Status);
    }

    private const int DevicePairingCap = 1000;

    [PostgresFact]
    public async Task Expired_unfinished_pairings_stop_counting_toward_the_cap_and_cleanup_removes_old_rows()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await CreateAsync(rig.Desktop, rig.Key);
        var first = created.GetProperty("id").GetGuid();
        rig.Db.Clock.Now += TimeSpan.FromMinutes(10);
        var next = await CreateAsync(rig.Desktop, rig.Key);
        await using (var db = rig.Db.CreateDbContext())
            Assert.Equal("expired", (await db.Pairings.SingleAsync(x => x.Id == first)).Status);
        var cleanup = new IdentityCleanup(rig.Db, rig.Db.Clock, NullLogger<IdentityCleanup>.Instance);
        await cleanup.CleanAsync(default);
        await using (var db = rig.Db.CreateDbContext()) Assert.Equal(2, await db.Pairings.CountAsync());
        // Finished and expired rows go a day after their last deadline; the live one stays.
        rig.Db.Clock.Now += TimeSpan.FromDays(1) - TimeSpan.FromMinutes(5);
        await rig.Desktop.DeleteAsync($"/api/auth/pairings/{next.GetProperty("id").GetGuid()}");
        await cleanup.CleanAsync(default);
        await using (var db = rig.Db.CreateDbContext()) Assert.Equal(1, await db.Pairings.CountAsync(x => x.Id == next.GetProperty("id").GetGuid()));
        rig.Db.Clock.Now += TimeSpan.FromMinutes(10);
        await cleanup.CleanAsync(default);
        await using (var db = rig.Db.CreateDbContext()) Assert.Empty(await db.Pairings.ToListAsync());
    }

    [PostgresFact]
    public async Task Create_and_approve_share_one_per_session_bucket_and_claim_and_complete_are_limited_per_client()
    {
        await using var rig = await Rig.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await IdentityHttpTest.Code(await rig.Desktop.PostAsync("/api/auth/pairings", null), 403, "step-up-required");
            await IdentityHttpTest.Code(await ApproveAsync(rig.Desktop, Guid.NewGuid(), "123456"), 404, "not-found");
        }
        foreach (var limited in new[] { await rig.Desktop.PostAsync("/api/auth/pairings", null), await ApproveAsync(rig.Desktop, Guid.NewGuid(), "123456") })
        {
            await IdentityHttpTest.Code(limited, 429, "rate-limited");
            Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 600);
        }
        // Another session has its own bucket, and the step-up bucket is untouched.
        var sibling = await rig.AnotherDesktopAsync();
        await IdentityHttpTest.Code(await sibling.PostAsync("/api/auth/pairings", null), 403, "step-up-required");
        Assert.Equal(HttpStatusCode.OK, (await rig.Desktop.PostAsJsonAsync("/api/auth/step-up/challenge",
            new { action = "device.pair", address = rig.Key.GetPublicAddress() })).StatusCode);

        using var phone = rig.Phone();
        for (var i = 0; i < 10; i++)
            await IdentityHttpTest.Code(await ClaimAsync(phone, IdentityTokens.Create()), 400, "pairing-invalid");
        var claimLimited = await ClaimAsync(phone, IdentityTokens.Create());
        await IdentityHttpTest.Code(claimLimited, 429, "rate-limited");
        Assert.True(claimLimited.Headers.RetryAfter!.Delta!.Value.TotalSeconds >= 1);
        // Complete has its own, larger allowance.
        for (var i = 0; i < 60; i++)
            await IdentityHttpTest.Code(await CompleteAsync(phone), 400, "pairing-invalid");
        await IdentityHttpTest.Code(await CompleteAsync(phone), 429, "rate-limited");
        sibling.Dispose();
    }

    [Fact]
    public void Claim_complete_and_pairing_session_limits_partition_as_specified()
    {
        static DefaultHttpContext Context(string path, string address, string? session = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
            if (session is not null)
                context.User = new(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(SessionAuthentication.SessionClaim, session)], SessionAuthentication.SchemeName));
            return context;
        }
        using var limits = new IdentityRateLimits();
        bool Acquire(DefaultHttpContext context) { using var lease = limits.Requests.AttemptAcquire(context); return lease.IsAcquired; }

        // Claim: 10 per minute per client, 300 per minute overall.
        for (var i = 0; i < 10; i++) Assert.True(Acquire(Context("/api/auth/pairings/claim", "192.0.2.1")));
        Assert.False(Acquire(Context("/api/auth/pairings/claim", "192.0.2.1")));
        for (var i = 0; i < 290; i++) Assert.True(Acquire(Context("/api/auth/pairings/claim", $"10.{i / 256}.{i % 256}.1")));
        Assert.False(Acquire(Context("/api/auth/pairings/claim", "192.0.2.9")));
        // Complete: 60 per minute per client and no global cap; it does not share the claim or sign-in buckets.
        for (var i = 0; i < 60; i++) Assert.True(Acquire(Context("/api/auth/pairings/complete", "192.0.2.2")));
        Assert.False(Acquire(Context("/api/auth/pairings/complete", "192.0.2.2")));
        for (var i = 0; i < 100; i++) Assert.True(Acquire(Context("/api/auth/pairings/complete", $"172.16.{i / 256}.{i % 256}")));
        Assert.True(Acquire(Context("/api/auth/challenge", "192.0.2.2")));
        // Create and approve: 10 per ten minutes per session, shared, and separate from step-up.
        var session = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(Acquire(Context("/api/auth/pairings", "192.0.2.3", session)));
            Assert.True(Acquire(Context($"/api/auth/pairings/{Guid.NewGuid()}/approve", "192.0.2.4", session)));
        }
        Assert.False(Acquire(Context("/api/auth/pairings", "192.0.2.3", session)));
        Assert.False(Acquire(Context($"/api/auth/pairings/{Guid.NewGuid()}/approve", "192.0.2.3", session)));
        Assert.True(Acquire(Context("/api/auth/step-up", "192.0.2.3", session)));
        Assert.True(Acquire(Context("/api/auth/pairings", "192.0.2.3", Guid.NewGuid().ToString())));
        // Status and cancel are not POSTs of the pairing buckets.
        var status = Context($"/api/auth/pairings/{Guid.NewGuid()}", "192.0.2.3", session);
        status.Request.Method = "GET";
        Assert.True(Acquire(status));
    }
}
