using Vessel.Application.Common;
using Vessel.Application.Metadata;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Application.Plays.Execution;

namespace Vessel.Tests;

public sealed class VenueRegistryTests : VenueRegistryTestsSupport
{

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
        Assert.Equal(["hyperliquid", "lighter", "lighter-rh", "risex", "manual"], VenueLogos.All);
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
