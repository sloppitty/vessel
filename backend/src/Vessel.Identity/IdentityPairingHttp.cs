using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Vessel.Identity;

public static partial class IdentityHttp
{
    // Device pairing (device-pairing-plan.md). Create, status, approve and cancel need the creating desktop's session;
    // claim and complete are public routes for the phone, guarded like sign-in and bound to its binding cookie.
    private static void MapPairingEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/pairings", async (HttpContext context, DevicePairingService service, SessionPrincipal principal) =>
            Results.Json(await service.CreateAsync(principal.Authenticated!, context.Request.Headers.Origin.ToString(),
                context.RequestAborted), statusCode: 201)).RequireAuthorization();
        group.MapGet("/pairings/{id:guid}", async (Guid id, DevicePairingService service, SessionPrincipal principal, CancellationToken ct) =>
            Results.Ok(await service.StatusAsync(principal.Authenticated!, id, ct))).RequireAuthorization();
        group.MapPost("/pairings/{id:guid}/approve", async (Guid id, HttpContext context, DevicePairingService service, SessionPrincipal principal) =>
        {
            await service.ApproveAsync(principal.Authenticated!, id, await BodyAsync<ApprovePairingRequest>(context), context.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization();
        group.MapDelete("/pairings/{id:guid}", async (Guid id, DevicePairingService service, SessionPrincipal principal, CancellationToken ct) =>
        {
            await service.CancelAsync(principal.Authenticated!, id, ct);
            return Results.NoContent();
        }).RequireAuthorization();
        group.MapPost("/pairings/claim", async (HttpContext context, DevicePairingService service, IdentityCookies cookies) =>
        {
            var request = await BodyAsync<ClaimPairingRequest>(context);
            var binding = cookies.BindingToken(context);
            binding = IdentityTokens.Valid(binding) ? binding! : IdentityTokens.Create();
            var claimed = await service.ClaimAsync(request, context.Request.Headers.Origin.ToString(), binding,
                context.Request.Headers.UserAgent, context.RequestAborted);
            cookies.SetBinding(context, binding);
            return Results.Ok(claimed);
        }).AllowAnonymous();
        group.MapPost("/pairings/complete", async (HttpContext context, DevicePairingService service, IdentityCookies cookies) =>
        {
            var request = await BodyAsync<CompletePairingRequest>(context);
            var result = await service.CompleteAsync(request, cookies.BindingToken(context), cookies.SessionToken(context),
                context.Request.Headers.UserAgent, context.RequestAborted);
            if (result.SignedIn is not { } issued) return Results.Ok((object)new { status = result.Status });
            cookies.SetSession(context, issued);
            cookies.ClearBinding(context);
            return Results.Ok((object)new { status = result.Status, session = issued.Session });
        }).AllowAnonymous();
    }
}
