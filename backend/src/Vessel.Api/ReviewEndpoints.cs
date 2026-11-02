using Vessel.Application.Review;

namespace Vessel.Api;

public static class ReviewEndpoints
{
    public static void MapReview(this WebApplication app)
    {
        var api = app.MapGroup("/api/review").RequireAuthorization();
        // Drill-downs list Plays only, but take and validate the report's positions filters like the report itself.
        api.MapGet("/exclusions", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            string? positions, int? minPlays, string? tradingMode, ReviewService service, CancellationToken ct) => Results.Ok(await service.ExclusionsAsync(
                new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0, positions ?? "exclude",
                    minPlays ?? ReviewQuery.DefaultMinPlays, tradingMode), ct)));
        // Phase 1.4b: one process cell, lane, mistake tag or skipped pre-flight check, with Review's filters.
        api.MapGet("/process", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            string? positions, int? minPlays, string? tradingMode, string? cell, string? lane, string? mistakeTag, string? skippedCheck,
            ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.ProcessAsync(new(new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0,
                positions ?? "exclude", minPlays ?? ReviewQuery.DefaultMinPlays, tradingMode), cell, lane, mistakeTag, skippedCheck), ct)));
        // Phase 2, item 5a: the report's own rows as CSV, from one snapshot. Owner data, so never cached (the API default).
        api.MapGet("/export", async (string? period, string? portfolio, Guid? accountId, string? instrument,
            string? positions, int? minPlays, string? tradingMode, ReviewService service, CancellationToken ct) =>
        {
            var export = await service.ExportAsync(new(period ?? "month", portfolio ?? "all", accountId, instrument, 0, positions ?? "exclude",
                minPlays ?? ReviewQuery.DefaultMinPlays, tradingMode), ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(export.Content), "text/csv; charset=utf-8", export.FileName);
        });
        api.MapGet("/mistake-tags", async (ReviewService service, CancellationToken ct) => Results.Ok(await service.MistakeTagsAsync(ct)));
        api.MapGet("", async (string? period, string? portfolio, Guid? accountId, string? instrument, int? offset,
            string? positions, int? minPlays, string? tradingMode, ReviewService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(new(period ?? "month", portfolio ?? "all", accountId, instrument, offset ?? 0,
                positions ?? "exclude", minPlays ?? ReviewQuery.DefaultMinPlays, tradingMode), ct)));
    }
}
