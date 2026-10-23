using Vessel.Application.Common;
using Vessel.Application.Credentials;
using Vessel.Application.Metadata;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Infrastructure.Venues.Hyperliquid;
using Vessel.Application.Plays.Execution;
using Vessel.Persistence;

namespace Vessel.Tests;

public sealed class VenueRegistryTests
{
    /// <summary>A second venue: its own name, source rule, capabilities and reader, never mentioned by a use case.</summary>
    private sealed class OtherReader : IPerpetualVenueReader
    {
        public string VenueId => "other";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueInstrument>>([new("ABC", 3, 10, "USDC")]);
        public Task<PerpetualVenueReadResult> ReadAsync(string address, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly VenueDescriptor Other = new("other", "Other", "read-only", VenueSources.EvmAddress,
        new VenueCapabilities(Sync: true, Instruments: true, Orders: false, Candles: false, MarketContext: false, Stream: false, StablecoinWallet: false), "USDC");

    private static VenueRegistry Registry(params object[] adapters) => new(
        [VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, Other],
        adapters.OfType<IPerpetualVenueReader>(), [], [], [], [], [], []);

    [Fact]
    public void Adapters_are_found_by_venue_id_and_the_last_registration_wins()
    {
        var first = new FixtureReader(); var second = new FixtureReader(); var other = new OtherReader();
        var registry = Registry(first, other, second);
        Assert.Same(second, registry.Reader("hyperliquid"));
        Assert.Same(other, registry.Reader("other"));
        Assert.Null(registry.Reader("lighter"));
        Assert.Null(registry.Candles("hyperliquid"));
        Assert.Equal("Other", registry.Descriptor("other")!.Name);
        Assert.Null(registry.Descriptor("lighter"));
        Assert.Equal(["manual", "hyperliquid", "other"], registry.Descriptors.Select(d => d.Id));
    }

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

    [Fact]
    public async Task A_second_venue_works_through_create_instruments_and_sync_rules_without_use_case_changes()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var service = new WorkspaceService(store, new CoreOwner(owner), Registry(new FixtureReader(), new OtherReader()), TestUnitOfWork.None);
        const string address = "0x0123456789abcdef0123456789abcdef01234567";

        var bad = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Bad", "other", "0x12"), default));
        Assert.Equal((400, "A matching 42-character public hexadecimal address is required."), (bad.StatusCode, bad.Message));
        var unknown = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Nope", "lighter", address), default));
        Assert.Equal((400, "Choose manual, hyperliquid or other."), (unknown.StatusCode, unknown.Message));

        var account = await service.CreateAccountAsync(new(null, "Second", "other", address), default);
        Assert.Equal("other", account.VenueId);
        var catalogue = await service.InstrumentsAsync(account.Id, default);
        Assert.Equal("other", catalogue.VenueId);
        Assert.Equal("ABC", Assert.Single(catalogue.Instruments).ContractId);
        var duplicate = await Assert.ThrowsAsync<ProblemException>(() => service.CreateAccountAsync(new(null, "Again", "other", address.ToUpperInvariant().Replace("0X", "0x")), default));
        Assert.Equal(409, duplicate.StatusCode);
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

    [Fact]
    public void Order_tracking_follows_the_venue_capability()
    {
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "other", "A");
        account.Configure(null, "0x0123456789abcdef0123456789abcdef01234567", null);
        var play = Plan(account);
        Assert.Equal("Orders are only tracked at venues that provide them.", PlayExecutionService.UntrackedReason(play, account, Other));
        var tracked = Other with { Capabilities = Other.Capabilities with { Orders = true } };
        Assert.Null(PlayExecutionService.UntrackedReason(play, account, tracked));
        Assert.NotNull(PlayExecutionService.UntrackedReason(play, account, null));
    }

    [Fact]
    public void System_metadata_lists_registered_venues_then_planned_ones_with_manual_last()
    {
        var metadata = SystemMetadata.ForOwner(new JournalOwner(Guid.NewGuid(), "Owner"), Registry());
        // Lighter has adapters now; only Quantfury remains a planned entry.
        Assert.Equal(["hyperliquid", "other", "quantfury", "manual"], metadata.Venues.Select(v => v.Id));
        Assert.True(metadata.Venues.Single(v => v.Id == "other").Capabilities.Sync);
        Assert.Equal(VenueCapabilities.None, metadata.Venues.Single(v => v.Id == "quantfury").Capabilities);
        Assert.Equal(VenueLogos.Hyperliquid, metadata.Venues.Single(v => v.Id == "hyperliquid").Logo);
        Assert.Equal(VenueLogos.Manual, metadata.Venues.Single(v => v.Id == "manual").Logo);
        Assert.Null(metadata.Venues.Single(v => v.Id == "other").Logo);

        // A venue that gains an adapter replaces its planned entry instead of duplicating it.
        var quantfury = new VenueDescriptor("quantfury", "Quantfury", "read-only", VenueSources.None, VenueCapabilities.None);
        var registered = SystemMetadata.ForOwner(new JournalOwner(Guid.NewGuid(), "Owner"),
            new VenueRegistry([VenueDescriptor.Manual, quantfury], [], [], [], [], [], [], []));
        Assert.Single(registered.Venues, v => v.Id == "quantfury");
        Assert.Equal("read-only", registered.Venues.Single(v => v.Id == "quantfury").Status);
    }

    [Fact]
    public void Registration_accepts_only_the_fixed_logo_keys()
    {
        Assert.Equal(["hyperliquid", "lighter", "lighter-rh", "risex", "pacifica", "manual"], VenueLogos.All);
        foreach (var logo in VenueLogos.All)
            Assert.Equal(logo, new VenueRegistry([Other with { Logo = logo }], [], [], [], [], [], [], []).Descriptor("other")!.Logo);
        Assert.Null(new VenueRegistry([Other], [], [], [], [], [], [], []).Descriptor("other")!.Logo);
        foreach (var logo in new[] { "", "Lighter", "https://example.com/logo.svg", "../manual", "folder" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => new VenueRegistry([Other with { Logo = logo }], [], [], [], [], [], [], []));
            if (logo.Length > 0) Assert.DoesNotContain(logo, error.Message);
        }
    }

    private static Play Plan(Account account)
    {
        var play = TestPlays.Create(account, new PerpetualInstrument(account.VenueId, "ABC"));
        play.MarkPlanned(DateTimeOffset.UnixEpoch);
        return play;
    }
}
