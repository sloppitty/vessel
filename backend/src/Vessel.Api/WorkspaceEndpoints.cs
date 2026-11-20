using Vessel.Application.Activity;
using Vessel.Application.MarketSources;
using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Workspace;
using Vessel.Application.Credentials;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Monitoring;
using Vessel.Infrastructure.Monitoring;
using Vessel.Domain.Common;

namespace Vessel.Api;

public static class WorkspaceEndpoints
{
    public static void MapWorkspace(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTradingModeBoundary();
        api.MapGet("/venues/{venueId}/accounts", async (string venueId, string? address, AccountDiscoveryService service, CancellationToken ct) =>
            Results.Ok(await service.DiscoverAsync(venueId, address, ct))).RealOnly();
        // This read-only preview never stores the token. It carries the same HTTPS guard, no-store and
        // bounded body as credential saves; tokens never enter a URL.
        api.MapPost("/venues/{venueId}/accounts/credential",
            async (string venueId, CredentialDiscoveryRequest request, AccountDiscoveryService service, CancellationToken ct) =>
                Results.Ok(await service.DiscoverAsync(venueId, request, ct))).CarriesCredential().RealOnly();
        var credential = api.MapGroup("/accounts/{id:guid}/credential").CarriesCredential();
        credential.MapGet("",async (Guid id, AccountCredentialService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, ct)));
        credential.MapPut("", async (Guid id, SaveAccountCredentialRequest request, AccountCredentialService service, CancellationToken ct) =>
            Results.Ok(await service.PutAsync(id, request, ct)));
        credential.MapDelete("", async (Guid id, AccountCredentialService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapGet("/portfolios", async (string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.PortfoliosAsync(TradingModes.FromRequest(tradingMode), ct)));
        api.MapGet("/portfolios/{id:guid}", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.PortfolioAsync(id, TradingModes.FromRequest(tradingMode), ct)));
        api.MapPost("/portfolios", async (CreatePortfolioRequest request, WorkspaceService service, CancellationToken ct) =>
        {
            var result = await service.CreatePortfolioAsync(request, ct);
            return Results.Created($"/api/portfolios/{result.Id}", result);
        });
        api.MapPatch("/portfolios/{id:guid}", async (Guid id, string? tradingMode, UpdatePortfolioRequest request, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.UpdatePortfolioAsync(id, request, TradingModes.FromRequest(tradingMode), ct)));
        api.MapDelete("/portfolios/{id:guid}", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
        {
            await service.DeletePortfolioAsync(id, TradingModes.FromRequest(tradingMode), ct);
            return Results.NoContent();
        });
        api.MapPost("/accounts/portfolio", async (string? tradingMode, MoveAccountsRequest request, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.MoveAccountsAsync(request, TradingModes.FromRequest(tradingMode), ct)));
        api.MapPut("/accounts/{id:guid}", async (Guid id, string? tradingMode, UpdateAccountRequest request, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAccountAsync(id, request, TradingModes.FromRequest(tradingMode), ct)));
        api.MapDelete("/accounts/{id:guid}", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
        {
            await service.DeleteAccountAsync(id, TradingModes.FromRequest(tradingMode), ct);
            return Results.NoContent();
        });
        api.MapGet("/accounts", async (string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.AccountsAsync(TradingModes.FromRequest(tradingMode), ct)));
        api.MapGet("/accounts/{id:guid}", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.AccountAsync(id, TradingModes.FromRequest(tradingMode), ct)));
        api.MapPost("/accounts", async (CreateAccountRequest request, WorkspaceService service, CancellationToken ct) =>
        {
            var result = await service.CreateAccountAsync(request, ct);
            return Results.Created($"/api/accounts/{result.Id}", result);
        });
        api.MapGet("/accounts/{id:guid}/snapshot", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
        {
            var result = await service.SnapshotAsync(id, TradingModes.FromRequest(tradingMode), ct);
            // Results.Ok(null) emits an empty body; the contract requires literal JSON null.
            return result is null ? Results.Text("null", "application/json") : Results.Ok(result);
        });
        api.MapGet("/accounts/{id:guid}/fills", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.FillsAsync(id, TradingModes.FromRequest(tradingMode), ct)));
        api.MapGet("/accounts/{id:guid}/history-coverage", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryCoverageAsync(id, TradingModes.FromRequest(tradingMode), ct))).RealOnly();
        // Phase 2, item 6b: the owner starts a backfill of one gap; the monitor worker reads it behind protection checks.
        api.MapPost("/accounts/{id:guid}/history-gaps/{gapId:guid}/backfill", async (Guid id, Guid gapId, MonitorCoordinator monitor,
            CancellationToken ct) => Results.Accepted(value: await monitor.StartBackfillAsync(id, gapId, ct))).RealOnly();
        api.MapGet("/accounts/{id:guid}/import-conflicts", async (Guid id, string? tradingMode, WorkspaceService service, CancellationToken ct) =>
            Results.Ok(await service.FillConflictsAsync(id, TradingModes.FromRequest(tradingMode), ct))).RealOnly();
        api.MapGet("/accounts/{id:guid}/instruments", async (Guid id, string? tradingMode, WorkspaceService service, MarketSourceService sources, CancellationToken ct) =>
            Results.Ok(await sources.ApplyAsync(await service.InstrumentsAsync(id, TradingModes.FromRequest(tradingMode), ct), ct))).WithMetadata(new CompressMarketData());
        api.MapGet("/accounts/{id:guid}/candles", async (Guid id, string? instrument, string? interval, long? endTime,
            string? refresh, CandleService service, CancellationToken ct) =>
            Results.Ok(await service.CandlesAsync(id, instrument, interval, endTime, ct, refresh)))
            .WithMetadata(new CompressMarketData());
        api.MapGet("/accounts/{id:guid}/market-context", async (Guid id, string? instrument,
            MarketContextService service, CancellationToken ct) =>
            Results.Ok(await service.ContextAsync(id, instrument, ct))).WithMetadata(new CompressMarketData());
        api.MapGet("/accounts/{id:guid}/market-stream", MarketStreamEndpoint.StreamAsync);
        api.MapPost("/accounts/{id:guid}/sync", async (Guid id, string? tradingMode, WorkspaceService service, HttpContext context,
            MonitorRunExecutor executor, CancellationToken ct) =>
        {
            var account = await service.AccountAsync(id, TradingModes.FromRequest(tradingMode), ct);
            if (account.TradingMode != TradingMode.Real) throw new ProblemException(409, "Paper accounts cannot refresh real venue history.");
            try
            {
                if (!await executor.RefreshAsync(id, ct))
                    throw new ProblemException(502, "The venue refresh failed or was superseded by changed account settings. Try again later.");
            }
            catch (MonitorAdmissionException ex) { return MonitorEndpoints.Busy(context, ex); }
            return Results.Ok(await service.AccountAsync(id, TradingModes.FromRequest(tradingMode), ct));
        });
        api.MapGet("/activity", async ([AsParameters] ActivityQuery query, ActivityService service, CancellationToken ct) =>
            Results.Ok(await service.PageAsync(query, ct)));
        api.MapGet("/activity/fills", async (Guid? accountId, string? operationId, string? tradingMode, ActivityService service, CancellationToken ct) =>
            Results.Ok(await service.FillsAsync(accountId, operationId, TradingModes.FromRequest(tradingMode), ct)));
        api.MapGet("/overview/summary", async (string? groupBy, string? period, string? scope, string? tradingMode,
            Vessel.Application.Overview.OverviewSummaryService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(groupBy, period, scope, TradingModes.FromRequest(tradingMode), ct)));
        api.MapGet("/overview", async (string? tradingMode, WorkspaceService service, CancellationToken ct) => Results.Ok(await service.OverviewAsync(TradingModes.FromRequest(tradingMode), ct)));
    }
}
