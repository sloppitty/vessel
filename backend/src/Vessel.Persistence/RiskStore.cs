using Microsoft.EntityFrameworkCore;
using Vessel.Application.Common;
using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Risk;
using Vessel.Domain.Common;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;
using Vessel.Domain.Risk;

namespace Vessel.Persistence;

public sealed class RiskStore(VesselDbContext db, UnitOfWork unitOfWork) : IOpenRiskStore
{
    private static readonly PlayStatus[] Active = [PlayStatus.Planned, PlayStatus.Paused, PlayStatus.Open, PlayStatus.Unresolved];

    public async Task<List<OpenRiskFacts>> ActivePlaysAsync(IReadOnlyCollection<Guid>? playIds, CancellationToken ct)
    {
        var query = db.Plays.Where(p => p.TradingMode == TradingMode.Real && Active.Contains(p.Status) &&
            db.Accounts.Any(a => a.Id == p.AccountId && a.IsEnabled && a.TradingMode == TradingMode.Real));
        if (playIds is not null) query = query.Where(p => playIds.Contains(p.Id));
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
        return plays.Select(play =>
        {
            var plan = PlayDocuments.Read(play.Plan);
            if (manual[play.Id].Any()) return Manual(play, manual[play.Id]);
            var entrySide = ExecutionIntegrity.EntrySide(plan);
            var executions = new List<RiskExecution>();
            var resting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in linked[play.Id])
            {
                if (!orders.TryGetValue(link.ImportedOrderId, out var order)) continue;
                if (link.Role == OrderLinkRole.Stop && order.Status == "open" && link.EntryId is not null && link.LevelId is not null)
                    resting.Add(link.EntryId + "\u001f" + link.LevelId);
                foreach (var fill in fills[OrderKey.Of(order)])
                    executions.Add(new(link.Role, link.Role == OrderLinkRole.Exit ? null : link.EntryId, link.LevelId, fill.Quantity, fill.Price,
                        ExecutionIntegrity.Contradicts(link.Role, entrySide, fill)));
            }
            return new OpenRiskFacts(play, executions, resting, false);
        }).ToList();
    }

    /// <summary>Owner-entered facts: entries name their entry; exits belong to the position.</summary>
    private static OpenRiskFacts Manual(Play play, IEnumerable<ManualExecution> rows)
    {
        var executions = new List<RiskExecution>();
        foreach (var row in rows)
        {
            if (ExactDecimal.ParseManualFact(row.Quantity, true, out var quantity) != ManualFactError.None ||
                ExactDecimal.ParseManualFact(row.Price, true, out var price) != ManualFactError.None || row.Role is not ("entry" or "exit"))
                return new(play, [], new HashSet<string>(), true, "A manual execution cannot be read.");
            executions.Add(new(row.Role == "entry" ? OrderLinkRole.Entry : OrderLinkRole.Exit, row.Role == "entry" ? row.EntryId : null, null,
                quantity, price));
        }
        return new(play, executions, new HashSet<string>(), true);
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
