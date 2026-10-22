using Microsoft.EntityFrameworkCore;
using Vessel.Application.Plays;

namespace Vessel.Persistence.Search;

/// <summary>What one owner's rebuild found and did: all their Plays, those it would project and those it projected.</summary>
public sealed record PlaySearchRebuildResult(int Plays, int Pending, int Projected);

/// <summary>
/// The one-time initialization of the journal search projection (Phase 2, item 13), for one owner scope: it projects the
/// owner's Plays that have no projection of the current version, or all of them with <c>all</c>, in bounded units of work in
/// ID order. A dry run only counts. It takes the same per-Play locks as live writes, so it is safe beside running hosts, and
/// running it again finds nothing left to do.
/// </summary>
public sealed class PlaySearchRebuild(VesselDbContext db, UnitOfWork unitOfWork, TimeProvider? time = null)
{
    public const int DefaultBatch = 100, MaxBatch = 500;

    public async Task<PlaySearchRebuildResult> RunAsync(bool apply, bool all, int batch, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch, MaxBatch);
        var plays = await db.Plays.CountAsync(ct);
        var pending = all ? plays : await Pending().CountAsync(ct);
        if (!apply) return new(plays, pending, 0);
        var projected = 0;
        Guid? after = null;
        while (true)
        {
            // Keyset pages over IDs, so every Play is visited once even while others are created or projected meanwhile.
            var candidates = all ? db.Plays.AsQueryable() : Pending();
            if (after is { } last) candidates = candidates.Where(p => p.Id.CompareTo(last) > 0);
            var page = await candidates.OrderBy(p => p.Id).Select(p => p.Id).Take(batch).ToListAsync(ct);
            if (page.Count == 0) break;
            projected += await unitOfWork.ExecuteAsync(() =>
                PlaySearchProjection.ProjectAsync(db, page, (time ?? TimeProvider.System).GetUtcNow(), ct), ct);
            after = page[^1];
        }
        return new(plays, pending, projected);
    }

    private IQueryable<Vessel.Domain.Plays.Play> Pending() =>
        db.Plays.Where(p => !db.PlaySearch.Any(s => s.PlayId == p.Id && s.Version == JournalSearch.Version));
}
