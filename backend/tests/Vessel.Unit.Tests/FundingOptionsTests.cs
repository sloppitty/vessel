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
    // Phase 2, item 9c: the schedule is slow, at most a week, and never longer than the lookback it continues.
    [InlineData(30, 7, 1, 8, 1, 20, 0)]
    [InlineData(30, 7, 1, 8, 1, 20, 169)]
    [InlineData(1, 1, 1, 8, 1, 20, 25)]
    public void Options_are_bounded(int lookbackDays, int windowDays, int minimumMinutes, int maxReads, int recheckDays, int budgetSeconds = 20,
        int intervalHours = 6)
    {
        new FundingOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new FundingOptions
        {
            Lookback = TimeSpan.FromDays(lookbackDays), Window = TimeSpan.FromDays(windowDays),
            MinimumWindow = TimeSpan.FromMinutes(minimumMinutes), MaxReads = maxReads, Recheck = TimeSpan.FromDays(recheckDays),
            TimeBudget = TimeSpan.FromSeconds(budgetSeconds), Interval = TimeSpan.FromHours(intervalHours),
        }.Validate());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(168)]
    public void The_funding_schedule_interval_defaults_to_six_hours_and_allows_an_hour_to_a_week(int hours)
    {
        Assert.Equal(TimeSpan.FromHours(6), new FundingOptions().Interval);
        new FundingOptions { Interval = TimeSpan.FromHours(hours) }.Validate();
    }
}
