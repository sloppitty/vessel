using Vessel.Application.Review;

namespace Vessel.Api;

public static class ReviewEndpoints
{
    public static void MapReview(this WebApplication app)
    {
        var api = app.MapGroup("/api/review").RequireAuthorization();
        api.MapGet("/exclusions", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            ReviewService service, CancellationToken ct) => Results.Ok(await service.ExclusionsAsync(
                new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0), ct)));
        api.MapGet("", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            string? positions, int? minPlays, ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0,
                positions ?? "exclude", minPlays ?? ReviewQuery.DefaultMinPlays), ct)));
    }
}
