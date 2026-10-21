using Vessel.Application.Risk;

namespace Vessel.Api;

/// <summary>Open risk and the owner's soft risk limits (Phase 2, item 1). Real accounts only: paper requests get 409.</summary>
public static class RiskEndpoints
{
    /// <summary>A preview holds at most 20 entries of 10 stops, each field at most 64 characters.</summary>
    internal const long PreviewBodyBytes = 131_072;
    internal const long SettingsBodyBytes = 1_024;

    public static void MapRisk(this WebApplication app)
    {
        var api = app.MapGroup("/api/risk").RequireAuthorization();
        api.MapGet("/open", async (string? scope, OpenRiskService service, CancellationToken ct) => Results.Ok(await service.GetAsync(scope, ct)));
        api.MapPost("/open/preview", async (OpenRiskPreviewRequest request, OpenRiskService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(request, ct))).WithBodyLimit(PreviewBodyBytes, "Open-risk preview is too large.");
        api.MapGet("/settings", async (OpenRiskService service, CancellationToken ct) => Results.Ok(await service.SettingsAsync(ct)));
        api.MapPut("/settings", async (UpdateRiskSettingsRequest request, OpenRiskService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateSettingsAsync(request, ct))).WithBodyLimit(SettingsBodyBytes, "Risk settings request is too large.");
    }
}
