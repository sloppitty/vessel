using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nethereum.Signer;
using Vessel.Application.Common;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

// Regressions from the PR #74 review: ending the creating session ends its pairings, completion is single and
// claims contest safely, and the migration rolls back and reapplies over live wallet sessions.
public sealed class DevicePairingRevocationTests
{
    private const string Origin = "https://vessel.test";

    private static IdentityAccountService Accounts(IdentityDatabase db) =>
        new(new(db), db, new WalletSignatures(), db.Clock);

    private static DevicePairingService Pairings(IdentityDatabase db) =>
        new(new(db), db, db.Settings, db.Clock);

    private static async Task<PairingCreatedDto> CreateAsync(
        IdentityDatabase db, AuthenticatedSession desktop, EthECKey signer)
    {
        var accounts = Accounts(db);
        var challenge = await accounts.StepUpChallengeAsync(desktop,
            new("device.pair", signer.GetPublicAddress()), Origin, default);
        await accounts.StepUpAsync(desktop,
            new(challenge.ChallengeId, IdentityDatabase.Sign(signer, challenge.Message)), Origin, default);
        return await Pairings(db).CreateAsync(desktop, Origin, default);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoking_the_creating_session_must_prevent_a_later_paired_sign_in(bool revokeOthers)
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var keeper = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        var desktop = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        var pairing = await CreateAsync(db, desktop, key);
        var binding = IdentityTokens.Create();
        var claimed = await Pairings(db).ClaimAsync(new(pairing.Secret), Origin, binding, null, default);
        await Pairings(db).ApproveAsync(desktop, pairing.Id, new(claimed.Code), default);

        if (revokeOthers)
            await Accounts(db).RevokeOthersAsync(keeper, default);
        else
            await Accounts(db).RevokeSessionAsync(keeper, desktop.Session.Id, default);

        await using (var verify = db.CreateDbContext())
            Assert.NotNull((await verify.Sessions.SingleAsync(x => x.Id == desktop.Session.Id)).RevokedAt);
        await using (var verify = db.CreateDbContext())
            Assert.Equal("cancelled", (await verify.Pairings.SingleAsync(x => x.Id == pairing.Id)).Status);
        var completed = await Pairings(db).CompleteAsync(new(true), binding, null, null, default);
        Assert.Null(completed.SignedIn);
        Assert.Equal("cancelled", completed.Status);
    }

    [PostgresFact]
    public async Task Signing_out_the_creating_session_cancels_its_approved_pairing()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var desktop = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        var pairing = await CreateAsync(db, desktop, key);
        var binding = IdentityTokens.Create();
        var claimed = await Pairings(db).ClaimAsync(new(pairing.Secret), Origin, binding, null, default);
        await Pairings(db).ApproveAsync(desktop, pairing.Id, new(claimed.Code), default);

        await db.Service().SignOutAsync(desktop, desktop.Session.Id, default);

