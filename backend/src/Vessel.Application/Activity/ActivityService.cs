using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel.Application.Workspace;
using Vessel.Application.Common;
using Vessel.Application.Venues;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Activity;

public sealed class ActivityService(IActivityStore store, IWorkspaceStore workspace, TimeProvider time, IVenueRegistry? venues = null,
    IActivitySummaryReader? summaries = null)
{
    /// <summary>Retrospective preview limit; whole-order attribution beyond it is refused.</summary>
    public const int MaxFillsPerOperation = 200;
    public const int MaxExpandedFills = 1_000, MaxOperationIdLength = 200;
    public const string ScopeNote = "Position round trips from all retained fills on enabled accounts, not trading intent or retrospective Plays. " +
        "Retained history is bounded by venue imports; balanced fills do not prove lifetime coverage. Newest activity first. " +
        "Quantities, sums and flip allocations are exact. Weighted prices round only when necessary to 28 decimal places, halfway away from zero. " +
        NominalStablecoins.Caveat + " Net result subtracts listed stablecoin fees on gross fills; other fee tokens leave it unknown. Net-of-fee P&L is not charged twice. " +
        "Settled funding is attributed only inside an unambiguous native-contract position interval. Positive funding is received; negative is paid. " +
        "Execution net preserves the existing fee-adjusted reported P&L, not a claim of funding-free P&L. " +
        "Combined total adds funding only with verified coverage and a funding-exclusive execution P&L basis; funding-inclusive P&L is never charged twice. " +
        "Unverified inclusion or missing funding is not zero. Open-operation funding is only through the latest retained refresh. " +
        "Play Review and sizing remain execution-only. " +
        "Leverage is the venue-reported position leverage observed during an operation, on its executions or in refresh snapshots; " +
        "a range means it changed. It is never inferred, and older or unobserved operations show it as unknown.";

    /// <summary>
    /// Aggregates every retained fill of the scoped enabled accounts, then filters, orders and pages the operations.
    /// Metrics, coverage, facets and the version never depend on the filters or the page.
    /// </summary>
    public async Task<ActivityPageDto> PageAsync(ActivityQuery query, CancellationToken ct)
    {
        var mode = TradingMode.Validate(query.TradingMode ?? TradingMode.Real);
        var (page, pageSize) = ListPaging.Parse(query.Page, query.PageSize);
        var scope = ListScope.Parse(query.Scope);
        if (query.AccountId == Guid.Empty) throw new ProblemException(400, "Choose a valid account.");
        var asOf = time.GetUtcNow();
        var filter = ActivityFilter.Parse(query, asOf);
        var accounts = await workspace.AccountsAsync(ct, mode);
        var portfolios = scope.Kind == "portfolio" ? await workspace.PortfoliosAsync(ct, mode) : [];
        scope.RequireKnown(id => portfolios.Any(p => p.Id == id), id => accounts.Any(a => a.Id == id));
        if (query.AccountId is { } detail && !accounts.Any(a => a.Id == detail)) throw new ProblemException(404, "Account not found.");
        var members = accounts.Where(a => scope.Includes(a.Id, a.PortfolioId, a.IsEnabled) && (query.AccountId is null || a.Id == query.AccountId))
            .Select(a => a.Id).ToHashSet();
        var summary = await ReadSummariesAsync(null, ct, members, mode);
        var portfolioOf = summary.Facts.Accounts.ToDictionary(a => a.Id, a => a.PortfolioId);
        var scoped = summary.Operations.OrderByDescending(o => o.LastOccurredAtUtc)
            .ThenBy(o => o.AccountId.ToString("D"), StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        var filtered = scoped.Where(o => filter.Matches(o, portfolioOf.GetValueOrDefault(o.AccountId))).ToList();
        page = ListPaging.Clamp(page, pageSize, filtered.Count);
        var rows = filtered.Skip(ListPaging.Skip(page, pageSize)).Take(pageSize).Select(ActivityOperationSummaryDto.From).ToList();
        // Titles are read for the shown rows only, after the cached aggregate, so a renamed Play shows its current title.
        var linked = rows.Where(r => r.PlayId is not null).Select(r => r.PlayId!.Value).Distinct().ToList();
        if (linked.Count > 0)
        {
            var titles = await store.PlayTitlesAsync(linked, ct);
            rows = rows.Select(r => r.PlayId is { } id && titles.TryGetValue(id, out var title) ? r with { PlayTitle = title } : r).ToList();
        }
        return new(rows,
            page, pageSize, filtered.Count, scoped.Count, summary.Facts.Fills.Count, asOf, summary.Version,
            new(scoped.Select(o => o.AccountId).Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList(),
                scoped.Select(o => o.ContractId).Distinct().Order(StringComparer.Ordinal).ToList()),
            summary.Coverage, mode == TradingMode.Paper ? PaperActivity.Notice : ScopeNote)
        {
            TradingMode = mode == TradingMode.Paper ? mode : null, PaperExecutionCount = scoped.Sum(o => o.PaperExecutionCount),
            UnassignedFundingPayments = summary.UnassignedFunding.OrderByDescending(p => p.OccurredAtUtc)
                .ThenBy(p => p.SourceId, StringComparer.Ordinal).Take(100).Select(OperationFunding.ToDto).ToList(),
            OmittedUnassignedFundingPaymentCount = Math.Max(0, summary.UnassignedFunding.Count - 100)
        };
    }

    /// <summary>One operation's constituent rows, from that account only. Changed history is 404.</summary>
    public async Task<OperationFillsDto> FillsAsync(Guid? accountId, string? operationId, CancellationToken ct, string? tradingMode = "real")
    {
        var mode = TradingMode.Validate(tradingMode ?? TradingMode.Real);
        if (accountId is null || accountId == Guid.Empty || string.IsNullOrWhiteSpace(operationId) || operationId.Length > MaxOperationIdLength)
            throw new ProblemException(400, "Choose a valid account and retained operation.");
        // Missing and foreign accounts are 404 in the store; a disabled account has no visible operations.
        var summary = await ReadSummariesAsync(accountId, ct, tradingMode: mode);
        var operation = summary.Operations.SingleOrDefault(o => o.Id == operationId)
            ?? throw new ProblemException(404, "Retained operation not found. Reload Activity.");
        var shown = operation.Fills.Take(MaxExpandedFills).ToList();
        return new(operation.Id, shown, operation.Fills.Count - shown.Count)
        {
            TradingMode = mode == TradingMode.Paper ? mode : null,
            PaperExecutions = mode == TradingMode.Paper ? operation.PaperExecutions.Take(MaxExpandedFills).ToList() : null,
            OmittedPaperExecutionCount = Math.Max(0, operation.PaperExecutions.Count - MaxExpandedFills),
            FundingPayments = operation.FundingPayments.Take(MaxExpandedFills).Select(OperationFunding.ToDto).ToList(),
            OmittedFundingPaymentCount = Math.Max(0, operation.FundingPayments.Count - MaxExpandedFills)
        };
    }

    // Include every displayed operation metric and coverage notice, plus retained funding details. Summary projection
    // keeps constituent execution DTOs lazy; hash rows incrementally rather than buffering the complete payload.
    private static string Version(IEnumerable<ActivityOperationDto> operations, ActivityFacts facts, IEnumerable<ActivityCoverageDto> coverage)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append<T>(string section, IEnumerable<T> rows)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(section));
            foreach (var row in rows) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
        }
        Append("operations", operations.Select(ActivityOperationSummaryDto.From));
        Append("paper-executions", operations.Where(o => o.TradingMode == TradingMode.Paper).SelectMany(o => o.PaperExecutions));
        Append("coverage", coverage.OrderBy(c => c.AccountId).ThenBy(c => c.ContractId, StringComparer.Ordinal)
            .ThenBy(c => c.Notice, StringComparer.Ordinal));
        Append("funding-payments", facts.FundingPayments.OrderBy(p => p.Id).Select(OperationFunding.ToDto));
        Append("funding-states", facts.FundingStates.OrderBy(s => s.AccountId).Select(s => new
        {
            s.AccountId, s.LastAttemptToUtc, s.LastReadComplete, s.PnlBasis, s.Notice
        }));
        // Phase 2, item 7b: markers change the operations they break, and a newly seen one changes the page.
        Append("omissions", facts.Omissions.OrderBy(m => m.Id).Select(m => new { m.Id, m.OccurredAtUtc, m.UncertainFromUtc, m.UncertainToUtc, m.OrderId }));
        return Convert.ToHexStringLower(hash.GetHashAndReset(), 0, 16);
    }

    private const int MaxForcedRows = 50;

    /// <summary>One venue-forced execution, its facts as reported and why it is excluded; unknown money reads as unknown.</summary>
    private static string ForcedText(ForcedEvent e)
    {
        var kind = e.Kind == ForcedEvent.Settlement ? "Market settlement" : e.Kind == ForcedEvent.Deleverage ? "Deleverage" : "Liquidation";
        var price = e.Price is { } p ? $" at {ExactAmount.From(p)}" : ", price not reported";
        var pnl = e.RealizedPnl is { } r ? ExactAmount.From(r).ToString() : "unknown";
        var fee = e.Fee is { } f ? ExactAmount.From(f).ToString() : "unknown";
        return string.Create(CultureInfo.InvariantCulture,
            $"{kind} on {e.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC: {e.Side} {ExactAmount.From(e.Quantity)}{price}; realized P&L {pnl}, fee {fee}. ") +
            "Excluded: venue-forced, kept beside the fills, never linked or scored.";
    }

    // Overview, Activity pages and the fills endpoint share these unbounded aggregate results; constituent DTOs
    // are only built for the rows a caller returns.
    internal async Task<ActivitySummaries> ReadSummariesAsync(Guid? accountId, CancellationToken ct, IReadOnlySet<Guid>? accountIds = null,
        string tradingMode = "real")
    {
        tradingMode = TradingMode.Validate(tradingMode);
        if (accountId == Guid.Empty) throw new ProblemException(400, "Choose a valid account.");
        if (accountIds is { Count: 0 }) return new([], [], new([], [], [], new Dictionary<Guid, Guid>()));
        if (summaries is not null) return await summaries.ReadAsync(accountId, tradingMode, ct, accountIds);
        return Aggregate(await store.ReadAsync(accountId, tradingMode, ct, accountIds), accountId, accountIds, venues, tradingMode);
    }

    public static ActivitySummaries Aggregate(ActivityFacts facts, Guid? accountId = null,
        IReadOnlySet<Guid>? accountIds = null, IVenueRegistry? venues = null, string tradingMode = "real")
    {
        tradingMode = TradingMode.Validate(tradingMode);
        var accounts = facts.Accounts.Where(a => a.TradingMode == tradingMode && a.IsEnabled && (accountId == null || a.Id == accountId) &&
            (accountIds == null || accountIds.Contains(a.Id))).ToDictionary(a => a.Id);
        if (tradingMode == TradingMode.Paper)
        {
            var paper = PaperActivity.Aggregate(new(accounts.Values.ToList(), [], [], new Dictionary<Guid, Guid>())
            { PaperPlays = facts.PaperPlays });
            return paper with { Version = Version(paper.Operations.OrderBy(o => o.Id), paper.Facts, paper.Coverage) };
        }
        var fills = facts.Fills.Where(f => accounts.ContainsKey(f.AccountId)).ToList();
        var omissions = facts.Omissions.Where(m => accounts.ContainsKey(m.AccountId)).ToList();
        var forced = facts.ForcedEvents.Where(e => accounts.ContainsKey(e.AccountId)).OrderByDescending(e => e.OccurredAtUtc)
            .ThenBy(e => e.Id).ToList();
        var positions = new ActivityPositions(fills, facts.Snapshots, facts.FundingPayments.Where(p => accounts.ContainsKey(p.AccountId)).ToList(),
            omissions);
        var operations = OperationLeverage.Apply(OperationAggregator.AggregateSummaries(fills, facts.FillPlayIds, positions),
            facts with { LeverageObservations = facts.LeverageObservations.Where(o => accounts.ContainsKey(o.AccountId)).ToList() },
            positions);
        var lastByContract = new Dictionary<ActivityContractKey, int>();
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (!lastByContract.TryGetValue(operation.ContractKey, out var previous) ||
                operation.LastOccurredAtUtc > operations[previous].LastOccurredAtUtc ||
                operation.LastOccurredAtUtc == operations[previous].LastOccurredAtUtc && operations[previous].OpenQuantity == "0" && operation.OpenQuantity != "0")
                lastByContract[operation.ContractKey] = i;
        }
        var contractsByAccount = positions.Contracts.GroupBy(p => p.Key.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        var coverage = new List<ActivityCoverageDto>();
        foreach (var account in accounts.Values)
        {
            if (account.HistoryNotice is { Length: > 0 } notice) coverage.Add(new(account.Id, null, notice));
            var snapshot = positions.Snapshots.GetValueOrDefault(account.Id);
            if (snapshot is null) coverage.Add(new(account.Id, null, "No latest position snapshot is available for comparison."));
            foreach (var (key, contract) in contractsByAccount.GetValueOrDefault(account.Id) ?? [])
            {
                // Same-time close/open operations use remaining position, not UUID display ordering.
                var hasLast = lastByContract.TryGetValue(key, out var index);
                var last = hasLast ? operations[index] : null;
                var observed = positions.Quantity(key);
                if (snapshot is null && observed is not null) continue;
                string? reason = observed is null ? ActivityPositions.AmbiguousIdentityNotice :
                    last?.LastOccurredAtUtc > snapshot!.ObservedAtUtc ? "Latest snapshot predates retained fills; position comparison is unavailable." :
                    last is not null && last.OpenQuantity is null ? "Computed position is unknown because retained history is incomplete; snapshot comparison is unavailable." : null;
                if (reason is null)
                {
                    var computedText = last?.OpenQuantity ?? "0";
                    // Compare exact strings after normalizing through the aggregator's own signed position.
                    var signed = last?.Direction == "short" && computedText != "0" ? "-" + computedText : computedText;
                    if (observed!.Value.ToString() != signed)
                        reason = $"Latest snapshot position {observed} disagrees with retained-fill position {signed}. Neither is corrected.";
                }
                if (reason is not null)
                {
                    coverage.Add(new(account.Id, contract, reason));
                    if (last is not null && !last.Coverage.Contains(reason))
                    {
                        operations[index] = last with { Coverage = last.Coverage.Append(reason).ToList() };
                    }
                }
            }
        }
        // Phase 2, item 7b: each venue-forced execution is a visible row with its exclusion, at most 50 with a count.
        foreach (var e in forced.Take(MaxForcedRows))
            coverage.Add(new(e.AccountId, e.ContractId, ForcedText(e)));
        foreach (var group in forced.Skip(MaxForcedRows).GroupBy(e => e.AccountId))
            coverage.Add(new(group.Key, null, $"{group.Count()} more venue-forced executions are retained and excluded."));
        facts = facts with
        {
            Accounts = accounts.Values.ToList(), Fills = fills,
            Omissions = omissions, ForcedEvents = forced,
            PaperPlays = [],
            FundingPayments = facts.FundingPayments.Where(p => accounts.ContainsKey(p.AccountId)).ToList(),
            FundingStates = facts.FundingStates.Where(s => accounts.ContainsKey(s.AccountId)).ToList()
        };
        var fundingAccounts = facts.FundingStates.Select(s => s.AccountId)
            .Concat(facts.FundingPayments.Select(p => p.AccountId))
            .Concat(accounts.Values.Where(a => venues?.Funding(a.VenueId) is not null).Select(a => a.Id)).ToHashSet();
        var funding = OperationFunding.Apply(operations, facts, positions, fundingAccounts);
        foreach (var state in facts.FundingStates)
            coverage.Add(new(state.AccountId, null, state.Notice));
        foreach (var group in funding.Unassigned.GroupBy(p => p.AccountId))
            coverage.Add(new(group.Key, null, $"{group.Count()} retained funding settlements could not be attributed safely to an operation. They are not added to operation results."));
        var ordered = funding.Operations.OrderByDescending(o => o.LastOccurredAtUtc)
            .ThenBy(o => o.AccountId.ToString("D"), StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        return new(funding.Operations, coverage, facts) { UnassignedFunding = funding.Unassigned, Version = Version(ordered, facts, coverage) };
    }
}

public sealed record ActivitySummaries(IReadOnlyList<ActivityOperationDto> Operations,
    IReadOnlyList<ActivityCoverageDto> Coverage, ActivityFacts Facts)
{
    public string Version { get; init; } = Convert.ToHexStringLower(SHA256.HashData([]), 0, 16);
    public IReadOnlyList<Vessel.Domain.Workspace.FundingPayment> UnassignedFunding { get; init; } = [];
}
