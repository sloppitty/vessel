using Vessel.Application.Events;

namespace Vessel.Tests;
internal sealed class DiscrepancyEvents : IVesselEventSink
{
    public List<VesselEventDraft> Items { get; } = [];
    public Task AppendAsync(VesselEventDraft draft, CancellationToken ct) { Items.Add(draft); return Task.CompletedTask; }
    public Task SetRunAsync(Guid runId, string trigger, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureMonitoringBaselineAsync(Guid accountId, CancellationToken ct) => Task.CompletedTask;
}
