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
