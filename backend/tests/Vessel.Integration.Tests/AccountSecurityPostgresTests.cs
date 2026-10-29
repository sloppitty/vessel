using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Credentials;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class AccountSecurityPostgresTests
{
    private const string Before = "20261005174006_VenueContractIds";

    [PostgresFact]
    public async Task Migration_backfills_existing_identity_and_native_positions_and_installs_owner_scoped_unique_source()
    {
        await using var database = await CoreDatabase.CreateAsync(Before);
        var owner = Guid.NewGuid(); var id = Guid.NewGuid(); var manual = Guid.NewGuid();
        await using var db = database.Context(owner);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id", "OwnerId", "Name", "VenueId", "Address", "SyncStatus", "IsEnabled")
            VALUES ({id}, {owner}, 'Old', 'hyperliquid', {SecurityFixture.Address}, 'synced', false),
                   ({manual}, {owner}, 'Manual', 'manual', NULL, 'manual', true)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO account_snapshots ("OwnerId", "AccountId", "ObservedAtUtc", "ValueScope") VALUES ({owner}, {id}, now(), 'primary-perpetual-dex');
            INSERT INTO account_positions ("OwnerId", "AccountId", "ContractId", "SignedQuantity", "EntryPrice", "UnrealizedPnlUsd", "MarginUsedUsd")
            VALUES ({owner}, {id}, 'kPEPE', 2, 0.001, 0, 1);
            """);
        await db.Database.MigrateAsync();
        var old = await db.Accounts.SingleAsync(a => a.Id == id);
        Assert.Equal(SecurityFixture.Address, old.SourceId); Assert.Equal(old.SourceId, old.Address); Assert.False(old.IsEnabled);
        Assert.Null((await db.Accounts.SingleAsync(a => a.Id == manual)).SourceId);
        var position = await db.Positions.SingleAsync();
        Assert.Equal("1000PEPE", position.ContractId); Assert.Equal("kPEPE", position.VenueContractId);
        Assert.False(db.Database.HasPendingModelChanges());
        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.For(db));
        var duplicate = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Duplicate", "hyperliquid", Address: SecurityFixture.Address), default));
        Assert.Equal(409, duplicate.StatusCode);
        var created = await service.CreateAccountAsync(new(null, "Index", SecurityFixture.Venue.Id, SourceId: "9223372036854775807"), default);
        Assert.Equal("9223372036854775807", created.SourceId);
        var disabled = await service.UpdateAccountAsync(created.Id, new("Index", null, false, 1), default);
        Assert.False(disabled.IsEnabled);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Again", SecurityFixture.Venue.Id, SourceId: created.SourceId), default))).StatusCode);
        await using var other = database.Context(Guid.NewGuid());
        var otherService = new WorkspaceService(new WorkspaceStore(other, TestUnitOfWork.For(other)), new CoreOwner(other.CurrentOwnerId), SecurityFixture.Registry(), TestUnitOfWork.For(other));
        Assert.NotNull(await otherService.CreateAccountAsync(new(null, "Other owner", SecurityFixture.Venue.Id, SourceId: created.SourceId), default));
    }

    [PostgresFact]
    public async Task Database_rejects_noncanonical_sources_and_mismatched_address()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        foreach (var source in new[] { "01", "-1", "1.0", "9223372036854775808", "1e2" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO accounts ("Id", "OwnerId", "Name", "VenueId", "SourceId", "SyncStatus", "IsEnabled")
                VALUES ({Guid.NewGuid()}, {owner}, 'Bad', 'index-fixture', {source}, 'not-synced', true)
                """));
        }
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts ("Id", "OwnerId", "Name", "VenueId", "Address", "SourceId", "SyncStatus", "IsEnabled")
            VALUES ({Guid.NewGuid()}, {owner}, 'Bad', 'hyperliquid', {SecurityFixture.Address}, '12', 'not-synced', true)
            """));
    }

    [PostgresFact]
    public async Task Ciphertext_only_owner_filters_composite_fk_and_delete_cascade()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid();
        await using var db = database.Context(owner); var account = SecurityFixture.Account(owner); db.Accounts.Add(account); await db.SaveChangesAsync();
        var store = new WorkspaceStore(db, TestUnitOfWork.For(db)); var credentialStore = new AccountCredentialStore(db, TestUnitOfWork.For(db)); using var vault = SecurityFixture.Vault();
        var service = SecurityFixture.Service(owner, store, credentialStore, vault, unitOfWork: TestUnitOfWork.For(db));
        await service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var row = await db.AccountCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(owner, row.OwnerId); Assert.Equal(account.Id, row.AccountId);
        Assert.Equal(SecurityFixture.Venue.Id + "-read-token", row.Purpose); Assert.Equal("v1", row.KeyId);
        Assert.DoesNotContain(SecurityFixture.Token, System.Text.Encoding.UTF8.GetString(row.Ciphertext));
        var reader = new AccountCredentialReader(store, credentialStore, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.For(db));
        Assert.Equal(SecurityFixture.Token, await reader.ReadAsync(account.Id, default));
        await using var foreign = database.Context(Guid.NewGuid());
        Assert.Empty(await foreign.AccountCredentials.ToListAsync());
        Assert.Null(await new AccountCredentialStore(foreign, TestUnitOfWork.For(foreign)).FindAsync(account.Id, row.Purpose, default));
        var foreignService = SecurityFixture.Service(foreign.CurrentOwnerId, new WorkspaceStore(foreign, TestUnitOfWork.For(foreign)), new AccountCredentialStore(foreign, TestUnitOfWork.For(foreign)), vault, unitOfWork: TestUnitOfWork.For(foreign));
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => foreignService.GetAsync(account.Id, default))).StatusCode);
        await Assert.ThrowsAsync<PostgresException>(() => foreign.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO account_credentials ("OwnerId", "AccountId", "Purpose", "KeyId", "Nonce", "Ciphertext", "Tag", "Scope", "ExpiresAt", "CreatedAt", "LastVerifiedAt")
            VALUES ({foreign.CurrentOwnerId}, {account.Id}, {row.Purpose}, {row.KeyId}, {row.Nonce}, {row.Ciphertext}, {row.Tag}, 'single', now(), now(), now())
            """));
        var stolen = new AccountCredential
        {
            OwnerId = owner, AccountId = Guid.NewGuid(), Purpose = "read-token", KeyId = row.KeyId, Nonce = row.Nonce, Ciphertext = row.Ciphertext, Tag = row.Tag,
            Scope = "single", CreatedAt = SecurityFixture.Now, LastVerifiedAt = SecurityFixture.Now, ExpiresAt = SecurityFixture.Now.AddDays(20)
        };
        foreign.AccountCredentials.Add(stolen);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => foreign.SaveChangesAsync());
        await new WorkspaceService(store, new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.For(db)).DeleteAccountAsync(account.Id, default);
        Assert.Empty(await db.AccountCredentials.AsNoTracking().ToListAsync()); Assert.Empty(await db.Accounts.ToListAsync());
    }

    [PostgresFact]
    public async Task Disable_during_credential_verification_completes_and_the_save_is_refused_and_disabled_reader_never_opens_token()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = SecurityFixture.Account(owner);
        await using (var seed = database.Context(owner)) { seed.Accounts.Add(account); await seed.SaveChangesAsync(); }
        await using var writerDb = database.Context(owner); await using var managementDb = database.Context(owner);
        using var vault = SecurityFixture.Vault(); var verifier = new BlockingVerifier();
        var writer = new AccountCredentialService(new WorkspaceStore(writerDb, TestUnitOfWork.For(writerDb)), new AccountCredentialStore(writerDb, TestUnitOfWork.For(writerDb)), new CoreOwner(owner), SecurityFixture.Registry(verifier), vault, SecurityFixture.Clock, TestUnitOfWork.For(writerDb), MonitorServerState.Reads(writerDb, SecurityFixture.Clock));
        var saving = writer.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var management = new WorkspaceService(new WorkspaceStore(managementDb, TestUnitOfWork.For(managementDb)), new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.For(managementDb));
        // Verification holds no transaction or account lock, so disabling does not wait for the venue.
        Assert.False((await management.UpdateAccountAsync(account.Id, new("Disabled", null, false, 1), default).WaitAsync(TimeSpan.FromSeconds(5))).IsEnabled);
        verifier.Release.SetResult();
        var refused = await Assert.ThrowsAsync<ProblemException>(() => saving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(409, refused.StatusCode); Assert.Equal(AccountCredentialService.ChangedWhileVerifying, refused.Message);
        await using var check = database.Context(owner);
        Assert.False((await check.Accounts.SingleAsync()).IsEnabled); Assert.Empty(await check.AccountCredentials.ToListAsync());
        var reader = new AccountCredentialReader(new WorkspaceStore(check, TestUnitOfWork.For(check)), new AccountCredentialStore(check, TestUnitOfWork.For(check)), new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.For(check));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => reader.ReadAsync(account.Id, default))).StatusCode);
        await management.DeleteAccountAsync(account.Id, default); Assert.Empty(await check.AccountCredentials.AsNoTracking().ToListAsync());
    }

    [PostgresFact]
    public async Task Credential_write_during_a_refresh_supersedes_it_and_the_adapter_reads_in_its_own_transaction()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = SecurityFixture.Account(owner);
        await using (var seed = database.Context(owner)) { seed.Accounts.Add(account); await seed.SaveChangesAsync(); }
        await using var syncDb = database.Context(owner); await using var writeDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var syncStore = new WorkspaceStore(syncDb, TestUnitOfWork.For(syncDb));
        var credentials = new AccountCredentialReader(syncStore, new AccountCredentialStore(syncDb, TestUnitOfWork.For(syncDb)), new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.For(syncDb));
        var reader = new BlockingReader(credentials);
        var syncing = MonitorTestPaths.RefreshAsync(syncDb, owner, SecurityFixture.Registry(reader), account.Id, credentials);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The refresh holds its monitor lease, not the account row, while the venue read is outstanding.
        var verifier = new BlockingVerifier(); verifier.Release.SetResult();
        var writer = new AccountCredentialService(new WorkspaceStore(writeDb, TestUnitOfWork.For(writeDb)), new AccountCredentialStore(writeDb, TestUnitOfWork.For(writeDb)), new CoreOwner(owner), SecurityFixture.Registry(reader, verifier), vault, SecurityFixture.Clock, TestUnitOfWork.For(writeDb), MonitorServerState.Reads(writeDb, SecurityFixture.Clock));
        try { await writer.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { reader.Release.TrySetResult(); }
        Assert.True(verifier.Entered.Task.IsCompleted);
        // The new credential revision discards the read taken before it; nothing is written for it.
        Assert.Equal(502, (await Assert.ThrowsAsync<ProblemException>(() => syncing.WaitAsync(TimeSpan.FromSeconds(5)))).StatusCode);
        await using var verify = database.Context(owner);
        Assert.Single(await verify.AccountCredentials.ToListAsync()); Assert.Equal("not-synced", (await verify.Accounts.SingleAsync()).SyncStatus);
        Assert.Empty(await verify.Snapshots.ToListAsync());
    }

    private sealed class BlockingVerifier : IVenueCredentialVerifier
    {
        public string VenueId => SecurityFixture.Venue.Id;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VerifiedVenueCredential> VerifyAsync(string sourceId, string token, CancellationToken cancellationToken)
        {
            Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); return new("single", SecurityFixture.Now.AddDays(60));
        }
    }

    private sealed class BlockingReader(IAccountCredentialReader credentials) : IPerpetualVenueReader
    {
        public string VenueId => SecurityFixture.Venue.Id;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VenueInstrument>>([]);
        public Task<PerpetualVenueReadResult> ReadAsync(string source, CancellationToken cancellationToken) => throw new InvalidOperationException("Use the Account overload.");
        public async Task<PerpetualVenueReadResult> ReadAsync(Account account, CancellationToken cancellationToken)
        {
            Assert.Null(await credentials.ReadAsync(account.Id, cancellationToken));
            Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken);
            return new(new(SecurityFixture.Now, "primary-perpetuals", 100m, null, 0m, []), [], [], "Bounded fixture");
        }
    }
}
