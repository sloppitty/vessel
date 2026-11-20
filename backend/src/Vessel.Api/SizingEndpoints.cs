using Vessel.Application.Sizing;
using Vessel.Domain.Common;

namespace Vessel.Api;

public static class SizingEndpoints
{
    public static void MapSizing(this WebApplication app)
    {
        var api = app.MapGroup("/api/sizing").RequireAuthorization();
        api.MapGet("", async (string? tradingMode, SizingService service, CancellationToken ct) => Results.Ok(await service.GetAsync(TradingModes.FromRequest(tradingMode), ct)));
        api.MapPut("/settings", async (string? tradingMode, UpdateSizingSettingsRequest request, SizingService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateSettingsAsync(request, TradingModes.FromRequest(tradingMode), ct)));
    }
}
