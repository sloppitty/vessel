using Vessel.Application.Plays.Execution;

namespace Vessel.Tests;

public sealed class EarlierOrderOptionsTests
{
    [Fact]
    public void The_lookback_is_bounded()
    {
        new PlayExecutionOptions().Validate();
        Assert.Equal(TimeSpan.FromHours(24), new PlayExecutionOptions().EarlierOrderLookback);
        Assert.Throws<InvalidOperationException>(() => new PlayExecutionOptions { EarlierOrderLookback = TimeSpan.FromDays(8) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayExecutionOptions { EarlierOrderLookback = TimeSpan.FromHours(-1) }.Validate());
    }
}
