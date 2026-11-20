using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vessel.Application.Workspace;
using Vessel.Application.Common;
using Vessel.Application.MarketData;
using Vessel.Application.Venues;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Workspace;
using Vessel.Domain.Numerics;

namespace Vessel.Application.Activity;

public sealed class ActivityService(IActivityStore store, IWorkspaceStore workspace, TimeProvider time, IVenueRegistry? venues = null,
    IActivitySummaryReader? summaries = null, MarketContextCache? marks = null, IActivityPageReader? pages = null)
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
        var mode = TradingModes.FromRequest(query.TradingMode);
        var (page, pageSize) = ListPaging.Parse(query.Page, query.PageSize);
        var scope = ListScope.Parse(query.Scope);
        if (query.AccountId == Guid.Empty) throw new ProblemException(400, "Choose a valid account.");
        var asOf = time.GetUtcNow();
        var filter = ActivityFilter.Parse(query, asOf);
        var accounts = await workspace.AccountsAsync(mode, ct);
        var portfolios = scope.Kind == "portfolio" ? await workspace.PortfoliosAsync(mode, ct) : [];
        scope.RequireKnown(id => portfolios.Any(p => p.Id == id), id => accounts.Any(a => a.Id == id));
        if (query.AccountId is { } detail && !accounts.Any(a => a.Id == detail)) throw new ProblemException(404, "Account not found.");
        var members = accounts.Where(a => scope.Includes(a.Id, a.PortfolioId, a.IsEnabled) && (query.AccountId is null || a.Id == query.AccountId))
            .Select(a => a.Id).ToHashSet();
        // Issue #108: real Activity pages, filters and counts come from the per-account read model in SQL when it is enabled.
        if (pages is not null && mode == TradingMode.Real && members.Count > 0 &&
            await pages.PageAsync(members, filter, page, pageSize, ct) is { } projected)
        {
            var shown = await OperationUnrealized.ApplyAsync(projected.Rows, projected.Observed, venues, marks, ct);
            return new(await TitledAsync(shown, ct), projected.Page, pageSize, projected.TotalCount, projected.ScopeCount,
                projected.RetainedFillCount, asOf, projected.Version, projected.Facets, projected.Coverage, ScopeNote)
            {
                UnassignedFundingPayments = UnassignedPage(projected.UnassignedFunding),
                OmittedUnassignedFundingPaymentCount = Math.Max(0, projected.UnassignedFunding.Count - 100)
            };
        }
        var summary = await ReadSummariesAsync(null, mode, ct, members);
        var portfolioOf = summary.Facts.Accounts.ToDictionary(a => a.Id, a => a.PortfolioId);
        // #107: the cached aggregate keeps its operations in page order and its facets, so a hit sorts nothing.
        var scoped = summary.Ordered;
        var filtered = scoped.Where(o => filter.Matches(o, portfolioOf.GetValueOrDefault(o.AccountId))).ToList();
        page = ListPaging.Clamp(page, pageSize, filtered.Count);
        // Marks are read for the shown open rows only, after the cached aggregate, so they never change the version.
        var rows = await TitledAsync(await OperationUnrealized.ApplyAsync(filtered.Skip(ListPaging.Skip(page, pageSize)).Take(pageSize).ToList(),
            summary.Facts, venues, marks, ct), ct);
        return new(rows,
            page, pageSize, filtered.Count, scoped.Count, summary.Facts.Fills.Count, asOf, summary.Version, summary.Facets,
            summary.Coverage, mode == TradingMode.Paper ? PaperActivity.Notice : ScopeNote)
        {
            TradingMode = mode == TradingMode.Paper ? mode : null, PaperExecutionCount = scoped.Sum(o => o.PaperExecutionCount),
            UnassignedFundingPayments = UnassignedPage(summary.UnassignedFunding.Select(OperationFunding.ToDto)),
            OmittedUnassignedFundingPaymentCount = Math.Max(0, summary.UnassignedFunding.Count - 100)
        };
    }

    private static List<OperationFundingDto> UnassignedPage(IEnumerable<OperationFundingDto> payments) =>
        payments.OrderByDescending(p => p.OccurredAtUtc).ThenBy(p => p.SourceId, StringComparer.Ordinal).ThenBy(p => p.Id).Take(100).ToList();

    // Titles are read for the shown rows only, after the aggregate, so a renamed Play shows its current title.
    private async Task<List<ActivityOperationSummaryDto>> TitledAsync(List<ActivityOperationSummaryDto> rows, CancellationToken ct)
    {
        var linked = rows.Where(r => r.PlayId is not null).Select(r => r.PlayId!.Value).Distinct().ToList();
        if (linked.Count == 0) return rows;
        var titles = await store.PlayTitlesAsync(linked, ct);
        return rows.Select(r => r.PlayId is { } id && titles.TryGetValue(id, out var title) ? r with { PlayTitle = title } : r).ToList();
    }

    /// <summary>One operation's constituent rows, from that account only. Changed history is 404.</summary>
    public async Task<OperationFillsDto> FillsAsync(Guid? accountId, string? operationId, TradingMode tradingMode, CancellationToken ct)
    {
        var mode = TradingModes.Validate(tradingMode);
        if (accountId is null || accountId == Guid.Empty || string.IsNullOrWhiteSpace(operationId) || operationId.Length > MaxOperationIdLength)
            throw new ProblemException(400, "Choose a valid account and retained operation.");
        // Missing and foreign accounts are 404 in the store; a disabled account has no visible operations.
        var summary = await ReadSummariesAsync(accountId, mode, ct);
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
        Append(hash, "operations", operations.Select(ActivityOperationSummaryDto.From));
        Append(hash, "paper-executions", operations.Where(o => o.TradingMode == TradingMode.Paper).SelectMany(o => o.PaperExecutions));
        return VersionTail(hash, coverage, facts.FundingPayments, facts.FundingStates, facts.Omissions);
    }

    private static void Append<T>(IncrementalHash hash, string section, IEnumerable<T> rows)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(section));
        foreach (var row in rows) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
    }

    /// <summary>
    /// Issue #108: the same version from operation list rows already serialized as <see cref="ActivityOperationSummaryDto"/>
    /// JSON, in page order, as the read model stores them. Real operations only, so the paper section is empty.
    /// </summary>
    internal static async Task<string> VersionAsync(IAsyncEnumerable<string> operationJson, IEnumerable<ActivityCoverageDto> coverage,
        IEnumerable<FundingPayment> funding, IEnumerable<AccountFundingState> states, IEnumerable<ActivityOmission> omissions,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("operations"));
        await foreach (var json in operationJson.WithCancellation(ct)) hash.AppendData(Encoding.UTF8.GetBytes(json));
        hash.AppendData(Encoding.UTF8.GetBytes("paper-executions"));
        return VersionTail(hash, coverage, funding, states, omissions);
    }

    private static string VersionTail(IncrementalHash hash, IEnumerable<ActivityCoverageDto> coverage, IEnumerable<FundingPayment> funding,
        IEnumerable<AccountFundingState> states, IEnumerable<ActivityOmission> omissions)
    {
        Append(hash, "coverage", coverage.OrderBy(c => c.AccountId).ThenBy(c => c.ContractId, StringComparer.Ordinal)
            .ThenBy(c => c.Notice, StringComparer.Ordinal));
        Append(hash, "funding-payments", funding.OrderBy(p => p.Id).Select(OperationFunding.ToDto));
        Append(hash, "funding-states", states.OrderBy(s => s.AccountId).Select(s => new
        {
            s.AccountId, s.LastAttemptToUtc, s.LastReadComplete, s.PnlBasis, s.Notice
        }));
        // Phase 2, item 7b: markers change the operations they break, and a newly seen one changes the page.
        Append(hash, "omissions", omissions.OrderBy(m => m.Id).Select(m => new
        {
            m.Id, m.ContractId, m.VenueContractId, m.OccurredAtUtc, m.UncertainFromUtc, m.UncertainToUtc
        }));
        return Convert.ToHexStringLower(hash.GetHashAndReset(), 0, 16);
    }

    public const string IncompleteSnapshotNotice = "The latest position read was incomplete; positions are unknown and snapshot comparison is unavailable.";

    /// <summary>Phase 2, item 7b: forced events shown as rows; the rest of each account's are counted.</summary>
    public const int MaxForcedEventRows = 50;

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
    internal async Task<ActivitySummaries> ReadSummariesAsync(Guid? accountId, TradingMode tradingMode, CancellationToken ct,
        IReadOnlySet<Guid>? accountIds = null)
    {
        tradingMode = TradingModes.Validate(tradingMode);
        if (accountId == Guid.Empty) throw new ProblemException(400, "Choose a valid account.");
        if (accountIds is { Count: 0 }) return new([], [], new([], [], [], new Dictionary<Guid, Guid>()));
        if (summaries is not null) return await summaries.ReadAsync(accountId, tradingMode, ct, accountIds);
        return Aggregate(await store.ReadAsync(accountId, tradingMode, ct, accountIds), tradingMode, accountId, accountIds, venues);
    }

    public static ActivitySummaries Aggregate(ActivityFacts facts, TradingMode tradingMode, Guid? accountId = null,
        IReadOnlySet<Guid>? accountIds = null, IVenueRegistry? venues = null)
    {
        tradingMode = TradingModes.Validate(tradingMode);
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
        var (forced, forcedCounts) = Forced(facts.ForcedEvents, facts.ForcedEventCounts, accounts.ContainsKey);
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
        // Issue #108: each account's partitions in a fixed order, not the unordered fill read's, so one account's own aggregate
        // lists its notices as the aggregate over several accounts does. The version sorts coverage and is unaffected.
        var contractsByAccount = positions.Contracts.GroupBy(p => p.Key.AccountId).ToDictionary(g => g.Key,
            g => g.OrderBy(p => p.Key.Identity, StringComparer.Ordinal).ThenBy(p => p.Key.IsNative).ToList());
        var coverage = new List<ActivityCoverageDto>();
        foreach (var account in accounts.Values)
        {
            if (account.HistoryNotice is { Length: > 0 } notice) coverage.Add(new(account.Id, null, notice));
            var snapshot = positions.Snapshots.GetValueOrDefault(account.Id);
            if (snapshot is null) coverage.Add(new(account.Id, null, "No latest position snapshot is available for comparison."));
            else if (!snapshot.PositionsComplete) coverage.Add(new(account.Id, null, IncompleteSnapshotNotice));
            foreach (var (key, contract) in contractsByAccount.GetValueOrDefault(account.Id) ?? [])
            {
                // Same-time close/open operations use remaining position, not UUID display ordering.
                var hasLast = lastByContract.TryGetValue(key, out var index);
                var last = hasLast ? operations[index] : null;
                var observed = positions.Quantity(key);
                // #89: a partial read compares nothing; its absent positions are unknown, not flat.
                if ((snapshot is null || !snapshot.PositionsComplete) && observed is not null) continue;
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
        var accountCoverage = coverage.Count;
        facts = facts with
        {
            Accounts = accounts.Values.ToList(), Fills = fills,
            Omissions = omissions, ForcedEvents = forced, ForcedEventCounts = forcedCounts,
            PaperPlays = [],
            FundingPayments = facts.FundingPayments.Where(p => accounts.ContainsKey(p.AccountId)).ToList(),
            FundingStates = facts.FundingStates.Where(s => accounts.ContainsKey(s.AccountId)).ToList()
        };
        var fundingAccounts = facts.FundingStates.Select(s => s.AccountId)
            .Concat(facts.FundingPayments.Select(p => p.AccountId))
            .Concat(accounts.Values.Where(a => venues?.Funding(a.VenueId) is not null).Select(a => a.Id)).ToHashSet();
        var funding = OperationFunding.Apply(operations, facts, positions, fundingAccounts);
        var unassigned = funding.Unassigned.GroupBy(p => p.AccountId).ToDictionary(g => g.Key, g => g.Count());
        coverage.AddRange(CoverageTail(forced, forcedCounts, facts.FundingStates,
            accounts.Keys.Where(unassigned.ContainsKey).Select(id => (id, unassigned[id]))));
        var ordered = ActivitySummaries.PageOrder(funding.Operations);
        return new(funding.Operations, coverage, facts)
        {
            UnassignedFunding = funding.Unassigned, Version = Version(ordered, facts, coverage), Positions = positions, Ordered = ordered,
            AccountCoverageCount = accountCoverage
        };
    }

    /// <summary>
    /// Phase 2, item 7b: the newest forced events shown as rows, and each scoped account's retained count. Sol round 1 on 7b:
    /// the store reads only the newest rows and counts the rest; in-memory facts may hold them all.
    /// </summary>
    internal static (List<ForcedEvent> Rows, Dictionary<Guid, int> Counts) Forced(IReadOnlyList<ForcedEvent> events,
        IReadOnlyDictionary<Guid, int>? counts, Func<Guid, bool> scoped)
    {
        var rows = events.Where(e => scoped(e.AccountId)).OrderByDescending(e => e.OccurredAtUtc).ThenBy(e => e.Id).Take(MaxForcedEventRows).ToList();
        var all = (counts?.Select(c => (Account: c.Key, Count: c.Value))
                ?? events.GroupBy(e => e.AccountId).Select(g => (Account: g.Key, Count: g.Count())))
            .Where(c => scoped(c.Account)).ToDictionary(c => c.Account, c => c.Count);
        return (rows, all);
    }

    /// <summary>
    /// The coverage that follows each account's own notices: forced events, funding coverage and unattributed funding.
    /// Issue #108: unattributed funding is listed in account order (it followed the funding read's order before), so the
    /// read model assembles the same list from per-account parts. The version sorts coverage, so it is unaffected.
    /// </summary>
    internal static IEnumerable<ActivityCoverageDto> CoverageTail(IReadOnlyList<ForcedEvent> forced, IReadOnlyDictionary<Guid, int> forcedCounts,
        IEnumerable<AccountFundingState> states, IEnumerable<(Guid AccountId, int Count)> unassigned)
    {
        // Phase 2, item 7b: each venue-forced execution is a visible row with its exclusion, at most 50 with a count.
        foreach (var e in forced)
            yield return new(e.AccountId, e.ContractId, ForcedText(e));
        foreach (var (account, count) in forcedCounts.OrderBy(c => c.Key.ToString("D"), StringComparer.Ordinal))
            if (count - forced.Count(e => e.AccountId == account) is > 0 and var more)
                yield return new(account, null, $"{more} more venue-forced executions are retained and excluded.");
        foreach (var state in states)
            yield return new(state.AccountId, null, state.Notice);
        foreach (var (account, count) in unassigned)
            yield return new(account, null, $"{count} retained funding settlements could not be attributed safely to an operation. They are not added to operation results.");
    }

    /// <summary>
    /// Issue #107: a real aggregate moved onto the observations read at <paramref name="revision"/>. The execution facts are
    /// the cached ones (the caller read the same execution revision). When every observation the walk, the coverage, the
    /// funding attribution and the leverage read is unchanged, or only a snapshot's time moved without passing any retained
    /// fact of its account, the operations, coverage and order are reused; otherwise the cached facts are aggregated again
    /// with the new observations, without another database read. Snapshot freshness stays an input: the result always
    /// carries the new snapshots and revision, which the funding proof's fence and unrealized marks read.
    /// </summary>
    public static ActivitySummaries Rebase(ActivitySummaries cached, ActivityObservations observed, long revision, Guid? accountId = null,
        IReadOnlySet<Guid>? accountIds = null, IVenueRegistry? venues = null)
    {
        if (cached.Facts.Revision == revision) return cached;
        bool Scoped(Account a) => a.TradingMode == TradingMode.Real && a.IsEnabled && (accountId == null || a.Id == accountId) &&
            (accountIds == null || accountIds.Contains(a.Id));
        var accounts = observed.Accounts.Where(Scoped).ToList();
        var ids = accounts.Select(a => a.Id).ToHashSet();
        var facts = cached.Facts with
        {
            Accounts = accounts, Snapshots = observed.Snapshots, Revision = revision,
            FundingStates = observed.FundingStates.Where(s => ids.Contains(s.AccountId)).ToList()
        };
        if (cached.Positions is null || !SameObservations(cached, facts)) return Aggregate(facts, TradingMode.Real, accountId, accountIds, venues);
        var version = SameVersionInputs(cached.Facts.FundingStates, facts.FundingStates) ? cached.Version
            : Version(cached.Ordered, facts, cached.Coverage);
        return cached.Rebased(facts, version, cached.Positions.WithSnapshots(facts.Snapshots));
    }

    /// <summary>
    /// Issue #107: one account's facts out of a cached aggregate over a wider account set at the same execution revision, with
    /// that account's current observations, or null when the wider aggregate does not hold all of them (it reads only the
    /// newest forced events across its accounts).
    /// </summary>
    public static ActivityFacts? Narrow(ActivitySummaries wide, Guid accountId, ActivityObservations observed, long revision)
    {
        var facts = wide.Facts;
        if (facts.PaperPlays.Count > 0 || !facts.Accounts.Any(a => a.Id == accountId)) return null;
        var forced = facts.ForcedEvents.Where(e => e.AccountId == accountId).ToList();
        var count = facts.ForcedEventCounts?.GetValueOrDefault(accountId) ?? forced.Count;
        if (forced.Count < Math.Min(MaxForcedEventRows, count)) return null;
        var fills = facts.Fills.Where(f => f.AccountId == accountId).ToList();
        return new(observed.Accounts, fills, observed.Snapshots,
            fills.Where(f => facts.FillPlayIds.ContainsKey(f.Id)).ToDictionary(f => f.Id, f => facts.FillPlayIds[f.Id]))
        {
            FundingPayments = facts.FundingPayments.Where(p => p.AccountId == accountId).ToList(),
            FundingStates = observed.FundingStates,
            LeverageObservations = facts.LeverageObservations.Where(o => o.AccountId == accountId).ToList(),
            Omissions = facts.Omissions.Where(m => m.AccountId == accountId).ToList(),
            ForcedEvents = forced, ForcedEventCounts = count > forced.Count ? new Dictionary<Guid, int> { [accountId] = count } : null,
            Revision = revision
        };
    }

    // Everything the aggregate reads from accounts, snapshots and funding coverage, compared field by field. A snapshot's
    // time is compared with every retained instant of its account the walk, coverage, funding or leverage compares it with.
    private static bool SameObservations(ActivitySummaries cached, ActivityFacts next)
    {
        var before = cached.Facts;
        if (before.Accounts.Count != next.Accounts.Count) return false;
        var accounts = before.Accounts.ToDictionary(a => a.Id);
        foreach (var account in next.Accounts)
            if (!accounts.TryGetValue(account.Id, out var old) || old.VenueId != account.VenueId || old.HistoryNotice != account.HistoryNotice ||
                old.IsEnabled != account.IsEnabled || old.TradingMode != account.TradingMode)
                return false;
        var snapshots = cached.Positions!.Snapshots;
        if (snapshots.Count != next.Snapshots.Count) return false;
        foreach (var snapshot in next.Snapshots)
        {
            if (!snapshots.TryGetValue(snapshot.AccountId, out var old) || old.PositionsComplete != snapshot.PositionsComplete ||
                !SamePositions(old.Positions, snapshot.Positions))
                return false;
            if (old.ObservedAtUtc != snapshot.ObservedAtUtc && cached.Passes(snapshot.AccountId, old.ObservedAtUtc, snapshot.ObservedAtUtc))
                return false;
        }
        if (before.FundingStates.Count != next.FundingStates.Count) return false;
        var states = before.FundingStates.ToDictionary(s => s.AccountId);
        return next.FundingStates.All(s => states.TryGetValue(s.AccountId, out var old) && old.CoveredFromUtc == s.CoveredFromUtc &&
            old.CoveredToUtc == s.CoveredToUtc && old.PnlBasis == s.PnlBasis && old.Notice == s.Notice);
    }

    private static bool SamePositions(List<AccountPosition> before, List<AccountPosition> after)
    {
        static IEnumerable<string> Key(IEnumerable<AccountPosition> positions) => positions.Select(p => string.Join('\u001f', p.ContractId,
            p.VenueContractId ?? "\u0000", p.SignedQuantity.ToString(CultureInfo.InvariantCulture), p.Leverage?.ToString(CultureInfo.InvariantCulture) ?? "\u0000"))
            .Order(StringComparer.Ordinal);
        return before.Count == after.Count && Key(before).SequenceEqual(Key(after), StringComparer.Ordinal);
    }

    // The funding-state fields only the version hashes.
    private static bool SameVersionInputs(IReadOnlyList<AccountFundingState> before, IReadOnlyList<AccountFundingState> after)
    {
        var states = before.ToDictionary(s => s.AccountId);
        return before.Count == after.Count && after.All(s => states.TryGetValue(s.AccountId, out var old) &&
            old.LastAttemptToUtc == s.LastAttemptToUtc && old.LastReadComplete == s.LastReadComplete);
    }
}

public sealed record ActivitySummaries(IReadOnlyList<ActivityOperationDto> Operations,
    IReadOnlyList<ActivityCoverageDto> Coverage, ActivityFacts Facts)
{
    public string Version { get; init; } = Convert.ToHexStringLower(SHA256.HashData([]), 0, 16);
    public IReadOnlyList<Vessel.Domain.Workspace.FundingPayment> UnassignedFunding { get; init; } = [];
    // The partitions, anchors and omissions this aggregate was walked with, kept for Phase 2, item 9b.
    internal ActivityPositions? Positions { get; init; }
    /// <summary>Issue #108: how many leading <see cref="Coverage"/> entries are the accounts' own notices, in account order.</summary>
    internal int AccountCoverageCount { get; init; }
    private ActivityProofIndex? proof;
    private IReadOnlyList<ActivityOperationDto>? ordered;
    private ActivityFacetsDto? facets;
    private Dictionary<Guid, DateTimeOffset[]>? instants;

    /// <summary>
    /// Phase 2, item 9b: the funding proof's indexes, built on first use and kept with this aggregate, so a cached
    /// aggregate (one facts revision) builds them once however often a Play is read.
    /// </summary>
    internal ActivityProofIndex Proof => LazyInitializer.EnsureInitialized(ref proof, () => new ActivityProofIndex(this));

    /// <summary>Issue #107: the operations in Activity's page order (newest first), sorted once per aggregate, never per read.</summary>
    [JsonIgnore]
    public IReadOnlyList<ActivityOperationDto> Ordered
    {
        get => LazyInitializer.EnsureInitialized(ref ordered, () => PageOrder(Operations));
        init => ordered = value;
    }

    /// <summary>Issue #107: the accounts and contracts the operations carry, built once per aggregate.</summary>
    [JsonIgnore]
    public ActivityFacetsDto Facets => LazyInitializer.EnsureInitialized(ref facets, () => new(
        Ordered.Select(o => o.AccountId).Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList(),
        Ordered.Select(o => o.ContractId).Distinct().Order(StringComparer.Ordinal).ToList()));

    internal static List<ActivityOperationDto> PageOrder(IEnumerable<ActivityOperationDto> operations) =>
        operations.OrderByDescending(o => o.LastOccurredAtUtc).ThenBy(o => o.AccountId.ToString("D"), StringComparer.Ordinal)
            .ThenBy(o => o.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Issue #107: whether a snapshot moving from <paramref name="from"/> to <paramref name="to"/> passes a retained instant of
    /// its account that the aggregate compares snapshot times with: a fill, funding settlement, leverage observation or
    /// omission bound, or the funding coverage bounds. A millisecond either side covers the funding window's whole-millisecond
    /// rounding.
    /// </summary>
    internal bool Passes(Guid accountId, DateTimeOffset from, DateTimeOffset to)
    {
        if (!Instants().TryGetValue(accountId, out var sorted)) return false;
        var (low, high) = from < to ? (from, to) : (to, from);
        low = low > DateTimeOffset.MinValue.AddMilliseconds(1) ? low.AddMilliseconds(-1) : DateTimeOffset.MinValue;
        high = high < DateTimeOffset.MaxValue.AddMilliseconds(-1) ? high.AddMilliseconds(1) : DateTimeOffset.MaxValue;
        var first = LowerBound(sorted, low);
        return first < sorted.Length && sorted[first] <= high;
    }

    /// <summary>
    /// Issue #108: the snapshot times <see cref="Passes"/> lets an account's snapshot move to from <paramref name="at"/>,
    /// as an open interval (null bounds are unbounded). Pinned when a retained instant lies within a millisecond of it, so
    /// only <paramref name="at"/> itself keeps this aggregate.
    /// </summary>
    internal (DateTimeOffset? After, DateTimeOffset? Before, bool Pinned) SnapshotWindow(Guid accountId, DateTimeOffset at)
    {
        if (!Instants().TryGetValue(accountId, out var sorted)) return (null, null, false);
        var low = at > DateTimeOffset.MinValue.AddMilliseconds(1) ? at.AddMilliseconds(-1) : DateTimeOffset.MinValue;
        var high = at < DateTimeOffset.MaxValue.AddMilliseconds(-1) ? at.AddMilliseconds(1) : DateTimeOffset.MaxValue;
        var first = LowerBound(sorted, low);
        if (first < sorted.Length && sorted[first] <= high) return (at, at, true);
        // Every instant before `first` is earlier than low; the one at `first` is later than high.
        DateTimeOffset? after = first > 0 ? sorted[first - 1].AddMilliseconds(1) : null;
        DateTimeOffset? before = first < sorted.Length ? sorted[first].AddMilliseconds(-1) : null;
        return (after, before, false);
    }

    private static int LowerBound(DateTimeOffset[] sorted, DateTimeOffset value)
    {
        int first = 0, last = sorted.Length;
        while (first < last)
        {
            var middle = (first + last) / 2;
            if (sorted[middle] < value) first = middle + 1; else last = middle;
        }
        return first;
    }

    private Dictionary<Guid, DateTimeOffset[]> Instants()
    {
        return LazyInitializer.EnsureInitialized(ref instants, () =>
        {
            var facts = Facts;
            return facts.Fills.Select(f => (f.AccountId, At: f.OccurredAtUtc))
                .Concat(facts.FundingPayments.Select(p => (p.AccountId, At: p.OccurredAtUtc)))
                .Concat(facts.LeverageObservations.Select(o => (o.AccountId, At: o.ObservedAtUtc)))
                .Concat(facts.Omissions.SelectMany(m => new[] { m.OccurredAtUtc, m.UncertainFromUtc, m.UncertainToUtc }
                    .OfType<DateTimeOffset>().Select(at => (m.AccountId, At: at))))
                .Concat(facts.FundingStates.SelectMany(s => new[] { s.CoveredFromUtc, s.CoveredToUtc }
                    .OfType<DateTimeOffset>().Select(at => (s.AccountId, At: at))))
                .GroupBy(i => i.AccountId).ToDictionary(g => g.Key, g => g.Select(i => i.At).Order().ToArray());
        });
    }

    /// <summary>Issue #107: this aggregate over newer observations, sharing its operations, order, facets and indexes.</summary>
    internal ActivitySummaries Rebased(ActivityFacts facts, string version, ActivityPositions positions)
    {
        var next = new ActivitySummaries(Operations, Coverage, facts)
        {
            Version = version, UnassignedFunding = UnassignedFunding, Positions = positions, Ordered = Ordered,
            AccountCoverageCount = AccountCoverageCount
        };
        next.facets = facets;
        // Funding coverage bounds are instants too, and Rebase only shares the index while they are unchanged.
        next.instants = instants;
        if (proof is { } built) next.proof = built.WithPositions(positions);
        return next;
    }
}
