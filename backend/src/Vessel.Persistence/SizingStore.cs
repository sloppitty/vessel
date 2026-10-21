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
        if (tradingMode == TradingMode.Paper)
        {
            var facts = (await PaperFactsAsync(db, plays, ct)).Select(f => new ClosedPlayFacts(f.Play, f.Revisions, []) { Paper = f }).ToList();
            if (transaction is not null) await transaction.CommitAsync(ct);
            return facts;
        }
        var ids = plays.Select(p => p.Id).ToList();
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
        return plays.Select(p => manual[p.Id].Any() ? ManualFacts(p, revisions[p.Id].ToList(), manual[p.Id]) :
            new ClosedPlayFacts(p, revisions[p.Id].ToList(),
            linked[p.Id].SelectMany(l => FillsOf(l).Select(f => new LinkedFill(l.Role, f))).ToList(),
            linked[p.Id].Where(l => known.TryGetValue(l.ImportedOrderId, out var order) &&
                ExecutionIntegrity.MissingExecutions(order, FillsOf(l))).Select(l => known[l.ImportedOrderId].ExternalOrderId).ToList())).ToList();
    }

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
