using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Reporting;
using Vessel.Application.Risk;
using Vessel.Domain.Common;
using Vessel.Domain.Plays;
using Vessel.Domain.Reporting;
using Vessel.Domain.Risk;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 10b: soft daily and weekly loss limits in R, by the reporting setting's days and weeks. The loss is the sum
/// of estimated R of real Plays closed in the span; Plays without R are counted apart and never as zero. Nothing is blocked.
/// </summary>
public sealed class LossLimitTests
{
    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Limits(RiskSettings? settings) : IOpenRiskStore
    {
        public Task<RiskSettings?> SettingsAsync(CancellationToken ct) => Task.FromResult(settings);
        public Task<List<OpenRiskFacts>> ActivePlaysAsync(IReadOnlyCollection<Guid>? playIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Play?> PlayAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<RiskSettings> SettingsForUpdateAsync(RiskSettings initial, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Reporting(ReportingSettings? settings) : IReportingStore
    {
        public Task<ReportingSettings?> SettingsAsync(CancellationToken ct) => Task.FromResult(settings);
        public Task<ReportingSettings> SettingsForUpdateAsync(ReportingSettings initial, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct) => throw new NotSupportedException();
    }

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

    [Fact]
    public void A_cooldown_note_belongs_to_a_reached_loss_limit_only_and_an_empty_one_is_none()
    {
        var checks = PlayPreflight.Normalize([new("loss-limit", "skipped", "  Walk away until Monday.  "), new("thesis", "passed", " ")]);
        Assert.Equal(["thesis", "loss-limit"], checks!.Select(c => c.Code));
        Assert.Equal("Walk away until Monday.", checks![1].Note);
        Assert.Null(checks[0].Note);
        Assert.DoesNotContain("note", PlayPreflight.Serialize([new("thesis", "passed")]));
        foreach (var invalid in new PreflightCheckDto[]
                 {
                     new("thesis", "passed", "A note"), new("loss-limit", "passed", "A note"), new("loss-limit", "skipped", new string('x', 501)),
                     new("loss-limit", "skipped", "Bell\a"),
                 })
            Assert.Equal(400, Assert.Throws<ProblemException>(() => PlayPreflight.Normalize([invalid])).StatusCode);
    }

    [Fact]
    public async Task Today_and_this_week_sum_the_estimated_R_of_closed_Plays_in_the_reporting_zone_and_count_those_without()
    {
        // Now is Friday 9 October 2026, 14:00 in Madrid. Each Play risks 5 to its stop, so a loss of 5 is −1 R.
        var f = new ReviewFixture();
        f.Add(-5, Utc("2026-10-09T10:00:00Z")); // Today: −1 R.
        f.Add(-3, Utc("2026-10-09T11:00:00Z"), risk: false); // Today, without R.
        f.Add(-10, Utc("2026-10-06T10:00:00Z")); // This week: −2 R.
        f.Add(-5, Utc("2026-10-04T21:00:00Z")); // Sunday 23:00 in Madrid: the week before.
        var settings = new RiskSettings(f.Owner, ReviewClock.Now);
        settings.SetLossLimits(1m, 5m, ReviewClock.Now);
        var reporting = new ReportingSettings(f.Owner, ReviewClock.Now);
        reporting.Set("Europe/Madrid", ReportingSettings.Monday, ReviewClock.Now);
        var loss = await new LossLimitService(new Limits(settings), new Reporting(reporting), f.Service, new ReviewClock()).GetAsync(default);
        Assert.Equal(new LossWindowDto("2026-10-09", Utc("2026-10-08T22:00:00Z"), Utc("2026-10-09T22:00:00Z"), "1", "-1", 1, 1, true), loss.Day);
        Assert.Equal(new LossWindowDto("2026-10-05", Utc("2026-10-04T22:00:00Z"), Utc("2026-10-11T22:00:00Z"), "5", "-3", 2, 1, false), loss.Week);
        Assert.Equal(("Europe/Madrid", "monday"), (loss.TimeZone, loss.WeekStart));
        // Without limits nothing is breached, and the losses still read.
        var unset = await new LossLimitService(new Limits(null), new Reporting(null), f.Service, new ReviewClock()).GetAsync(default);
        Assert.Equal((null, "-1", false), (unset.Day.LimitR, unset.Day.NetR, unset.Day.Breached));
        Assert.Equal(("2026-10-09", "UTC"), (unset.Day.Start, unset.TimeZone));
    }
}
