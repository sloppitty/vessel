using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Vessel.Application.Common;
using Vessel.Domain.Evidence;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Search;

/// <summary>
/// What every save touched that a journal search projection reads (Phase 2, item 13), kept on the context so no write path
/// and no host composition can miss it: Plays by ID, and fills and orders whose Plays are resolved through their links at
/// the end of the unit. It over-approximates: projecting an unchanged Play rewrites the same rows.
/// </summary>
internal sealed class PlaySearchCapture
{
    private HashSet<Guid> plays = [];
    private HashSet<OrderKey> fillOrders = [];
    private HashSet<Guid> orders = [];

    public bool IsEmpty => plays.Count == 0 && fillOrders.Count == 0 && orders.Count == 0;

    /// <summary>Records the pending changes of one save. It runs before EF accepts them, so original values are still known.</summary>
    public void Capture(ChangeTracker tracker)
    {
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (entry.Entity)
            {
                case Play play: plays.Add(play.Id); break;
                case PlayPlanRevision revision: plays.Add(revision.PlayId); break;
                case PlayReviewRevision review: plays.Add(review.PlayId); break;
                case PlayStatusChange change: plays.Add(change.PlayId); break;
                case PlayNote note: plays.Add(note.PlayId); break;
                case ManualExecution execution: plays.Add(execution.PlayId); break;
                case PlayOrderLink link: plays.Add(link.PlayId); break;
                // A Play's part of an allocated fill (Phase 2, item 11), added, changed or removed.
                case FillAllocationLine line: plays.Add(line.PlayId); break;
                case PaperExecution execution: plays.Add(execution.PlayId); break;
                // Markup alone is not searchable; a new, removed or renoted image is.
                case PlayEvidence evidence when entry.State != EntityState.Modified || Changed(entry, nameof(PlayEvidence.Note)):
                    plays.Add(evidence.PlayId); break;
                // Scheduling fields change on every check; only the simulation's outcome inputs matter.
                case PaperSimulation simulation when entry.State != EntityState.Modified ||
                    Changed(entry, nameof(PaperSimulation.State), nameof(PaperSimulation.IssuesJson), nameof(PaperSimulation.SuspensionsJson)):
                    plays.Add(simulation.PlayId); break;
                // A fill reaches a Play only through a linked order with its account, contract and external ID.
                case ImportedFill fill: fillOrders.Add(OrderKey.Of(fill)); break;
                // A refresh observes every retained order; only what decides missing executions changes an outcome.
                case ImportedOrder order when entry.State == EntityState.Modified &&
                    Changed(entry, nameof(ImportedOrder.Status), nameof(ImportedOrder.OriginalSize), nameof(ImportedOrder.IsPositionTpsl)):
                    orders.Add(order.Id); break;
            }
        }
    }

    /// <summary>Hands over everything captured so far and starts empty.</summary>
    public Touched Take()
    {
        var taken = new Touched(plays, fillOrders, orders);
        plays = []; fillOrders = []; orders = [];
        return taken;
    }

    private static bool Changed(EntityEntry entry, params string[] properties) => properties.Any(p => entry.Property(p).IsModified);

    internal sealed record Touched(IReadOnlySet<Guid> Plays, IReadOnlySet<OrderKey> FillOrders, IReadOnlySet<Guid> Orders);
}
