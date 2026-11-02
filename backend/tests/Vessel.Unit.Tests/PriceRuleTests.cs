using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 2 item 14b (F-11, probe C9): Hyperliquid prices take at most five significant figures, any integer, and at most
/// 6 − szDecimals decimals. A planned 0.051234 at szDecimals = 1 can only be placed as 0.05123, and now matches it.
/// </summary>
public sealed class PriceRuleTests
{

    [Fact]
    public void The_minimum_step_is_the_tick_or_the_last_allowed_decimal()
    {
        Assert.Equal(0.00001m, PriceRules.MinimumStep(new VenueInstrument("KPEPE", 1, 10, "USDC", MaxPriceDecimals: 5)));
        Assert.Equal(1m, PriceRules.MinimumStep(new VenueInstrument("BTC", 6, 40, "USDC", MaxPriceDecimals: 0)));
        Assert.Equal(0.5m, PriceRules.MinimumStep(new VenueInstrument("ETH", 3, 20, "USDC", PriceStep: 0.5m)));
        Assert.Null(PriceRules.MinimumStep(new VenueInstrument("SOL", 2, 20, "USDC")));
        // Before the fix the fifth significant figure alone (0.000001 here) refused the only price the venue accepts.
        Assert.False(ExecutionMatcher.Near(0.05123m, 0.051234m));
        Assert.True(ExecutionMatcher.Near(0.05123m, 0.051234m, PriceRules.DecimalStep(5)));
        Assert.False(ExecutionMatcher.Near(0.05125m, 0.051234m, PriceRules.DecimalStep(5)));
        // Coarse prices keep today's fifth-figure step: nothing becomes stricter.
        Assert.True(ExecutionMatcher.Near(84542m, 84541m, PriceRules.DecimalStep(1)));
    }
}
