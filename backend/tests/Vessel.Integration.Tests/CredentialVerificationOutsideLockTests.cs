using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>
/// Saving a read-only credential reads and validates, verifies with the venue with nothing open, then rechecks under a
/// short account lock. A change to the account or its stored credential during verification refuses the save (409).
/// </summary>
public sealed class CredentialVerificationOutsideLockTests
{
    [PostgresFact]
    public async Task Verification_holds_no_transaction_and_a_concurrent_rename_completes_then_the_save_is_refused()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await SeedAsync(database, owner);
        await using var writerDb = database.Context(owner); await using var managementDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var work = TestUnitOfWork.For(writerDb);
        var verifier = new GatedVerifier(() => work.IsActive || writerDb.Database.CurrentTransaction is not null);
        var saving = Service(writerDb, owner, vault, verifier).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(verifier.SawOpenUnit);
        // The account row is not locked while the venue is being asked.
        var renamed = await Management(managementDb, owner).UpdateAccountAsync(account.Id, new("Renamed", null, true, 1), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Renamed", renamed.Name);
        verifier.Release.SetResult();
        var refused = await Assert.ThrowsAsync<ProblemException>(() => saving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal((409, AccountCredentialService.ChangedWhileVerifying), (refused.StatusCode, refused.Message));
        await using var check = database.Context(owner);
        Assert.Empty(await check.AccountCredentials.ToListAsync());
    }

    [PostgresFact]
    public async Task A_save_that_finishes_after_another_save_committed_is_refused()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await SeedAsync(database, owner);
        await using var firstDb = database.Context(owner); await using var secondDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var firstVerifier = new GatedVerifier(expires: SecurityFixture.Now.AddDays(30));
        var secondVerifier = new GatedVerifier(expires: SecurityFixture.Now.AddDays(90));
        var first = Service(firstDb, owner, vault, firstVerifier).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var second = Service(secondDb, owner, vault, secondVerifier).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await Task.WhenAll(firstVerifier.Entered.Task, secondVerifier.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        firstVerifier.Release.SetResult();
        Assert.Equal(SecurityFixture.Now.AddDays(30), (await first.WaitAsync(TimeSpan.FromSeconds(5))).Credential!.ExpiresAt);
        secondVerifier.Release.SetResult();
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)))).StatusCode);
        await using var check = database.Context(owner);
        Assert.Equal(SecurityFixture.Now.AddDays(30), (await check.AccountCredentials.SingleAsync()).ExpiresAt);
    }

    [PostgresFact]
    public async Task A_save_racing_a_credential_delete_is_refused_and_the_delete_stands()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await SeedAsync(database, owner);
        await using var writerDb = database.Context(owner); await using var deleteDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var opened = new GatedVerifier(); opened.Release.SetResult();
        await Service(writerDb, owner, vault, opened).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        writerDb.ChangeTracker.Clear();
        var verifier = new GatedVerifier();
        var saving = Service(writerDb, owner, vault, verifier).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Service(deleteDb, owner, vault, new GatedVerifier()).DeleteAsync(account.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        verifier.Release.SetResult();
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => saving.WaitAsync(TimeSpan.FromSeconds(5)))).StatusCode);
        await using var check = database.Context(owner);
        Assert.Empty(await check.AccountCredentials.ToListAsync());
    }

    [PostgresFact]
    public async Task A_refusal_mark_during_verification_does_not_conflict_and_a_replacement_keeps_its_creation_time()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await SeedAsync(database, owner);
        await using var writerDb = database.Context(owner); await using var readerDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var opened = new GatedVerifier(); opened.Release.SetResult();
        var original = await Service(writerDb, owner, vault, opened).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        Assert.Equal("valid", original.Credential!.Status);
        DateTimeOffset created;
        await using (var read = database.Context(owner)) created = (await read.AccountCredentials.SingleAsync()).CreatedAt;
        writerDb.ChangeTracker.Clear();
        var verifier = new GatedVerifier(expires: SecurityFixture.Now.AddDays(45));
        var saving = Service(writerDb, owner, vault, verifier).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A monitor read refused the stored token meanwhile; LastError is not part of the credential revision.
        var reader = new AccountCredentialReader(new WorkspaceStore(readerDb, TestUnitOfWork.For(readerDb)),
            new AccountCredentialStore(readerDb, TestUnitOfWork.For(readerDb)), new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.For(readerDb));
        var attempt = (await reader.ReadAttemptAsync(account.Id, default))!;
        await reader.MarkRefusedAsync(account.Id, attempt.Revision, default);
        verifier.Release.SetResult();
        var saved = await saving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((SecurityFixture.Now.AddDays(45), "valid"), (saved.Credential!.ExpiresAt, saved.Credential.Status));
        await using var check = database.Context(owner);
        var row = await check.AccountCredentials.SingleAsync();
        Assert.Null(row.LastError);
        Assert.Equal(created, row.CreatedAt);
    }

    [PostgresFact]
    public async Task A_save_whose_account_was_deleted_during_verification_reports_not_found()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await SeedAsync(database, owner);
        await using var writerDb = database.Context(owner); await using var managementDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var verifier = new GatedVerifier();
        var saving = Service(writerDb, owner, vault, verifier).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Management(managementDb, owner).DeleteAccountAsync(account.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        verifier.Release.SetResult();
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => saving.WaitAsync(TimeSpan.FromSeconds(5)))).StatusCode);
        await using var check = database.Context(owner);
        Assert.Empty(await check.AccountCredentials.ToListAsync());
    }

    [PostgresFact]
    public async Task Expiry_is_checked_against_the_time_of_the_save_not_of_the_request()
    {
        await using var database = await CoreDatabase.CreateAsync(); var owner = Guid.NewGuid(); var account = await SeedAsync(database, owner);
        await using var writerDb = database.Context(owner);
        using var vault = SecurityFixture.Vault();
        var clock = new MovingClock(SecurityFixture.Now);
        var verifier = new GatedVerifier(expires: SecurityFixture.Now.AddMinutes(1));
        var saving = Service(writerDb, owner, vault, verifier, clock).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now = SecurityFixture.Now.AddMinutes(2);
        verifier.Release.SetResult();
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => saving.WaitAsync(TimeSpan.FromSeconds(5)))).StatusCode);
        await using var check = database.Context(owner);
        Assert.Empty(await check.AccountCredentials.ToListAsync());
    }

    [Fact]
    public async Task Verification_refuses_to_start_inside_an_open_unit_of_work()
    {
        var owner = Guid.NewGuid(); var account = SecurityFixture.Account(owner);
        using var vault = SecurityFixture.Vault();
        var verifier = new GatedVerifier(); verifier.Release.SetResult();
        var work = TestUnitOfWork.None;
        var store = new MemoryWorkspaceStore(owner); store.Accounts.Add(account);
        var service = new AccountCredentialService(store, new SecurityCredentialStore(), new CoreOwner(owner),
            SecurityFixture.Registry(verifier), vault, SecurityFixture.Clock, work, MonitorServerState.Reads());
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync(
            () => service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default), default));
        Assert.False(verifier.Entered.Task.IsCompleted);
    }

    private static async Task<Account> SeedAsync(CoreDatabase database, Guid owner)
    {
        var account = SecurityFixture.Account(owner);
        await using var seed = database.Context(owner);
        seed.Accounts.Add(account); await seed.SaveChangesAsync();
        return account;
    }

    private static AccountCredentialService Service(VesselDbContext db, Guid owner, ICredentialVault vault, GatedVerifier verifier,
        TimeProvider? clock = null) =>
        new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new AccountCredentialStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner),
            SecurityFixture.Registry(verifier), vault, clock ?? SecurityFixture.Clock, TestUnitOfWork.For(db), MonitorServerState.Reads(db, clock ?? SecurityFixture.Clock));

    private static WorkspaceService Management(VesselDbContext db, Guid owner) =>
        new(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.For(db));

    private sealed class GatedVerifier(Func<bool>? unitOpen = null, DateTimeOffset? expires = null) : IVenueCredentialVerifier
    {
        public string VenueId => SecurityFixture.Venue.Id;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawOpenUnit { get; private set; }
        public async Task<VerifiedVenueCredential> VerifyAsync(string sourceId, string token, CancellationToken cancellationToken)
        {
            SawOpenUnit = unitOpen?.Invoke() ?? false;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new("single", expires ?? SecurityFixture.Now.AddDays(60));
        }
    }

    private sealed class MovingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
