using Microsoft.AspNetCore.Http.Features;
using Vessel.Application.Common;
using Vessel.Application.Evidence;

namespace Vessel.Api;

public static class EvidenceEndpoints
{
    // Room for multipart boundaries, headers and the note around the image.
    private const long FormOverheadBytes = 64 * 1024;

    public static void MapEvidence(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTradingModeBoundary();
        api.MapGet("/plays/{playId:guid}/evidence", async (Guid playId, EvidenceService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(playId, ct)));
        api.MapPost("/plays/{playId:guid}/evidence", UploadAsync);
        api.MapGet("/evidence/{id:guid}/content", async (Guid id, HttpContext context, EvidenceService service, CancellationToken ct) =>
        {
            var content = await service.ContentAsync(id, ct);
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.CacheControl = "private, no-store";
            headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.Stream(content.Content, content.ContentType,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{content.Sha256}\""));
        });
        api.MapPatch("/evidence/{id:guid}", async (Guid id, UpdateEvidenceNoteRequest request, EvidenceService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateNoteAsync(id, request, ct)));
        api.MapPut("/evidence/{id:guid}/markup", async (Guid id, ImageMarkup markup, EvidenceService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateMarkupAsync(id, markup, ct)));
        api.MapDelete("/evidence/{id:guid}/markup", async (Guid id, EvidenceService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateMarkupAsync(id, null, ct)));
        api.MapDelete("/evidence/{id:guid}", async (Guid id, EvidenceService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    /// <summary>multipart/form-data with one <c>file</c>, optional <c>note</c>, <c>source</c> (capture or upload) and <c>markup</c> JSON.</summary>
    private static async Task<IResult> UploadAsync(Guid playId, HttpRequest request, EvidenceService service, EvidenceLimits limits, CancellationToken ct)
    {
        var maxBody = limits.MaxUploadBytes + FormOverheadBytes;
        var tooLarge = new ProblemException(413, $"Images are limited to {limits.MaxUploadBytes / (1024 * 1024)} MB.");
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = maxBody;
        if (request.ContentLength > maxBody) throw tooLarge;
        if (!request.HasFormContentType) throw new ProblemException(415, "Send the image as multipart/form-data.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = maxBody }, ct); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge) { throw tooLarge; }
        catch (InvalidDataException) { throw new ProblemException(400, "The upload form is invalid."); }
        if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
            throw new ProblemException(400, "Attach exactly one image in the file field.");
        if (form["note"].Count > 1 || form["source"].Count > 1 || form["markup"].Count > 1)
            throw new ProblemException(400, "Send at most one note, source and markup.");
        await using var content = file.OpenReadStream();
        var result = await service.UploadAsync(playId, content, form["note"].SingleOrDefault(), form["source"].SingleOrDefault(), form["markup"].SingleOrDefault(), ct);
        return Results.Created($"/api/evidence/{result.Id}", result);
    }
}
