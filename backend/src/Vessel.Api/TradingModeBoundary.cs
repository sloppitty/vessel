using Vessel.Application.Common;
using Vessel.Application.Evidence;
using Vessel.Application.Plays;
using Vessel.Application.Workspace;

namespace Vessel.Api;

/// <summary>Mode checks run after bounded model binding, before any handler reads or writes owner resources.</summary>
public static class TradingModeBoundary
{
    /// <summary>Selection-only checks precede handler service resolution, without reading resources or request bodies.</summary>
    public static void UseTradingModeBoundary(this WebApplication app) => app.Use(async (context, next) =>
    {
        var parts = Parts(context);
        if (parts is { Length: >= 2 } && parts[0] == "api")
        {
            var mode = Mode(context);
            if (mode == "paper" && ActualOnly(parts))
                throw new ProblemException(409, "Paper accounts do not use actual-account discovery or monitoring.");
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
        var mode = Mode(context);
        if (mode == "paper" && ActualOnly(parts))
            throw new ProblemException(409, "Paper accounts do not use actual-account discovery or monitoring.");
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
                "portfolios" => (await services.GetRequiredService<IWorkspaceStore>().PortfoliosAsync(ct, mode)).SingleOrDefault(p => p.Id == id)?.TradingMode,
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
    private static bool ActualOnly(string[] parts) => parts[1] is "monitor" or "risk" ||
        parts is [_, "venues", _, "accounts", ..] ||
        // Phase 2, item 5b: an account's raw retained venue facts (Sol round 1 on #39: refused before any read).
        parts is [_, "accounts", _, "facts"] ||
        // Phase 2, item 14c: retained fills reported again with different venue facts.
        parts is [_, "accounts", _, "import-conflicts"] ||
        parts is [_, "activity" or "plays", "retrospective"];
    private static string Mode(HttpContext context)
    {
        var values = context.Request.Query["tradingMode"];
        return values.Count == 0 ? "real" : values.Count == 1 && values[0] is "real" or "paper"
            ? values[0]! : throw new ProblemException(400, "Trading mode must be real or paper.");
    }

    private static async Task<string?> EvidenceMode(IEvidenceMetadataStore store, Guid id, CancellationToken ct) =>
        await store.FindAsync(id, ct) is { } evidence ? (await store.PlayAsync(evidence.PlayId, ct))?.TradingMode : null;
}
