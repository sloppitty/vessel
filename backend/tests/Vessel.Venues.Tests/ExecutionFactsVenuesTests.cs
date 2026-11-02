using Vessel.Infrastructure.Venues.Hyperliquid;

namespace Vessel.Tests;

public sealed class ExecutionFactsVenuesTests
{
    [Theory]
    [InlineData("B", "buy")]
    [InlineData("A", "sell")]
    public void Hyperliquid_sides_become_buy_and_sell(string venue, string vessel) =>
        Assert.Equal(vessel, HyperliquidPerpetualReader.SideOf(venue));

    [Theory]
    [InlineData("Open Long", "open")]
    [InlineData("Open Short", "open")]
    [InlineData("Close Long", "close")]
    [InlineData("Close Short", "close")]
    [InlineData("Long > Short", "flip")]
    [InlineData("Short > Long", "flip")]
    [InlineData("Liquidated Isolated Long", "unknown")]
    [InlineData("Settlement", "unknown")]
    public void Hyperliquid_directions_map_to_position_effects_without_guessing(string direction, string effect) =>
        Assert.Equal(effect, HyperliquidPerpetualReader.EffectOf(direction));
}