        Assert.Equal("cancelled", (await Pairings(db).CompleteAsync(new(true), binding, null, null, default)).Status);
        await using var verify = db.CreateDbContext();
        Assert.Empty(await verify.Sessions.Where(x => x.Source == "pairing").ToListAsync());
    }

    [PostgresFact]
    public async Task Completion_refuses_an_approval_whose_creating_session_ended_without_cancelling()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var desktop = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        var pairing = await CreateAsync(db, desktop, key);
        var binding = IdentityTokens.Create();
        var claimed = await Pairings(db).ClaimAsync(new(pairing.Secret), Origin, binding, null, default);
        await Pairings(db).ApproveAsync(desktop, pairing.Id, new(claimed.Code), default);
        // An idle lapse ends the desktop's session without any revocation path running.
        await using (var lapse = db.CreateDbContext())
            await lapse.Sessions.Where(x => x.Id == desktop.Session.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.IdleExpiresAt, db.Clock.Now.AddSeconds(-1)));

        Assert.Equal("cancelled", (await Pairings(db).CompleteAsync(new(true), binding, null, null, default)).Status);
        await using var verify = db.CreateDbContext();
        Assert.Empty(await verify.Sessions.Where(x => x.Source == "pairing").ToListAsync());
    }

    [PostgresFact]
    public async Task Revoke_others_must_also_cancel_approved_pairings_over_the_real_HTTP_stack()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        await using var factory = IdentityHttpTest.Factory(db, owner.User.Id);
        using var keeper = IdentityHttpTest.Client(factory);
        using var desktop = IdentityHttpTest.Client(factory);
        using var phone = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(keeper, key);
        await IdentityHttpTest.SignInAsync(desktop, key);
        var challengeResponse = await desktop.PostAsJsonAsync("/api/auth/step-up/challenge",
            new { action = "device.pair", address = key.GetPublicAddress() });
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await desktop.PostAsJsonAsync("/api/auth/step-up",
            new { challengeId = challenge.ChallengeId, signature = IdentityDatabase.Sign(key, challenge.Message) })).StatusCode);
        var createResponse = await desktop.PostAsync("/api/auth/pairings", null);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<PairingCreatedDto>())!;
        var claimResponse = await phone.PostAsJsonAsync("/api/auth/pairings/claim", new { secret = created.Secret });
        claimResponse.EnsureSuccessStatusCode();
        var claimed = (await claimResponse.Content.ReadFromJsonAsync<PairingClaimedDto>())!;
        Assert.Equal(HttpStatusCode.NoContent,
            (await desktop.PostAsJsonAsync($"/api/auth/pairings/{created.Id}/approve", new { code = claimed.Code })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await keeper.PostAsync("/api/auth/sessions/revoke-others", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/auth/session")).StatusCode);
        var completion = await phone.PostAsJsonAsync("/api/auth/pairings/complete", new { remember = true });
        completion.EnsureSuccessStatusCode();
        var result = await completion.Content.ReadFromJsonAsync<JsonElement>();
        if (result.GetProperty("status").GetString() == "signed-in")
            Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal("cancelled", result.GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task Concurrent_completion_issues_exactly_one_session()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var desktop = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        var pairing = await CreateAsync(db, desktop, key);
        var binding = IdentityTokens.Create();
        var claim = await Pairings(db).ClaimAsync(new(pairing.Secret), Origin, binding, null, default);
        await Pairings(db).ApproveAsync(desktop, pairing.Id, new(claim.Code), default);

        async Task<string?> Complete()
        {
            try { return (await Pairings(db).CompleteAsync(new(false), binding, null, null, default)).Status; }
            catch (ProblemException error) { return error.Code; }
        }
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Complete()));
        Assert.Single(statuses, status => status == "signed-in");
        Assert.Equal(7, statuses.Count(status => status == "pairing-invalid"));
        await using var verify = db.CreateDbContext();
        Assert.Equal(1, await verify.Sessions.CountAsync(x => x.Source == "pairing"));
    }

    [PostgresFact]
    public async Task Concurrent_secret_claims_contest_without_replacing_the_first_binding()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var desktop = await db.Sessions().AuthenticateAsync((await db.SignInAsync(key)).Token, false, default);
        var pairing = await CreateAsync(db, desktop, key);
        var bindings = new[] { IdentityTokens.Create(), IdentityTokens.Create() };

        async Task<string?> Claim(string binding)
        {
            try { await Pairings(db).ClaimAsync(new(pairing.Secret), Origin, binding, null, default); return "claimed"; }
            catch (ProblemException error) { return error.Code; }
        }
        var statuses = await Task.WhenAll(bindings.Select(Claim));
        Assert.Single(statuses, status => status == "claimed");
        Assert.Single(statuses, status => status == "pairing-contested");
        Assert.Equal("contested", (await Pairings(db).StatusAsync(desktop, pairing.Id, default)).Status);
        var error = await Assert.ThrowsAsync<ProblemException>(() =>
            Pairings(db).ApproveAsync(desktop, pairing.Id, new("123456"), default));
        Assert.Equal("pairing-invalid", error.Code);
        await using var verify = db.CreateDbContext();
        Assert.Empty(await verify.Sessions.Where(x => x.Source == "pairing").ToListAsync());
    }

    [PostgresFact]
    public async Task Pairing_migration_can_roll_back_and_reapply_with_an_existing_wallet_session()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await db.SeedAsync(key);
        var issued = await db.SignInAsync(key);
        await using (var migrate = db.CreateDbContext())
            await migrate.GetService<IMigrator>().MigrateAsync("20261017155708_InitialIdentity");
        await using (var migrate = db.CreateDbContext())
            await migrate.Database.MigrateAsync();
        var session = await db.Sessions().AuthenticateAsync(issued.Token, false, default);
        Assert.Equal("wallet", session.Session.Source);
        await using var verify = db.CreateDbContext();
        Assert.False(verify.Database.HasPendingModelChanges());
    }
}
