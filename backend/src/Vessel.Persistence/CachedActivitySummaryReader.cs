using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Application.Activity;
using Vessel.Application.Ownership;
using Vessel.Application.Venues;

namespace Vessel.Persistence;

public sealed class CachedActivitySummaryReader(IJournalOwnerContext owner, IOwnerScopeFactory scopes,
    ActivitySummaryCache cache) : IActivitySummaryReader
{
    public async Task<ActivitySummaries> ReadAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null)
    {
        // Copy mutable caller input before crossing a request lifetime.
        var ids = accountIds?.ToHashSet();
        var scope = scopes.Create(owner.OwnerId);
        var transferred = false;
        try
        {
            var db = scope.Services.GetRequiredService<VesselDbContext>();
            await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var revision = await db.Database.SqlQuery<long>($"""
                SELECT COALESCE((SELECT revision FROM activity_facts_revisions WHERE owner_id = {scope.OwnerId}), 0) AS "Value"
                """).SingleAsync(ct);
            var key = new ActivitySummaryKey(scope.OwnerId, revision, accountId,
                ids is null ? "*" : string.Join(',', ids.Order()));
            return await cache.ReadAsync(key, async token =>
            {
                transferred = true;
                await using (scope)
                {
                    var facts = await scope.Services.GetRequiredService<IActivityStore>().ReadAsync(accountId, token, ids);
                    var transaction = db.Database.CurrentTransaction!;
                    await transaction.CommitAsync(token);
                    await transaction.DisposeAsync();
                    await db.Database.CloseConnectionAsync();
                    token.ThrowIfCancellationRequested();
                    var result = ActivityService.Aggregate(facts, accountId, ids, scope.Services.GetService<IVenueRegistry>());
                    token.ThrowIfCancellationRequested();
                    return result;
                }
            }, () => { transferred = true; return scope.DisposeAsync(); }, ct);
        }
        finally { if (!transferred) await scope.DisposeAsync(); }
    }
}
