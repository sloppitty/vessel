using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Nethereum.Signer;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Domain.Accounts;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class AdministrationTests
{
    [PostgresFact]
    public async Task Suspend_restore_and_role_commands_update_the_live_owner_directory_and_preserve_revocation()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var memberKey = EthECKey.GenerateKey();
        var member = await database.SeedAsync(memberKey, role: "member");
        var session = await database.SignInAsync(memberKey);
        var config = database.Configuration(owner.User.Id);
        var output = new StringWriter();
        var directory = new IdentityOwnerDirectory(database);
        Task<int> Run(string command, Guid id) => IdentityCommands.RunAsync([command, "--user", id.ToString()], config, true, output);
        Assert.Equal(3, await Run("demote", owner.User.Id));
        Assert.Equal(0, await Run("promote", member.User.Id));
        Assert.Equal(0, await Run("demote", owner.User.Id));
        Assert.Equal(3, await Run("demote", member.User.Id));
        Assert.Equal(0, await Run("promote", owner.User.Id));
        Assert.Equal(0, await Run("suspend", member.User.Id));
        Assert.Equal(JournalOwnerStatus.Suspended, await directory.StatusAsync(member.User.Id, default));
        Assert.DoesNotContain(member.User.Id, await directory.ActiveOwnersAsync(null, 200, default));
        Assert.False(await database.Sessions().StillValidAsync(session.Session.Session.Id, default));
        var failure = await Assert.ThrowsAsync<ProblemException>(() => database.SignInAsync(memberKey));
        Assert.Equal("user-suspended", failure.Code);
        await using (var db = database.CreateDbContext())
        {
            var suspended = await db.Users.SingleAsync(x => x.Id == member.User.Id);
            Assert.NotNull(suspended.SuspendedAt);
            Assert.Equal(3, suspended.Revision);
            Assert.Equal("suspended", (await db.Sessions.SingleAsync(x => x.Id == session.Session.Session.Id)).RevokeReason);
        }
        Assert.Equal(0, await Run("restore", member.User.Id));
        Assert.Equal(JournalOwnerStatus.Active, await directory.StatusAsync(member.User.Id, default));
        Assert.Contains(member.User.Id, await directory.ActiveOwnersAsync(null, 200, default));
        Assert.False(await database.Sessions().StillValidAsync(session.Session.Session.Id, default));
        Assert.NotNull(await database.SignInAsync(memberKey));
        Assert.Equal(3, await Run("suspend", Guid.NewGuid()));
        Assert.Equal(JournalOwnerStatus.Unknown, await directory.StatusAsync(Guid.NewGuid(), default));
        Assert.DoesNotContain(session.Token, output.ToString());
    }

    [PostgresFact]
    public async Task Concurrent_demotions_cannot_remove_the_last_active_operator()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var a = await database.SeedAsync(EthECKey.GenerateKey());
        var b = await database.SeedAsync(EthECKey.GenerateKey());
        var config = database.Configuration(a.User.Id);
        var results = await Task.WhenAll(new[] { a.User.Id, b.User.Id }.Select(id =>
            IdentityCommands.RunAsync(["demote", "--user", id.ToString()], config, true, new StringWriter())));
        Assert.Equal(new[] { 0, 3 }, results.Order().ToArray());
        await using var state = database.CreateDbContext();
        Assert.Single(await state.Users.Where(x => x.Role == "operator" && x.Status == "active").ToListAsync());
    }

    [PostgresFact]
    public async Task Revoke_sessions_honours_user_or_all_and_unbind_refuses_the_last_wallet()
    {
        await using var db = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await db.SeedAsync(key);
        var otherKey = EthECKey.GenerateKey();
        await db.SeedAsync(otherKey);
        var one = await db.SignInAsync(key);
        var two = await db.SignInAsync(otherKey);
        var config = db.Configuration(owner.User.Id);
        var output = new StringWriter();
        Assert.Equal(0, await IdentityCommands.RunAsync(["revoke-sessions", "--user", owner.User.Id.ToString()], config, true, output));
        Assert.False(await db.Sessions().StillValidAsync(one.Session.Session.Id, default));
        Assert.True(await db.Sessions().StillValidAsync(two.Session.Session.Id, default));
        Assert.Equal(0, await IdentityCommands.RunAsync(["revoke-sessions", "--all"], config, true, output));
        Assert.False(await db.Sessions().StillValidAsync(two.Session.Session.Id, default));
        Assert.Equal(3, await IdentityCommands.RunAsync(["unbind-wallet", "--wallet", owner.Wallet.Id.ToString()], config, true, output));
        Assert.Equal(3, await IdentityCommands.RunAsync(["unbind-wallet", "--wallet", Guid.NewGuid().ToString()], config, true, output));
        Assert.Equal(3, await IdentityCommands.RunAsync(["revoke-sessions", "--user", Guid.NewGuid().ToString()], config, true, output));
        Assert.DoesNotContain(one.Token, output.ToString());
        Assert.DoesNotContain(two.Token, output.ToString());
        await using var state = db.CreateDbContext();
        Assert.Equal(2, await state.Wallets.CountAsync());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_after_restore_suspends_unconfirmed_users_and_prevents_resurrected_wallets_from_signing_in(bool keepOperator)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var restoredKey = EthECKey.GenerateKey();
        var restored = await database.SeedAsync(restoredKey, role: "member");
        var suspendedKey = EthECKey.GenerateKey();
        var lostSuspension = await database.SeedAsync(suspendedKey, role: "member");
        var oldSession = await database.SignInAsync(restoredKey);
        await database.SignInAsync(key);
        var principal = await database.Sessions().AuthenticateAsync(oldSession.Token, false, default);
        var accounts = new IdentityAccountService(new(database), database, new WalletSignatures(), database.Clock);
        var step = await accounts.StepUpChallengeAsync(principal, new("wallet.link", restoredKey.GetPublicAddress()), "https://vessel.test", default);
        await accounts.StepUpAsync(principal, new(step.ChallengeId, IdentityDatabase.Sign(restoredKey, step.Message)), "https://vessel.test", default);
        var challenge = await database.Service().ChallengeAsync(restoredKey.GetPublicAddress(), "https://vessel.test", IdentityTokens.Create(), default);
        var pendingToken = IdentityTokens.Create();
        var inviteCode = InvitationCodes.Create();
        var now = DateTimeOffset.UtcNow;
        await using (var setup = database.CreateDbContext())
        {
            setup.Registrations.Add(new PendingRegistration { Id = Guid.NewGuid(), Address = restoredKey.GetPublicAddress().ToLowerInvariant(),
                TokenHash = IdentityTokens.Hash(pendingToken), BindingHash = IdentityTokens.Hash(IdentityTokens.Create()),
                Origin = "https://vessel.test", CreatedAt = now, ExpiresAt = now.AddMinutes(10) });
            setup.Invitations.Add(new Invitation { Id = Guid.NewGuid(), CodeHash = IdentityTokens.Hash(InvitationCodes.Normalize(inviteCode)),
                Note = "secret-note", CreatedAt = now, ExpiresAt = now.AddDays(7), MaxUses = 1 });
            setup.Invitations.Add(new Invitation { Id = Guid.NewGuid(), CodeHash = IdentityTokens.Hash("expired"), CreatedAt = now.AddDays(-2), ExpiresAt = now.AddDays(-1), MaxUses = 1 });
            await setup.SaveChangesAsync();
        }
        var output = new StringWriter();
        var config = database.Configuration(owner.User.Id);
        string[] args = keepOperator ? ["reset-after-restore", "--keep-active", owner.User.Id.ToString()] : ["reset-after-restore"];
        Assert.Equal(0, await IdentityCommands.RunAsync(args, config, true, output));
        var table = output.ToString();
        Assert.Contains("hosts stopped", table);
        Assert.Equal(!keepOperator, table.Contains("WARNING: No active operator with a linked wallet remains.", StringComparison.Ordinal));
        Assert.Contains("USER ID | ROLE | STATUS | WALLET ID | ADDRESS", table);
        Assert.Contains(restored.User.Id.ToString(), table);
        Assert.Contains(restored.Wallet.Id.ToString(), table);
        Assert.Contains(restoredKey.GetPublicAddress(), table);
        foreach (var secret in new[] { pendingToken, inviteCode, oldSession.Token, challenge.Message, "secret-note" }) Assert.DoesNotContain(secret, table);
        await using var state = database.CreateDbContext();
        Assert.Equal(keepOperator ? "active" : "suspended", (await state.Users.SingleAsync(x => x.Id == owner.User.Id)).Status);
        Assert.All(await state.Users.Where(x => x.Id != owner.User.Id).ToListAsync(), user => Assert.Equal("suspended", user.Status));
        Assert.All(await state.Sessions.ToListAsync(), session =>
        {
            Assert.Equal("reset-after-restore", session.RevokeReason);
            Assert.NotNull(session.RevokedAt);
            Assert.Null(session.StepUpAction);
            Assert.Null(session.StepUpTarget);
            Assert.Null(session.StepUpWalletId);
            Assert.Null(session.StepUpExpiresAt);
        });
        Assert.Empty(await state.Challenges.ToListAsync());
        Assert.Empty(await state.Registrations.ToListAsync());
        Assert.NotNull((await state.Invitations.SingleAsync(x => x.Note == "secret-note")).RevokedAt);
        Assert.Null((await state.Invitations.SingleAsync(x => x.Note == null)).RevokedAt);
        // Both kinds of lost post-backup revocation fail closed: a restored wallet and a lost user suspension.
        foreach (var signer in new[] { restoredKey, suspendedKey })
        {
            var error = await Assert.ThrowsAsync<ProblemException>(() => database.SignInAsync(signer));
            Assert.Equal("user-suspended", error.Code);
        }
        Assert.Equal("suspended", (await state.Users.SingleAsync(x => x.Id == lostSuspension.User.Id)).Status);
        Assert.Equal(0, await IdentityCommands.RunAsync(args, config, true, new StringWriter()));
    }

    [PostgresFact]
    public async Task Reset_validates_all_keep_active_ids_before_any_change_and_never_reactivates_a_suspended_user()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var session = await database.SignInAsync(key);
        var config = database.Configuration(owner.User.Id);
        var output = new StringWriter();
        Assert.Equal(3, await IdentityCommands.RunAsync(["reset-after-restore", "--keep-active", owner.User.Id.ToString(), Guid.NewGuid().ToString()], config, true, output));
        Assert.True(await database.Sessions().StillValidAsync(session.Session.Session.Id, default));
        await database.SeedAsync(EthECKey.GenerateKey());
        Assert.Equal(0, await IdentityCommands.RunAsync(["suspend", "--user", owner.User.Id.ToString()], config, true, output));
        Assert.Equal(0, await IdentityCommands.RunAsync(["reset-after-restore", "--keep-active", owner.User.Id.ToString()], config, true, output));
        Assert.Equal(JournalOwnerStatus.Suspended, await new IdentityOwnerDirectory(database).StatusAsync(owner.User.Id, default));
    }

    [Theory]
    [InlineData("unbind-wallet")]
    [InlineData("suspend", "--user", "bad")]
    [InlineData("restore", "--user", "00000000-0000-0000-0000-000000000000")]
    [InlineData("promote", "--all")]
    [InlineData("demote", "--user")]
    [InlineData("revoke-sessions")]
    [InlineData("revoke-sessions", "--all", "--user", "00000000-0000-0000-0000-000000000001")]
    [InlineData("reset-after-restore", "--keep-active")]
    [InlineData("reset-after-restore", "--keep-active", "bad")]
    [InlineData("verify", "--all")]
    public async Task Malformed_administration_options_are_usage_errors(params string[] args)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vessel:Auth:OwnerId"] = Guid.NewGuid().ToString(),
            ["Vessel:Identity:Origins:0"] = "https://vessel.test", ["ConnectionStrings:Vessel"] = "Host=unused"
        }).Build();
        Assert.Equal(2, await IdentityCommands.RunAsync(args, config, true, new StringWriter()));
    }

    [PostgresFact]
    public async Task Verify_uses_the_journal_inventory_through_API_composition_and_is_read_only()
    {
        await using var identity = await IdentityDatabase.CreateAsync();
        await using var journal = await CoreDatabase.CreateAsync();
        var owner = await identity.SeedAsync(EthECKey.GenerateKey());
        var withoutWallet = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        await using (var setup = identity.CreateDbContext())
        {
            setup.Users.Add(new IdentityUser { Id = withoutWallet, DisplayName = "Walletless", CreatedAt = identity.Clock.Now, UpdatedAt = identity.Clock.Now });
            await setup.SaveChangesAsync();
        }
        await using (var setup = journal.Context(orphan))
        {
            setup.Accounts.Add(new Account(Guid.NewGuid(), orphan, "manual", "Synthetic orphan"));
            await setup.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddConfiguration(identity.Configuration(owner.User.Id))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vessel"] = journal.ConnectionString }).Build();
        var output = new StringWriter();
        Assert.Equal(3, await IdentityCommands.RunAsync(["verify"], config, true, output, verify: Program.VerifyIdentityAsync));
        Assert.Contains($"Journal owner without identity user: {orphan}", output.ToString());
        Assert.Contains($"Identity user without wallet: {withoutWallet}", output.ToString());
        await using var state = identity.CreateDbContext();
        Assert.Equal(2, await state.Users.CountAsync());
        Assert.Single(await state.Wallets.ToListAsync());
        Assert.Empty(await state.Sessions.ToListAsync());
        await using var journalState = journal.Context(orphan);
        Assert.Single(await journalState.Accounts.ToListAsync());
        Assert.Equal(0, await Program.VerifyIdentityAsync([new(owner.User.Id, true)], new Inventory([]), new StringWriter(), default));
    }

    [Fact]
    public async Task Verify_pages_the_inventory_instead_of_assuming_one_owner_page()
    {
        var owners = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).Order().ToArray();
        var inventory = new Inventory(owners);
        var output = new StringWriter();
        Assert.Equal(0, await Program.VerifyIdentityAsync(owners.Select(id => new IdentityVerificationUser(id, true)).ToArray(), inventory, output, default));
        Assert.Equal(2, inventory.Calls);
    }

    private sealed class Inventory(Guid[] owners) : IJournalOwnerInventory
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<Guid>> DistinctOwnersAsync(Guid? after, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Guid>>(owners.Where(id => after is null || id.CompareTo(after.Value) > 0).Take(limit).ToArray());
        }
    }
}
