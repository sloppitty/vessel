using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Venues;
using Vessel.Application.Workspace;
using Vessel.Domain.Accounts;
using Vessel.Domain.Plays;

namespace Vessel.Application.Monitoring;

/// <summary>Composes stored monitor state without changing execution writes or introducing an importer/service cycle.</summary>
public sealed class MonitorExecutionView(IPlayStore plays, IWorkspaceStore workspace, IVenueRegistry venues,
    MonitorCoordinator monitor, IMonitorEvaluator? evaluator = null)
{
    public async Task<PlayExecutionDto> ComposeAsync(PlayExecutionDto execution, CancellationToken ct)
    {
        var play = await RequirePlayAsync(execution.PlayId, ct);
        var account = await workspace.AccountAsync(play.AccountId, ct);
        if (!CanMonitor(play, account)) return execution;
        return execution with
        {
            Monitor = await monitor.AccountAsync(play.AccountId, ct),
            Discrepancies = evaluator is null ? [] : await evaluator.ReadAsync(play.AccountId, play.Id, ct),
            Watches = await monitor.WatchesAsync(play.Id, ct)
        };
    }

    public async Task<MonitorRunAccepted> RequestAsync(Guid playId, CancellationToken ct)
    {
        var play = await RequirePlayAsync(playId, ct);
        if (!CanMonitor(play, await workspace.AccountAsync(play.AccountId, ct)))
            throw new ProblemException(409, "This Play cannot be monitored.");
        return await monitor.RequestAsync(play.AccountId, ct);
    }

    private async Task<Play> RequirePlayAsync(Guid id, CancellationToken ct) =>
        await plays.FindAsync(id, ct) ?? throw new ProblemException(404, "Play not found.");

    private bool CanMonitor(Play play, Account? account) =>
        account is { TradingMode: "real", IsEnabled: true, SourceId: not null } &&
        play.Origin == PlayOrigin.Planned && play.InstrumentSource == InstrumentSource.Venue &&
        play.Status != PlayStatus.Draft &&
        venues.Descriptor(account.VenueId) is { Capabilities.Sync: true, Capabilities.Orders: true };
}
