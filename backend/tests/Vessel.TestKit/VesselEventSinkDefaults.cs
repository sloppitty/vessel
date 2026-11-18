using Vessel.Application.Events;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>Test-only default for <see cref="IVesselEventSink"/> (issue #114): claiming historical fills does nothing.</summary>
public interface IVesselEventSinkDefaults : IVesselEventSink
{
    Task IVesselEventSink.ClaimHistoricalFillsAsync(IReadOnlyCollection<ImportedFill> fills, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
