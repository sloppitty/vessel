using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nethereum.Signer;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

/// <summary>
/// Cookie authentication reads session, user and wallet in one statement and writes only when an idle refresh is due,
/// with every outcome of the former four-command path.
/// </summary>
public sealed class SessionReadTests
{
    [PostgresFact]
    public async Task A_recent_session_authenticates_with_one_read_and_no_refresh_write()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        var commands = Count(database);
        database.Clock.Now = issued.Session.Session.CreatedAt.AddMinutes(5); // Strictly older than five minutes is due.
        var authenticated = await database.Sessions().AuthenticateAsync(issued.Token, true, default);
        Assert.Equal(["SELECT"], commands.Verbs());
        Assert.Equal(owner.User.Id, authenticated.User.Id);
        Assert.Equal(owner.Wallet.Id, authenticated.Wallet.Id);
        Assert.Equal(issued.Session.Session.Id, authenticated.Session.Id);
        Assert.Equal(issued.Session.Session.CreatedAt, authenticated.Session.LastSeenAt);
        Assert.Equal(issued.Session.Session.IdleExpiresAt, authenticated.Session.IdleExpiresAt);
        Assert.Equal(issued.Session, authenticated.ToDto(database.Settings));
        await using var db = database.CreateDbContext();
        var stored = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal(issued.Session.Session.CreatedAt, stored.LastSeenAt);
        Assert.Equal(issued.Session.Session.IdleExpiresAt, stored.IdleExpiresAt);
    }

    [PostgresFact]
    public async Task A_due_refresh_still_runs_the_conditional_update_and_returns_the_refreshed_session()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        var commands = Count(database);
        database.Clock.Now = issued.Session.Session.CreatedAt.AddMinutes(5).AddMilliseconds(1);
        var authenticated = await database.Sessions().AuthenticateAsync(issued.Token, true, default);
        Assert.Equal(["SELECT", "UPDATE"], commands.Verbs());
        Assert.Equal(database.Clock.Now, authenticated.Session.LastSeenAt);
        Assert.Equal(database.Clock.Now + database.Settings.IdleLifetime(false), authenticated.Session.IdleExpiresAt);
        await using var db = database.CreateDbContext();
        var stored = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal(authenticated.Session.LastSeenAt, stored.LastSeenAt);
        Assert.Equal(authenticated.Session.IdleExpiresAt, stored.IdleExpiresAt);
        // Without refresh, a due session is read once and left untouched.
        commands.Clear();
        database.Clock.Now = database.Clock.Now.AddMinutes(6);
        var read = await database.Sessions().AuthenticateAsync(issued.Token, false, default);
        Assert.Equal(["SELECT"], commands.Verbs());
        Assert.Equal(stored.LastSeenAt, read.Session.LastSeenAt);
    }

    [PostgresFact]
    public async Task A_revocation_between_the_read_and_the_refresh_is_not_extended()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        var commands = Count(database);
        commands.BeforeUpdate = async () =>
        {
            await using var connection = new NpgsqlConnection(database.Connection);
            await connection.OpenAsync();
            await using var revoke = new NpgsqlCommand(
                $"UPDATE \"{database.Schema.Name}\".sessions SET revoked_at = now(), revoke_reason = 'sign-out'", connection);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        };
        database.Clock.Now = issued.Session.Session.CreatedAt.AddMinutes(6);
        // The request had already authenticated against the row it read, exactly as before; the write refuses to extend.
        var authenticated = await database.Sessions().AuthenticateAsync(issued.Token, true, default);
        Assert.Equal(["SELECT", "UPDATE"], commands.Verbs());
        Assert.Equal(issued.Session.Session.CreatedAt, authenticated.Session.LastSeenAt);
        Assert.Equal(issued.Session.Session.IdleExpiresAt, authenticated.Session.IdleExpiresAt);
        await using var db = database.CreateDbContext();
        var stored = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(issued.Session.Session.CreatedAt, stored.LastSeenAt);
        Assert.Equal(issued.Session.Session.IdleExpiresAt, stored.IdleExpiresAt);
        commands.BeforeUpdate = null;
        await Error(() => database.Sessions().AuthenticateAsync(issued.Token, true, default), 401, "session-revoked");
        Assert.False(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
    }

    [PostgresFact]
    public async Task Every_refusal_keeps_its_code_and_reads_once()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var owner = await database.SeedAsync(key);
        var other = await database.SeedAsync(EthECKey.GenerateKey(), role: "member");
        var issued = await database.SignInAsync(key);
        var id = issued.Session.Session.Id;
        var commands = Count(database);
        async Task Refused(int status, string code, string? token = null)
        {
            commands.Clear();
            await Error(() => database.Sessions().AuthenticateAsync(token ?? issued.Token, true, default), status, code);
            Assert.Equal(["SELECT"], commands.Verbs());
            commands.Clear();
            Assert.False(await database.Sessions().StillValidAsync(id, default));
            Assert.Equal(["SELECT"], commands.Verbs());
        }
        async Task Sql(string sql)
        {
            await using var connection = new NpgsqlConnection(database.Connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql.Replace("{schema}", $"\"{database.Schema.Name}\""), connection);
            await command.ExecuteNonQueryAsync();
        }

        commands.Clear();
        await Error(() => database.Sessions().AuthenticateAsync("not a token", true, default), 401, "session-required");
        Assert.Empty(commands.Verbs());
        commands.Clear();
        await Error(() => database.Sessions().AuthenticateAsync(IdentityTokens.Create(), true, default), 401, "session-revoked");
        Assert.Equal(["SELECT"], commands.Verbs());
        commands.Clear();
        Assert.False(await database.Sessions().StillValidAsync(Guid.NewGuid(), default));
        Assert.Equal(["SELECT"], commands.Verbs());

        // A wallet now belonging to another user no longer carries the session.
        await Sql($"UPDATE {{schema}}.user_wallets SET user_id = '{other.User.Id}' WHERE id = '{owner.Wallet.Id}'");
        await Refused(401, "session-revoked");
        await Sql($"UPDATE {{schema}}.user_wallets SET user_id = '{owner.User.Id}' WHERE id = '{owner.Wallet.Id}'");
        Assert.True(await database.Sessions().StillValidAsync(id, default));

        await Sql("UPDATE {schema}.users SET status = 'suspended'");
        await Refused(403, "user-suspended");
        // Revocation and expiry are checked before the user, exactly as before.
        await Sql("UPDATE {schema}.sessions SET revoked_at = now()");
        await Refused(401, "session-revoked");
        await Sql("UPDATE {schema}.sessions SET revoked_at = NULL");
        database.Clock.Now = issued.Session.Session.IdleExpiresAt;
        await Refused(401, "session-expired");
        database.Clock.Now = issued.Session.Session.CreatedAt;
        await Sql("UPDATE {schema}.users SET status = 'active'");
        Assert.True(await database.Sessions().StillValidAsync(id, default));
        await Sql("UPDATE {schema}.sessions SET absolute_expires_at = created_at");
        await Refused(401, "session-expired");
    }

    private static async Task Error(Func<Task> action, int status, string code)
    {
        var error = await Assert.ThrowsAsync<ProblemException>(action);
        Assert.Equal((status, code), (error.StatusCode, error.Code));
    }

    private static CommandLog Count(IdentityDatabase database)
    {
        var log = new CommandLog();
        database.Interceptor = log;
        return log;
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<string> verbs = [];
        public Func<Task>? BeforeUpdate { get; set; }
        public void Clear() { lock (verbs) verbs.Clear(); }
        public string[] Verbs() { lock (verbs) return [.. verbs]; }

        private void Record(DbCommand command)
        {
            lock (verbs) verbs.Add(command.CommandText.TrimStart().Split([' ', '\n', '\r'], 2)[0].ToUpperInvariant());
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            if (BeforeUpdate is { } hook && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                await hook();
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
