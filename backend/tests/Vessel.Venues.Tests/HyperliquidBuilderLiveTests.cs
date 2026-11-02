using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

/// <summary>
/// Reads Hyperliquid's real public API, so fixtures cannot drift from its builder-market payloads. Skipped unless
/// Vessel_TEST_HYPERLIQUID_ADDRESS holds a public address with builder-market history.
/// </summary>
public sealed class HyperliquidBuilderLiveTests
{
    private sealed class LiveFact : FactAttribute
    {
        public LiveFact()
        {
            if (string.IsNullOrWhiteSpace(Address))
                Skip = "Set Vessel_TEST_HYPERLIQUID_ADDRESS to read Hyperliquid's public API.";
        }
    }

    private static string? Address => Environment.GetEnvironmentVariable("Vessel_TEST_HYPERLIQUID_ADDRESS");

    private static HyperliquidPerpetualReader Reader() =>
        new(new HttpClient { BaseAddress = new Uri("https://api.hyperliquid.xyz/"), Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System, null, true);

    [LiveFact]
    public async Task Catalogue_lists_gold_and_its_chart_and_context_work()
    {
        var reader = Reader();
        var gold = Assert.Single(await reader.ReadInstrumentsAsync(default), i => i.ContractId == "xyz:GOLD");
        Assert.Equal(("xyz", MarketCategories.Commodities, "USDC"), (gold.Source, gold.Category, gold.QuoteAsset));
        Assert.Contains("xau", gold.Keywords!);
        Assert.Contains(await reader.ReadMarketContextsAsync(default), c => c.ContractId == "xyz:GOLD");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.NotEmpty(await reader.ReadCandlesAsync("xyz:GOLD", "1h", now - 86_400_000, now, default));
    }

    [LiveFact]
    public async Task A_public_builder_account_reads_positions_fills_orders_and_funding()
    {
        var reader = Reader();
        var result = await reader.ReadAsync(Address!, default);
        Assert.True(result.PositionsComplete);
        Assert.Contains(result.Fills, f => f.ContractId.Contains(':'));
        Assert.Contains(result.Snapshot.Positions, p => p.ContractId.Contains(':'));
        Assert.NotEmpty((await reader.ReadOrdersAsync(Address!, default)).Orders);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", "Live");
        account.Configure(null, null, null, Address);
        var funding = await reader.ReadFundingAsync(account, now.AddDays(-7), now, default);
        Assert.Contains(funding.Payments, p => p.ContractId.Contains(':'));
    }
}
