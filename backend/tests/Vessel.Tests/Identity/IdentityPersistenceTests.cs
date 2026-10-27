using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.Signer;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Identity;

namespace Vessel.Tests.Identity;

public sealed class IdentityPersistenceTests
{
    [PostgresFact]
    public async Task Real_migrations_and_history_are_isolated_and_cache_keys_include_the_schema()
    {
        static async Task<string[]> Inventory()
        {
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("Vessel_TEST_POSTGRES"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema IN ('identity','public') ORDER BY 1", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var tables = new List<string>();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            return tables.ToArray();
        }
        var before = await Inventory();
        await using var first = await IdentityDatabase.CreateAsync();
        await using var second = await IdentityDatabase.CreateAsync();
        await using var db = first.CreateDbContext();
        await using var other = second.CreateDbContext();
        Assert.Equal(before, await Inventory());
        Assert.Equal(first.Schema.Name, db.Model.GetDefaultSchema());
        Assert.Equal(second.Schema.Name, other.Model.GetDefaultSchema());
        Assert.Equal(["20261017155708_InitialIdentity", "20261027213124_DevicePairing"], await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(7, db.Model.GetEntityTypes().Count());
        Assert.All(db.Model.GetEntityTypes(), entity => Assert.Equal(first.Schema.Name, entity.GetSchema()));
        Assert.All(db.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()),
            key => Assert.Equal(first.Schema.Name, key.PrincipalEntityType.GetSchema()));
        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.DoesNotContain("\"identity\"", script);
        Assert.Contains(first.Schema.Name, script);
        Assert.Contains("__EFMigrationsHistory", script);
    }

    [PostgresFact]
    public async Task Challenges_are_single_use_even_when_verification_fails_and_accept_only_the_clock_skew()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var binding = IdentityTokens.Create();
        var service = database.Service();
        var challenge = await service.ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        var request = new VerifyRequest(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false);
        await Problem(() => service.VerifyAsync(request, "https://other.test", binding, null, null, default), "origin-rejected");
        await Problem(() => service.VerifyAsync(request, "https://vessel.test", binding, null, null, default), "challenge-invalid");
        challenge = await service.ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        request = new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false);
        await Problem(() => service.VerifyAsync(request, "https://vessel.test", IdentityTokens.Create(), null, null, default), "challenge-invalid");
        await Problem(() => service.VerifyAsync(request, "https://vessel.test", binding, null, null, default), "challenge-invalid");
        challenge = await service.ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        database.Clock.Now = challenge.ExpiresAt.AddSeconds(29);
        var valid = await service.VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
            "https://vessel.test", binding, null, null, default);
        Assert.NotNull(valid.SignedIn);
        challenge = await service.ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        database.Clock.Now = challenge.ExpiresAt.AddSeconds(31);
        await Problem(() => service.VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
            "https://vessel.test", binding, null, null, default), "challenge-expired");
    }

    [PostgresFact]
    public async Task Concurrent_verify_of_one_challenge_allows_exactly_one_session()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var binding = IdentityTokens.Create();
        var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                await database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
                    "https://vessel.test", binding, null, null, default);
                return "success";
            }
            catch (ProblemException error) { return error.Code; }
        }));
        Assert.Single(results, result => result == "success");
        Assert.Equal(3, results.Count(result => result == "challenge-invalid"));
    }

    [PostgresFact]
    public async Task Challenge_and_pending_registration_caps_evict_the_oldest_without_address_counters()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        var oldest = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var tied = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var expiredChallenge = Guid.NewGuid();
        var expiredRegistration = Guid.NewGuid();
        await using (var db = database.CreateDbContext())
        {
            db.Challenges.AddRange(Enumerable.Range(0, 10000).Select(i => new AuthChallenge
            {
                Id = i == 9999 ? oldest : i == 9998 ? tied : Guid.NewGuid(), Address = key.GetPublicAddress().ToLowerInvariant(),
                Origin = "https://vessel.test", IssuedAt = database.Clock.Now.AddSeconds(i >= 9998 ? -9999 : -i),
                ExpiresAt = database.Clock.Now.AddMinutes(5)
            }));
            db.Challenges.Add(new AuthChallenge { Id = expiredChallenge, IssuedAt = database.Clock.Now, ExpiresAt = database.Clock.Now.AddSeconds(-31) });
            db.Registrations.Add(new PendingRegistration { Id = expiredRegistration, TokenHash = [1],
                CreatedAt = database.Clock.Now, ExpiresAt = database.Clock.Now });
            await db.SaveChangesAsync();
        }
        var capture = new CaptureCommands();
        database.Interceptor = capture;
        database.Settings = database.Settings with { Registration = "Open", MaxPendingRegistrations = 1 };
        var binding = IdentityTokens.Create();
        async Task<string> Pending()
        {
            var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
            var verified = await database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
                "https://vessel.test", binding, null, null, default);
            return verified.Registration!;
        }
        var first = await Pending();
        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(10000, await db.Challenges.CountAsync());
            Assert.False(await db.Challenges.AnyAsync(x => x.Id == oldest));
            Assert.True(await db.Challenges.AnyAsync(x => x.Id == tied));
            Assert.False(await db.Challenges.AnyAsync(x => x.Id == expiredChallenge));
            Assert.False(await db.Registrations.AnyAsync(x => x.Id == expiredRegistration));
        }
        var second = await Pending();
        await Problem(() => database.Service().RegisterAsync(new(first, "Name", null, false), "https://vessel.test", binding, null, null, default),
            "registration-invalid");
        await database.Service().RegisterAsync(new(second, "Name", null, false), "https://vessel.test", binding, null, null, default);
        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.Registrations.ToListAsync());
        foreach (var table in new[] { "auth_challenges", "pending_registrations" })
        {
            Assert.Contains(capture.Commands, sql => sql.Contains("SELECT count(*)", StringComparison.Ordinal) &&
                sql.Contains(table, StringComparison.Ordinal));
            Assert.Contains(capture.Commands, sql => sql.StartsWith("DELETE FROM", StringComparison.Ordinal) &&
                sql.Contains(table, StringComparison.Ordinal) && sql.Contains("ORDER BY", StringComparison.Ordinal) &&
                sql.Contains("LIMIT", StringComparison.Ordinal));
        }
    }

    [PostgresFact]
    public async Task Caps_preserve_challenge_skew_and_break_pending_timestamp_ties_by_id()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        database.Settings = database.Settings with { Registration = "Open", MaxPendingRegistrations = 2 };
        var key = EthECKey.GenerateKey();
        var binding = IdentityTokens.Create();
        var oldest = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var retained = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var skew = Guid.NewGuid();
        await using (var db = database.CreateDbContext())
        {
            db.Challenges.Add(new AuthChallenge { Id = skew, ExpiresAt = database.Clock.Now.AddSeconds(-30) });
            db.Registrations.AddRange(new[] { oldest, retained }.Select((id, index) => new PendingRegistration
            {
                Id = id, TokenHash = [(byte)index], CreatedAt = database.Clock.Now.AddMinutes(-1),
                ExpiresAt = database.Clock.Now.AddMinutes(5)
            }));
            await db.SaveChangesAsync();
        }
        var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        await database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
            "https://vessel.test", binding, null, null, default);
        await using var verify = database.CreateDbContext();
        Assert.True(await verify.Challenges.AnyAsync(x => x.Id == skew));
        Assert.False(await verify.Registrations.AnyAsync(x => x.Id == oldest));
        Assert.True(await verify.Registrations.AnyAsync(x => x.Id == retained));
        Assert.Equal(2, await verify.Registrations.CountAsync());
    }

    [PostgresFact]
    public async Task Bad_signature_cannot_enumerate_wallets_and_is_consumed()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        database.Settings = database.Settings with { Registration = "Closed" };
        var key = EthECKey.GenerateKey();
        var binding = IdentityTokens.Create();
        var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        await Problem(() => database.Service().VerifyAsync(new(challenge.ChallengeId, "0x12", false), "https://vessel.test", binding, null, null, default),
            "signature-unsupported");
        challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        await Problem(() => database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
            "https://vessel.test", binding, null, null, default), "registration-closed");
    }

    [PostgresFact]
    public async Task Registration_rolls_back_on_bad_invitation_and_invitation_exhaustion_is_atomic()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        database.Settings = database.Settings with { Registration = "Invitation" };
        var code = InvitationCodes.Create();
        await using (var db = database.CreateDbContext())
        {
            db.Invitations.Add(new Invitation { Id = Guid.NewGuid(), CodeHash = IdentityTokens.Hash(InvitationCodes.Normalize(code)),
                CreatedAt = database.Clock.Now, ExpiresAt = database.Clock.Now.AddDays(7), MaxUses = 1 });
            await db.SaveChangesAsync();
        }
        async Task<(string Token, string Binding)> Pending()
        {
            var key = EthECKey.GenerateKey();
            var binding = IdentityTokens.Create();
            var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
            var verified = await database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
                "https://vessel.test", binding, null, null, default);
            return (verified.Registration!, binding);
        }
        var first = await Pending();
        await Problem(() => database.Service().RegisterAsync(new(first.Token, "Name", InvitationCodes.Create(), false),
            "https://vessel.test", first.Binding, null, null, default), "invitation-invalid");
        var second = await Pending();
        var results = await Task.WhenAll(new[] { first, second }.Select(async pending =>
        {
            try
            {
                await database.Service().RegisterAsync(new(pending.Token, "Name", code, false),
                    "https://vessel.test", pending.Binding, null, null, default);
                return "success";
            }
            catch (ProblemException error) { return error.Code; }
        }));
        Assert.Single(results, x => x == "success");
        Assert.Single(results, x => x == "invitation-invalid");
        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.Users.ToListAsync());
        Assert.Single(await verify.Wallets.ToListAsync());
        Assert.Equal(1, (await verify.Invitations.SingleAsync()).Uses);
        Assert.Single(await verify.Registrations.Where(x => x.ConsumedAt == null).ToListAsync());
    }

    [PostgresFact]
    public async Task Two_pending_registrations_for_one_wallet_cannot_create_two_users()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        database.Settings = database.Settings with { Registration = "Open" };
        var key = EthECKey.GenerateKey();
        var binding = IdentityTokens.Create();
        async Task<string> Pending()
        {
            var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
            return (await database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
                "https://vessel.test", binding, null, null, default)).Registration!;
        }
        var tokens = new[] { await Pending(), await Pending() };
        var results = await Task.WhenAll(tokens.Select(async token =>
        {
            try { await database.Service().RegisterAsync(new(token, "Name", null, false), "https://vessel.test", binding, null, null, default); return "success"; }
            catch (ProblemException error) { return error.Code; }
        }));
        Assert.Single(results, x => x == "success");
        Assert.Single(results, x => x == "wallet-taken");
    }

    [PostgresFact]
    public async Task Directory_is_singleton_safe_and_keyset_pages_only_active_owners()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        // Insert out of order so the page order cannot accidentally follow insertion order.
        var second = await database.SeedAsync(EthECKey.GenerateKey(), Guid.Parse("f0000000-0000-0000-0000-000000000002"), role: "member");
        var first = await database.SeedAsync(EthECKey.GenerateKey(), Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var directory = new IdentityOwnerDirectory(database);
        Assert.Equal([first.User.Id, second.User.Id], await directory.ActiveOwnersAsync(null, 200, default));
        Assert.Equal([first.User.Id], await directory.ActiveOwnersAsync(null, 1, default));
        Assert.Equal([second.User.Id], await directory.ActiveOwnersAsync(first.User.Id, 1, default));
        Assert.Empty(await directory.ActiveOwnersAsync(second.User.Id, 1, default));
        foreach (var limit in new[] { -1, 0, 201 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => directory.ActiveOwnersAsync(null, limit, default));
        await using var db = database.CreateDbContext();
        await db.Users.Where(x => x.Id == first.User.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "suspended"));
        Assert.Equal(JournalOwnerStatus.Suspended, await directory.StatusAsync(first.User.Id, default));
        Assert.Equal(JournalOwnerStatus.Active, await directory.StatusAsync(second.User.Id, default));
        Assert.Equal(JournalOwnerStatus.Unknown, await directory.StatusAsync(Guid.NewGuid(), default));
        Assert.Equal([second.User.Id], await directory.ActiveOwnersAsync(null, 1, default));
        Assert.Equal([second.User.Id], await directory.ActiveOwnersAsync(first.User.Id, 200, default));
        Assert.Empty(await directory.ActiveOwnersAsync(second.User.Id, 200, default));
    }

    [PostgresFact]
    public async Task Cleanup_preserves_recent_records_and_challenge_skew()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var issued = await database.SignInAsync(key);
        await using (var db = database.CreateDbContext())
        {
            db.Challenges.AddRange(new AuthChallenge { Id = Guid.NewGuid(), Address = key.GetPublicAddress(), ExpiresAt = database.Clock.Now.AddSeconds(-20) },
                new AuthChallenge { Id = Guid.NewGuid(), Address = key.GetPublicAddress(), ExpiresAt = database.Clock.Now.AddDays(-1) });
            db.Registrations.Add(new PendingRegistration { Id = Guid.NewGuid(), Address = key.GetPublicAddress(), TokenHash = [1], ExpiresAt = database.Clock.Now.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        var cleanup = new IdentityCleanup(database, database.Clock, NullLogger<IdentityCleanup>.Instance);
        await cleanup.CleanAsync(default);
        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.Challenges.Where(x => x.ConsumedAt == null).ToListAsync());
        Assert.Empty(await verify.Registrations.ToListAsync());
        Assert.Single(await verify.Sessions.ToListAsync());
        database.Clock.Now = database.Clock.Now.AddDays(32);
        await cleanup.CleanAsync(default);
        Assert.Empty(await verify.Sessions.AsNoTracking().ToListAsync());
        Assert.False(await database.Sessions().StillValidAsync(issued.Session.Session.Id, default));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_waits_for_verify_or_register_to_commit_before_deleting_its_expired_row(bool register)
    {
        await using var database = await IdentityDatabase.CreateAsync();
        database.Settings = database.Settings with { Registration = "Open" };
        var key = EthECKey.GenerateKey();
        await database.SeedAsync(key);
        var binding = IdentityTokens.Create();
        var challenge = await database.Service().ChallengeAsync(key.GetPublicAddress(), "https://vessel.test", binding, default);
        var token = IdentityTokens.Create();
        if (register)
        {
            await using var db = database.CreateDbContext();
            db.Registrations.Add(new PendingRegistration
            {
                Id = Guid.NewGuid(), TokenHash = IdentityTokens.Hash(token), Address = EthECKey.GenerateKey().GetPublicAddress().ToLowerInvariant(),
                BindingHash = IdentityTokens.Hash(binding), Origin = "https://vessel.test",
                CreatedAt = database.Clock.Now, ExpiresAt = database.Clock.Now.AddMinutes(10)
            });
            await db.SaveChangesAsync();
        }
        var pause = new PauseConsumption();
        database.Interceptor = pause;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task mutation = register
            ? database.Service().RegisterAsync(new(token, "Synthetic registration", null, false),
                "https://vessel.test", binding, null, null, timeout.Token)
            : database.Service().VerifyAsync(new(challenge.ChallengeId, IdentityDatabase.Sign(key, challenge.Message), false),
                "https://vessel.test", binding, null, null, timeout.Token);
        Task? cleanup = null;
        try
        {
            await pause.Entered.Task.WaitAsync(timeout.Token);
            database.Clock.Now = database.Clock.Now.AddMinutes(11);
            cleanup = new IdentityCleanup(database, database.Clock, NullLogger<IdentityCleanup>.Instance).CleanAsync(timeout.Token);
            // Observe the real PostgreSQL wait, not a timing-dependent delay.
            await using var connection = new NpgsqlConnection(database.Connection);
            await connection.OpenAsync(timeout.Token);
            await using var waiting = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @name AND wait_event = 'advisory')
                """, connection);
            waiting.Parameters.AddWithValue("name", database.Schema.Name);
            while (!cleanup.IsCompleted && !Equals(await waiting.ExecuteScalarAsync(timeout.Token), true))
                await Task.Delay(10, timeout.Token);
            Assert.False(cleanup.IsCompleted, "Cleanup must wait for the identity transaction that is consuming the row.");
        }
        finally
        {
            pause.Resume.TrySetResult();
            await mutation.WaitAsync(timeout.Token);
            if (cleanup is not null) await cleanup.WaitAsync(timeout.Token);
        }
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Challenges.ToListAsync());
        Assert.Empty(await verify.Registrations.ToListAsync());
        Assert.Single(await verify.Sessions.ToListAsync());
    }

    private sealed class PauseConsumption : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry =>
                entry.Entity is AuthChallenge { ConsumedAt: not null } or PendingRegistration { ConsumedAt: not null }))
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CaptureCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static async Task Problem(Func<Task> action, string code) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<ProblemException>(action)).Code);
}
