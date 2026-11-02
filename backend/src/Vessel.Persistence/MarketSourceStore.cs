using Microsoft.EntityFrameworkCore;
using Vessel.Application.MarketSources;
using Vessel.Domain.MarketSources;

namespace Vessel.Persistence;

public sealed class MarketSourceStore(VesselDbContext db, UnitOfWork unitOfWork) : IMarketSourceStore
{
    public async Task<IReadOnlyList<DisabledMarketSource>> DisabledAsync(CancellationToken ct) =>
        await db.DisabledMarketSources.AsNoTracking().ToListAsync(ct);

    public async Task SetEnabledAsync(Guid ownerId, string venueId, string sourceId, bool enabled, DateTimeOffset now, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        if (ownerId != db.CurrentOwnerId) throw new InvalidOperationException("Market sources belong to the current owner.");
        // Idempotent either way: repeating a toggle, or racing another tab, leaves one consistent row or none.
        await (enabled
            ? db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM disabled_market_sources
                WHERE ""OwnerId"" = {ownerId} AND ""VenueId"" = {venueId} AND ""SourceId"" = {sourceId}", ct)
            : db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO disabled_market_sources(""OwnerId"", ""VenueId"", ""SourceId"", ""DisabledAtUtc"")
                VALUES({ownerId}, {venueId}, {sourceId}, {now}) ON CONFLICT DO NOTHING", ct));
    }
}
