using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using Vessel.Application.Common;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class SessionTests
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idle_and_absolute_expiry_are_enforced_including_remember_lifetimes(bool remember)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key, remember);
        Assert.Equal(database.Settings.IdleLifetime(remember), issued.Session.Session.IdleExpiresAt - issued.Session.Session.CreatedAt);
        Assert.Equal(database.Settings.AbsoluteLifetime(remember), issued.Session.Session.AbsoluteExpiresAt - issued.Session.Session.CreatedAt);
        database.Clock.Now = issued.Session.Session.IdleExpiresAt.AddSeconds(1);
        await Error(() => database.Sessions().AuthenticateAsync(issued.Token, true, default), "session-expired");
        database.Clock.Now = issued.Session.Session.CreatedAt;
        await using (var db = database.CreateDbContext())
            await db.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.IdleExpiresAt, database.Clock.Now.AddDays(365)));
        database.Clock.Now = issued.Session.Session.AbsoluteExpiresAt;
        await Error(() => database.Sessions().AuthenticateAsync(issued.Token, true, default), "session-expired");
        Assert.False(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
    }

    [PostgresFact]
    public async Task Conditional_idle_refresh_does_not_keep_extending_under_concurrent_requests()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        await using (var setup = database.CreateDbContext())
        {
            var schema = database.Schema.Name;
            var sql = $"""
                CREATE TABLE "{schema}".refresh_count (value integer NOT NULL);
                INSERT INTO "{schema}".refresh_count VALUES (0);
                CREATE FUNCTION "{schema}".count_refresh() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN UPDATE "{schema}".refresh_count SET value = value + 1; RETURN NEW; END; $$;
                CREATE TRIGGER count_refresh AFTER UPDATE ON "{schema}".sessions
                FOR EACH ROW EXECUTE FUNCTION "{schema}".count_refresh();
                """;
            await setup.Database.ExecuteSqlRawAsync(sql);
        }
        database.Clock.Now = database.Clock.Now.AddMinutes(6);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.Sessions().AuthenticateAsync(issued.Token, true, default)));
        await using var db = database.CreateDbContext();
        var session = await db.Sessions.SingleAsync();
        Assert.Equal(database.Clock.Now, session.LastSeenAt);
        var countSql = $"SELECT value AS \"Value\" FROM \"{database.Schema.Name}\".refresh_count";
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(countSql).SingleAsync());
        var idle = session.IdleExpiresAt;
        database.Clock.Now = database.Clock.Now.AddMinutes(4);
        await database.Sessions().AuthenticateAsync(issued.Token, true, default);
        await db.Entry(session).ReloadAsync();
        Assert.Equal(idle, session.IdleExpiresAt);
        database.Clock.Now = database.Clock.Now.AddMinutes(1);
        await database.Sessions().AuthenticateAsync(issued.Token, true, default);
        await db.Entry(session).ReloadAsync();
        Assert.Equal(idle, session.IdleExpiresAt); // Strictly older than five minutes, not <=.
        database.Clock.Now = database.Clock.Now.AddSeconds(1);
        await database.Sessions().AuthenticateAsync(issued.Token, true, default);
        await db.Entry(session).ReloadAsync();
        Assert.True(session.IdleExpiresAt > idle);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>(countSql).SingleAsync());
    }

    [PostgresFact]
    public async Task Session_cap_is_atomic_and_sign_in_rotates_the_sent_cookie_even_across_users()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        database.Settings = database.Settings with { MaxSessionsPerUser = 2 };
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var first = await database.SignInAsync(key);
        database.Clock.Now = database.Clock.Now.AddMinutes(1);
        var second = await database.SignInAsync(key);
        database.Clock.Now = database.Clock.Now.AddMinutes(1);
        var third = await database.SignInAsync(key);
        await Error(() => database.Sessions().AuthenticateAsync(first.Token, false, default), "session-revoked");
        Assert.True(await database.Sessions().StillValidAsync(second.Session.Session.Id, default));
        var otherKey = EthECKey.GenerateKey();
        await database.SeedAsync(otherKey);
        await database.SignInAsync(otherKey, previous: third.Token);
        await Error(() => database.Sessions().AuthenticateAsync(third.Token, false, default), "session-revoked");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => database.SignInAsync(key)));
        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.Sessions.CountAsync(x => x.UserId == owner.User.Id && x.RevokedAt == null));
        Assert.All(await db.Sessions.ToListAsync(), session => Assert.Equal(32, session.TokenHash.Length));
    }

    [PostgresFact]
    public async Task Revocation_suspension_and_missing_wallet_are_checked_fresh_and_stream_checks_do_not_refresh()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        database.Clock.Now = database.Clock.Now.AddMinutes(10);
        Assert.True(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
        await using var db = database.CreateDbContext();
        Assert.Equal(issued.Session.Session.CreatedAt, (await db.Sessions.AsNoTracking().SingleAsync()).LastSeenAt);
        await db.Users.ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "suspended"));
        Assert.False(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
        await Error(() => database.Sessions().AuthenticateAsync(issued.Token, false, default), "user-suspended");
        var binding = IdentityTokens.Create();
        var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        await Error(() => database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
            "https://vessel.test", binding, null, null, default), "user-suspended");
        await db.Users.ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "active"));
        await db.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, database.Clock.Now));
        await db.Wallets.Where(x => x.Id == owner.Wallet.Id).ExecuteDeleteAsync();
        Assert.False(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
        await Error(() => database.Sessions().AuthenticateAsync(issued.Token, false, default), "session-revoked");
    }

    [PostgresFact]
    public async Task Sign_out_with_a_stale_session_ID_does_not_revoke()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        var authenticated = await database.Sessions().AuthenticateAsync(issued.Token, false, default);
        await Error(() => database.Service().SignOutAsync(authenticated, Guid.NewGuid(), default), "session-changed");
        Assert.True(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
        await database.Service().SignOutAsync(authenticated, issued.Session.Session.Id, default);
        Assert.False(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
    }

    private static async Task Error(Func<Task> action, string code) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<ProblemException>(action)).Code);
}
