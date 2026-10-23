using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Risk;
using Vessel.Domain.Common;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Risk;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence;

public sealed class RiskStore(VesselDbContext db, UnitOfWork unitOfWork) : IOpenRiskStore
{
    private static readonly PlayStatus[] Active = [PlayStatus.Planned, PlayStatus.Paused, PlayStatus.Open, PlayStatus.Unresolved];

    public async Task<List<OpenRiskFacts>> ActivePlaysAsync(IReadOnlyCollection<Guid>? playIds, CancellationToken ct)
    {
        // Named Plays (the editor's preview) are read whatever their status or account state, so their executions always
        // count; only the aggregate keeps to active Plays on enabled accounts.
        var query = playIds is not null
            ? db.Plays.Where(p => p.TradingMode == TradingMode.Real && playIds.Contains(p.Id))
            : db.Plays.Where(p => p.TradingMode == TradingMode.Real && Active.Contains(p.Status) &&
                db.Accounts.Any(a => a.Id == p.AccountId && a.IsEnabled && a.TradingMode == TradingMode.Real));
        var plays = await query.AsNoTracking().ToListAsync(ct);
        if (plays.Count == 0) return [];
        var ids = plays.Select(p => p.Id).ToList();
        var links = await db.OrderLinks.Where(l => ids.Contains(l.PlayId) && l.State == OrderLinkState.Linked).AsNoTracking().ToListAsync(ct);
        var orderIds = links.Select(l => l.ImportedOrderId).Distinct().ToList();
        var linkedOrders = db.Orders.Where(o => orderIds.Contains(o.Id));
        var orders = (await linkedOrders.AsNoTracking().ToListAsync(ct)).ToDictionary(o => o.Id);
        var fills = (await db.FillsOf(linkedOrders).AsNoTracking().ToListAsync(ct)).ToLookup(OrderKey.Of);
        var linked = links.ToLookup(l => l.PlayId);
        var manual = (await db.ManualExecutions.Where(r => ids.Contains(r.PlayId)).AsNoTracking().ToListAsync(ct)).ToLookup(r => r.PlayId);
        var coverage = (await db.Set<MonitorCoverage>().Where(c => ids.Contains(c.PlayId)).AsNoTracking().ToListAsync(ct)).ToDictionary(c => c.PlayId);
        // Owner-confirmed allocations (Phase 2, item 11): an allocated fill counts only through the Play's own line.
        var allocations = await AllocatedFills.ForAsync(db, ids,
            links.Where(l => orders.ContainsKey(l.ImportedOrderId)).SelectMany(l => fills[OrderKey.Of(orders[l.ImportedOrderId])]).Select(f => f.Id).ToList(), ct);
        return plays.Select(play =>
        {
            if (manual[play.Id].Any()) return Manual(play, manual[play.Id]);
            var executions = new List<RiskExecution>();
            string? problem = null;
            var pairs = new List<(PlayOrderLink, ImportedFill)>();
            foreach (var link in linked[play.Id])
            {
                if (!orders.TryGetValue(link.ImportedOrderId, out var order)) continue;
                var own = fills[OrderKey.Of(order)].ToList();
                // A filled order whose retained executions fall short leaves its prices and quantity unknown.
                if (ExecutionIntegrity.MissingExecutions(order, own)) problem = OpenRiskCalculator.MissingExecutions;
                pairs.AddRange(own.Select(fill => (link, fill)));
            }
            if (FillAllocations.Incomplete(play.Id, allocations).Any()) problem = OpenRiskCalculator.MissingExecutions;
            // Whether a fill contradicts its role depends on the plan measured, saved or edited: the service decides.
            foreach (var fill in FillAllocations.For(play.Id, pairs, allocations))
                executions.Add(new(fill.Role, fill.Role == OrderLinkRole.Exit ? null : fill.EntryId, fill.LevelId, fill.Fill.Quantity, fill.Fill.Price)
                    { Fill = fill.Fill });
            return new OpenRiskFacts(play, executions, coverage.GetValueOrDefault(play.Id), false, problem);
        }).ToList();
    }

    public Task<Play?> PlayAsync(Guid id, CancellationToken ct) => db.Plays.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>Owner-entered facts: entries name their entry; exits belong to the position.</summary>
    private static OpenRiskFacts Manual(Play play, IEnumerable<ManualExecution> rows)
    {
        var executions = new List<RiskExecution>();
        foreach (var row in rows)
        {
            if (ExactDecimal.ParseManualFact(row.Quantity, true, out var quantity) != ManualFactError.None ||
                ExactDecimal.ParseManualFact(row.Price, true, out var price) != ManualFactError.None || row.Role is not ("entry" or "exit"))
                return new(play, [], null, true, "A manual execution cannot be read.");
            executions.Add(new(row.Role == "entry" ? OrderLinkRole.Entry : OrderLinkRole.Exit, row.Role == "entry" ? row.EntryId : null, null,
                quantity, price));
        }
        return new(play, executions, null, true);
    }

    public Task<RiskSettings?> SettingsAsync(CancellationToken ct) => db.RiskSettings.SingleOrDefaultAsync(ct);

    public async Task<RiskSettings> SettingsForUpdateAsync(RiskSettings initial, CancellationToken ct)
    {
        unitOfWork.RequireActive();
        if (initial.OwnerId != db.CurrentOwnerId) throw new InvalidOperationException("Risk settings belong to the current owner.");
        // A concurrent first write waits on the conflicting insert, then both read and lock the one committed row.
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO risk_settings(""OwnerId"", ""OpenRiskLimitPercent"", ""UpdatedAtUtc"")
            VALUES({initial.OwnerId}, {initial.OpenRiskLimitPercent}, {initial.UpdatedAtUtc}) ON CONFLICT DO NOTHING", ct);
        return await db.RiskSettings.FromSqlInterpolated(
            $@"SELECT * FROM risk_settings WHERE ""OwnerId"" = {db.CurrentOwnerId} FOR UPDATE").SingleAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => unitOfWork.SaveChangesAsync(ct);
}
