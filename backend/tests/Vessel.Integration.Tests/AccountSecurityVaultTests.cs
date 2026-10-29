using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Workspace;
using Vessel.Infrastructure.Credentials;

namespace Vessel.Tests;

public sealed class AccountSecurityVaultTests
{
    [Fact]
    public async Task Pasted_token_whitespace_is_removed_before_verification_and_storage()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        var verifier = new SecurityVerifier();
        var service = SecurityFixture.Service(owner, store, credentials, vault, verifier);
        await service.PutAsync(account.Id, new() { Token = " \r\n" + SecurityFixture.Token + "\t " }, default);
        Assert.Equal(SecurityFixture.Token, verifier.ReceivedToken);
        var reader = new AccountCredentialReader(store, credentials, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.None);
        Assert.Equal(SecurityFixture.Token, await reader.ReadAsync(account.Id, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task Empty_token_is_rejected_before_verification_or_name_discovery(string token)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        var verifier = new SecurityVerifier();
        var service = SecurityFixture.Service(owner, store, credentials, vault, verifier);
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            service.PutAsync(account.Id, new() { Token = token }, default))).StatusCode);
        Assert.Null(verifier.ReceivedToken); Assert.Null(credentials.Stored);
        var discovery = new SecurityDiscovery([new("123", "Main", "main")]);
        var discover = new AccountDiscoveryService(store, new CoreOwner(owner), SecurityFixture.Registry(discovery), MonitorServerState.Reads());
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() =>
            discover.DiscoverAsync(SecurityFixture.Venue.Id,
                new CredentialDiscoveryRequest { Address = SecurityFixture.Address, Token = token }, default))).StatusCode);
        Assert.Null(discovery.ReceivedToken);
    }

    [Fact]
    public async Task Credential_metadata_never_contains_secrets_and_unavailable_storage_allows_delete()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        var service = SecurityFixture.Service(owner, store, credentials, vault);
        var saved = await service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        Assert.Equal("valid", saved.Credential!.Status); Assert.True(saved.StorageConfigured);
        var json = JsonSerializer.Serialize(saved);
        Assert.DoesNotContain(SecurityFixture.Token, json); Assert.DoesNotContain("Ciphertext", json); Assert.DoesNotContain("KeyId", json);
        var reader = new AccountCredentialReader(store, credentials, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.None);
        Assert.Equal(SecurityFixture.Token, await reader.ReadAsync(account.Id, default));
        using var absent = new AesGcmCredentialVault(new());
        var unavailable = SecurityFixture.Service(owner, store, credentials, absent);
        Assert.Equal("unavailable", (await unavailable.GetAsync(account.Id, default)).Credential!.Status);
        Assert.Equal(503, (await Assert.ThrowsAsync<ProblemException>(() => unavailable.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default))).StatusCode);
        await unavailable.DeleteAsync(account.Id, default); Assert.Null(credentials.Stored);
    }

    [Fact]
    public async Task Expiring_expired_disabled_foreign_and_verification_failure_are_safe()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        var verifier = new SecurityVerifier { Expiry = SecurityFixture.Now.AddDays(7) };
        var service = SecurityFixture.Service(owner, store, credentials, vault, verifier);
        Assert.Equal("expiring", (await service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default)).Credential!.Status);
        verifier.Error = new ProblemException(400, SecurityFixture.Token);
        var error = await Assert.ThrowsAsync<ProblemException>(() => service.PutAsync(account.Id, new() { Token = "replacement" }, default));
        Assert.DoesNotContain(SecurityFixture.Token, error.ToString()); Assert.Equal(SecurityFixture.Now.AddDays(7), credentials.Stored!.ExpiresAt);
        credentials.Stored.ExpiresAt = SecurityFixture.Now;
        Assert.Equal("expired", (await service.GetAsync(account.Id, default)).Credential!.Status);
        var reader = new AccountCredentialReader(store, credentials, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.None);
        Assert.Null(await reader.ReadAsync(account.Id, default));
        account.UpdateSettings(account.Name, null, false);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => reader.ReadAsync(account.Id, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProblemException>(() => service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default))).StatusCode);
        Assert.NotNull((await service.GetAsync(account.Id, default)).Credential);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.GetAsync(Guid.NewGuid(), default))).StatusCode);
    }

    [Fact]
    public async Task Venue_refusal_marks_credential_unavailable_until_it_is_saved_again()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        var service = SecurityFixture.Service(owner, store, credentials, vault);
        await service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var reader = new AccountCredentialReader(store, credentials, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.None);
        var attempt = (await reader.ReadAttemptAsync(account.Id, default))!;
        await reader.MarkRefusedAsync(account.Id, attempt.Revision, default);
        Assert.Equal(AccountCredentialReader.RefusedError, credentials.Stored!.LastError);
        Assert.Equal("unavailable", (await service.GetAsync(account.Id, default)).Credential!.Status);
        Assert.False(await reader.IsUsableAsync(account, default));
        Assert.Null(await reader.ReadAsync(account.Id, default));
        await Assert.ThrowsAsync<ProblemException>(() =>
            new AccountCredentialReader(store, credentials, new CoreOwner(Guid.NewGuid()), vault, SecurityFixture.Clock, TestUnitOfWork.None).MarkRefusedAsync(account.Id, attempt.Revision, default));
        await service.PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        Assert.Null(credentials.Stored!.LastError);
        Assert.True(await reader.IsUsableAsync(account, default));
    }

    [Fact]
    public async Task Usable_status_reads_metadata_only_and_matches_reader_rules()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner); var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        var credentials = new SecurityCredentialStore(); using var vault = SecurityFixture.Vault();
        await SecurityFixture.Service(owner, store, credentials, vault).PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        var reader = new AccountCredentialReader(store, credentials, new CoreOwner(owner), vault, SecurityFixture.Clock, TestUnitOfWork.None);
        Assert.True(await reader.IsUsableAsync(account, default));
        using var rotated = new Vessel.Infrastructure.Credentials.AesGcmCredentialVault(new() { ActiveKeyId = "v2", Keys = new() { ["v2"] = SecurityFixture.Key } });
        Assert.False(await new AccountCredentialReader(store, credentials, new CoreOwner(owner), rotated, SecurityFixture.Clock, TestUnitOfWork.None).IsUsableAsync(account, default));
        Assert.False(await new AccountCredentialReader(store, credentials, new CoreOwner(Guid.NewGuid()), vault, SecurityFixture.Clock, TestUnitOfWork.None).IsUsableAsync(account, default));
        credentials.Stored!.ExpiresAt = SecurityFixture.Now;
        Assert.False(await reader.IsUsableAsync(account, default));
        credentials.Stored.ExpiresAt = SecurityFixture.Now.AddDays(1);
        account.UpdateSettings(account.Name, null, false);
        Assert.False(await reader.IsUsableAsync(account, default));
    }
}
