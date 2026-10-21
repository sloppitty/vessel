using Microsoft.AspNetCore.Http.Features;
using Vessel.Application.Common;

namespace Vessel.Api;

/// <summary>
/// Marks a route whose request can carry a read-only venue token. Token-bearing methods need HTTPS outside Development
/// unless the request is loopback, and every response, errors included, is <c>no-store</c>.
/// </summary>
public sealed class CredentialEndpoint;

/// <summary>
/// Bounds the request body after authorization and before model binding, including chunked bodies.
/// This deliberately is not IRequestSizeLimitMetadata: routing would apply it before authorization.
/// </summary>
public sealed class RequestBodyLimit(long maxBytes, string tooLargeMessage)
{
    public long MaxBytes => maxBytes;
    public string TooLargeMessage => tooLargeMessage;
}

/// <summary>Transport rules declared as endpoint metadata, so a new route cannot miss them by its path.</summary>
public static class EndpointTransport
{
    public const long CredentialBodyBytes = 4096;
    internal const long ManualExecutionBodyBytes = 16_384;
    /// <summary>A correction batch holds up to 200 full rows and one reason (phase 1, 1.3b).</summary>
    internal const long ManualCorrectionBatchBodyBytes = 262_144;
    internal const long RetrospectiveBodyBytes = 131_072;
    internal const string DiaryTooLarge = "Diary request is too large.";

    /// <summary>A route that carries a venue token: the HTTPS guard, <c>no-store</c> and a 4 KiB body.</summary>
    public static TBuilder CarriesCredential<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new CredentialEndpoint(), new RequestBodyLimit(CredentialBodyBytes, "Credential request is too large."));

    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, long maxBytes, string tooLargeMessage)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new RequestBodyLimit(maxBytes, tooLargeMessage));

    public static bool IsCredentialEndpoint(this HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<CredentialEndpoint>() is not null ||
        context.GetEndpoint() is not RouteEndpoint &&
        context.Request.Path.Value?.TrimEnd('/').EndsWith("/credential", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Applies the matched endpoint's transport metadata. Register after authorization, so a request without a valid
    /// bearer token gets 401 before any transport or size answer. Never reads or logs the body.
    /// </summary>
    public static void UseEndpointTransport(this WebApplication app) => app.Use(async (context, next) =>
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (context.IsCredentialEndpoint())
        {
            context.Response.Headers.CacheControl = "no-store";
            // Local/LAN development may use HTTP. Every other environment retains the transport guard,
            // independently of the frontend build; headers and request bodies cannot enable this exception.
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method) &&
                !app.Environment.IsDevelopment() &&
                !CredentialTransport.IsAllowed(context))
                throw new ProblemException(403, "Credential entry requires HTTPS outside localhost.");
        }
        var limit = metadata?.GetMetadata<RequestBodyLimit>() ?? UnmatchedBodyLimit(context);
        if (limit is not null)
        {
            var serverLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (serverLimit is { IsReadOnly: false }) serverLimit.MaxRequestBodySize = limit.MaxBytes;
            if (context.Request.ContentLength > limit.MaxBytes)
                throw new ProblemException(413, limit.TooLargeMessage);
        }
        await next(context);
    });

    // Keep the old rejection contract for malformed URLs and unsupported methods. Matched routes use
    // only their declared metadata; this compatibility path neither reads a body nor invokes a handler.
    private static RequestBodyLimit? UnmatchedBodyLimit(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint) return null;
        if (context.IsCredentialEndpoint()) return new(CredentialBodyBytes, "Credential request is too large.");
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || !request.Path.StartsWithSegments("/api/plays")) return null;
        if (request.Path.Value?.Contains("/manual-executions", StringComparison.OrdinalIgnoreCase) == true)
            return new(ManualExecutionBodyBytes, DiaryTooLarge);
        return string.Equals(request.Path.Value?.TrimEnd('/'), "/api/plays/retrospective", StringComparison.OrdinalIgnoreCase)
            ? new(RetrospectiveBodyBytes, DiaryTooLarge) : null;
    }
}
