using Vessel.Application.Monitoring;

namespace Vessel.Tests;

public sealed class FundingOptionsTests
{

    [Theory]
    [InlineData(32, 7, 1, 8, 1)]
    [InlineData(30, 31, 1, 8, 1)]
    [InlineData(30, 7, 0, 8, 1)]
    [InlineData(30, 7, 1, 0, 1)]
    [InlineData(30, 7, 1, 8, -1)]
    [InlineData(30, 7, 1, 8, 31)]
    [InlineData(30, 7, 1, 8, 1, 0)]
    [InlineData(30, 7, 1, 8, 1, 61)]
    public void Options_are_bounded(int lookbackDays, int windowDays, int minimumMinutes, int maxReads, int recheckDays, int budgetSeconds = 20)
    {
        new FundingOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new FundingOptions
        {
            Lookback = TimeSpan.FromDays(lookbackDays), Window = TimeSpan.FromDays(windowDays),
            MinimumWindow = TimeSpan.FromMinutes(minimumMinutes), MaxReads = maxReads, Recheck = TimeSpan.FromDays(recheckDays),
            TimeBudget = TimeSpan.FromSeconds(budgetSeconds),
        }.Validate());
    }

}
