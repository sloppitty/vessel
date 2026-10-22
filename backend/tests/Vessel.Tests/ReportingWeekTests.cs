using Vessel.Application.Common;
using Vessel.Application.Review;
using Vessel.Domain.Reporting;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 2: one reporting setting (IANA zone and week start) decides the week page's half-open boundaries, in local
/// days of that zone. Rolling Review periods are unchanged.
/// </summary>
public sealed class ReportingWeekTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("Europe/Madrid", true)]
    [InlineData("UTC", true)]
    [InlineData("America/New_York", true)]
    [InlineData(" Europe/Madrid", false)]
    [InlineData("Romance Standard Time", false)] // A Windows ID .NET also resolves on Linux.
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    public void Only_an_IANA_zone_as_given_is_accepted(string id, bool valid) => Assert.Equal(valid, ReportingSettings.TryZone(id, out _));

    [Fact]
    public void The_setting_keeps_a_valid_zone_and_week_start_and_refuses_others()
    {
        var settings = new ReportingSettings(Guid.NewGuid(), Utc("2026-10-22T10:00:00Z"));
        Assert.Equal(("UTC", "monday"), (settings.TimeZone, settings.WeekStart));
        settings.Set("Europe/Madrid", "sunday", Utc("2026-10-22T11:00:00Z"));
        Assert.Equal(("Europe/Madrid", "sunday", Utc("2026-10-22T11:00:00Z")), (settings.TimeZone, settings.WeekStart, settings.UpdatedAtUtc));
        Assert.Throws<ArgumentException>(() => settings.Set("Europe/Nowhere", "monday", Utc("2026-10-22T12:00:00Z")));
        Assert.Throws<ArgumentException>(() => settings.Set("UTC", "tuesday", Utc("2026-10-22T12:00:00Z")));
        Assert.Equal("Europe/Madrid", settings.TimeZone);
    }

    [Fact]
    public void A_week_holds_its_date_from_the_week_start_in_the_zone_and_is_half_open()
    {
        // Thursday 22 October 2026. Madrid leaves summer time on Sunday the 25th, so this Monday week lasts 169 hours.
        var monday = ReportingCalendar.Week(new(2026, 10, 22), Madrid, ReportingSettings.Monday);
        Assert.Equal((new DateOnly(2026, 10, 19), Utc("2026-10-18T22:00:00Z"), Utc("2026-10-25T23:00:00Z")), (monday.Start, monday.FromUtc, monday.ToUtc));
        Assert.True(monday.Contains(monday.FromUtc));
        Assert.True(monday.Contains(monday.ToUtc.AddTicks(-1)));
        Assert.False(monday.Contains(monday.ToUtc));
        Assert.Equal(monday, ReportingCalendar.Week(new(2026, 10, 25), Madrid, ReportingSettings.Monday)); // Its Sunday.
        var sunday = ReportingCalendar.Week(new(2026, 10, 22), Madrid, ReportingSettings.Sunday);
        Assert.Equal((new DateOnly(2026, 10, 18), Utc("2026-10-17T22:00:00Z"), Utc("2026-10-24T22:00:00Z")), (sunday.Start, sunday.FromUtc, sunday.ToUtc));
    }

    [Fact]
    public void A_day_whose_midnight_is_skipped_or_repeated_starts_at_its_first_instant()
    {
        // Havana changes its clocks at midnight: on 8 March 2026 00:00 does not exist (01:00 CDT), on 1 November it occurs twice.
        var havana = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        Assert.Equal(Utc("2026-03-08T05:00:00Z"), ReportingCalendar.StartOfDay(new(2026, 3, 8), havana));
        Assert.Equal(Utc("2026-11-01T04:00:00Z"), ReportingCalendar.StartOfDay(new(2026, 11, 1), havana));
        Assert.Equal(new DateOnly(2026, 10, 25), ReportingCalendar.Day(Utc("2026-10-25T22:59:59Z"), Madrid));
    }
}

/// <summary>Review on a reporting week (Phase 2, item 2): the same computations over the week's half-open range.</summary>
public sealed class ReviewWeekTests
{
    private readonly ReviewFixture f = new();
    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private void Madrid(string weekStart = ReportingSettings.Monday)
    {
        f.Reporting = new ReportingSettings(f.Owner, ReviewClock.Now);
        f.Reporting.Set("Europe/Madrid", weekStart, ReviewClock.Now);
    }

