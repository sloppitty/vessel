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
        // Phase 1.4b: one process cell, lane, mistake tag or skipped pre-flight check, with Review's filters.
        api.MapGet("/process", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            string? cell, string? lane, string? mistakeTag, string? skippedCheck, ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.ProcessAsync(new(new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0),
                cell, lane, mistakeTag, skippedCheck), ct)));
        api.MapGet("/mistake-tags", async (ReviewService service, CancellationToken ct) => Results.Ok(await service.MistakeTagsAsync(ct)));
        api.MapGet("", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0), ct)));
    }
}
