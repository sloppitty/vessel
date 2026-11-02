using System.Text.Json.Serialization;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Domain.Common;
using Vessel.Domain.Reporting;

namespace Vessel.Application.Reporting;

/// <summary>The reporting setting: its IANA zone, week start, and whether the owner has set it.</summary>
public sealed record ReportingSettingsDto(string TimeZone, string WeekStart, bool IsDefault);
public sealed record UpdateReportingSettingsRequest([property: JsonRequired] string TimeZone, [property: JsonRequired] string WeekStart);

public interface IReportingStore
{
    Task<ReportingSettings?> SettingsAsync(CancellationToken ct);
    /// <summary>The owner's row, created with <paramref name="initial"/> if missing, locked for this unit of work.</summary>
    Task<ReportingSettings> SettingsForUpdateAsync(ReportingSettings initial, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

/// <summary>
/// The owner's one reporting setting (Phase 2, item 2), stored on the server: the zone and week start of the week page and
/// of the soft loss limits. It works in both trading modes; nothing shifts until the owner sets it.
/// </summary>
public sealed class ReportingService(IReportingStore store, IJournalOwnerContext owner, TimeProvider time, IUnitOfWork unitOfWork)
{
    public async Task<ReportingSettingsDto> SettingsAsync(CancellationToken ct) => Dto(await store.SettingsAsync(ct));

    public async Task<ReportingSettingsDto> UpdateAsync(UpdateReportingSettingsRequest request, CancellationToken ct)
    {
        if (!ReportingSettings.TryZone(request.TimeZone, out _))
            throw new ProblemException(400, "Choose an IANA time zone such as Europe/Madrid or UTC.");
        if (!ReportingSettings.WeekStarts.Contains(request.WeekStart))
            throw new ProblemException(400, "Choose a week starting on monday or sunday.");
        var now = time.GetUtcNow();
        var saved = await unitOfWork.ExecuteAsync(async () =>
        {
            var settings = await store.SettingsForUpdateAsync(new ReportingSettings(owner.OwnerId, now), ct);
            settings.Set(request.TimeZone, request.WeekStart, now);
            await store.SaveAsync(ct);
            return settings;
        }, ct);
        return Dto(saved);
    }

    private static ReportingSettingsDto Dto(ReportingSettings? settings) => settings is null
        ? new(ReportingSettings.DefaultTimeZone, ReportingSettings.Monday, true)
        : new(settings.TimeZone, settings.WeekStart, false);
}
