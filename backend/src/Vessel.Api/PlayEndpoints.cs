using Vessel.Application.Monitoring;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using static Vessel.Api.EndpointTransport;

namespace Vessel.Api;

public static class PlayEndpoints
{
    /// <summary>A rule is two parts of at most 500 characters and a source Play.</summary>
    internal const long RuleBodyBytes = 8_192;

    public static void MapPlays(this WebApplication app)
    {
        var api = app.MapGroup("/api/plays").RequireAuthorization().WithTradingModeBoundary();
        api.MapGet("", async (string? view, string? q, string? scope, string? page, string? pageSize, string? tradingMode, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(view, q, scope, page, pageSize, ct, tradingMode)));
        api.MapGet("/counts", async (string? q, string? scope, string? tradingMode, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.CountsAsync(q, scope, ct, tradingMode)));
        // Phase 2, item 13: journal-wide search over the search projection, in the requested mode, paged by offset.
        api.MapGet("/search", async (string? q, int? offset, string? tradingMode, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(q, offset, ct, tradingMode)));
        api.MapPost("", async (CreatePlayRequest request, string? tradingMode, PlayService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct, tradingMode);
            return Results.Created($"/api/plays/{result.Summary.Id}", result);
        });
        api.MapPost("/retrospective", async (CreateRetrospectiveRequest request, DiaryService service, CancellationToken ct) =>
            Results.Ok(await service.CreateRetrospectiveAsync(request, ct)))
            .WithBodyLimit(RetrospectiveBodyBytes, DiaryTooLarge);
        api.MapGet("/{id:guid}/manual-executions", async (Guid id, DiaryService service, CancellationToken ct) =>
            Results.Ok(await service.ManualAsync(id, ct)));
        api.MapPost("/{id:guid}/manual-executions", async (Guid id, ManualExecutionRequest request, DiaryService service, CancellationToken ct) =>
            Results.Ok(await service.RecordAsync(id, null, request, ct)))
            .WithBodyLimit(ManualExecutionBodyBytes, DiaryTooLarge);
        api.MapPut("/{id:guid}/manual-executions/{executionId:guid}", async (Guid id, Guid executionId, ManualExecutionRequest request,
            DiaryService service, CancellationToken ct) => Results.Ok(await service.RecordAsync(id, executionId, request, ct)))
            .WithBodyLimit(ManualExecutionBodyBytes, DiaryTooLarge);
        // Phase 1.3b: several corrections with one reason, all or nothing.
        api.MapPost("/{id:guid}/manual-executions/corrections", async (Guid id, ManualCorrectionBatchRequest request,
            DiaryService service, CancellationToken ct) => Results.Ok(await service.CorrectBatchAsync(id, request, ct)))
            .WithBodyLimit(ManualCorrectionBatchBodyBytes, DiaryTooLarge);
        // Previewing a retrospective Play starts from an Activity operation, so it lives under /api/activity.
        var activity = app.MapGroup("/api/activity").RequireAuthorization();
        activity.MapGet("/retrospective", async (Guid accountId, string operationId, DiaryService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(accountId, operationId, ct)));
        api.MapGet("/{id:guid}", async (Guid id, PlayService service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)));
        api.MapPut("/{id:guid}", async (Guid id, UpdatePlayRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, request, ct)));
        api.MapPost("/{id:guid}/status", async (Guid id, ChangePlayStatusRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.ChangeStatusAsync(id, request, ct)));
        api.MapGet("/next-actions", async (string? tradingMode, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.NextActionsAsync(ct, tradingMode)));
        // Phase 2, item 3: the one active if-then rule in the requested mode.
        api.MapGet("/rule", async (string? tradingMode, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.RuleAsync(tradingMode, ct)));
        api.MapPut("/rule", async (string? tradingMode, SetPlayRuleRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.SetRuleAsync(tradingMode, request, ct))).WithBodyLimit(RuleBodyBytes, DiaryTooLarge);
        api.MapDelete("/rule", async (string? tradingMode, int expectedVersion, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.ArchiveRuleAsync(tradingMode, expectedVersion, ct)));
        // Review completion and the in-trade log (phase 1, 1.2).
        api.MapPost("/{id:guid}/review/complete", async (Guid id, ReviewCompletionRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.CompleteReviewAsync(id, request, ct)));
        api.MapPost("/{id:guid}/review/reopen", async (Guid id, ReviewCompletionRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.ReopenReviewAsync(id, request, ct)));
        api.MapPost("/{id:guid}/review/not-needed", async (Guid id, ReviewCompletionRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.NoReviewNeededAsync(id, request, ct)));
        api.MapGet("/{id:guid}/notes", async (Guid id, PlayService service, CancellationToken ct) => Results.Ok(await service.NotesAsync(id, ct)));
        api.MapPost("/{id:guid}/notes", async (Guid id, AddPlayNoteRequest request, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.AddNoteAsync(id, request, ct)));
        api.MapGet("/{id:guid}/history", async (Guid id, PlayService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryAsync(id, ct)));
        api.MapGet("/{id:guid}/execution", async (Guid id, PlayExecutionService service, MonitorExecutionView monitor, CancellationToken ct) =>
            Results.Ok(await monitor.ComposeAsync(await service.GetAsync(id, ct), ct)));
        // Documented API alias (play-monitor-api-contract.md): requests the Play account's monitor run.
        // The browser uses POST /api/accounts/{id}/monitor/run; backend tests still cover this route.
        api.MapPost("/{id:guid}/execution/check", async (Guid id, HttpContext context, MonitorExecutionView monitor, CancellationToken ct) =>
        {
            try { return Results.Accepted(value: await monitor.RequestAsync(id, ct)); }
            catch (MonitorAdmissionException ex) { return MonitorEndpoints.Busy(context, ex); }
        });
        api.MapPost("/{id:guid}/execution/links", async (Guid id, LinkOrderRequest request, PlayExecutionService service,
            MonitorExecutionView monitor, CancellationToken ct) =>
            Results.Ok(await monitor.ComposeAsync(await service.LinkAsync(id, request, ct), ct)));
        // Phase 1.6d: "Not this play" for a whole order, in one step.
        api.MapPost("/{id:guid}/execution/rejections", async (Guid id, RejectOrderRequest request, PlayExecutionService service,
            MonitorExecutionView monitor, CancellationToken ct) =>
            Results.Ok(await monitor.ComposeAsync(await service.RejectOrderAsync(id, request, ct), ct)));
        api.MapDelete("/{id:guid}/execution/links/{linkId:guid}", async (Guid id, Guid linkId, PlayExecutionService service,
            MonitorExecutionView monitor, CancellationToken ct) =>
            Results.Ok(await monitor.ComposeAsync(await service.UnlinkAsync(id, linkId, ct), ct)));
        // Phase 2, item 11: the owner splits one fill between Plays (a shared exit or a flip), replaces or removes the split.
        api.MapPost("/{id:guid}/execution/allocations", async (Guid id, AllocateFillRequest request, PlayExecutionService service,
            MonitorExecutionView monitor, CancellationToken ct) =>
            Results.Ok(await monitor.ComposeAsync(await service.AllocateAsync(id, request, ct), ct)));
        // Answers level.drift: one plan revision moves the linked level to the observed order's price. Returns the Play.
        api.MapPost("/{id:guid}/execution/links/{linkId:guid}/rebind", async (Guid id, Guid linkId, RebindLevelRequest request,
            PlayExecutionService service, CancellationToken ct) => Results.Ok(await service.RebindAsync(id, linkId, request, ct)));
        // Phase 1.3a: the owner declares a stuck Open Play unresolved, or returns it to Open. Returns the Play.
        api.MapPost("/{id:guid}/unresolved", async (Guid id, DeclareUnresolvedRequest request, PlayExecutionService service,
            CancellationToken ct) => Results.Ok(await service.DeclareUnresolvedAsync(id, request, ct)));
        api.MapPost("/{id:guid}/return-to-open", async (Guid id, ReturnToOpenRequest request, PlayExecutionService service,
            CancellationToken ct) => Results.Ok(await service.ReturnToOpenAsync(id, request, ct)));
        api.MapDelete("/{id:guid}", async (Guid id, PlayService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }
}
