using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using Vessel.Application.Common;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class AccountRouteSecurityTests
{
    [PostgresFact]
    public async Task Every_A2_and_notification_route_requires_a_session_tab_binding_and_the_write_guard_and_returns_no_store()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        var id = Guid.NewGuid();
        (string Method, string Path, string? Body)[] routes =
        [
            ("GET", "/sessions", null), ("DELETE", $"/sessions/{id}", null), ("POST", "/sessions/revoke-others", null),
            ("PATCH", "/profile", "{\"displayName\":\"Name\",\"expectedRevision\":1}"), ("GET", "/wallets", null),
            ("POST", "/step-up/challenge", "{}"), ("POST", "/step-up", "{}"), ("POST", "/wallets/challenge", "{}"),
            ("POST", "/wallets", "{}"), ("PATCH", $"/wallets/{id}", "{\"label\":null}"), ("DELETE", $"/wallets/{id}", null),
            ("GET", "/api/notifications/head", null), ("GET", "/api/notifications", null),
            ("POST", "/api/notifications/claim", null), ("POST", $"/api/notifications/{id}/read", null),
            ("POST", "/api/notifications/read", "{\"upTo\":\"0.0.0\"}"),
            ("GET", "/api/notifications/preferences", null), ("PUT", "/api/notifications/preferences", "{}"),
            ("POST", "/api/notifications/test", null),
            ("GET", "/api/events/stream?after=0&notificationsAfter=0.0.0", null)
        ];
        using var client = factory.CreateClient(new() { BaseAddress = new("https://vessel.test"), HandleCookies = false });
        foreach (var (method, path, body) in routes)
        {
            async Task<HttpResponseMessage> Send(bool cookie, string? user, string? origin = "https://vessel.test", string? site = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method),
                    path.StartsWith("/api/", StringComparison.Ordinal) ? path : "/api/auth" + path);
                if (cookie) request.Headers.Add("Cookie", "__Host-vessel-session=" + issued.Token);
                if (user is not null) request.Headers.Add("X-Vessel-User", user);
                if (origin is not null) request.Headers.Add("Origin", origin);
                if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
                if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return await client.SendAsync(request);
            }
            await IdentityHttpTest.Code(await Send(false, null), 401, "session-required");
            await IdentityHttpTest.Code(await Send(true, null), 401, "session-user-mismatch");
            await IdentityHttpTest.Code(await Send(true, Guid.NewGuid().ToString()), 401, "session-user-mismatch");
            if (method == "GET") continue;
            await IdentityHttpTest.Code(await Send(true, owner.User.Id.ToString(), null), 403, "origin-rejected");
            await IdentityHttpTest.Code(await Send(true, owner.User.Id.ToString(), "null"), 403, "origin-rejected");
            await IdentityHttpTest.Code(await Send(true, owner.User.Id.ToString(), site: "cross-site"), 403, "csrf-rejected");
        }
    }

    [PostgresTheory]
    [InlineData("step-up", "wallets")]
    [InlineData("wallets", "step-up")]
    public async Task The_per_session_step_up_and_link_limits_are_independent_and_return_retry_after(string exhausted, string other)
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var first = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(first, key);
        foreach (var bucket in new[] { exhausted, other })
        {
            // Both routes spend the same bucket, even when the body is invalid.
            for (var i = 0; i < 10; i++)
                await IdentityHttpTest.Code(await first.PostAsJsonAsync($"/api/auth/{bucket}" + (i % 2 == 0 ? "/challenge" : ""),
                    new { }), 400, "request-invalid");
            foreach (var suffix in new[] { "", "/challenge" })
            {
                var limited = await first.PostAsJsonAsync($"/api/auth/{bucket}{suffix}", new { });
                await IdentityHttpTest.Code(limited, 429, "rate-limited");
                Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 600);
            }
        }
        using var second = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(second, key);
        foreach (var bucket in new[] { exhausted, other })
            await IdentityHttpTest.Code(await second.PostAsJsonAsync($"/api/auth/{bucket}/challenge", new { }), 400, "request-invalid");
    }

    [PostgresFact]
    public async Task A2_JSON_bodies_are_bounded_and_required_fields_are_enforced()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(client, key);
        foreach (var (method, path) in new[] { ("PATCH", "/profile"), ("POST", "/step-up/challenge"), ("POST", "/step-up"),
            ("POST", "/wallets/challenge"), ("POST", "/wallets"), ("PATCH", $"/wallets/{owner.Wallet.Id}") })
        {
            using var missing = new HttpRequestMessage(new HttpMethod(method), "/api/auth" + path) { Content = JsonContent.Create(new { }) };
            await IdentityHttpTest.Code(await client.SendAsync(missing), 400, "request-invalid");
            // Every auth body is bounded before any JSON is parsed.
            using var tooLarge = new HttpRequestMessage(new HttpMethod(method), "/api/auth" + path)
            { Content = new StringContent(new string('x', 16385), Encoding.UTF8, "application/json") };
            await IdentityHttpTest.Code(await client.SendAsync(tooLarge), 413, "request-too-large");
        }
    }

    [PostgresFact]
    public async Task Bodyless_and_read_routes_also_reject_oversized_chunked_bodies()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        foreach (var (method, path) in new[] { ("GET", "/sessions"), ("POST", "/sessions/revoke-others"),
            ("DELETE", $"/sessions/{session.Session.Id}"), ("DELETE", $"/wallets/{owner.Wallet.Id}") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/api/auth" + path)
            { Content = new StreamContent(new MemoryStream(new byte[16385])) };
            request.Headers.TransferEncodingChunked = true;
            await IdentityHttpTest.Code(await client.SendAsync(request), 413, "request-too-large");
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [PostgresFact]
    public async Task Profile_and_label_validation_and_profile_concurrency_preserve_current_values()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var current = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        IdentityAccountService Service() => new(new(db), db, new WalletSignatures(), db.Clock);
        async Task<string> Rename(string name)
        {
            try { await Service().UpdateProfileAsync(current, new(name, 1), default); return "ok"; }
            catch (ProblemException error) { return error.Code!; }
        }
        Assert.Equal(new[] { "ok", "revision-conflict" }, (await Task.WhenAll(Rename("First"), Rename("Second"))).Order().ToArray());
        var error = await Assert.ThrowsAsync<ProblemException>(() => Service().UpdateWalletAsync(current, owner.Wallet.Id, new("in\u200bvisible"), default));
        Assert.Equal("label-invalid", error.Code);
        await using var state = db.CreateDbContext();
        Assert.Equal(2, (await state.Users.SingleAsync()).Revision);
        Assert.Null((await state.Wallets.SingleAsync()).Label);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u200bb")]
    [InlineData("a\u0378b")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    public void Text_rules_reject_the_six_forbidden_Unicode_categories(string text)
    {
        Assert.Equal("display-name-invalid", Assert.Throws<ProblemException>(() => IdentityText.DisplayName(text)).Code);
        Assert.Equal("label-invalid", Assert.Throws<ProblemException>(() => IdentityText.Label(text)).Code);
        Assert.Equal("request-invalid", Assert.Throws<ProblemException>(() => IdentityText.Note(text)).Code);
    }

    [Fact]
    public void Text_rules_reject_unpaired_surrogates_without_rejecting_the_replacement_character()
    {
        // Construct at runtime: test discovery's serialization replaces invalid UTF-16 in InlineData.
        var text = "a" + (char)0xd800 + "b";
        Assert.Equal("display-name-invalid", Assert.Throws<ProblemException>(() => IdentityText.DisplayName(text)).Code);
        Assert.Equal("label-invalid", Assert.Throws<ProblemException>(() => IdentityText.Label(text)).Code);
        Assert.Equal("request-invalid", Assert.Throws<ProblemException>(() => IdentityText.Note(text)).Code);
    }

    [Fact]
    public void Text_rules_trim_allow_private_use_and_count_UTF16_not_runes()
    {
        Assert.Equal("Private\ue000", IdentityText.DisplayName("  Private\ue000  "));
        Assert.Equal("a\ufffdb", IdentityText.DisplayName("a\ufffdb"));
        var thirtyEmoji = string.Concat(Enumerable.Repeat("\U0001f600", 30));
        Assert.Equal(thirtyEmoji, IdentityText.DisplayName(thirtyEmoji));
        Assert.Throws<ProblemException>(() => IdentityText.DisplayName(thirtyEmoji + "a"));
        Assert.Equal(new string('x', 40), IdentityText.Label(new string('x', 40)));
        Assert.Throws<ProblemException>(() => IdentityText.Label(new string('x', 41)));
        Assert.Null(IdentityText.Label("\t\n  "));
    }
}
