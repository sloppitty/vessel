using System.Globalization;
using Vessel.Application.Activity;
using Vessel.Application.Common;
using Vessel.Application.Workspace;
using Vessel.Domain.Numerics;
using Vessel.Domain.Workspace;
using Vessel.Domain.Common;

namespace Vessel.Application.Overview;

public sealed class OverviewSummaryService(IWorkspaceStore store, WorkspaceService workspace, ActivityService activity, TimeProvider time)
{
    public const int MaxGroups = 200;
    public const string ScopeNote = "Enabled owner accounts only, including Unassigned. Nominal USD, no FX/depeg valuation. " +
        NominalStablecoins.Caveat + " Venues with different quote stablecoins are pooled nominally. " +
        "Balance and availability use the workspace's reported ledgers, never inferred funds. Positions and unrealized P&L use latest retained snapshots, not live prices. " +
        "Realized result uses closed retained position operations and actual exit times, not Plays; incomplete boundaries and unknown net results are excluded. " +
        "No closed result is unknown, not zero. Retained imports do not prove complete history. Listed stablecoin gross fees are deducted once; separate funding is not added and reported funding inclusion is unverified. " +
        "Shares are exact ratios of known balance subtotals, not claims of complete valuation. At most 200 groups; totals include omitted groups.";

