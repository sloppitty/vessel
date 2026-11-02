using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class OperatorReadinessTests
{
    [PostgresTheory]
    [InlineData("suspend")]
    [InlineData("demote")]
    public async Task Operator_changes_require_another_active_operator_with_a_linked_wallet(string command)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var walletless = await database.SeedAsync(EthECKey.GenerateKey());
        var suspended = await database.SeedAsync(EthECKey.GenerateKey());
        await database.SeedAsync(EthECKey.GenerateKey(), role: "member");
        await using (var setup = database.CreateDbContext())
        {
            await setup.Wallets.Where(x => x.Id == walletless.Wallet.Id).ExecuteDeleteAsync();
            await setup.Users.Where(x => x.Id == suspended.User.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "suspended"));
        }
        var session = await database.SignInAsync(key);
        var output = new StringWriter();
        var config = database.Configuration(owner.User.Id);
        Task<int> Run() => IdentityCommands.RunAsync([command, "--user", owner.User.Id.ToString()], config, true, output);
        Assert.Equal(3, await Run());
        Assert.Contains("active operator", output.ToString());
        Assert.Contains("linked wallet", output.ToString());
        Assert.True(await database.Sessions().StillValidAsync(session.Session.Session.Id, default));
        await using (var state = database.CreateDbContext())
        {
            var unchanged = await state.Users.SingleAsync(x => x.Id == owner.User.Id);
            Assert.Equal("active", unchanged.Status);
            Assert.Equal("operator", unchanged.Role);
            Assert.Equal(1, unchanged.Revision);
        }
        Assert.Equal(0, await IdentityCommands.RunAsync(["bind-wallet", "--user", walletless.User.Id.ToString(),
            "--address", EthECKey.GenerateKey().GetPublicAddress()], config, true, new StringWriter()));
        Assert.Equal(0, await Run());
        await using var changed = database.CreateDbContext();
        var user = await changed.Users.SingleAsync(x => x.Id == owner.User.Id);
        Assert.Equal(command == "suspend" ? "suspended" : "active", user.Status);
        Assert.Equal(command == "demote" ? "member" : "operator", user.Role);
        Assert.Equal(command != "suspend", await database.Sessions().StillValidAsync(session.Session.Session.Id, default));
    }

    [PostgresTheory]
    [InlineData("suspend")]
    [InlineData("demote")]
    public async Task Members_and_walletless_operators_change_while_no_operator_has_a_wallet(string command)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var owner = await database.SeedAsync(EthECKey.GenerateKey());
        var walletless = await database.SeedAsync(EthECKey.GenerateKey());
        var member = await database.SeedAsync(EthECKey.GenerateKey(), role: "member");
        await using (var setup = database.CreateDbContext())
            await setup.Wallets.Where(x => x.UserId == owner.User.Id || x.UserId == walletless.User.Id).ExecuteDeleteAsync();
        var config = database.Configuration(owner.User.Id);
        Assert.Equal(0, await IdentityCommands.RunAsync([command, "--user", walletless.User.Id.ToString()], config, true, new StringWriter()));
        if (command == "suspend")
            Assert.Equal(0, await IdentityCommands.RunAsync([command, "--user", member.User.Id.ToString()], config, true, new StringWriter()));
        await using var state = database.CreateDbContext();
        var changed = await state.Users.SingleAsync(x => x.Id == walletless.User.Id);
        Assert.Equal(command == "suspend" ? "suspended" : "active", changed.Status);
        Assert.Equal(command == "demote" ? "member" : "operator", changed.Role);
        if (command == "suspend") Assert.Equal("suspended", (await state.Users.SingleAsync(x => x.Id == member.User.Id)).Status);
    }

    [PostgresFact]
    public async Task Concurrent_suspensions_keep_one_active_operator_with_a_wallet()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var a = await database.SeedAsync(EthECKey.GenerateKey());
        var b = await database.SeedAsync(EthECKey.GenerateKey());
        var config = database.Configuration(a.User.Id);
        var results = await Task.WhenAll(new[] { a.User.Id, b.User.Id }.Select(id =>
            IdentityCommands.RunAsync(["suspend", "--user", id.ToString()], config, true, new StringWriter())));
        Assert.Equal(new[] { 0, 3 }, results.Order().ToArray());
        await using var state = database.CreateDbContext();
        Assert.Single(await state.Users.Where(x => x.Role == "operator" && x.Status == "active" &&
            state.Wallets.Any(wallet => wallet.UserId == x.Id)).ToListAsync());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_wallet_removal_preserves_both_readiness_and_the_users_last_wallet(bool otherOperator)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        if (otherOperator) await database.SeedAsync(EthECKey.GenerateKey());
        var config = database.Configuration(owner.User.Id);
        await using var factory = IdentityHttpTest.Factory(database, owner.User.Id);
        using var client = IdentityHttpTest.Client(factory);
        var session = await IdentityHttpTest.SignInAsync(client, key);
        async Task Grant(Guid target)
        {
            var challenge = (await (await client.PostAsJsonAsync("/api/auth/step-up/challenge",
                new { action = "wallet.remove", target, address = key.GetPublicAddress() })).Content.ReadFromJsonAsync<ChallengeDto>())!;
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/step-up",
                new { challengeId = challenge.ChallengeId, signature = IdentityDatabase.Sign(key, challenge.Message) })).StatusCode);
        }
        Task<int> Unbind(Guid wallet) => IdentityCommands.RunAsync(["unbind-wallet", "--wallet", wallet.ToString()], config, true, new StringWriter());
        await Grant(owner.Wallet.Id);
        await IdentityHttpTest.Code(await client.DeleteAsync($"/api/auth/wallets/{owner.Wallet.Id}"), 409, "last-wallet");
        Assert.Equal(3, await Unbind(owner.Wallet.Id));
        await using (var state = database.CreateDbContext())
        {
            Assert.Equal(owner.Wallet.Id.ToString(), (await state.Sessions.SingleAsync(x => x.Id == session.Session.Id)).StepUpTarget);
            Assert.True(await state.Wallets.AnyAsync(x => x.Id == owner.Wallet.Id));
        }
        // Removing a non-last wallet is safe even for the sole qualifying operator.
        foreach (var api in new[] { true, false })
        {
            var spareAddress = EthECKey.GenerateKey().GetPublicAddress();
            Assert.Equal(0, await IdentityCommands.RunAsync(["bind-wallet", "--user", owner.User.Id.ToString(),
                "--address", spareAddress], config, true, new StringWriter()));
            await using var state = database.CreateDbContext();
            var spare = await state.Wallets.SingleAsync(x => x.Address == spareAddress.ToLowerInvariant());
            if (api)
            {
                await Grant(spare.Id);
                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/auth/wallets/{spare.Id}")).StatusCode);
            }
            else Assert.Equal(0, await Unbind(spare.Id));
            Assert.False(await state.Wallets.AnyAsync(x => x.Id == spare.Id));
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [PostgresTheory]
    [InlineData("operator", "active", true, false)]
    [InlineData("operator", "active", false, true)]
    [InlineData("operator", "suspended", true, true)]
    [InlineData("member", "active", true, true)]
    public async Task Reset_warns_without_refusing_when_no_qualifying_operator_remains(string role, string status, bool wallet, bool warning)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var kept = await database.SeedAsync(EthECKey.GenerateKey(), role: role);
        await database.SeedAsync(EthECKey.GenerateKey());
        await using (var setup = database.CreateDbContext())
        {
            await setup.Users.Where(x => x.Id == kept.User.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, status));
            if (!wallet) await setup.Wallets.Where(x => x.Id == kept.Wallet.Id).ExecuteDeleteAsync();
        }
        var output = new StringWriter();
        Assert.Equal(0, await IdentityCommands.RunAsync(["reset-after-restore", "--keep-active", kept.User.Id.ToString()],
            database.Configuration(kept.User.Id), true, output));
        Assert.Equal(warning, output.ToString().Contains("WARNING: No active operator with a linked wallet remains.", StringComparison.Ordinal));
        Assert.Contains("hosts stopped", output.ToString());
        await using var state = database.CreateDbContext();
        Assert.Equal(status, (await state.Users.SingleAsync(x => x.Id == kept.User.Id)).Status);
        Assert.All(await state.Users.Where(x => x.Id != kept.User.Id).ToListAsync(), user => Assert.Equal("suspended", user.Status));
    }
}
