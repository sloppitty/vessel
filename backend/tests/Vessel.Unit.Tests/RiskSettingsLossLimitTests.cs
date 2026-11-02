using Vessel.Domain.Common;
using Vessel.Domain.Risk;

namespace Vessel.Tests;

public sealed class RiskSettingsLossLimitTests
{
    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_limit_is_above_zero_and_at_most_100_R_with_four_decimals_or_cleared()
    {
        var settings = new RiskSettings(Guid.NewGuid(), Utc("2026-10-22T10:00:00Z"));
        settings.SetLossLimits(2.5m, 100m, Utc("2026-10-22T11:00:00Z"));
        Assert.Equal((2.5m, 100m), (settings.DailyLossLimitR, settings.WeeklyLossLimitR));
        foreach (var invalid in new[] { 0m, -1m, 100.0001m, 1.00001m })
            Assert.Throws<DomainValidationException>(() => settings.SetLossLimits(invalid, null, Utc("2026-10-22T12:00:00Z")));
        settings.SetLossLimits(null, null, Utc("2026-10-22T12:00:00Z"));
        Assert.Equal((null, null), (settings.DailyLossLimitR, settings.WeeklyLossLimitR));
    }

}
