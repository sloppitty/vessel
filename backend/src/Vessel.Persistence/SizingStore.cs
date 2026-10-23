using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Application.Plays;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;
using System.Text.Json;
using Vessel.Domain.Common;
using System.Data;

namespace Vessel.Persistence;

public sealed class SizingStore(VesselDbContext db, UnitOfWork unitOfWork) : ISizingStore
{
    public Task<SizingSettings?> SettingsAsync(CancellationToken ct) => db.SizingSettings.SingleOrDefaultAsync(ct);

    public Task<List<ClosedPlayFacts>> ClosedPlaysAsync(CancellationToken ct) => ClosedPlaysAsync(TradingMode.Real, ct);

    public async Task<List<ClosedPlayFacts>> ClosedPlaysAsync(string tradingMode, CancellationToken ct)
    {
        tradingMode = TradingMode.Validate(tradingMode);
        await using var transaction = tradingMode == TradingMode.Paper && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var plays = await db.Plays.Where(p => p.TradingMode == tradingMode &&
            (p.Status == PlayStatus.Closed || tradingMode == TradingMode.Paper &&
                db.PaperSimulations.Any(s => s.PlayId == p.Id && (s.State == "ambiguous" || s.State == "gap" ||
                    s.SuspensionsJson != "[]" && p.Status == PlayStatus.Cancelled))) &&
            db.Accounts.Any(a => a.Id == p.AccountId && a.IsEnabled && a.TradingMode == tradingMode))
            .AsNoTracking().ToListAsync(ct);
        if (plays.Count == 0) return [];
        var facts = await FactsAsync(db, plays, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return facts;
    }

    /// <summary>
    /// The facts Review and sizing score, for any of the owner's Plays, in their order: a paper Play's simulation, or a real
    /// Play's plan revisions with the fills of its linked orders and the orders known to miss executions, or its manual
    /// ledger. The journal search projection reads the same facts (Phase 2, item 13), so its outcomes follow these rules.
    /// </summary>
    internal static async Task<List<ClosedPlayFacts>> FactsAsync(VesselDbContext db, IReadOnlyList<Play> plays, CancellationToken ct)
    {
        var paper = plays.Any(p => p.TradingMode == TradingMode.Paper)
            ? (await PaperFactsAsync(db, plays, ct)).ToDictionary(f => f.Play.Id) : [];
        var real = plays.Where(p => p.TradingMode != TradingMode.Paper).ToList();
        if (real.Count == 0) return plays.Select(p => Paper(paper[p.Id])).ToList();
        var ids = real.Select(p => p.Id).ToList();
        var revisions = (await db.PlanRevisions.Where(r => ids.Contains(r.PlayId)).AsNoTracking().ToListAsync(ct)).ToLookup(r => r.PlayId);
        var links = await db.OrderLinks.Where(l => ids.Contains(l.PlayId) && l.State == OrderLinkState.Linked).AsNoTracking().ToListAsync(ct);
        var orderIds = links.Select(l => l.ImportedOrderId).Distinct().ToList();
        var linkedOrders = db.Orders.Where(o => orderIds.Contains(o.Id));
        // Balanced fills alone cannot prove completeness: a known filled order may have only part (or none) of its executions retained.
        var known = (await linkedOrders.AsNoTracking().ToListAsync(ct)).ToDictionary(o => o.Id);
        var fills = (await db.FillsOf(linkedOrders).AsNoTracking().ToListAsync(ct)).ToLookup(OrderKey.Of);
        IEnumerable<ImportedFill> FillsOf(PlayOrderLink link) => known.TryGetValue(link.ImportedOrderId, out var order) ? fills[OrderKey.Of(order)] : [];
        var linked = links.ToLookup(l => l.PlayId);
        var manual = (await db.ManualExecutions.Where(r => ids.Contains(r.PlayId)).AsNoTracking().ToListAsync(ct)).ToLookup(r => r.PlayId);
        // Owner-confirmed allocations (Phase 2, item 11): an allocated fill counts only through the Play's own line.
        var allocations = await AllocatedFills.ForAsync(db, ids, links.SelectMany(FillsOf).Select(f => f.Id).ToList(), ct);
        return plays.Select(p => paper.TryGetValue(p.Id, out var simulated) ? Paper(simulated) :
            manual[p.Id].Any() ? ManualFacts(p, revisions[p.Id].ToList(), manual[p.Id]) :
            new ClosedPlayFacts(p, revisions[p.Id].ToList(),
            FillAllocations.For(p.Id, linked[p.Id].SelectMany(l => FillsOf(l).Select(f => (l, f))), allocations).Select(f => f.Linked).ToList(),
            linked[p.Id].Where(l => known.TryGetValue(l.ImportedOrderId, out var order) &&
                ExecutionIntegrity.MissingExecutions(order, FillsOf(l))).Select(l => known[l.ImportedOrderId].ExternalOrderId)
                .Concat(FillAllocations.Incomplete(p.Id, allocations)).Distinct(StringComparer.Ordinal).ToList())).ToList();
    }

    private static ClosedPlayFacts Paper(PaperPlayFacts facts) => new(facts.Play, facts.Revisions, []) { Paper = facts };

    public Task<int> UnresolvedPlaysAsync(CancellationToken ct) =>
        db.Plays.CountAsync(p => p.Status == PlayStatus.Unresolved && p.TradingMode == TradingMode.Real &&
            db.Accounts.Any(a => a.Id == p.AccountId && a.IsEnabled), ct);

    public async Task<List<ImportedOrder>> OrdersAsync(IReadOnlyCollection<OrderKey> orders, CancellationToken ct)
    {
        if (orders.Count == 0) return [];
        var keys = orders.ToHashSet();
        var accounts = keys.Select(k => k.AccountId).Distinct().ToArray();
        var ids = keys.Select(k => k.ExternalOrderId).Distinct(StringComparer.Ordinal).ToArray();
        // SQL narrows by account and external ID; the shared OrderKey rule keeps only each key's own contract.
        return (await db.Orders.Where(o => accounts.Contains(o.AccountId) && ids.Contains(o.ExternalOrderId) &&
            db.Accounts.Any(a => a.Id == o.AccountId && a.TradingMode == TradingMode.Real)).AsNoTracking().ToListAsync(ct))
            .Where(o => keys.Contains(OrderKey.Of(o))).ToList();
    }

    public async Task<IReadOnlySet<Guid>> AllocatedFillIdsAsync(CancellationToken ct) =>
        (await db.FillAllocations.Where(a => a.Lines.Any() && db.Accounts.Any(x => x.Id == a.AccountId && x.TradingMode == TradingMode.Real))
            .Select(a => a.FillId).ToListAsync(ct)).ToHashSet();

    internal static async Task<List<PaperPlayFacts>> PaperFactsAsync(VesselDbContext db, IReadOnlyList<Play> plays, CancellationToken ct)
    {
        var ids = plays.Where(p => p.TradingMode == TradingMode.Paper).Select(p => p.Id).ToArray();
        var executions = (await db.PaperExecutions.Where(e => ids.Contains(e.PlayId) && e.TradingMode == TradingMode.Paper)
            .AsNoTracking().ToListAsync(ct)).ToLookup(e => e.PlayId);
        var states = (await db.PaperSimulations.Where(s => ids.Contains(s.PlayId) && s.TradingMode == TradingMode.Paper)
            .AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.PlayId);
        var revisions = (await db.PlanRevisions.Where(r => ids.Contains(r.PlayId)).AsNoTracking().ToListAsync(ct)).ToLookup(r => r.PlayId);
        return plays.Where(p => p.TradingMode == TradingMode.Paper).Select(p =>
        {
            var state = states.GetValueOrDefault(p.Id);
            var issues = state is null ? [] : (JsonSerializer.Deserialize<string[]>(state.IssuesJson) ?? []).ToList();
            if (state?.Suspensions().Count > 0)
                issues.Add("Paper evaluation was suspended. Disabled-period touches were not simulated; this outcome remains unscored. " +
                    string.Join(" ", state.Suspensions().Select(s => s.Reason)));
            return new PaperPlayFacts(p, revisions[p.Id].ToList(), executions[p.Id].ToList(), state?.State ?? "not-armed",
                issues);
        }).ToList();
    }

    private static ClosedPlayFacts ManualFacts(Play play, IReadOnlyList<PlayPlanRevision> revisions, IEnumerable<ManualExecution> rows)
    {
        var ledger = ManualLedger.Calculate(play, rows);
        return new(play, revisions, ledger.Projection, [], true, ledger.FeesComplete);
    }

    public async Task<SizingSettings> SettingsForUpdateAsync(SizingSettings initial, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        if (initial.OwnerId != db.CurrentOwnerId) throw new InvalidOperationException("Sizing settings belong to the current owner.");
        // A concurrent first write waits on the conflicting insert, then both read and lock the one committed row.
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO sizing_settings(""OwnerId"", ""RiskPercent"", ""UpdatedAtUtc"")
            VALUES({initial.OwnerId}, {initial.RiskPercent}, {initial.UpdatedAtUtc}) ON CONFLICT DO NOTHING", ct);
        return await db.SizingSettings.FromSqlInterpolated(
            $@"SELECT * FROM sizing_settings WHERE ""OwnerId"" = {db.CurrentOwnerId} FOR UPDATE").SingleAsync(ct);
    }

    public void Add(SizingSettings settings) { unitOfWork.RequireActive(); db.SizingSettings.Add(settings); }
    public Task SaveAsync(CancellationToken ct) => unitOfWork.SaveChangesAsync(ct);
}
