using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Events;
using Vessel.Application.Monitoring;
using Vessel.Persistence.Events.Subscriptions;

namespace Vessel.Messaging.Modules.Monitor;

/// <summary>
/// The account monitor. Any host can request a run: the pending request is written to <c>monitor_accounts</c> and a
/// <c>commands.monitor</c> notification is staged in the same transaction. A host that runs the module listens for it;
/// the worker adds the scheduler and run executor. It owns no event subscription.
/// </summary>
public sealed class MonitorModule : IVesselModule
{
    public const string ModuleName = "monitor";
    public string Name => ModuleName;
    public IReadOnlyList<CommandQueue> Commands { get; } =
        [new("monitor", typeof(MonitorAccountCommand), typeof(MonitorCommandListener))];
    public IReadOnlyList<EventSubscription> Subscriptions { get; } = [];

    public void AddRunningServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IMonitorWakeup, NoMonitorWakeup>();
        services.AddScoped<IVesselCommandHandler<MonitorAccountCommand>, MonitorCommandHandler>();
    }
}
