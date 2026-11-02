using Vessel.Application.Reporting;

namespace Vessel.Api;

/// <summary>Owner settings that belong to neither trading mode: the reporting zone and week start (Phase 2, item 2).</summary>
public static class SettingsEndpoints
{
    internal const long ReportingBodyBytes = 1_024;

    public static void MapSettings(this WebApplication app)
    {
        var api = app.MapGroup("/api/settings").RequireAuthorization();
        api.MapGet("/reporting", async (ReportingService service, CancellationToken ct) => Results.Ok(await service.SettingsAsync(ct)));
        api.MapPut("/reporting", async (UpdateReportingSettingsRequest request, ReportingService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(request, ct))).WithBodyLimit(ReportingBodyBytes, "Reporting settings request is too large.");
    }
}
