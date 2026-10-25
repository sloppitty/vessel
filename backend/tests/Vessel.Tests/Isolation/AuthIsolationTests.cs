using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Signer;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Vessel.Identity;
using Vessel.Tests.Identity;

namespace Vessel.Tests.Isolation;

/// <summary>
/// Contract §12 C1 and A2: two signed-in users' sessions, wallets, step-up grants, profiles and tab
/// binding, and suspension. Another user's session or wallet ID answers exactly like an unknown one, and every
/// refused attempt leaves both users' identity state unchanged.
/// </summary>
public sealed class AuthIsolationTests(WalletIsolationApi api) : IClassFixture<WalletIsolationApi>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The caller's identity views. Reads never refresh them: the shared clock stands still.</summary>
    private static readonly string[] IdentityViews = ["/api/auth/session", "/api/auth/sessions", "/api/auth/wallets"];

    private static async Task<ChallengeDto> Challenge(Owner owner, string path, object body) =>
        (await owner.Post(path, body)).Deserialize<ChallengeDto>(Web)!;

    private static string Sign(EthECKey key, ChallengeDto challenge) => IdentityDatabase.Sign(key, challenge.Message);

    private static Task<ChallengeDto> StepUpChallenge(Owner owner, string action, Guid? target = null) =>
        Challenge(owner, "/api/auth/step-up/challenge", new { action, target, address = owner.Login!.Key.GetPublicAddress() });

    /// <summary>A live step-up grant on the owner's current session.</summary>
    private static async Task Grant(Owner owner, string action, Guid? target = null)
    {
        var challenge = await StepUpChallenge(owner, action, target);
        await owner.Post("/api/auth/step-up", new { challengeId = challenge.ChallengeId, signature = Sign(owner.Login!.Key, challenge) },
            HttpStatusCode.NoContent);
    }

    /// <summary>Links a further sign-in wallet through step-up, challenge and signed completion.</summary>
    private static async Task<Guid> Link(Owner owner, EthECKey key)
    {
        await Grant(owner, "wallet.link");
        var challenge = await Challenge(owner, "/api/auth/wallets/challenge", new { address = key.GetPublicAddress() });
        var linked = await owner.Post("/api/auth/wallets", new { challengeId = challenge.ChallengeId, signature = Sign(key, challenge) },
            HttpStatusCode.Created);
        return linked.GetProperty("id").GetGuid();
    }

    private static IEnumerable<Guid> Ids(JsonElement list) => list.EnumerateArray().Select(row => row.GetProperty("id").GetGuid());

    [PostgresFact]
    [Covers("GET", "/api/auth/session")]
    [Covers("GET", "/api/auth/sessions")]
    [Covers("GET", "/api/auth/wallets")]
    public async Task Session_and_wallet_lists_never_include_the_other_user()
    {
        var (a, b) = await api.Owners();
        var a2 = await api.SignInAgain(a);
        var b2 = await api.SignInAgain(b);
        var aLinked = await Link(a, EthECKey.GenerateKey());
        foreach (var (owner, sessions, wallets, other) in new (Owner, Guid[], Guid[], Owner)[]
        {
            (a, new[] { a.Login!.Session, a2.Login!.Session }, new[] { a.Login.Wallet, aLinked }, b),
            (b, [b.Login!.Session, b2.Login!.Session], [b.Login.Wallet], a)
        })
        {
            var session = await owner.GetJson("/api/auth/session");
            Assert.Equal(owner.Id, session.GetProperty("user").GetProperty("id").GetGuid());
            Assert.Equal(owner.Login!.Session, session.GetProperty("session").GetProperty("id").GetGuid());
            // Positive controls: every own session and wallet is listed, and nothing else.
            Assert.Equal(sessions.Order(), Ids(await owner.GetJson("/api/auth/sessions")).Order());
            Assert.Equal(wallets.Order(), Ids(await owner.GetJson("/api/auth/wallets")).Order());
            var foreign = new List<object> { other.Id, other.Login!.Session, other.Login.Wallet, other.Login.Key.GetPublicAddress() };
            if (owner == b) foreign.AddRange([a2.Login!.Session, aLinked]);
            else foreign.Add(b2.Login!.Session);
            foreach (var path in IdentityViews) Isolated.NoLeak(await owner.GetText(path), [.. foreign]);
        }
    }

    [PostgresFact]
    [Covers("DELETE", "/api/auth/sessions/{id}")]
    public async Task Foreign_session_id_is_not_found_like_an_unknown_one()
    {
        var (a, b) = await api.Owners();
        var a2 = await api.SignInAgain(a);
        var b2 = await api.SignInAgain(b);
        var state = await Snapshot.Of((a, IdentityViews), (a2, IdentityViews));
        var bState = await Snapshot.Of(b, IdentityViews);

        // A's current session and A's other session, from B's session: 404 not-found, identical to a random ID.
        foreach (var target in new[] { a.Login!.Session, a2.Login!.Session })
        {
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Delete, id => $"/api/auth/sessions/{id}"),
                code: "not-found"));
            await bState.Unchanged();
        }
        await using (var db = api.Identity.CreateDbContext())
            Assert.All(await db.Sessions.Where(x => x.UserId == a.Id).ToListAsync(), session => Assert.Null(session.RevokedAt));

        // Positive control: B revokes its own other session, which then answers 401.
        await state.After(() => b.Send(HttpMethod.Delete, $"/api/auth/sessions/{b2.Login!.Session}", expected: HttpStatusCode.NoContent));
        await Isolated.Problem(await b2.Http.GetAsync("/api/auth/session"), HttpStatusCode.Unauthorized, "session-revoked");
        Assert.Equal([b.Login!.Session], Ids(await b.GetJson("/api/auth/sessions")));
    }

    [PostgresFact]
    [Covers("PATCH", "/api/auth/wallets/{id}")]
    [Covers("DELETE", "/api/auth/wallets/{id}")]
    [Covers("POST", "/api/auth/step-up/challenge")]
    public async Task Foreign_wallet_id_is_not_found_like_an_unknown_one()
    {
        var (a, b) = await api.Owners();
        var aKey = EthECKey.GenerateKey();
        var aSecond = await Link(a, aKey);
        // Positive control: A labels its own wallet.
        Assert.Equal("Recovery", (await a.Send(HttpMethod.Patch, $"/api/auth/wallets/{aSecond}", new { label = "Recovery" }))
            .GetProperty("label").GetString());
        // B holds a live remove grant for its own second wallet, so a refusal cannot be a missing grant.
        var bSecond = await Link(b, EthECKey.GenerateKey());
        await Grant(b, "wallet.remove", bSecond);
        // Step-up requests are limited per session, so B's step-up attempts come from its other session.
        var bOther = await api.SignInAgain(b);
        var state = await Snapshot.Of((a, IdentityViews), (b, IdentityViews));

        foreach (var target in new[] { a.Login!.Wallet, aSecond })
        {
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Patch, id => $"/api/auth/wallets/{id}",
                _ => new { label = "taken-by-b" }), code: "not-found"));
            await state.After(() => Isolated.NotFound(b, target, Isolated.Request(HttpMethod.Delete, id => $"/api/auth/wallets/{id}"),
                code: "not-found"));
            // B cannot even ask to remove it: the step-up target lookup refuses it like an unknown wallet.
            await state.After(() => Isolated.NotFound(bOther, target, Isolated.Request(HttpMethod.Post, _ => "/api/auth/step-up/challenge",
                id => new { action = "wallet.remove", target = id, address = b.Login!.Key.GetPublicAddress() }), code: "not-found"));
        }
        // A's address as B's step-up signer answers like any address B does not hold.
        var address = await Isolated.SameAsUnknown(bOther, aKey.GetPublicAddress(), EthECKey.GenerateKey().GetPublicAddress(),
            address => new(HttpMethod.Post, "/api/auth/step-up/challenge") { Content = JsonContent.Create(new { action = "wallet.link", address }) });
        Assert.Equal(HttpStatusCode.BadRequest, address);
        await state.Unchanged();
        await using (var db = api.Identity.CreateDbContext())
        {
            Assert.Equal(2, await db.Wallets.CountAsync(x => x.UserId == a.Id));
            Assert.Null((await db.Sessions.SingleAsync(x => x.Id == a.Login!.Session)).RevokedAt);
        }

        // B's grant survived the attempts and removes B's own wallet; A's views are unchanged.
        var aState = await Snapshot.Of(a, IdentityViews);
        await aState.After(() => b.Send(HttpMethod.Delete, $"/api/auth/wallets/{bSecond}", expected: HttpStatusCode.NoContent));
        Assert.Equal([b.Login!.Wallet], Ids(await b.GetJson("/api/auth/wallets")));
    }

    [PostgresFact]
    [Covers("POST", "/api/auth/step-up")]
    [Covers("POST", "/api/auth/wallets/challenge")]
    [Covers("POST", "/api/auth/wallets")]
    public async Task Step_up_grants_and_challenges_of_another_session_cannot_be_used()
    {
        var (a, b) = await api.Owners();
        var state = await Snapshot.Of((a, IdentityViews), (b, IdentityViews));
        async Task NoGrant(Owner owner) =>
            await Isolated.Problem(await owner.Http.PostAsJsonAsync("/api/auth/wallets/challenge",
                new { address = EthECKey.GenerateKey().GetPublicAddress() }), HttpStatusCode.Forbidden, "step-up-required");

        // A's step-up challenge, with A's valid signature, completed from B's session: refused like an unknown
        // challenge, unconsumed, and no grant reaches B.
        var stepUp = await StepUpChallenge(a, "wallet.link");
        var signature = Sign(a.Login!.Key, stepUp);
        Assert.Equal(HttpStatusCode.BadRequest, await state.After(() => Isolated.SameAsUnknown(b, stepUp.ChallengeId,
            Isolated.Request(HttpMethod.Post, _ => "/api/auth/step-up", id => new { challengeId = id, signature }))));
        await state.After(() => NoGrant(b));
        await state.After(() => NoGrant(a));
        await a.Post("/api/auth/step-up", new { challengeId = stepUp.ChallengeId, signature }, HttpStatusCode.NoContent);

        // A now holds a link grant on its session. It does not serve B's session.
        await state.After(() => NoGrant(b));
        var key = EthECKey.GenerateKey();
        var link = await Challenge(a, "/api/auth/wallets/challenge", new { address = key.GetPublicAddress() });
        // A's link challenge, signed by the new wallet, completed from B's session: refused like an unknown challenge.
        var linkSignature = Sign(key, link);
        Assert.Equal(HttpStatusCode.BadRequest, await state.After(() => Isolated.SameAsUnknown(b, link.ChallengeId,
            Isolated.Request(HttpMethod.Post, _ => "/api/auth/wallets", id => new { challengeId = id, signature = linkSignature }))));
        // A completes its own unconsumed challenge with its intact grant: the wallet is A's, never B's.
        var linked = (await a.Post("/api/auth/wallets", new { challengeId = link.ChallengeId, signature = linkSignature },
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Contains(linked, Ids(await a.GetJson("/api/auth/wallets")));
        Isolated.NoLeak(await b.GetText("/api/auth/wallets"), linked, key.GetPublicAddress());

        // A's remove grant for that wallet does not let B remove it; afterwards A's grant still does.
        await Grant(a, "wallet.remove", linked);
        var removable = await Snapshot.Of((a, IdentityViews), (b, IdentityViews));
        await removable.After(() => Isolated.NotFound(b, linked, Isolated.Request(HttpMethod.Delete, id => $"/api/auth/wallets/{id}"),
            code: "not-found"));
        await a.Send(HttpMethod.Delete, $"/api/auth/wallets/{linked}", expected: HttpStatusCode.NoContent);
        Assert.Equal([a.Login!.Wallet], Ids(await a.GetJson("/api/auth/wallets")));
    }

    [PostgresFact]
    [Covers("PATCH", "/api/auth/profile")]
    [Covers("POST", "/api/auth/sessions/revoke-others")]
    [Covers("POST", "/api/auth/sign-out")]
    public async Task Profile_revoke_others_and_sign_out_act_only_on_the_callers_user()
    {
        var (a, b) = await api.Owners();
        var a2 = await api.SignInAgain(a);
        var b2 = await api.SignInAgain(b);
        var state = await Snapshot.Of((a, IdentityViews), (a2, IdentityViews));

        // B renames itself: only B's profile changes.
        var revision = (await b.GetJson("/api/auth/session")).GetProperty("user").GetProperty("revision").GetInt32();
        var name = Isolated.Marker("b");
        var renamed = await state.After(() => b.Send(HttpMethod.Patch, "/api/auth/profile", new { displayName = name, expectedRevision = revision }));
        Assert.Equal((b.Id, name), (renamed.GetProperty("id").GetGuid(), renamed.GetProperty("displayName").GetString()));
        Isolated.NoLeak(await a.GetText("/api/auth/session"), name);

        // B's revoke-others revokes B's other session, never A's.
        await state.After(() => b.Post("/api/auth/sessions/revoke-others", expected: HttpStatusCode.NoContent));
        await Isolated.Problem(await b2.Http.GetAsync("/api/auth/session"), HttpStatusCode.Unauthorized, "session-revoked");

        // Signing out with A's session ID changes nothing for either user; with its own it ends only B's session.
        foreach (var expected in new[] { a.Login!.Session, a2.Login!.Session })
        {
            using var refused = await state.After(() => b.Http.PostAsJsonAsync("/api/auth/sign-out", new { expectedSessionId = expected }));
            await Isolated.Problem(refused, HttpStatusCode.Conflict, "session-changed");
            Assert.Equal(b.Id, (await b.GetJson("/api/auth/session")).GetProperty("user").GetProperty("id").GetGuid());
        }
        await state.After(() => b.Post("/api/auth/sign-out", new { expectedSessionId = b.Login!.Session }, HttpStatusCode.NoContent));
        await Isolated.Problem(await b.Http.GetAsync("/api/portfolios"), HttpStatusCode.Unauthorized, "session-required");
        await using var db = api.Identity.CreateDbContext();
        Assert.All(await db.Sessions.Where(x => x.UserId == a.Id).ToListAsync(), session => Assert.Null(session.RevokedAt));
        Assert.All(await db.Sessions.Where(x => x.UserId == b.Id).ToListAsync(), session => Assert.NotNull(session.RevokedAt));
    }

    [PostgresFact]
    public async Task Tab_binding_naming_another_user_is_refused_before_any_data()
    {
        var (a, b) = await api.Owners();
        var aName = Isolated.Marker("portfolio-a");
        var bName = Isolated.Marker("portfolio-b");
        var aPortfolio = await a.Portfolio(aName);
        var bPortfolio = await b.Portfolio(bName);
        var paths = new[] { "/api/portfolios", "/api/accounts", "/api/events" };
        var state = await Snapshot.Of((a, paths), (b, paths));
        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/system", null), (HttpMethod.Get, "/api/portfolios", null),
            (HttpMethod.Get, $"/api/portfolios/{aPortfolio}", null), (HttpMethod.Get, $"/api/portfolios/{bPortfolio}", null),
            (HttpMethod.Post, "/api/portfolios", new CreatePortfolioRequest("tab-bound-to-b")),
            (HttpMethod.Patch, $"/api/portfolios/{bPortfolio}", new UpdatePortfolioRequest("renamed-through-a", "folder")),
            (HttpMethod.Delete, $"/api/portfolios/{aPortfolio}", null), (HttpMethod.Get, "/api/events", null),
            (HttpMethod.Get, "/api/events/stream", null), (HttpMethod.Get, "/api/auth/sessions", null),
            (HttpMethod.Get, "/api/auth/wallets", null), (HttpMethod.Post, "/api/auth/sessions/revoke-others", null)
        };
        // A's session cookie with B's user ID, or an ID of nobody: 401 session-user-mismatch, never either user's data.
        foreach (var user in new[] { b.Id, Guid.NewGuid() })
            foreach (var (method, path, body) in attempts)
                await state.After(async () =>
                {
                    using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                    request.Headers.Add("X-Vessel-User", user.ToString());
                    using var response = await a.Http.SendAsync(request);
                    Isolated.NoLeak(await Isolated.Problem(response, HttpStatusCode.Unauthorized, "session-user-mismatch"),
                        aPortfolio, bPortfolio, aName, bName, a.Login!.Session, b.Login!.Session);
                });
        // The session probe needs no tab binding, and the header never selects its owner: it is still A's session.
        using (var probe = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session"))
        {
            probe.Headers.Add("X-Vessel-User", b.Id.ToString());
            using var response = await a.Http.SendAsync(probe);
            Assert.Equal(a.Id, (await Isolated.Json(response, HttpStatusCode.OK)).GetProperty("user").GetProperty("id").GetGuid());
        }
        // Neither the suite's test-scheme header nor a tab binding alone authenticates anyone through wallet sessions.
        using var anonymous = api.Client();
        anonymous.DefaultRequestHeaders.Add(TestOwnerAuthHandler.Header, a.Id.ToString("D"));
        anonymous.DefaultRequestHeaders.Add("X-Vessel-User", a.Id.ToString());
        await Isolated.Problem(await anonymous.GetAsync("/api/portfolios"), HttpStatusCode.Unauthorized, "session-required");
        await state.Unchanged();
    }

    [PostgresFact]
    public async Task Suspending_a_user_revokes_only_its_sessions_and_retains_its_journal()
    {
        var (a, b) = await api.Owners();
        var b2 = await api.SignInAgain(b);
        foreach (var owner in new[] { a, b })
        {
            var account = (await owner.ManualAccount(Isolated.Marker("manual"), await owner.Portfolio(Isolated.Marker("portfolio"))))
                .GetProperty("id").GetGuid();
            await owner.Post("/api/plays", new CreatePlayRequest(account, "BTC", "manual", Isolated.Marker("play"), TestPlays.Plan()),
                HttpStatusCode.Created);
        }
        var journal = new[] { "/api/portfolios", "/api/accounts", "/api/plays?view=all", "/api/plays/counts", "/api/events" };
        var aState = await Snapshot.Of((a, journal), (a, IdentityViews));
        var bJournal = await Isolated.State(b, journal);
        var directory = api.Factory.Services.GetRequiredService<IJournalOwnerDirectory>();

        Assert.Equal(0, await IdentityCommands.RunAsync(["suspend", "--user", b.Id.ToString()], api.Configuration, true, new StringWriter()));
        // Both of B's sessions are revoked: reads and writes answer 401 and create nothing.
        foreach (var session in new[] { b, b2 })
        {
            await Isolated.Problem(await session.Http.GetAsync("/api/portfolios"), HttpStatusCode.Unauthorized, "session-revoked");
            await Isolated.Problem(await session.Http.PostAsJsonAsync("/api/portfolios", new CreatePortfolioRequest("while-suspended")),
                HttpStatusCode.Unauthorized, "session-revoked");
        }
        // A is unaffected, and the owner directory sees B as suspended.
        await aState.Unchanged();
        Assert.Equal((JournalOwnerStatus.Active, JournalOwnerStatus.Suspended),
            (await directory.StatusAsync(a.Id, default), await directory.StatusAsync(b.Id, default)));
        // A valid signature of B's wallet does not sign B in.
        using (var client = api.Client())
        {
            var challenge = (await (await client.PostAsJsonAsync("/api/auth/challenge", new { address = b.Login!.Key.GetPublicAddress() }))
                .Content.ReadFromJsonAsync<ChallengeDto>())!;
            await Isolated.Problem(await client.PostAsJsonAsync("/api/auth/verify",
                new { challengeId = challenge.ChallengeId, signature = Sign(b.Login.Key, challenge), remember = false }),
                HttpStatusCode.Forbidden, "user-suspended");
        }
        await using (var db = api.Database.Context(b.Id))
            Assert.Equal((1, 1, 1), (await db.Portfolios.CountAsync(), await db.Accounts.CountAsync(), await db.Plays.CountAsync()));

        // After restore B signs in again and finds its journal exactly as it was; A is still unchanged.
        Assert.Equal(0, await IdentityCommands.RunAsync(["restore", "--user", b.Id.ToString()], api.Configuration, true, new StringWriter()));
        var restored = await api.SignInAgain(b);
        Assert.Equal(bJournal, await Isolated.State(restored, journal));
        await aState.Unchanged();
    }
}