    [Fact]
    public async Task A_week_counts_closes_from_its_first_local_instant_until_the_next_week_and_charts_its_local_days()
    {
        // Now is Friday 9 October 2026, 14:00 in Madrid; this week runs from Monday 5 October 00:00 CEST (4 October 22:00 UTC).
        Madrid();
        f.Add(10, Utc("2026-10-04T21:59:59Z")); // Sunday 23:59:59 in Madrid: the week before.
        f.Add(20, Utc("2026-10-04T22:00:00Z")); // Monday 00:00 in Madrid: this week.
        f.Add(-5, Utc("2026-10-08T10:00:00Z"));
        var report = await f.Service.GetAsync(new(Week: "2026-10-07"), default);
        Assert.Equal((2, "15"), (report.Metrics.Count, report.Metrics.Net));
        Assert.Equal(new ReviewWeekDto("2026-10-05", Utc("2026-10-04T22:00:00Z"), Utc("2026-10-11T22:00:00Z"), "Europe/Madrid", "monday", "some"),
            report.Week);
        Assert.Equal(Utc("2026-10-04T22:00:00Z"), report.FromUtc);
        // Monday to today (Friday), by Madrid days.
        Assert.Equal(["2026-10-05", "2026-10-06", "2026-10-07", "2026-10-08", "2026-10-09"],
            report.Series.Select(p => p.AtUtc.UtcDateTime.ToString("yyyy-MM-dd")));
        Assert.Equal([1, 0, 0, 1, 0], report.Series.Select(p => p.Count));
        Assert.Equal(report.Week, (await f.Service.GetAsync(new(Week: "current"), default)).Week); // Today in Madrid.
        var before = await f.Service.GetAsync(new(Week: "2026-09-28"), default);
        Assert.Equal((1, "10"), (before.Metrics.Count, before.Metrics.Net));
        Assert.Equal(7, before.Series.Count);
        // The rolling report is unchanged and has no week.
        Assert.Null((await f.Service.GetAsync(new(), default)).Week);
    }

    [Fact]
    public async Task Sunday_weeks_and_the_default_UTC_Monday_follow_the_setting()
    {
        f.Add(20, Utc("2026-10-04T22:00:00Z"));
        var utc = await f.Service.GetAsync(new(Week: "2026-10-07"), default);
        Assert.Equal(("2026-10-05", Utc("2026-10-05T00:00:00Z"), "UTC", 0), (utc.Week!.Start, utc.Week.FromUtc, utc.Week.TimeZone, utc.Metrics.Count));
        Madrid(ReportingSettings.Sunday);
        var sunday = await f.Service.GetAsync(new(Week: "2026-10-07"), default);
        Assert.Equal(("2026-10-04", Utc("2026-10-03T22:00:00Z"), "sunday", 1), (sunday.Week!.Start, sunday.Week.FromUtc, sunday.Week.WeekStart, sunday.Metrics.Count));
    }

    [Fact]
    public async Task A_week_without_data_says_so_apart_from_a_week_without_trades()
    {
        Madrid();
        f.Add(10, Utc("2026-09-10T10:00:00Z")); // Created two days before: Vessel holds facts from 8 September.
        Assert.Equal("empty", (await f.Service.GetAsync(new(Week: "2026-09-21"), default)).Week!.Data);
        Assert.Equal("none", (await f.Service.GetAsync(new(Week: "2026-08-31"), default)).Week!.Data);
        Assert.Equal("some", (await f.Service.GetAsync(new(Week: "2026-09-10"), default)).Week!.Data);
    }

    [Fact]
    public async Task A_Play_still_active_is_data_for_the_weeks_after_its_creation()
    {
        // Sol round 1: a Draft, Planned or Open Play with no fills used to leave later weeks reading as without data.
        Madrid();
        f.FirstPlay = Utc("2026-09-01T10:00:00Z");
        Assert.Equal("empty", (await f.Service.GetAsync(new(Week: "2026-09-21"), default)).Week!.Data);
        Assert.Equal("none", (await f.Service.GetAsync(new(Week: "2026-08-24"), default)).Week!.Data);
    }

    [Theory]
    [InlineData("2026-10-12")] // Next week.
    [InlineData("2026-10-7")]
    [InlineData("2026-13-01")]
    [InlineData("1999-12-27")]
    [InlineData("week")]
    [InlineData("9999-12-31")] // Sol round 1: the calendar's end used to overflow into a 500.
    public async Task A_later_or_malformed_week_is_refused(string week)
    {
        Madrid();
        var error = await Assert.ThrowsAsync<ProblemException>(() => f.Service.GetAsync(new(Week: week), default));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Drill_downs_exclusions_and_the_export_take_the_same_week()
    {
        Madrid();
        var inside = f.Add(20, Utc("2026-10-05T09:00:00Z"));
        inside.Play.Annotate(inside.Play.Title, inside.Play.Drawings, "", ReviewClock.Now, new("deviated", "Late entry"));
        var outside = f.Add(10, Utc("2026-09-30T09:00:00Z"));
        outside.Play.Annotate(outside.Play.Title, outside.Play.Drawings, "", ReviewClock.Now, new("deviated", "Late entry"));
        var week = new ReviewQuery(Week: "2026-10-07");
        var tags = (await f.Service.GetAsync(week, default)).Process!.MistakeTags;
        Assert.Equal(1, Assert.Single(tags).Wins);
        var cases = await f.Service.ProcessAsync(new(week, MistakeTag: "late entry"), default);
        Assert.Equal([inside.Play.Id], cases.Items.Select(i => i.Id));
        Assert.Equal(0, (await f.Service.ExclusionsAsync(week, default)).Total);
        var export = await f.Service.ExportAsync(week, default);
        Assert.Equal("vessel-review-week-20261005.csv", export.FileName);
        Assert.Contains("Week of 2026-10-05 (Europe/Madrid, weeks start monday)", export.Content);
    }
}
