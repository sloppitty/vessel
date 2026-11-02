using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.MarketSources;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;

namespace Vessel.Tests;

public sealed class MarketSourceServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private sealed class Reader(Func<IReadOnlyList<VenueInstrument>> read) : IPerpetualVenueReader
    {
        public string VenueId => "fixture";
        public Task<IReadOnlyList<VenueInstrument>> ReadInstrumentsAsync(CancellationToken ct) => Task.FromResult(read());
        public Task<PerpetualVenueReadResult> ReadAsync(string sourceId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Owners : IJournalOwnerContext { public Guid OwnerId => Owner; }

    private sealed class Registry(VenueCapabilities capabilities, IPerpetualVenueReader reader) : IVenueRegistry
    {
        private readonly VenueDescriptor venue = new("fixture", "Fixture", "read-only", VenueSources.EvmAddress, capabilities);
        public IReadOnlyList<VenueDescriptor> Descriptors => [venue];
        public VenueDescriptor? Descriptor(string venueId) => venueId == venue.Id ? venue : null;
        public IPerpetualVenueReader? Reader(string venueId) => venueId == venue.Id ? reader : null;
        public IVenueOrderReader? Orders(string venueId) => null;
        public ICandleReader? Candles(string venueId) => null;
        public IMarketContextReader? MarketContext(string venueId) => null;
        public IMarketStream? Stream(string venueId) => null;
        public IVenueAccountDiscovery? Discovery(string venueId) => null;
        public IVenueCredentialVerifier? CredentialVerifier(string venueId) => null;
    }

    private static readonly VenueCapabilities Sources = VenueCapabilities.None with { Instruments = true, MarketSources = true };
    private static readonly VenueInstrument Btc = new("BTC", 5, 40, "USDC", Category: MarketCategories.Crypto);
    private static VenueInstrument Builder(string coin, string dex) => new($"{dex}:{coin}", 2, 20, "USDC", Source: dex, SourceName: dex.ToUpperInvariant());

    private static (MarketSourceService Service, MemoryMarketSourceStore Store) Create(VenueCapabilities? capabilities = null, Func<IReadOnlyList<VenueInstrument>>? read = null)
    {
        var store = new MemoryMarketSourceStore();
        var reader = new Reader(read ?? (() => [Btc, Builder("GOLD", "xyz"), Builder("OIL", "xyz"), Builder("TOTAL2", "para")]));
        return (new(store, new Registry(capabilities ?? Sources, reader), new Owners(), TimeProvider.System, TestUnitOfWork.None), store);
    }

    [Fact]
    public async Task Lists_sources_most_markets_first_and_every_new_source_starts_on()
    {
        var (service, _) = Create();
        var venue = Assert.Single(await service.ListAsync(default));
        Assert.True(venue.Available);
        Assert.Equal([new MarketSourceDto("xyz", "XYZ", 2, true), new MarketSourceDto("para", "PARA", 1, true)], venue.Sources);
    }

    [Fact]
    public async Task Turning_a_source_off_and_on_is_idempotent()
    {
        var (service, store) = Create();
        await service.SetAsync("fixture", "xyz", new(false), default);
        await service.SetAsync("fixture", "xyz", new(false), default);
        Assert.Single(store.Rows);
        Assert.False((await service.ListAsync(default))[0].Sources[0].Enabled);
        await service.SetAsync("fixture", "xyz", new(true), default);
        await service.SetAsync("fixture", "xyz", new(true), default);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_catalogue_keeps_hidden_markets_and_names_their_sources()
    {
        var (service, _) = Create();
        await service.SetAsync("fixture", "xyz", new(false), default);
        var catalogue = new AccountInstrumentsDto("fixture", "perpetuals", InstrumentCatalogScopes.Venue, [Btc, Builder("GOLD", "xyz")], "n");
        var applied = await service.ApplyAsync(catalogue, default);
        Assert.Equal(2, applied.Instruments.Count);
        Assert.Equal(["xyz"], applied.DisabledSources);
    }

    [Fact]
    public async Task Venues_without_sources_are_left_alone()
    {
        var (service, _) = Create(VenueCapabilities.None with { Instruments = true });
        Assert.Empty(await service.ListAsync(default));
        var catalogue = new AccountInstrumentsDto("fixture", "perpetuals", InstrumentCatalogScopes.Venue, [Btc], "n");
        Assert.Null((await service.ApplyAsync(catalogue, default)).DisabledSources);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProblemException>(() => service.SetAsync("fixture", "xyz", new(false), default))).StatusCode);
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("xy z")]
    [InlineData("seventeen-letters")]
    [InlineData("")]
    public async Task Rejects_a_source_that_could_not_be_a_market_source(string source)
    {
        var (service, _) = Create();
        Assert.Equal(400, (await Assert.ThrowsAsync<ProblemException>(() => service.SetAsync("fixture", source, new(false), default))).StatusCode);
    }

    [Fact]
    public async Task A_failed_catalogue_read_reports_the_venue_as_unavailable()
    {
        var (service, _) = Create(read: () => throw new VenueReadException("down"));
        var venue = Assert.Single(await service.ListAsync(default));
        Assert.False(venue.Available);
        Assert.Empty(venue.Sources);
    }
}
