using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class WalletLimitTests
{
    [PostgresFact]
    public async Task Link_challenge_refuses_twenty_wallets_before_requesting_a_signature_or_step_up()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        await AddWallets(database, owner.User.Id, 19);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        await IdentityHttpTest.SignInAsync(client, key);
        await IdentityHttpTest.Code(await client.PostAsJsonAsync("/api/auth/wallets/challenge",
            new { address = EthECKey.GenerateKey().GetPublicAddress() }), 409, "wallet-limit");
        await using var state = database.CreateDbContext();
        Assert.Empty(await state.Challenges.Where(x => x.Purpose == "link").ToListAsync());
    }

    [PostgresFact]
    public async Task Completion_rechecks_the_limit_and_preserves_the_challenge_and_grant_for_retry()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        await AddWallets(database, owner.User.Id, 18);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        var step = (await (await client.PostAsJsonAsync("/api/auth/step-up/challenge",
            new { action = "wallet.link", address = key.GetPublicAddress() })).Content.ReadFromJsonAsync<ChallengeDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/step-up",
            new { challengeId = step.ChallengeId, signature = IdentityDatabase.Sign(key, step.Message) })).StatusCode);
        var target = EthECKey.GenerateKey();
        var link = (await (await client.PostAsJsonAsync("/api/auth/wallets/challenge",
            new { address = target.GetPublicAddress() })).Content.ReadFromJsonAsync<ChallengeDto>())!;
        var extra = await AddWallets(database, owner.User.Id, 1, 100);
        var request = new { challengeId = link.ChallengeId, signature = IdentityDatabase.Sign(target, link.Message) };
        await IdentityHttpTest.Code(await client.PostAsJsonAsync("/api/auth/wallets", request), 409, "wallet-limit");
        await using (var state = database.CreateDbContext())
        {
            Assert.Equal(20, await state.Wallets.CountAsync());
            Assert.Null((await state.Challenges.SingleAsync(x => x.Id == link.ChallengeId)).ConsumedAt);
            Assert.Equal("wallet.link", (await state.Sessions.SingleAsync(x => x.Id == session.Session.Id)).StepUpAction);
        }
        Assert.Equal(0, await IdentityCommands.RunAsync(["unbind-wallet", "--wallet", extra.Id.ToString()],
            database.Configuration(owner.User.Id), true, new StringWriter()));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/wallets", request)).StatusCode);
    }

    [PostgresFact]
    public async Task Bind_wallet_refuses_the_twenty_first_wallet_but_remains_idempotent_at_the_limit()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var owner = await database.SeedAsync(EthECKey.GenerateKey());
        await AddWallets(database, owner.User.Id, 18);
        var config = database.Configuration(owner.User.Id);
        var output = new StringWriter();
        var address = EthECKey.GenerateKey().GetPublicAddress();
        Task<int> Bind(string value) => IdentityCommands.RunAsync(["bind-wallet", "--user", owner.User.Id.ToString(),
            "--address", value], config, true, output);
        Assert.Equal(0, await Bind(address));
        Assert.Equal(0, await Bind(address));
        Assert.Equal(3, await Bind(EthECKey.GenerateKey().GetPublicAddress()));
        Assert.Contains("20", output.ToString());
        await using var state = database.CreateDbContext();
        Assert.Equal(20, await state.Wallets.CountAsync());
    }

    private static async Task<UserWallet> AddWallets(IdentityDatabase database, Guid user, int count, int start = 1)
    {
        await using var db = database.CreateDbContext();
        var wallets = Enumerable.Range(start, count).Select(i => new UserWallet { Id = Guid.NewGuid(), UserId = user,
            Address = "0x" + i.ToString("x40"), AddedAt = database.Clock.Now }).ToArray();
        db.Wallets.AddRange(wallets);
        await db.SaveChangesAsync();
        return wallets[^1];
    }
}
