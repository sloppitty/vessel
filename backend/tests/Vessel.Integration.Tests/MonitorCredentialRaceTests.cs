using Microsoft.EntityFrameworkCore;
using Vessel.Application.Credentials;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class MonitorCredentialRaceTests
{
    [PostgresFact]
    public async Task Cross_instance_refusal_is_fenced_to_the_sealed_credential_attempt()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var account = SecurityFixture.Account(owner);
        using var vault = SecurityFixture.Vault();
        await using (var seed = database.Context(owner))
        {
            seed.Add(account);
            await seed.SaveChangesAsync();
            var work = TestUnitOfWork.For(seed);
            await SecurityFixture.Service(owner, new WorkspaceStore(seed, work), new AccountCredentialStore(seed, work),
                vault, unitOfWork: work).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        }
        await using var worker = database.Context(owner);
        var workerWork = TestUnitOfWork.For(worker);
        var reader = new AccountCredentialReader(new WorkspaceStore(worker, workerWork),
            new AccountCredentialStore(worker, workerWork), new CoreOwner(owner), vault, SecurityFixture.Clock, workerWork);
        foreach (var replacement in new[] { SecurityFixture.Token, SecurityFixture.Token + "-replacement" })
        {
            var attempt = (await reader.ReadAttemptAsync(account.Id, default))!;
            await using (var api = database.Context(owner))
            {
                var work = TestUnitOfWork.For(api);
                await SecurityFixture.Service(owner, new WorkspaceStore(api, work), new AccountCredentialStore(api, work),
                    vault, unitOfWork: work).PutAsync(account.Id, new() { Token = replacement }, default);
            }
            await reader.MarkRefusedAsync(account.Id, attempt.Revision, default);
            Assert.True(await reader.IsUsableAsync(account, default));
            Assert.Equal(replacement, await reader.ReadAsync(account.Id, default));
            await using var verify = database.Context(owner);
            Assert.Null((await verify.AccountCredentials.SingleAsync()).LastError);
        }
        var current = (await reader.ReadAttemptAsync(account.Id, default))!;
        await reader.MarkRefusedAsync(account.Id, current.Revision, default);
        await using var final = database.Context(owner);
        Assert.Equal("refused", (await final.AccountCredentials.SingleAsync()).LastError);
        Assert.False(await reader.IsUsableAsync(account, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refusal_of_an_inflight_credential_cannot_disable_a_replacement(bool reverifySameToken)
    {
        var owner = Guid.NewGuid();
        var accounts = new MemoryWorkspaceStore(owner);
        var account = SecurityFixture.Account(owner);
        accounts.Accounts.Add(account);
        var credentials = new SecurityCredentialStore();
        using var vault = SecurityFixture.Vault();
        var service = SecurityFixture.Service(owner, accounts, credentials, vault);
        await service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var reader = new AccountCredentialReader(accounts, credentials, new CoreOwner(owner),
            vault, SecurityFixture.Clock, TestUnitOfWork.None);
        var attempt = Assert.IsType<CredentialAttempt>(await reader.ReadAttemptAsync(account.Id, default));
        Assert.Equal(SecurityFixture.Token, attempt.Token);
        Assert.DoesNotContain(SecurityFixture.Token, attempt.ToString()!);
        var replacement = reverifySameToken ? SecurityFixture.Token : SecurityFixture.Token + "-replacement";
        await service.PutAsync(account.Id, new() { Token = replacement }, default);
        await reader.MarkRefusedAsync(account.Id, attempt.Revision, default);
        Assert.Null(credentials.Stored!.LastError);
        Assert.True(await reader.IsUsableAsync(account, default));
        Assert.Equal(replacement, await reader.ReadAsync(account.Id, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_refusal_without_the_stored_revision_never_marks_the_credential(string revision)
    {
        var owner = Guid.NewGuid();
        var accounts = new MemoryWorkspaceStore(owner);
        var account = SecurityFixture.Account(owner);
        accounts.Accounts.Add(account);
        var credentials = new SecurityCredentialStore();
        using var vault = SecurityFixture.Vault();
        await SecurityFixture.Service(owner, accounts, credentials, vault).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var reader = new AccountCredentialReader(accounts, credentials, new CoreOwner(owner),
            vault, SecurityFixture.Clock, TestUnitOfWork.None);
        // Every read reports the sealed envelope's revision, never an empty one a refusal could match.
        Assert.NotEqual(revision, Assert.IsType<CredentialAttempt>(await reader.ReadAttemptAsync(account.Id, default)).Revision);
        await reader.MarkRefusedAsync(account.Id, revision, default);
        Assert.Null(credentials.Stored!.LastError);
        Assert.True(await reader.IsUsableAsync(account, default));
        Assert.Equal(SecurityFixture.Token, await reader.ReadAsync(account.Id, default));
    }
}
