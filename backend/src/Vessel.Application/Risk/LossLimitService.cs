using Vessel.Application.Reporting;
using Vessel.Application.Review;
using Vessel.Domain.Reporting;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Risk;

/// <summary>
/// The soft daily and weekly loss limits (Phase 2, item 10b): today's and this week's loss in R, in the reporting zone and
/// week start, against the owner's limits. A breach is flagged in capital context, Suggestions and the pre-flight; nothing is
/// blocked. Real Plays only.
/// </summary>
public sealed class LossLimitService(IOpenRiskStore risk, IReportingStore reporting, ReviewService review, TimeProvider time)
{
    public const string Notice = "Soft limits, never enforced. A day's or week's loss is the sum of the estimated R (planned-stop model) " +
        "of real Plays closed in it, in the reporting zone and week from Settings. Plays without R are counted apart, never as zero.";

    public async Task<LossLimitsDto> GetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var limits = await risk.SettingsAsync(ct);
        var settings = await reporting.SettingsAsync(ct);
        var zone = settings?.Zone() ?? TimeZoneInfo.Utc;
        var weekStart = settings?.WeekStart ?? ReportingSettings.Monday;
        var today = ReportingCalendar.Day(now, zone);
        var day = new ReportingWeek(today, ReportingCalendar.StartOfDay(today, zone), ReportingCalendar.StartOfDay(today.AddDays(1), zone));
        var week = ReportingCalendar.Week(today, zone, weekStart);
        var sums = await review.ClosedRAsync([(day.FromUtc, day.ToUtc), (week.FromUtc, week.ToUtc)], ct);
        return new(Window(day, limits?.DailyLossLimitR, sums[0]), Window(week, limits?.WeeklyLossLimitR, sums[1]), zone.Id, weekStart, Notice);
    }

    /// <summary>The known loss reached the limit: the net R is at or below minus the limit. Unknown R never breaches nor clears it.</summary>
    private static LossWindowDto Window(ReportingWeek span, decimal? limit, ClosedRSum sum) =>
        new(span.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), span.FromUtc, span.ToUtc,
            ExactDecimal.Format(limit), sum.NetR.ToString(), sum.WithR, sum.WithoutR,
            limit is { } value && (sum.NetR + ExactAmount.From(value)).Units.Sign <= 0);
}