    public async Task<OverviewSummaryDto> GetAsync(string? groupBy, string? period, string? scope, CancellationToken ct,
        string? tradingMode = "real")
    {
        var mode = TradingMode.Validate(tradingMode ?? TradingMode.Real);
        groupBy ??= "portfolio"; period ??= "30"; scope ??= "all";
        if (groupBy is not ("portfolio" or "account")) throw new ProblemException(400, "Group by portfolio or account.");
        if (period is not ("7" or "30" or "90" or "all")) throw new ProblemException(400, "Choose 7, 30, 90 days or all retained history.");
        var listScope = ListScope.Parse(scope);
        var asOf = time.GetUtcNow();
        DateTimeOffset? from = period == "all" ? null : asOf.AddDays(-int.Parse(period, CultureInfo.InvariantCulture));
        var portfolios = await store.PortfoliosAsync(ct, mode);
        var accounts = await workspace.AccountsAsync(ct, mode);
        listScope.RequireKnown(id => portfolios.Any(p => p.Id == id), id => accounts.Any(a => a.Id == id));
        var members = accounts.Where(a => listScope.Includes(a.Id, a.PortfolioId, a.IsEnabled)).ToList();
        var summaries = await activity.ReadSummariesAsync(null, ct, members.Select(a => a.Id).ToHashSet(), mode);
        var snapshots = summaries.Facts.Snapshots.ToDictionary(s => s.AccountId);
        var operations = summaries.Operations.GroupBy(o => o.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        var fills = summaries.Facts.Fills.GroupBy(f => f.AccountId).ToDictionary(g => g.Key, g => g.Count());
        var history = summaries.Coverage.GroupBy(c => c.AccountId).ToDictionary(g => g.Key, g => g.Count());

        OverviewSummaryTotals Totals(IReadOnlyList<AccountDto> selected)
        {
            SummaryAmount Sum(Func<AccountDto, string?> value, string reason)
            {
                var known = selected.Select(value).OfType<string>().ToList();
                return new(known.Count == 0 ? null : known.Aggregate(default(ExactAmount), (sum, v) => sum + ExactAmount.Parse(v)).ToString(),
                    new(known.Count, selected.Count, 0, 0, known.Count == selected.Count ? [] : [new(reason, selected.Count - known.Count)]));
            }
            var candidates = selected.SelectMany(a => operations.GetValueOrDefault(a.Id) ?? [])
                .Where(o => (o.Status == "closed" || o.Status == "incomplete") &&
                    (o.ClosedAtUtc ?? o.LastOccurredAtUtc) <= asOf && (from is null || (o.ClosedAtUtc ?? o.LastOccurredAtUtc) >= from)).ToList();
            var known = candidates.Where(o => o.Status == "closed" && o.ClosedAtUtc is not null && o.NetResultUsd is not null).ToList();
            var contributing = known.Select(o => o.AccountId).Distinct().Count();
            var reasons = new List<SummaryReason>();
            var incomplete = candidates.Count(o => o.Status == "incomplete");
            var unknown = candidates.Count(o => o.Status == "closed" && (o.NetResultUsd is null || o.ClosedAtUtc is null));
            if (incomplete > 0) reasons.Add(new(mode == TradingMode.Paper ? "Unresolved or incomplete simulated outcomes; excluded, never guessed." :
                "Incomplete operation boundaries; excluded using latest execution time when exit is unknown.", incomplete));
            if (unknown > 0) reasons.Add(new("Closed operation net result or exit time unavailable.", unknown));
            if (contributing < selected.Count) reasons.Add(new("Accounts without a known closed result in this period.", selected.Count - contributing));
            var limitedHistory = selected.Count(a => a.HistoryNotice is not null || history.ContainsKey(a.Id));
            if (limitedHistory > 0) reasons.Add(new(mode == TradingMode.Paper ? "Accounts with unresolved simulation coverage." :
                "Accounts with retained-history or snapshot comparison limitations.", limitedHistory));
            var realized = new SummaryAmount(known.Count == 0 ? null : known.Aggregate(default(ExactAmount),
                (sum, o) => sum + ExactAmount.Parse(o.NetResultUsd!)).ToString(), new(contributing, selected.Count, known.Count,
                    candidates.Count - known.Count, reasons));
            return new(selected.Count, Sum(a => a.BalanceUsd, mode == TradingMode.Paper ? "Virtual cash unavailable." : "Recorded balance unavailable."),
                Sum(a => a.AvailableBalanceUsd, mode == TradingMode.Paper ? "Virtual availability is unknown while capital remains unresolved." :
                    "Reported availability unavailable; manual values are not available funds."),
                selected.Sum(a => mode == TradingMode.Paper
                    ? a.PositionCount
                    : snapshots.GetValueOrDefault(a.Id)?.Positions.Count(p => p.SignedQuantity != 0) ?? 0),
                Sum(a => mode == TradingMode.Paper
                    ? a.AccountValueUsd is { } equity && a.BalanceUsd is { } cash
                        ? (ExactAmount.Parse(equity) - ExactAmount.Parse(cash)).ToString() : null
                    : snapshots.TryGetValue(a.Id, out var snapshot)
                    ? ExactDecimal.Sum(snapshot.Positions.Where(p => p.SignedQuantity != 0).Select(p => p.UnrealizedPnlUsd)) : null,
                    mode == TradingMode.Paper ? "Current marks unavailable; simulated unrealized P&L unknown." :
                    "Latest perpetual snapshot unavailable; unrealized P&L unknown."), realized,
                selected.Sum(a => fills.GetValueOrDefault(a.Id)),
                selected.Sum(a => (operations.GetValueOrDefault(a.Id) ?? []).Sum(o => o.PaperExecutionCount)));
        }
        var totals = Totals(members);
        var byPortfolio = portfolios.ToDictionary(p => p.Id);
        var rows = members.GroupBy(a => groupBy == "account" ? $"account:{a.Id}" : a.PortfolioId is { } id ? $"portfolio:{id}" : "unassigned")
            .Select(g =>
            {
                var first = g.First();
                var portfolio = first.PortfolioId is { } id ? byPortfolio.GetValueOrDefault(id) : null;
                var row = Totals(g.ToList());
                var share = row.Balance.AmountUsd is { } numerator && totals.Balance.AmountUsd is { } denominator &&
                    ExactAmount.Parse(denominator).Units.Sign > 0 ? new BalanceShare(numerator, denominator) : null;
                return new OverviewSummaryRow(g.Key, groupBy == "account" ? first.Name : portfolio?.Name ?? "Unassigned",
                    groupBy == "portfolio" ? portfolio?.Icon ?? PortfolioIcons.Default : null,
                    groupBy == "account" ? first.VenueId : null, row, share);
            }).OrderBy(r => r.Totals.Balance.AmountUsd is null)
            .ThenByDescending(r => r.Totals.Balance.AmountUsd is { } amount ? ExactAmount.Parse(amount) : default)
            .ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        return new(groupBy, period, scope, asOf, from, totals, rows.Take(MaxGroups).ToList(), rows.Count,
            Math.Max(0, rows.Count - MaxGroups), mode == TradingMode.Paper ? PaperActivity.Notice +
                " Virtual cash includes immutable starting capital; realized results never include the seed. Missing marks leave equity and unrealized P&L unknown."
                : ScopeNote, mode);
    }
}
