using System.Numerics;
using Vessel.Application.Plays;
using Vessel.Domain.Numerics;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;

namespace Vessel.Application.Sizing;

/// <summary>Explicit simulated facts. Never projected into imported fills, orders or manual actual executions.</summary>
public sealed record PaperPlayFacts(Play Play, IReadOnlyList<PlayPlanRevision> Revisions,
    IReadOnlyList<PaperExecution> Executions, string State, IReadOnlyList<string> Issues)
{
    public DateTimeOffset ClosedAt => Executions.Where(e => e.Role != "entry")
        .Select(e => (DateTimeOffset?)e.OccurredAtUtc).Max() ?? Play.EndedAtUtc ?? Play.UpdatedAtUtc;
    public bool HasIssues => State is "ambiguous" or "gap" || Issues.Count > 0;
    public PlayPlanDocument? InitialPlan
    {
        get
        {
            var first = Executions.Where(e => e.Role == "entry").OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id).FirstOrDefault();
            if (first is not null && Executions.Where(e => e.Role == "entry" && e.OccurredAtUtc == first.OccurredAtUtc)
                .Select(e => e.PlanVersion).Distinct().Skip(1).Any()) return null;
            var revision = first is null ? null : Revisions.SingleOrDefault(r => r.Number == first.PlanVersion);
            return revision is null ? null : PlayDocuments.Read(revision.Plan);
        }
    }

    public PlayOutcome? Outcome(DateTimeOffset asOf, out string? problem)
    {
        problem = null;
        if (HasIssues) { problem = "Simulated outcome is unresolved: " + string.Join(" ", Issues.Prepend(State)); return null; }
        if (State is not ("waiting" or "ready" or "disabled"))
        { problem = "The retained simulation state is unavailable."; return null; }
        var entries = Executions.Where(e => e.Role == "entry").ToList();
        var exits = Executions.Where(e => e.Role != "entry").ToList();
        if (Play.TradingMode != "paper" || Play.Status != PlayStatus.Closed || entries.Count == 0 || exits.Count == 0 ||
            Executions.Select(e => e.Id).Distinct().Count() != Executions.Count ||
            Executions.Any(e => e.TradingMode != "paper" || e.OwnerId != Play.OwnerId || e.AccountId != Play.AccountId ||
                e.PlayId != Play.Id || e.Quantity <= 0 || e.Price <= 0 || e.OccurredAtUtc > asOf ||
                e.Role is not ("entry" or "stop" or "target" or "exit") ||
                e.CandleOpenAtUtc >= e.CandleCloseAtUtc || e.OccurredAtUtc < e.CandleOpenAtUtc ||
                e.OccurredAtUtc > e.CandleCloseAtUtc) || entries.Any(e => e.PnlUsd != 0))
        { problem = "Complete, valid simulated entry and exit facts are unavailable."; return null; }
        if (entries.Any(entry => exits.Any(exit => exit.EntryId == entry.EntryId && exit.CandleOpenAtUtc <= entry.CandleOpenAtUtc)) ||
            exits.GroupBy(e => (e.EntryId, e.CandleOpenAtUtc)).Any(g => g.Any(e => e.Role == "stop") && g.Any(e => e.Role == "target")))
        { problem = "A candle cannot establish entry/exit or opposing stop/target ordering; the simulated outcome is unresolved."; return null; }
        // Each entry allocation must settle independently; sibling exits cannot borrow another entry's quantity.
        foreach (var group in Executions.GroupBy(e => e.EntryId))
        {
            var open = default(ExactAmount);
            foreach (var execution in group.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Role == "entry" ? 0 : 1))
            {
                open += ExactAmount.From(execution.Role == "entry" ? execution.Quantity : -execution.Quantity);
                if (open.Units.Sign < 0) { problem = "Simulated exits exceed their entered allocation."; return null; }
            }
            if (!open.Units.IsZero) { problem = "Simulated entry and exit quantities do not settle."; return null; }
        }
        try
        {
            var notional = PlayOutcomeCalculator.ToDecimal(entries.Aggregate(BigInteger.Zero,
                (sum, e) => sum + ExactAmount.Product(e.Quantity, e.Price)));
            var net = Executions.Aggregate(default(ExactAmount), (sum, e) => sum + ExactAmount.From(e.PnlUsd));
            decimal? risk = InitialPlan is { } plan && PlayOutcomeCalculator.StopDistance(plan) is { } distance
                ? notional * distance : null;
            var result = new PlayOutcome(Play.Id, ClosedAt, PlayOutcomeCalculator.ToDecimal(net), notional, risk, true)
            { GrossResult = net };
            _ = result.ReturnPercent;
            _ = result.RMultiple;
            return result;
        }
        catch (OverflowException)
        { problem = "Simulated result, entry notional, return or initial-risk R exceeds exact decimal precision."; return null; }
    }

    public static PlayLocalResult Local(PlayOutcome outcome) =>
        new(ExactAmount.From(outcome.NetResult), ExactAmount.From(outcome.NetResult), "paper-simulation", null);
}
