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
    public async Task Pasted_token_whitespace_is_removed_before_name_discovery()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var discovery = new SecurityDiscovery([new("123", "Main", "main")]);
        var service = new AccountDiscoveryService(store, new CoreOwner(owner), SecurityFixture.Registry(discovery), MonitorServerState.Reads());
        await service.DiscoverAsync(SecurityFixture.Venue.Id,
            new CredentialDiscoveryRequest { Address = SecurityFixture.Address, Token = " \r\n" + SecurityFixture.Token + "\t " }, default);
        Assert.Equal(SecurityFixture.Token, discovery.ReceivedToken);
    }
}
