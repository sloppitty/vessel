using Vessel.Domain.Common;

namespace Vessel.Domain.Reporting;

/// <summary>
/// The owner's one reporting setting (Phase 2, item 2): an IANA time zone and a week start. It decides the week page's
/// boundaries and the soft loss limits' days and weeks. The defaults, UTC and Monday, shift nothing until the owner sets
/// them. It is not the display zone (1.6e), which stays in the browser; Activity and Review's rolling periods keep UTC.
/// </summary>
public sealed class ReportingSettings : IOwned
{
    public const string DefaultTimeZone = "UTC";
    public const string Monday = "monday", Sunday = "sunday";
    public const int MaxTimeZoneLength = 64;
    public static readonly IReadOnlyList<string> WeekStarts = [Monday, Sunday];

    public Guid OwnerId { get; private set; }
    public string TimeZone { get; private set; } = DefaultTimeZone;
    public string WeekStart { get; private set; } = Monday;
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    private ReportingSettings() { }

    public ReportingSettings(Guid ownerId, DateTimeOffset now)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner ID must be nonempty.");
        OwnerId = ownerId;
        UpdatedAtUtc = now;
    }

    /// <summary>An IANA zone this server knows, such as Europe/Madrid, and monday or sunday.</summary>
    public void Set(string timeZone, string weekStart, DateTimeOffset now)
    {
        if (!TryZone(timeZone, out _)) throw new ArgumentException("Choose an IANA time zone such as Europe/Madrid or UTC.");
        if (!WeekStarts.Contains(weekStart)) throw new ArgumentException("Choose a week starting on Monday or Sunday.");
        TimeZone = timeZone;
        WeekStart = weekStart;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// The zone for an IANA ID as given. Windows IDs, which .NET also resolves on Linux, and anything with surrounding
    /// spaces are refused, so the stored ID is the one the owner chose.
    /// </summary>
    public static bool TryZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(id) || id.Length > MaxTimeZoneLength || id != id.Trim()) return false;
        try
        {
            var found = TimeZoneInfo.FindSystemTimeZoneById(id);
            if (!found.HasIanaId) return false;
            zone = found;
            return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The settings' zone; a stored ID this server no longer knows reads as UTC rather than failing the page.</summary>
    public TimeZoneInfo Zone() => TryZone(TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
}

/// <summary>One reporting week: its first local day and its half-open UTC range [<paramref name="FromUtc"/>, <paramref name="ToUtc"/>).</summary>
public sealed record ReportingWeek(DateOnly Start, DateTimeOffset FromUtc, DateTimeOffset ToUtc)
{
    public bool Contains(DateTimeOffset at) => at >= FromUtc && at < ToUtc;
}

/// <summary>Calendar arithmetic in the reporting zone. Days start at local midnight, or at the first instant after a gap.</summary>
public static class ReportingCalendar
{
    /// <summary>The week holding <paramref name="day"/>, a local date, under the zone and week start.</summary>
    public static ReportingWeek Week(DateOnly day, TimeZoneInfo zone, string weekStart)
    {
        var first = weekStart == ReportingSettings.Sunday ? DayOfWeek.Sunday : DayOfWeek.Monday;
        var start = day.AddDays(-(((int)day.DayOfWeek - (int)first + 7) % 7));
        return new(start, StartOfDay(start, zone), StartOfDay(start.AddDays(7), zone));
    }

    /// <summary>The local date of an instant in the zone.</summary>
    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>
    /// The first instant of a local day. Midnight skipped by a clock change starts the day at the first valid local time
    /// after it; a midnight that occurs twice starts it at the first occurrence.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Gaps last at most a few hours; stepping by minutes finds their end exactly.
        for (var i = 0; zone.IsInvalidTime(local) && i < 24 * 60; i++) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
