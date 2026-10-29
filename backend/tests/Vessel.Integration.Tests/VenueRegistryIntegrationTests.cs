using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class VenueRegistryIntegrationTests : VenueRegistryTestsSupport
{

    [Fact]
    public async Task Discovery_and_credential_verification_follow_the_same_last_registration_rule()
    {
        var firstDiscovery = new SecurityDiscovery([new("1", "First", "main")]);
        var lastDiscovery = new SecurityDiscovery([new("2", "Last", "main")]);
        var firstVerifier = new SecurityVerifier(); var lastVerifier = new SecurityVerifier();
        var registry = SecurityFixture.Registry(firstDiscovery, firstVerifier, lastDiscovery, lastVerifier);
        Assert.Same(lastDiscovery, registry.Discovery(SecurityFixture.Venue.Id));
        Assert.Same(lastVerifier, registry.CredentialVerifier(SecurityFixture.Venue.Id));
        Assert.Null(registry.Discovery("hyperliquid")); Assert.Null(registry.CredentialVerifier("hyperliquid"));

        // A duplicate registration no longer fails the use case; it reads through the registry's choice.
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var found = await new AccountDiscoveryService(store, new CoreOwner(owner), registry, MonitorServerState.Reads())
            .DiscoverAsync(SecurityFixture.Venue.Id, SecurityFixture.Address, default);
        Assert.Equal("Last", Assert.Single(found.Accounts).Name);
        var account = SecurityFixture.Account(owner); store.Accounts.Add(account);
        using var vault = SecurityFixture.Vault();
        await new AccountCredentialService(store, new SecurityCredentialStore(), new CoreOwner(owner), registry, vault, SecurityFixture.Clock, TestUnitOfWork.None, MonitorServerState.Reads(time: SecurityFixture.Clock))
            .PutAsync(account.Id, new() { Token = SecurityFixture.Token }, default);
        Assert.Null(firstVerifier.ReceivedToken); Assert.Equal(SecurityFixture.Token, lastVerifier.ReceivedToken);
    }

    [PostgresFact]
    public async Task Refresh_needs_the_sync_capability_and_an_adapter()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        var noSync = Other with { Capabilities = Other.Capabilities with { Sync = false } };
        var registry = new VenueRegistry([VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, noSync], [new FixtureReader(), new OtherReader()], [], [], [], [], [], []);
        var service = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), registry, TestUnitOfWork.For(db));
        var manual = await service.CreateAccountAsync(new(null, "Manual", "manual"), default);
        var refused = await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(db, owner, registry, manual.Id));
        Assert.Equal((400, "This account cannot be refreshed."), (refused.StatusCode, refused.Message));
        // The capability decides, not the venue name: a venue that does not advertise sync is refused the same way.
        var unsynced = await service.CreateAccountAsync(new(null, "No sync", "other", "0x0123456789abcdef0123456789abcdef01234567"), default);
        refused = await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(db, owner, registry, unsynced.Id));
        Assert.Equal((400, "This account cannot be refreshed."), (refused.StatusCode, refused.Message));

        // Sync is allowed for the venue but its adapter is not registered.
        var missing = new WorkspaceService(new WorkspaceStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), Registry(), TestUnitOfWork.For(db));
        var account = await missing.CreateAccountAsync(new(null, "Second", "other", "0x89abcdef0123456789abcdef0123456789abcdef"), default);
        var unavailable = await Assert.ThrowsAsync<ProblemException>(() => MonitorTestPaths.RefreshAsync(db, owner, Registry(), account.Id));
        Assert.Equal((503, "Venue reader is unavailable."), (unavailable.StatusCode, unavailable.Message));
    }
}
