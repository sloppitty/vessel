using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using Vessel.Application.Common;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class AccountManagementTests
{
    private const string Origin = "https://vessel.test";
    private static IdentityAccountService Accounts(IdentityDatabase db) => new(new(db), db, new WalletSignatures(), db.Clock);
    private static Task<AuthenticatedSession> Principal(IdentityDatabase db, IssuedSession session) =>
        db.Sessions().AuthenticateAsync(session.Token, false, default);
    private static async Task<UserWallet> AddWallet(IdentityDatabase database, Guid userId, EthECKey key)
    {
        await using var db = database.CreateDbContext();
        var wallet = new UserWallet { Id = Guid.NewGuid(), UserId = userId, Address = key.GetPublicAddress().ToLowerInvariant(), AddedAt = database.Clock.Now };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return wallet;
    }
    private static async Task Grant(IdentityDatabase db, AuthenticatedSession current, EthECKey signer, string action = "wallet.link", Guid? target = null)
    {
        var challenge = await Accounts(db).StepUpChallengeAsync(current, new(action, signer.GetPublicAddress(), target), Origin, default);
        await Accounts(db).StepUpAsync(current, new(challenge.ChallengeId, IdentityDatabase.Sign(signer, challenge.Message)), Origin, default);
    }
    private static async Task Error(Func<Task> action, string code, int? status = null)
    {
        var error = await Assert.ThrowsAsync<ProblemException>(action);
        Assert.Equal(code, error.Code);
        if (status is not null) Assert.Equal(status, error.StatusCode);
    }

    [PostgresFact]
    public async Task All_account_routes_match_the_client_DTOs_and_are_no_store()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var other = await db.SignInAsync(key, true);
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var current = await IdentityHttpTest.SignInAsync(client, key);
        var rows = await Success<SessionRowDto[]>(await client.GetAsync("/api/auth/sessions"));
        Assert.Equal(2, rows.Length);
        Assert.Equal(current.Session.Id, Assert.Single(rows, x => x.Current).Id);
        Assert.All(rows, row => Assert.Equal(key.GetPublicAddress(), row.WalletAddress));
        Assert.Equal("Firefox on Linux", Assert.Single(rows, x => !x.Current).UserAgent);
        var profile = await Success<UserDto>(await client.PatchAsJsonAsync("/api/auth/profile", new { displayName = "  Updated owner  ", expectedRevision = 1 }));
        Assert.Equal("Updated owner", profile.DisplayName);
        Assert.Equal(2, profile.Revision);
        await IdentityHttpTest.Code(await client.PatchAsJsonAsync("/api/auth/profile", new { displayName = "stale", expectedRevision = 1 }), 409, "revision-conflict");
        await NoContent(await client.DeleteAsync($"/api/auth/sessions/{other.Session.Session.Id}"));
        Assert.False(await db.Sessions().StillValidAsync(other.Session.Session.Id, default));
        var another = await db.SignInAsync(key);
        await NoContent(await client.PostAsync("/api/auth/sessions/revoke-others", null));
        Assert.False(await db.Sessions().StillValidAsync(another.Session.Session.Id, default));
        var wallets = await Success<WalletDto[]>(await client.GetAsync("/api/auth/wallets"));
        Assert.True(Assert.Single(wallets).Current);
        var required = await client.PostAsJsonAsync("/api/auth/wallets/challenge", new { address = key.GetPublicAddress() });
        var requiredBody = await required.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, required.StatusCode);
        Assert.True(required.Headers.CacheControl?.NoStore);
        Assert.Equal("step-up-required", requiredBody.GetProperty("code").GetString());
        Assert.Equal("wallet.link", requiredBody.GetProperty("action").GetString());
        var newKey = EthECKey.GenerateKey();
        var step = await Success<ChallengeDto>(await client.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "wallet.link", address = key.GetPublicAddress() }));
        Assert.Contains("Confirm adding a sign-in wallet", step.Message);
        await NoContent(await client.PostAsJsonAsync("/api/auth/step-up", new { challengeId = step.ChallengeId, signature = IdentityDatabase.Sign(key, step.Message) }));
        var link = await Success<ChallengeDto>(await client.PostAsJsonAsync("/api/auth/wallets/challenge", new { address = newKey.GetPublicAddress() }));
        Assert.Contains($"signed in with {key.GetPublicAddress()}", link.Message);
        var linked = await Success<WalletDto>(await client.PostAsJsonAsync("/api/auth/wallets", new { challengeId = link.ChallengeId, signature = IdentityDatabase.Sign(newKey, link.Message), label = "  Recovery  " }), HttpStatusCode.Created);
        Assert.Equal("Recovery", linked.Label);
        Assert.False(linked.Current);
        Assert.Equal(newKey.GetPublicAddress(), linked.Address);
        var labelled = await Success<WalletDto>(await client.PatchAsJsonAsync($"/api/auth/wallets/{linked.Id}", new { label = "  " }));
        Assert.Null(labelled.Label);
        var remove = await Success<ChallengeDto>(await client.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "wallet.remove", target = linked.Id, address = key.GetPublicAddress() }));
        Assert.Contains($"removing sign-in wallet {newKey.GetPublicAddress()}", remove.Message);
        await NoContent(await client.PostAsJsonAsync("/api/auth/step-up", new { challengeId = remove.ChallengeId, signature = IdentityDatabase.Sign(key, remove.Message) }));
        await NoContent(await client.DeleteAsync($"/api/auth/wallets/{linked.Id}"));
        Assert.Single(await Success<WalletDto[]>(await client.GetAsync("/api/auth/wallets")));
    }

    [PostgresFact]
    public async Task Foreign_and_absent_session_wallet_and_step_up_targets_are_identical_404s()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var foreignKey = EthECKey.GenerateKey();
        var foreign = await db.SeedAsync(foreignKey);
        var session = await db.SignInAsync(foreignKey);
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(client, key);
        foreach (var pair in new[] { ("sessions", session.Session.Session.Id), ("wallets", foreign.Wallet.Id) })
        {
            await SameNotFound(await client.DeleteAsync($"/api/auth/{pair.Item1}/{pair.Item2}"), await client.DeleteAsync($"/api/auth/{pair.Item1}/{Guid.NewGuid()}"));
        }
        await SameNotFound(await client.PatchAsJsonAsync($"/api/auth/wallets/{foreign.Wallet.Id}", new { label = "No" }),
            await client.PatchAsJsonAsync($"/api/auth/wallets/{Guid.NewGuid()}", new { label = "No" }));
        await SameNotFound(await client.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "wallet.remove", target = foreign.Wallet.Id, address = key.GetPublicAddress() }),
            await client.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "wallet.remove", target = Guid.NewGuid(), address = key.GetPublicAddress() }));
        await using var state = db.CreateDbContext();
        Assert.Equal(2, await state.Wallets.CountAsync());
        Assert.Null((await state.Sessions.SingleAsync(x => x.Id == session.Session.Session.Id)).RevokedAt);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_the_current_session_or_wallet_clears_the_cookie(bool wallet)
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        await AddWallet(db, owner.User.Id, EthECKey.GenerateKey());
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        if (wallet)
        {
            var step = await Success<ChallengeDto>(await client.PostAsJsonAsync("/api/auth/step-up/challenge", new { action = "wallet.remove", target = owner.Wallet.Id, address = key.GetPublicAddress() }));
            await NoContent(await client.PostAsJsonAsync("/api/auth/step-up", new { challengeId = step.ChallengeId, signature = IdentityDatabase.Sign(key, step.Message) }));
        }
        var response = await client.DeleteAsync(wallet ? $"/api/auth/wallets/{owner.Wallet.Id}" : $"/api/auth/sessions/{session.Session.Id}");
        await NoContent(response);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("__Host-vessel-session=;", StringComparison.Ordinal));
        await IdentityHttpTest.Code(await client.GetAsync("/api/auth/session"), 401, "session-required");
        Assert.False(await db.Sessions().StillValidAsync(session.Session.Id, default));
    }

    [PostgresFact]
    public async Task Grants_and_challenges_are_bound_to_the_session_action_target_and_origin()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var second = await AddWallet(db, owner.User.Id, EthECKey.GenerateKey());
        var one = await Principal(db, await db.SignInAsync(key));
        var two = await Principal(db, await db.SignInAsync(key));
        var service = Accounts(db);
        await Error(() => service.StepUpChallengeAsync(one, new("other", key.GetPublicAddress()), Origin, default), "request-invalid");
        await Error(() => service.StepUpChallengeAsync(one, new("wallet.remove", key.GetPublicAddress()), Origin, default), "request-invalid");
        await Error(() => service.StepUpChallengeAsync(one, new("wallet.link", key.GetPublicAddress(), second.Id), Origin, default), "request-invalid");
        await Error(() => service.StepUpChallengeAsync(one, new("wallet.link", EthECKey.GenerateKey().GetPublicAddress()), Origin, default), "address-invalid");
        var step = await service.StepUpChallengeAsync(one, new("wallet.remove", key.GetPublicAddress(), second.Id), Origin, default);
        var signed = new StepUpRequest(step.ChallengeId, IdentityDatabase.Sign(key, step.Message));
        await Error(() => service.StepUpAsync(two, signed, Origin, default), "challenge-invalid");
        await Error(() => service.StepUpAsync(one, signed, "https://another.test", default), "origin-rejected");
        await service.StepUpAsync(one, signed, Origin, default);
        await Error(() => service.StepUpAsync(one, signed, Origin, default), "challenge-invalid");
        await Error(() => service.RemoveWalletAsync(one, owner.Wallet.Id, default), "step-up-required");
        await Error(() => service.RemoveWalletAsync(two, second.Id, default), "step-up-required");
        await Error(() => service.WalletChallengeAsync(one, new(key.GetPublicAddress()), Origin, default), "step-up-required");
        await service.RemoveWalletAsync(one, second.Id, default);
        await Grant(db, one, key);
        var link = await service.WalletChallengeAsync(one, new(EthECKey.GenerateKey().GetPublicAddress()), Origin, default);
        await Error(() => service.LinkWalletAsync(two, new(link.ChallengeId, null), Origin, default), "challenge-invalid");
        db.Clock.Now = db.Clock.Now.AddMinutes(5);
        await Error(() => service.LinkWalletAsync(one, new(link.ChallengeId, null), Origin, default), "step-up-required");
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlinking_the_step_up_signer_cancels_challenges_and_grants_even_in_another_wallets_session(bool throughApi)
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var keyA = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(keyA);
        var keyB = EthECKey.GenerateKey();
        await AddWallet(db, owner.User.Id, keyB);
        var sessionB = await Principal(db, await db.SignInAsync(keyB));
        var pending = await Accounts(db).StepUpChallengeAsync(sessionB, new("wallet.link", keyA.GetPublicAddress()), Origin, default);
        await Grant(db, sessionB, keyA);
        var keyC = EthECKey.GenerateKey();
        var link = await Accounts(db).WalletChallengeAsync(sessionB, new(keyC.GetPublicAddress()), Origin, default);
        var output = new StringWriter();
        if (throughApi)
        {
            var remover = await Principal(db, await db.SignInAsync(keyB));
            await Grant(db, remover, keyB, "wallet.remove", owner.Wallet.Id);
            await Accounts(db).RemoveWalletAsync(remover, owner.Wallet.Id, default);
        }
        else Assert.Equal(0, await IdentityCommands.RunAsync(["unbind-wallet", "--wallet", owner.Wallet.Id.ToString()], db.Configuration(owner.User.Id), true, output));
        await Error(() => Accounts(db).StepUpAsync(sessionB, new(pending.ChallengeId, IdentityDatabase.Sign(keyA, pending.Message)), Origin, default), "address-invalid");
        await Error(() => Accounts(db).LinkWalletAsync(sessionB, new(link.ChallengeId, IdentityDatabase.Sign(keyC, link.Message)), Origin, default), "step-up-required");
        await using var state = db.CreateDbContext();
        var stored = await state.Sessions.SingleAsync(x => x.Id == sessionB.Session.Id);
        Assert.Null(stored.StepUpWalletId);
        Assert.Null(stored.StepUpAction);
        Assert.Null(stored.StepUpTarget);
        Assert.Null(stored.StepUpExpiresAt);
        Assert.True(await db.Sessions().StillValidAsync(sessionB.Session.Id, default));
        Assert.Single(await state.Wallets.ToListAsync());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Last_wallet_removal_precedes_step_up_and_preserves_any_existing_grant(bool hasOtherGrant)
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var spare = await AddWallet(db, owner.User.Id, EthECKey.GenerateKey());
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        Assert.Equal(2, (await Success<WalletDto[]>(await client.GetAsync("/api/auth/wallets"))).Length);
        if (hasOtherGrant)
        {
            var step = await Success<ChallengeDto>(await client.PostAsJsonAsync("/api/auth/step-up/challenge",
                new { action = "wallet.link", address = key.GetPublicAddress() }));
            await NoContent(await client.PostAsJsonAsync("/api/auth/step-up",
                new { challengeId = step.ChallengeId, signature = IdentityDatabase.Sign(key, step.Message) }));
        }
        // Another tab invalidates the displayed list while this session stays live.
        Assert.Equal(0, await IdentityCommands.RunAsync(["unbind-wallet", "--wallet", spare.Id.ToString()],
            db.Configuration(owner.User.Id), true, new StringWriter()));
        var response = await client.DeleteAsync($"/api/auth/wallets/{owner.Wallet.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("last-wallet", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("action", out _));
        await Success<SessionDto>(await client.GetAsync("/api/auth/session"));
        await using var state = db.CreateDbContext();
        Assert.Single(await state.Wallets.ToListAsync());
        var unchanged = await state.Sessions.SingleAsync(x => x.Id == session.Session.Id);
        Assert.Null(unchanged.RevokedAt);
        Assert.Equal(hasOtherGrant ? "wallet.link" : null, unchanged.StepUpAction);
        Assert.Equal(hasOtherGrant ? owner.Wallet.Id : (Guid?)null, unchanged.StepUpWalletId);
    }

    [PostgresFact]
    public async Task Concurrent_removal_of_two_remaining_wallets_keeps_one_and_revokes_only_removed_wallet_sessions()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var keyA = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(keyA);
        var keyB = EthECKey.GenerateKey();
        var walletB = await AddWallet(db, owner.User.Id, keyB);
        var a = await Principal(db, await db.SignInAsync(keyA));
        var b = await Principal(db, await db.SignInAsync(keyB));
        await Grant(db, a, keyA, "wallet.remove", owner.Wallet.Id);
        await Grant(db, b, keyB, "wallet.remove", walletB.Id);
        async Task<string> Remove(AuthenticatedSession session, Guid target)
        {
            try { await Accounts(db).RemoveWalletAsync(session, target, default); return "removed"; }
            catch (ProblemException error) { return error.Code!; }
        }
        var results = await Task.WhenAll(Remove(a, owner.Wallet.Id), Remove(b, walletB.Id));
        Assert.Equal(new[] { "last-wallet", "removed" }, results.Order().ToArray());
        await using var state = db.CreateDbContext();
        var remaining = await state.Wallets.SingleAsync();
        Assert.Single(await state.Sessions.Where(x => x.RevokedAt == null).ToListAsync());
        Assert.All(await state.Sessions.Where(x => x.RevokedAt == null).ToListAsync(), s => Assert.Equal(remaining.Id, s.WalletId));
    }

    [PostgresFact]
    public async Task Concurrent_completion_consumes_each_challenge_and_grant_at_most_once()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var current = await Principal(db, await db.SignInAsync(key));
        var service = Accounts(db);
        var step = await service.StepUpChallengeAsync(current, new("wallet.link", key.GetPublicAddress()), Origin, default);
        async Task<string> CompleteStep()
        {
            try { await Accounts(db).StepUpAsync(current, new(step.ChallengeId, IdentityDatabase.Sign(key, step.Message)), Origin, default); return "ok"; }
            catch (ProblemException error) { return error.Code!; }
        }
        Assert.Equal(new[] { "challenge-invalid", "ok" }, (await Task.WhenAll(CompleteStep(), CompleteStep())).Order().ToArray());
        var newKey = EthECKey.GenerateKey();
        var link = await service.WalletChallengeAsync(current, new(newKey.GetPublicAddress()), Origin, default);
        async Task<string> CompleteLink()
        {
            try { await Accounts(db).LinkWalletAsync(current, new(link.ChallengeId, IdentityDatabase.Sign(newKey, link.Message)), Origin, default); return "ok"; }
            catch (ProblemException error) { return error.Code!; }
        }
        Assert.Equal(new[] { "challenge-invalid", "ok" }, (await Task.WhenAll(CompleteLink(), CompleteLink())).Order().ToArray());
        await using var state = db.CreateDbContext();
        Assert.Equal(2, await state.Wallets.CountAsync());
        Assert.Null((await state.Sessions.SingleAsync()).StepUpAction);
    }

    [PostgresFact]
    public async Task Linked_addresses_are_disclosed_only_at_completion_and_failed_changes_do_not_consume_the_grant()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var otherKey = EthECKey.GenerateKey();
        await db.SeedAsync(otherKey);
        var current = await Principal(db, await db.SignInAsync(key));
        await Grant(db, current, key);
        var service = Accounts(db);
        foreach (var (target, code) in new[] { (key, "wallet-already-linked"), (otherKey, "wallet-taken") })
        {
            var challenge = await service.WalletChallengeAsync(current, new(target.GetPublicAddress()), Origin, default);
            await Error(() => service.LinkWalletAsync(current, new(challenge.ChallengeId, IdentityDatabase.Sign(target, challenge.Message)), Origin, default), code);
            await using var state = db.CreateDbContext();
            Assert.Null((await state.Challenges.SingleAsync(x => x.Id == challenge.ChallengeId)).ConsumedAt);
            Assert.Equal("wallet.link", (await state.Sessions.SingleAsync()).StepUpAction);
        }
    }

    [PostgresFact]
    public async Task Two_distinct_link_challenges_cannot_reuse_one_grant()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var current = await Principal(db, await db.SignInAsync(key));
        await Grant(db, current, key);
        var keys = new[] { EthECKey.GenerateKey(), EthECKey.GenerateKey() };
        var challenges = new List<ChallengeDto>();
        foreach (var target in keys)
            challenges.Add(await Accounts(db).WalletChallengeAsync(current, new(target.GetPublicAddress()), Origin, default));
        async Task<string> Link(int index)
        {
            try
            {
                await Accounts(db).LinkWalletAsync(current, new(challenges[index].ChallengeId,
                    IdentityDatabase.Sign(keys[index], challenges[index].Message)), Origin, default);
                return "ok";
            }
            catch (ProblemException error) { return error.Code!; }
        }
        Assert.Equal(new[] { "ok", "step-up-required" }, (await Task.WhenAll(Link(0), Link(1))).Order().ToArray());
        await using var state = db.CreateDbContext();
        Assert.Equal(2, await state.Wallets.CountAsync());
        Assert.Single(await state.Challenges.Where(x => x.Purpose == "link" && x.ConsumedAt != null).ToListAsync());
    }

    [PostgresFact]
    public async Task Completion_rechecks_session_user_purpose_signature_expiry_and_current_grant()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var spare = await AddWallet(db, owner.User.Id, EthECKey.GenerateKey());
        var current = await Principal(db, await db.SignInAsync(key));
        var service = Accounts(db);
        var signIn = await db.Service().ChallengeAsync(key.GetPublicAddress(), Origin, IdentityTokens.Create(), default);
        await Error(() => service.StepUpAsync(current, new(signIn.ChallengeId, IdentityDatabase.Sign(key, signIn.Message)), Origin, default), "challenge-invalid");
        var step = await service.StepUpChallengeAsync(current, new("wallet.link", key.GetPublicAddress()), Origin, default);
        await Error(() => service.StepUpAsync(current, new(step.ChallengeId, IdentityDatabase.Sign(EthECKey.GenerateKey(), step.Message)), Origin, default), "signature-mismatch");
        await Error(() => service.StepUpAsync(current, new(step.ChallengeId, "0x01"), Origin, default), "signature-unsupported");
        await service.StepUpAsync(current, new(step.ChallengeId, IdentityDatabase.Sign(key, step.Message)), Origin, default);
        var target = EthECKey.GenerateKey();
        var link = await service.WalletChallengeAsync(current, new(target.GetPublicAddress()), Origin, default);
        await Error(() => service.LinkWalletAsync(current, new(step.ChallengeId, null), Origin, default), "challenge-invalid");
        await Error(() => service.LinkWalletAsync(current, new(link.ChallengeId, IdentityDatabase.Sign(key, link.Message)), Origin, default), "signature-mismatch");
        await Grant(db, current, key, "wallet.remove", spare.Id);
        await Error(() => service.LinkWalletAsync(current, new(link.ChallengeId, IdentityDatabase.Sign(target, link.Message)), Origin, default), "step-up-required");
        var expires = await service.StepUpChallengeAsync(current, new("wallet.link", key.GetPublicAddress()), Origin, default);
        db.Clock.Now = db.Clock.Now.AddMinutes(5).AddSeconds(31);
        await Error(() => service.StepUpAsync(current, new(expires.ChallengeId, IdentityDatabase.Sign(key, expires.Message)), Origin, default), "challenge-expired");
        await Grant(db, current, key);
        link = await service.WalletChallengeAsync(current, new(target.GetPublicAddress()), Origin, default);
        var output = new StringWriter();
        await db.SeedAsync(EthECKey.GenerateKey());
        Assert.Equal(0, await IdentityCommands.RunAsync(["suspend", "--user", owner.User.Id.ToString()], db.Configuration(owner.User.Id), true, output));
        await Error(() => service.LinkWalletAsync(current, new(link.ChallengeId, IdentityDatabase.Sign(target, link.Message)), Origin, default), "user-suspended");
        Assert.Equal(0, await IdentityCommands.RunAsync(["restore", "--user", owner.User.Id.ToString()], db.Configuration(owner.User.Id), true, output));
        await Error(() => service.StepUpAsync(current, new(expires.ChallengeId, null), Origin, default), "session-revoked");
        await using var state = db.CreateDbContext();
        Assert.Equal(2, await state.Wallets.CountAsync(x => x.UserId == owner.User.Id));
        Assert.Null((await state.Challenges.SingleAsync(x => x.Id == link.ChallengeId)).ConsumedAt);
    }

    [PostgresFact]
    public async Task Sessions_list_excludes_expired_and_revoked_sessions_and_other_users()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var current = await Principal(db, await db.SignInAsync(key));
        var idle = await db.SignInAsync(key);
        var absolute = await db.SignInAsync(key);
        var revoked = await db.SignInAsync(key);
        var foreign = EthECKey.GenerateKey();
        await db.SeedAsync(foreign);
        await db.SignInAsync(foreign);
        await using (var state = db.CreateDbContext())
        {
            await state.Sessions.Where(x => x.Id == idle.Session.Session.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.IdleExpiresAt, db.Clock.Now));
            await state.Sessions.Where(x => x.Id == absolute.Session.Session.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.AbsoluteExpiresAt, db.Clock.Now));
            await state.Sessions.Where(x => x.Id == revoked.Session.Session.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, db.Clock.Now));
        }
        var row = Assert.Single(await Accounts(db).SessionsAsync(current, default));
        Assert.Equal(current.Session.Id, row.Id);
        Assert.True(row.Current);
    }

    private static async Task<T> Success<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static Task NoContent(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return Task.CompletedTask;
    }
    private static async Task SameNotFound(HttpResponseMessage foreign, HttpResponseMessage missing)
    {
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(foreign.Headers.CacheControl?.NoStore);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        var a = await foreign.Content.ReadFromJsonAsync<JsonElement>();
        var b = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not-found", a.GetProperty("code").GetString());
        foreach (var field in new[] { "status", "title", "detail", "code" }) Assert.Equal(a.GetProperty(field).ToString(), b.GetProperty(field).ToString());
    }
}
