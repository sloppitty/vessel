using Vessel.Application.Common;
using Vessel.Application.Evidence;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;
using Vessel.Domain.Common;

namespace Vessel.Api;

/// <summary>
/// Marks an endpoint that reads or changes actual venue facts (discovery, monitoring, risk, retained facts, retrospective
/// Plays, fill allocations). A paper request to it is refused before anything runs.
/// </summary>
public sealed class RealOnlyMetadata
{
    public static readonly RealOnlyMetadata Instance = new();
    private RealOnlyMetadata() { }
}

/// <summary>Mode checks run after bounded model binding, before any handler reads or writes owner resources.</summary>
public static class TradingModeBoundary
{
    public const string RealOnlyRefusal = "Paper accounts do not use actual-account discovery or monitoring.";

    /// <summary>The endpoint is real-only: <c>?tradingMode=paper</c> answers 409 before services, resources or bodies are read.</summary>
    public static TBuilder RealOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(RealOnlyMetadata.Instance);

    /// <summary>Selection-only checks precede handler service resolution, without reading resources or request bodies.</summary>
    public static void UseTradingModeBoundary(this WebApplication app) => app.Use(async (context, next) =>
    {
        var parts = Parts(context);
        if (parts is { Length: >= 2 } && parts[0] == "api")
        {
            var mode = Mode(context);
            if (mode == TradingMode.Paper && context.GetEndpoint()?.Metadata.GetMetadata<RealOnlyMetadata>() is not null)
                throw new ProblemException(409, RealOnlyRefusal);
        }
        await next(context);
    });

    public static TBuilder WithTradingModeBoundary<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (invocation, next) =>
    {
        var context = invocation.HttpContext;
        // ASP.NET routing is case-insensitive; the boundary must recognize exactly the same paths.
        var parts = Parts(context);
        if (parts is not { Length: >= 2 } || parts[0] != "api")
        {
            return await next(invocation);
        }
        // UseTradingModeBoundary has already refused a paper request to a real-only endpoint.
        var mode = Mode(context);
        // Ordinary account/portfolio ID handlers already enforce the mode inside WorkspaceService. Avoid a
        // duplicate pre-read there, preserving their existing diagnostics and failure behavior.
        if (parts.Length >= 3 && !(parts.Length == 3 && parts[1] is "accounts" or "portfolios") &&
            Guid.TryParse(parts[2], out var id) &&
            parts[1] is "plays" or "accounts" or "portfolios" or "evidence")
        {
            var services = context.RequestServices;
            var ct = context.RequestAborted;
            // Every query retains the existing owner filter; a different mode is indistinguishable from a missing ID.
            var actual = parts[1] switch
            {
                "plays" when parts is [_, _, _, "evidence", ..] =>
                    (await services.GetRequiredService<IEvidenceMetadataStore>().PlayAsync(id, ct))?.TradingMode,
                "plays" => (await services.GetRequiredService<IPlayStore>().FindAsync(id, ct))?.TradingMode,
                "accounts" => (await services.GetRequiredService<IWorkspaceStore>().AccountAsync(id, ct))?.TradingMode,
                "portfolios" => (await services.GetRequiredService<IWorkspaceStore>().PortfoliosAsync(mode, ct)).SingleOrDefault(p => p.Id == id)?.TradingMode,
                _ => await EvidenceMode(services.GetRequiredService<IEvidenceMetadataStore>(), id, ct)
            };
            if (actual != mode) throw new ProblemException(404, parts[1] switch
            {
                "plays" => "Play not found.", "accounts" => "Account not found.",
                "portfolios" => "Portfolio not found.", _ => "Evidence not found."
            });
        }
        return await next(invocation);
    });

    private static string[]? Parts(HttpContext context) => context.Request.Path.Value?
        .Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => segment.ToLowerInvariant()).ToArray();
    private static TradingMode Mode(HttpContext context)
    {
        var values = context.Request.Query["tradingMode"];
        return values.Count == 0 ? TradingMode.Real : values.Count == 1 && TradingModes.TryParse(values[0], out var mode)
            ? mode : throw new ProblemException(400, TradingModes.Invalid);
    }

    private static async Task<TradingMode?> EvidenceMode(IEvidenceMetadataStore store, Guid id, CancellationToken ct) =>
        await store.FindAsync(id, ct) is { } evidence ? (await store.PlayAsync(evidence.PlayId, ct))?.TradingMode : null;
}
