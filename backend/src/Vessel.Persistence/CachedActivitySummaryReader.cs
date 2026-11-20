using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;
using Vessel.Domain.Common;

namespace Vessel.Persistence;

public sealed class CachedActivitySummaryReader(IJournalOwnerContext owner, IOwnerScopeFactory scopes,
    ActivitySummaryCache cache, IVenueRegistry? venues = null) : IActivitySummaryReader
{
    public async Task<ActivitySummaries> ReadAsync(Guid? accountId, TradingMode tradingMode, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
    {
        tradingMode = TradingModes.Validate(tradingMode);
        // Copy mutable caller input before crossing a request lifetime.
        var ids = accountIds?.ToHashSet();
        var scope = scopes.Create(owner.OwnerId);
        var transferred = false;
        try
        {
            var db = scope.Services.GetRequiredService<VesselDbContext>();
            await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            // Paper's ledger/checkpoints are not imported-fact revisions. Read a fresh consistent snapshot instead of
            // serving stale simulated results under an unrelated real-import revision.
            if (tradingMode == TradingMode.Paper)
            {
                var facts = await scope.Services.GetRequiredService<IActivityStore>().ReadAsync(accountId, tradingMode, ct, ids);
                await db.Database.CurrentTransaction!.CommitAsync(ct);
                return ActivityService.Aggregate(facts, tradingMode, accountId, ids);
            }
            // Issue #107: the cache is keyed by the execution revision. Observations (snapshots, funding coverage, sync state)
            // are read here every time, in the same snapshot, and the cached aggregate is rebased onto them.
            var revisions = await ActivityStore.RevisionsAsync(db, scope.OwnerId, ct);
            var observed = await ActivityStore.ObservationsAsync(db, accountId, tradingMode, ct, ids);
            var key = new ActivitySummaryKey(scope.OwnerId, revisions.ExecutionRevision, accountId,
                ids is null ? "*" : string.Join(',', ids.Order()), tradingMode);
            var cached = await cache.ReadAsync(key, async token =>
            {
                transferred = true;
                await using (scope)
                {
                    // Issue #107: an account's read narrows a cached aggregate over several accounts that holds its history.
                    var narrowed = accountId is { } one && ids is null &&
                        cache.FindContaining(scope.OwnerId, revisions.ExecutionRevision, tradingMode, one) is { } wide
                        ? ActivityService.Narrow(wide, one, observed, revisions.Revision) : null;
                    var facts = narrowed ?? await scope.Services.GetRequiredService<IActivityStore>().ReadAsync(accountId, tradingMode, token, ids);
                    var transaction = db.Database.CurrentTransaction!;
                    await transaction.CommitAsync(token);
                    await transaction.DisposeAsync();
                    await db.Database.CloseConnectionAsync();
                    token.ThrowIfCancellationRequested();
                    var result = ActivityService.Aggregate(facts, tradingMode, accountId, ids, scope.Services.GetService<IVenueRegistry>());
                    token.ThrowIfCancellationRequested();
                    return result;
                }
            }, () => { transferred = true; return scope.DisposeAsync(); }, ct);
            var rebased = ActivityService.Rebase(cached, observed, revisions.Revision, accountId, ids, venues);
            if (!ReferenceEquals(rebased, cached)) cache.Replace(key, cached, rebased);
            return rebased;
        }
        finally { if (!transferred) await scope.DisposeAsync(); }
    }
}
