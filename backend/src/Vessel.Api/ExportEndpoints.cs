using Vessel.Application.Export;

namespace Vessel.Api;

/// <summary>Owner exports (Phase 2, item 5). Owner data is never cached: the API's default `no-store` applies.</summary>
public static class ExportEndpoints
{
    public static void MapExports(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        // 5b: one real account's retained executions or funding, streamed in time order.
        api.MapGet("/accounts/{id:guid}/facts", async (Guid id, string? kind, string? from, string? to, FactsExportService service,
            CancellationToken ct) =>
        {
            var export = await service.PrepareAsync(id, kind, from, to, ct);
            return Results.Stream(stream => export.Write(stream, ct), export.ContentType, export.FileName);
        });
        // 5c: one Play as a zip, in its own trading mode; a Play of the other mode or owner is not found.
        api.MapGet("/plays/{id:guid}/bundle", async (Guid id, PlayBundleService service, CancellationToken ct) =>
        {
            var export = await service.PrepareAsync(id, ct);
            return Results.Stream(stream => export.Write(stream, ct), export.ContentType, export.FileName);
        }).WithTradingModeBoundary();
    }
}
