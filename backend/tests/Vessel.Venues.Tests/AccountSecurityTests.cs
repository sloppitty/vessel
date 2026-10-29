using System.Text.Json;
using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Workspace;
using Vessel.Infrastructure.Credentials;

namespace Vessel.Tests;

public sealed class AccountSecurityTests
{

    [Theory]
    [InlineData("0")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    public async Task Account_index_is_an_exact_string(string source)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.None);
        var created = await service.CreateAccountAsync(new(null, "Lighter", SecurityFixture.Venue.Id, SourceId: source), default);
        Assert.Equal(source, created.SourceId); Assert.Null(created.Address); Assert.Equal(source, store.Accounts.Single().SourceId);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("00")] [InlineData("0123")] [InlineData("+1")] [InlineData("-1")]
    [InlineData("1.0")] [InlineData("1e3")] [InlineData(" 1")] [InlineData("1 ")] [InlineData("１２")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_account_indices_do_not_create_accounts(string? source)
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.None);
        var error = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Account", SecurityFixture.Venue.Id, SourceId: source), default));
        Assert.Equal(400, error.StatusCode); Assert.Empty(store.Accounts);
    }

    [Fact]
    public async Task Address_compatibility_and_conflicting_source_checks()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), SecurityFixture.Registry(), TestUnitOfWork.None);
        var address = "0x" + new string('A', 40);
        var account = await service.CreateAccountAsync(new(null, "EVM", "hyperliquid", Address: address), default);
        Assert.Equal(address.ToLowerInvariant(), account.Address); Assert.Equal(account.Address, account.SourceId);
        var error = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "EVM", "hyperliquid", Address: address, SourceId: "0x" + new string('b', 40)), default));
        Assert.Equal(400, error.StatusCode);
        await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Lighter", SecurityFixture.Venue.Id, Address: address, SourceId: "1"), default));
    }

    [Fact]
    public async Task Discovery_overlays_only_owner_records_including_disabled_and_preserves_exact_balance()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var added = SecurityFixture.Account(owner, "9223372036854775807"); added.UpdateSettings(added.Name, null, false); store.Accounts.Add(added);
        store.Accounts.Add(SecurityFixture.Account(Guid.NewGuid(), "2"));
        var discovery = new SecurityDiscovery([new("9223372036854775807", "Main", "main", "-12.000000000000000000001",
            "15.000000000000000001", "-0.01"), new("2", "Sub", "subaccount")]);
        var service = new AccountDiscoveryService(store, new CoreOwner(owner), SecurityFixture.Registry(discovery), MonitorServerState.Reads());
        var result = await service.DiscoverAsync(SecurityFixture.Venue.Id, SecurityFixture.Address, default);
        Assert.Equal(added.Id, result.Accounts[0].ExistingAccountId); Assert.False(result.Accounts[0].IsEnabled);
        Assert.Equal("-12.000000000000000000001", result.Accounts[0].AccountValueUsd);
        Assert.Equal("15.000000000000000001", result.Accounts[0].CollateralUsd);
        Assert.Equal("-0.01", result.Accounts[0].AvailableBalanceUsd);
        Assert.Null(result.Accounts[1].ExistingAccountId); Assert.Null(result.Accounts[1].IsEnabled);
        Assert.Equal(2, store.Accounts.Count);
    }

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

    [Fact]
    public async Task Pasted_token_whitespace_is_removed_before_name_discovery()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var discovery = new SecurityDiscovery([new("123", "Main", "main")]);
        var service = new AccountDiscoveryService(store, new CoreOwner(owner), SecurityFixture.Registry(discovery), MonitorServerState.Reads());
        await service.DiscoverAsync(SecurityFixture.Venue.Id,
            new CredentialDiscoveryRequest { Address = SecurityFixture.Address, Token = " \r\n" + SecurityFixture.Token + "\t " }, default);
        Assert.Equal(SecurityFixture.Token, discovery.ReceivedToken);
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
