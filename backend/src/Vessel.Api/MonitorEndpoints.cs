using Vessel.Application.Monitoring;

namespace Vessel.Api;

public static class MonitorEndpoints
{
    public static void MapMonitor(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTradingModeBoundary();
        api.MapGet("/monitor", async (MonitorCoordinator monitor, CancellationToken ct) =>
            Results.Ok(await monitor.SummaryAsync(ct)));
        api.MapGet("/accounts/{id:guid}/monitor", async (Guid id, MonitorCoordinator monitor, CancellationToken ct) =>
            Results.Ok(await monitor.AccountAsync(id, ct)));
        api.MapPost("/accounts/{id:guid}/monitor/run", async (Guid id, HttpContext context, MonitorCoordinator monitor, CancellationToken ct) =>
        {
            try { return Results.Accepted(value: await monitor.RequestAsync(id, ct)); }
            catch (MonitorAdmissionException ex) { return Busy(context, ex); }
        });
        api.MapPost("/accounts/{id:guid}/discrepancies/{discrepancyId:guid}/dismiss",
            async (Guid id, Guid discrepancyId, DismissMonitorDiscrepancyRequest request, MonitorDiscrepancyService service, CancellationToken ct) =>
        {
            await service.DismissAsync(id, null, discrepancyId, request.Note, ct);
            return Results.NoContent();
        });
        api.MapPost("/plays/{id:guid}/discrepancies/{discrepancyId:guid}/dismiss",
            async (Guid id, Guid discrepancyId, DismissMonitorDiscrepancyRequest request, MonitorDiscrepancyService service, CancellationToken ct) =>
        {
            await service.DismissPlayAsync(id, discrepancyId, request.Note, ct);
            return Results.NoContent();
        });
    }

    public static IResult Busy(HttpContext context, MonitorAdmissionException error)
    {
        context.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: 429, title: "This account check is running or cooling down.");
    }
}
