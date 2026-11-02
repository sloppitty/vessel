using Vessel.Application.MarketSources;
using Vessel.Domain.MarketSources;

namespace Vessel.Tests;

public sealed class MemoryMarketSourceStore : IMarketSourceStore
{
    private readonly object gate = new();
    public List<DisabledMarketSource> Rows { get; } = [];

    public Task<IReadOnlyList<DisabledMarketSource>> DisabledAsync(CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<DisabledMarketSource>>(Rows.ToList());
    }

    public Task SetEnabledAsync(Guid ownerId, string venueId, string sourceId, bool enabled, DateTimeOffset now, CancellationToken ct)
    {
        lock (gate)
        {
            Rows.RemoveAll(r => r.OwnerId == ownerId && r.VenueId == venueId && r.SourceId == sourceId);
            if (!enabled) Rows.Add(new(ownerId, venueId, sourceId, now));
        }
        return Task.CompletedTask;
    }
}
