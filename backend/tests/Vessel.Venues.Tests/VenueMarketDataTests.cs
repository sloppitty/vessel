using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

/// <summary>Market data and matching rules that come from a venue's descriptor rather than from Hyperliquid.</summary>
public sealed class VenueMarketDataTests
{
    private static readonly VenueDescriptor Tick = new("tick", "Tick venue", "read-only", VenueSources.EvmAddress,
        new VenueCapabilities(Sync: true, Instruments: true, Orders: true, Candles: true, MarketContext: true, Stream: false, StablecoinWallet: false),
        QuoteAsset: "USDC", Intervals: ["1m", "1h", "1d"], PriceRule: PriceRules.TickSize,
        CandleNotice: "Tick venue candles.", MarketContextNotice: "Tick venue context.");

    private sealed class TickCandles : ICandleReader
    {
        public string VenueId => "tick";
        public string? LastInterval;
        public Task<IReadOnlyList<VenueCandle>> ReadCandlesAsync(string contractId, string interval, long fromMs, long toMs, CancellationToken ct)
        {
            LastInterval = interval;
            // This venue reports no trade count.
            return Task.FromResult<IReadOnlyList<VenueCandle>>([new(toMs - 3_600_000, toMs - 1, "1", "2", "0.5", "1.5", "10", null)]);
        }
    }

    private sealed class TickContexts : IMarketContextReader
    {
        public string VenueId => "tick";
        public Task<IReadOnlyList<VenueMarketContext>> ReadMarketContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VenueMarketContext>>([new("DOGE", "0.0946", "0.0947", null, null, "1000", "50", "0.00001", null)]);
    }

    private static (MemoryWorkspaceStore Store, Account Account) Setup()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "tick", "Tick account");
        store.Accounts.Add(account);
        return (store, account);
    }

    private static VenueRegistry Registry(params object[] adapters) => new(
        [VenueDescriptor.Manual, HyperliquidPerpetualReader.Descriptor, Tick],
        [new FixtureReader { VenueId = "tick", Result = new FixtureReader().Result with { Instruments = [new("DOGE", 5, 10, "USDC", VenueContractId: "17")] } }], [],
        adapters.OfType<ICandleReader>(), adapters.OfType<IMarketContextReader>(), [], [], []);

    [Fact]
    public async Task Candles_use_the_venues_own_intervals_and_notice_and_allow_a_missing_trade_count()
    {
        var (store, account) = Setup(); var reader = new TickCandles();
        var service = new CandleService(store, Registry(reader), new CandleCache(TimeProvider.System), TimeProvider.System);

        var refused = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "DOGE", "4h", null, default));
        Assert.Equal((400, "Interval must be one of 1m, 1h, 1d."), (refused.StatusCode, refused.Message));
        Assert.Null(reader.LastInterval);

        var candles = await service.CandlesAsync(account.Id, "DOGE", "1h", null, default);
        Assert.Equal("1h", reader.LastInterval);
        Assert.Equal("Tick venue candles.", candles.Notice);
        Assert.Null(Assert.Single(candles.Candles).Trades);
    }

    [Fact]
    public async Task Hyperliquid_keeps_its_full_interval_list_and_notice()
    {
        var owner = Guid.NewGuid(); var store = new MemoryWorkspaceStore(owner);
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "HL");
        store.Accounts.Add(account);
        var service = new CandleService(store, TestVenues.With(), new CandleCache(TimeProvider.System), TimeProvider.System);
        var refused = await Assert.ThrowsAsync<ProblemException>(() => service.CandlesAsync(account.Id, "BTC", "2M", null, default));
        Assert.Equal("Interval must be one of 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 8h, 12h, 1d, 3d, 1w, 1M.", refused.Message);
        Assert.Equal("Hyperliquid exposes only the latest 5,000 candles per interval. Prices are trade candles, not fills.",
            HyperliquidPerpetualReader.Descriptor.CandleNotice);
    }

    [Fact]
    public async Task Market_context_allows_a_missing_previous_day_price_and_uses_the_venue_notice()
    {
        var (store, account) = Setup();
        var service = new MarketContextService(store, Registry(new TickContexts()), new MarketContextCache(TimeProvider.System));
        var context = await service.ContextAsync(account.Id, "DOGE", default);
        Assert.Null(context.PreviousDayPrice);
        Assert.Equal("Tick venue context.", context.Notice);
    }

    [Theory]
    // At 0.0946 the fifth significant figure is 0.000001, but the instrument trades in 0.00001.
    [InlineData(0.09461, 0.094605, null, false)]
    [InlineData(0.09461, 0.094605, 0.00001, true)]
    [InlineData(0.09463, 0.094605, 0.00001, false)]
    // A tick finer than the significant-figure step does not tighten the tolerance.
    [InlineData(84541, 84540.2, 0.1, true)]
    [InlineData(84542, 84540.2, 0.1, false)]
    public void Matching_tolerance_is_the_larger_of_the_significant_figure_step_and_the_tick(double actual, double planned, double? tick, bool near) =>
        Assert.Equal(near, ExecutionMatcher.Near((decimal)actual, (decimal)planned, tick is null ? null : (decimal)tick.Value));
}
