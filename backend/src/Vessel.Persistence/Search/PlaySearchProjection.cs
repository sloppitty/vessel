using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vessel.Application.Common;
using Vessel.Application.Ownership;
using Vessel.Application.Plays;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Search;

/// <summary>
/// Rewrites journal search projections (Phase 2, item 13) from the facts of the current transaction. Every Play is locked
/// first, in ID order, so two units projecting the same Play take turns, and the later one reads what the earlier
/// committed. The lock is the last any unit takes, just before its commit, so it never closes a cycle.
/// </summary>
internal static class PlaySearchProjection
{
    /// <summary>
    /// Projects the owner's Plays with these IDs inside the active unit: their texts and outcome, by
    /// <see cref="JournalSearch"/>'s rules. A Play that no longer exists keeps no rows. Returns how many Plays were projected.
    /// </summary>
    public static Task<int> ProjectAsync(VesselDbContext db, IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken ct) =>
        UnitOfWork.ForCurrentContext(db).ParticipateAsync(async () =>
    {
        if (ids.Count == 0) return 0;
        var wanted = ids.Distinct().ToList();
        // A Play this unit deleted is not locked: its rows went with it, and locking it could wait on a unit that waits on it.
        var present = await db.Plays.Where(p => wanted.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in present.Order())
        {
            var key = $"vessel-play-search:{db.CurrentOwnerId}:{id}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        }
        // Read after the locks, so each statement sees every projection of these Plays committed before this one.
        var plays = await db.Plays.Where(p => present.Contains(p.Id)).AsNoTracking().ToListAsync(ct);
        var found = plays.Select(p => p.Id).ToList();
        var log = (await db.PlayNotes.Where(n => found.Contains(n.PlayId)).OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id)
            .Select(n => new { n.PlayId, n.Text }).ToListAsync(ct)).ToLookup(n => n.PlayId, n => n.Text);
        var images = (await db.Evidence.Where(e => found.Contains(e.PlayId)).OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .Select(e => new { e.PlayId, e.Note }).ToListAsync(ct)).ToLookup(e => e.PlayId, e => e.Note);
        // Review's own facts, for the Closed Plays whose outcome they decide.
        var closed = plays.Where(p => p.Status == PlayStatus.Closed).ToList();
        var facts = closed.Count == 0 ? [] : (await SizingStore.FactsAsync(db, closed, ct)).ToDictionary(f => f.Play.Id);

        var entries = await db.PlaySearch.Where(s => wanted.Contains(s.PlayId)).ToDictionaryAsync(s => s.PlayId, ct);
        var texts = (await db.PlaySearchTexts.Where(t => wanted.Contains(t.PlayId)).ToListAsync(ct)).ToLookup(t => t.PlayId);
        foreach (var gone in wanted.Except(found))
        {
            db.PlaySearchTexts.RemoveRange(texts[gone]);
            if (entries.TryGetValue(gone, out var entry)) db.PlaySearch.Remove(entry);
        }
        foreach (var play in plays)
        {
            Outcome(db, entries.GetValueOrDefault(play.Id), play.Id, JournalSearch.Outcome(play, facts.GetValueOrDefault(play.Id), now), now);
            var desired = JournalSearch.Texts(play, log[play.Id], images[play.Id])
                .Select(t => (Position: JournalSearch.Position(t.Field, t.Item), t.Field, Text: JournalSearch.Normalize(t.Text)))
                .Where(t => t.Text.Length > 0).ToDictionary(t => t.Position);
            var current = texts[play.Id].ToDictionary(t => t.Position);
            foreach (var (position, row) in current)
                if (!desired.ContainsKey(position)) db.PlaySearchTexts.Remove(row);
            foreach (var (position, want) in desired)
            {
                if (current.TryGetValue(position, out var row))
                {
                    if (row.Text == want.Text && row.Field == want.Field) continue;
                    (row.Field, row.Text, row.Folded) = (want.Field, want.Text, JournalSearch.Fold(want.Text));
                }
                else db.PlaySearchTexts.Add(new PlaySearchText
                {
                    OwnerId = db.CurrentOwnerId, PlayId = play.Id, Position = position, Field = want.Field,
                    Text = want.Text, Folded = JournalSearch.Fold(want.Text),
                });
            }
        }
        await UnitOfWork.ForCurrentContext(db).SaveChangesAsync(ct);
        // Rows are reread under the lock next time, never answered from a tracked copy another unit has since replaced.
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is PlaySearchEntry or PlaySearchText).ToList())
            entry.State = EntityState.Detached;
        return plays.Count;
    }, ct);

    private static void Outcome(VesselDbContext db, PlaySearchEntry? entry, Guid playId, PlayOutcomeFacts outcome, DateTimeOffset now)
    {
        if (entry is null) db.PlaySearch.Add(entry = new PlaySearchEntry { OwnerId = db.CurrentOwnerId, PlayId = playId });
        else if (entry.Version == JournalSearch.Version && entry.State == outcome.State && entry.ExitAtUtc == outcome.ExitAtUtc &&
            entry.RReason == outcome.RReason && Same(entry.ResultUsd, outcome.ResultUsd) && Same(entry.EstimatedR, outcome.EstimatedR)) return;
        entry.Version = JournalSearch.Version;
        entry.ProjectedAtUtc = now;
        entry.State = outcome.State;
        entry.ExitAtUtc = outcome.ExitAtUtc;
        entry.ResultUsd = outcome.ResultUsd;
        entry.EstimatedR = outcome.EstimatedR;
        entry.RReason = outcome.RReason;
    }

    // Equal amounts compare by their exact text, which is what the list shows.
    private static bool Same(decimal? stored, decimal? value) => ExactDecimal.Format(stored) == ExactDecimal.Format(value);

    /// <summary>
    /// The owner's Plays reached by touched fills and orders: through their linked orders by the shared <see cref="OrderKey"/>
    /// rule, never by an external ID alone.
    /// </summary>
    public static async Task<List<Guid>> LinkedPlaysAsync(VesselDbContext db, PlaySearchCapture.Touched touched, CancellationToken ct)
    {
        var orders = touched.Orders.ToList();
        if (touched.FillOrders.Count > 0)
        {
            var accounts = touched.FillOrders.Select(k => k.AccountId).Distinct().ToArray();
            var external = touched.FillOrders.Select(k => k.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
            // SQL narrows by account and external ID; the OrderKey rule keeps only each fill's own contract.
            orders.AddRange((await db.Orders.Where(o => accounts.Contains(o.AccountId) && external.Contains(o.ExternalOrderId))
                    .Select(o => new { o.Id, o.AccountId, o.ContractId, o.ExternalOrderId }).ToListAsync(ct))
                .Where(o => touched.FillOrders.Contains(new OrderKey(o.AccountId, o.ContractId, o.ExternalOrderId))).Select(o => o.Id));
        }
        if (orders.Count == 0) return [];
        var ids = orders.Distinct().ToList();
        return await db.OrderLinks.Where(l => ids.Contains(l.ImportedOrderId) && l.State == OrderLinkState.Linked)
            .Select(l => l.PlayId).Distinct().ToListAsync(ct);
    }
}

/// <summary>
/// Keeps journal search projections in step with every unit of work (Phase 2, item 13). Before the commit, it projects the
/// Plays the unit's saves touched, from the database state of that same transaction, so the projection commits or rolls
/// back with the change.
/// </summary>
public sealed class PlaySearchProjector(VesselDbContext db, TimeProvider? time = null) : IUnitOfWorkParticipant
{
    public async Task BeforeCommitAsync(CancellationToken cancellationToken)
    {
        if (db.SearchCapture.IsEmpty) return;
        // A read snapshot writes nothing; anything held from earlier saves waits for the next writing unit.
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel == IsolationLevel.RepeatableRead) return;
        var touched = db.SearchCapture.Take();
        var ids = touched.Plays.ToHashSet();
        ids.UnionWith(await PlaySearchProjection.LinkedPlaysAsync(db, touched, cancellationToken));
        await PlaySearchProjection.ProjectAsync(db, ids, (time ?? TimeProvider.System).GetUtcNow(), cancellationToken);
    }

    public Task AfterCommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Touches survive a rollback: a savepoint rollback keeps the unit's earlier saves, and projecting a Play again is harmless.
    public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
