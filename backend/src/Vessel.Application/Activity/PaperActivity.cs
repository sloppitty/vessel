using System.Numerics;
using Vessel.Application.Sizing;
using Vessel.Domain.Numerics;
using Vessel.Domain.Paper;

namespace Vessel.Application.Activity;

internal static class PaperActivity
{
    internal const string Notice = "Simulated paper executions only, not imported fills or actual trades. " +
        "Full touched allocations at their defined price, zero fees and zero slippage. Starting capital is not profit. " +
        "Unresolved simulations are incomplete and excluded from scored results. Real Activity is separate.";

    internal static ActivitySummaries Aggregate(ActivityFacts facts)
    {
        var accounts = facts.Accounts.Select(a => a.Id).ToHashSet();
        var plays = facts.PaperPlays.Where(p => accounts.Contains(p.Play.AccountId) && p.Play.TradingMode == "paper").ToList();
        var operations = plays.Where(p => p.Executions.Count > 0).Select(Operation).ToList();
        var coverage = plays.Where(p => p.HasIssues).SelectMany(p => p.Issues.Prepend("Simulation " + p.State + ".")
            .Distinct().Select(issue => new ActivityCoverageDto(p.Play.AccountId, p.Play.ContractId, issue))).ToList();
        return new(operations, coverage, facts with { PaperPlays = plays });
    }

    private static ActivityOperationDto Operation(PaperPlayFacts facts)
    {
        var executions = facts.Executions.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id).ToList();
        var entries = executions.Where(e => e.Role == "entry").ToList();
        var exits = executions.Where(e => e.Role != "entry").ToList();
        var entered = Quantity(entries);
        var exited = Quantity(exits);
        var net = executions.Aggregate(default(ExactAmount), (sum, e) => sum + ExactAmount.From(e.PnlUsd));
        var outcome = facts.Outcome(DateTimeOffset.MaxValue, out var problem);
        var closed = entered > default(ExactAmount) && entered == exited;
        var incomplete = facts.HasIssues || exited > entered || closed && outcome is null;
        // Minute buckets do not establish the order of independent entries and exits. Keep peak quantity unknown
        // when their possible order could set a new peak, rather than calling cumulative entries the position size.
        var current = default(ExactAmount);
        var peak = default(ExactAmount);
        var unknownPeak = false;
        foreach (var group in executions.GroupBy(e => e.OccurredAtUtc).OrderBy(g => g.Key))
        {
            var added = Quantity(group.Where(e => e.Role == "entry"));
            var removed = Quantity(group.Where(e => e.Role != "entry"));
            if (added.Units.Sign > 0 && removed.Units.Sign > 0 && current + added > peak) unknownPeak = true;
            current += added - removed;
            if (current > peak) peak = current;
            if (current.Units.Sign < 0) incomplete = true;
        }
        var coverage = facts.Issues.Concat(incomplete && problem is not null ? [problem] : Array.Empty<string>()).Distinct().ToList();
        if (unknownPeak) coverage.Add("Peak simulated position quantity is unknown because minute-bucketed entry and exit ordering is not known.");
        return new(facts.Play.Id.ToString("D"), facts.Play.AccountId, facts.Play.ContractId ?? "",
            facts.Play.Direction, incomplete ? "incomplete" : closed ? "closed" : "open",
            entries.Count == 0 ? null : entries.Min(e => e.OccurredAtUtc), closed ? facts.ClosedAt : null,
            executions.Max(e => e.OccurredAtUtc), unknownPeak || incomplete ? null : peak.ToString(), entered.ToString(), exited.ToString(),
            (entered - exited).ToString(), Average(entries), Average(exits), 0, 0, 0,
            net.ToString(), "0", [], incomplete ? null : net.ToString(), "paper-simulation", facts.Play.Id, "paper",
            coverage, [])
        {
            TradingMode = "paper", ExecutionSource = "paper", PaperExecutions = executions.Select(ToDto).ToList(),
            PaperExecutionCount = executions.Count, GrossClosedPnlUsd = incomplete ? null : net.ToString(),
            FundingStatus = "not-applicable"
        };
    }

    private static ExactAmount Quantity(IEnumerable<PaperExecution> rows) =>
        rows.Aggregate(default(ExactAmount), (sum, e) => sum + ExactAmount.From(e.Quantity));
    private static string? Average(IReadOnlyList<PaperExecution> rows) => ExactAmount.Average(
        rows.Aggregate(BigInteger.Zero, (sum, e) => sum + ExactAmount.Product(e.Quantity, e.Price)), Quantity(rows));
    internal static PaperExecutionDto ToDto(PaperExecution e) => new(e.Id, e.EntryId, e.LevelId, e.Role,
        ExactDecimal.Format(e.Price), ExactDecimal.Format(e.Quantity), ExactDecimal.Format(e.PnlUsd),
        e.OccurredAtUtc, e.CandleOpenAtUtc, e.CandleCloseAtUtc);
}
