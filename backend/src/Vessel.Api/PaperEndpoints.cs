using Vessel.Application.Paper;

namespace Vessel.Api;

public static class PaperEndpoints
{
    public static void MapPaperEndpoints(this WebApplication app)
    {
        var paper = app.MapGroup("/api/paper").RequireAuthorization().WithTradingModeBoundary();
        paper.MapGet("/settings", async (PaperTradingService service, CancellationToken ct) =>
            Results.Ok(await service.GetSettingsAsync(ct)));
        paper.MapPut("/settings", async (PaperSettingsDto request, PaperTradingService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateSettingsAsync(request, ct)));
        app.MapGet("/api/plays/{id:guid}/paper-state", async (Guid id, PaperTradingService service, CancellationToken ct) =>
            Results.Ok(await service.StateAsync(id, ct))).RequireAuthorization().WithTradingModeBoundary();
    }
}
