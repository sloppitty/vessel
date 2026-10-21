using Vessel.Application.Sizing;

namespace Vessel.Api;

public static class SizingEndpoints
{
    public static void MapSizing(this WebApplication app)
    {
        var api = app.MapGroup("/api/sizing").RequireAuthorization();
        api.MapGet("", async (string? tradingMode, SizingService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct, tradingMode)));
        api.MapPut("/settings", async (string? tradingMode, UpdateSizingSettingsRequest request, SizingService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateSettingsAsync(request, ct, tradingMode)));
    }
}
