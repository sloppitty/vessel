using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vessel.Application.Ownership;
using Vessel.Application.Paper;
using Vessel.Domain.Accounts;
using Vessel.Domain.Common;
using Vessel.Domain.Paper;
using Vessel.Domain.Plays;

namespace Vessel.Persistence;

public sealed class PaperStore(VesselDbContext db, UnitOfWork unit) : IPaperStore
{
    public Guid OwnerId => db.CurrentOwnerId;
    public void RequireActive() => unit.RequireActive();
    public Task<T> WithAccountLockAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken ct) =>
        new ExecutionStore(db, unit).WithAccountLockAsync(accountId, action, ct);
    public Task<T> WithSettingsLockAsync<T>(Func<Task<T>> action, CancellationToken ct) => unit.ParticipateAsync(async () =>
    {
        var key = "vessel-paper-settings:" + OwnerId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
        foreach (var entry in db.ChangeTracker.Entries<PaperSettings>().Where(x => x.State == EntityState.Unchanged))
            await entry.ReloadAsync(ct);
        return await action();
    }, ct);

    public Task<PaperSettings?> SettingsAsync(CancellationToken ct) => db.Set<PaperSettings>().SingleOrDefaultAsync(ct);
    public Task<Account?> AccountAsync(Guid id, CancellationToken ct) => db.Accounts.SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<Play?> PlayAsync(Guid id, CancellationToken ct)
    {
        var play = await db.Plays.SingleOrDefaultAsync(x => x.Id == id && x.TradingMode == TradingMode.Paper, ct);
        if (play is not null && unit.IsActive && db.Entry(play).State == EntityState.Unchanged) await db.Entry(play).ReloadAsync(ct);
        return play;
    }
    public Task<PaperSimulation?> SimulationAsync(Guid playId, CancellationToken ct) =>
        db.Set<PaperSimulation>().SingleOrDefaultAsync(x => x.PlayId == playId, ct);
    public Task<List<PaperSimulation>> AccountSimulationsAsync(Guid accountId, CancellationToken ct) =>
        db.Set<PaperSimulation>().Where(x => x.AccountId == accountId).ToListAsync(ct);
    public Task<List<PaperSimulation>> ActiveSimulationsAsync(CancellationToken ct) =>
        db.Set<PaperSimulation>().Where(x => x.NextCheckAtUtc != null || x.State == "gap" || x.State == "ambiguous").ToListAsync(ct);
    public Task<List<PaperExecution>> ExecutionsAsync(Guid playId, CancellationToken ct) =>
        db.Set<PaperExecution>().Where(x => x.PlayId == playId).AsNoTracking().OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
    public Task<List<PaperPlanActivation>> ActivationsAsync(Guid playId, CancellationToken ct) =>
        db.Set<PaperPlanActivation>().Where(x => x.PlayId == playId).AsNoTracking().OrderBy(x => x.EffectiveAtUtc).ThenBy(x => x.PlanVersion).ToListAsync(ct);
    public Task<PaperReservation?> ReservationAsync(Guid playId, CancellationToken ct) =>
        db.Set<PaperReservation>().SingleOrDefaultAsync(x => x.PlayId == playId, ct);
    public async Task<PaperCapital> CapitalAsync(Guid accountId, Guid exceptReservationPlayId, bool includeOwnIssues, CancellationToken ct)
    {
        var cash = await db.Set<PaperLedgerEntry>().Where(x => x.AccountId == accountId).SumAsync(x => x.AmountUsd, ct);
        var open = await db.Set<PaperExecution>().Where(x => x.AccountId == accountId).SumAsync(x => x.MarginUsd, ct);
        var reserved = await db.Set<PaperReservation>().Where(x => x.AccountId == accountId && x.PlayId != exceptReservationPlayId).SumAsync(x => x.AmountUsd, ct);
        var unresolved = await db.Set<PaperSimulation>().AnyAsync(x => x.AccountId == accountId &&
            (includeOwnIssues || x.PlayId != exceptReservationPlayId) &&
            (x.State == "gap" || x.State == "ambiguous" || x.IssuesJson != "[]"), ct);
        return new(cash, open, reserved, unresolved);
    }
    public async Task<PaperCapital> TimelineCapitalAsync(Guid accountId, IReadOnlyCollection<Guid> recoveringPlayIds, CancellationToken ct)
    {
        var cash = await db.Set<PaperLedgerEntry>().Where(x => x.AccountId == accountId).SumAsync(x => x.AmountUsd, ct);
        var open = await db.Set<PaperExecution>().Where(x => x.AccountId == accountId).SumAsync(x => x.MarginUsd, ct);
        var reserved = await db.Set<PaperReservation>().Where(x => x.AccountId == accountId).SumAsync(x => x.AmountUsd, ct);
        // Complete public windows can recover transient read errors together. Frozen frontiers never recover by guessing.
        var ids = recoveringPlayIds.ToArray();
        var unresolved = await db.Set<PaperSimulation>().AnyAsync(x => x.AccountId == accountId &&
            (x.State == "gap" || x.State == "ambiguous" || x.IssuesJson != "[]" && !ids.Contains(x.PlayId)), ct);
        return new(cash, open, reserved, unresolved);
    }
    public Task<bool> HasExecutionsAfterAsync(Guid accountId, DateTimeOffset frontier, CancellationToken ct) =>
        db.Set<PaperExecution>().AnyAsync(x => x.AccountId == accountId && x.OccurredAtUtc > frontier, ct);
    public Task<List<PaperDuePlay>> AccountCandidatesAsync(Guid accountId, Guid? after, int limit, CancellationToken ct) =>
        (from s in db.Set<PaperSimulation>().AsNoTracking()
         join p in db.Plays.AsNoTracking() on s.PlayId equals p.Id
         where s.AccountId == accountId && s.NextCheckAtUtc != null && (s.State == "ready" || s.State == "waiting") &&
             p.TradingMode == TradingMode.Paper && (p.Status == PlayStatus.Planned || p.Status == PlayStatus.Open) && p.ContractId != null &&
             (after == null || s.PlayId.CompareTo(after.Value) > 0)
         orderby s.PlayId
         select new PaperDuePlay(s.OwnerId, s.AccountId, s.PlayId, p.ContractId!, p.Version,
             s.CheckpointUtc ?? db.Set<PaperPlanActivation>().Where(x => x.PlayId == p.Id).Min(x => x.EffectiveAtUtc),
             s.CheckpointUtc, s.NextCheckAtUtc)).Take(limit).ToListAsync(ct);
    public async Task DeferAccountAsync(Guid accountId, string issuesJson, DateTimeOffset now, CancellationToken ct)
    {
        unit.RequireActive();
        await db.Set<PaperSimulation>().Where(x => x.AccountId == accountId && x.NextCheckAtUtc != null &&
            (x.State == "ready" || x.State == "waiting")).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CheckedAtUtc, now)
                .SetProperty(x => x.NextCheckAtUtc, now.AddMinutes(1))
                .SetProperty(x => x.IssuesJson, issuesJson), ct);
    }
    public Task<bool> HasActiveAsync(CancellationToken ct) =>
        db.Set<PaperSimulation>().AnyAsync(x => x.NextCheckAtUtc != null || x.State == "gap" || x.State == "ambiguous", ct);

    public Task<List<PaperDuePlay>> DueAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        (from s in db.Set<PaperSimulation>().AsNoTracking()
         join p in db.Plays.AsNoTracking() on s.PlayId equals p.Id
         join a in db.Accounts.AsNoTracking() on p.AccountId equals a.Id
         where s.NextCheckAtUtc <= now && (s.State == "ready" || s.State == "waiting") &&
             a.IsEnabled && a.TradingMode == TradingMode.Paper && p.TradingMode == TradingMode.Paper &&
             (p.Status == PlayStatus.Planned || p.Status == PlayStatus.Open) && p.ContractId != null &&
             db.Set<PaperSettings>().Any(settings => settings.Enabled)
         orderby s.NextCheckAtUtc, s.PlayId
         select new PaperDuePlay(s.OwnerId, s.AccountId, s.PlayId, p.ContractId!, p.Version,
             s.CheckpointUtc ?? db.Set<PaperPlanActivation>().Where(x => x.PlayId == p.Id).Min(x => x.EffectiveAtUtc),
             s.CheckpointUtc, s.NextCheckAtUtc))
        .Take(limit).ToListAsync(ct);

    public void Add(PaperSettings value) { unit.RequireActive(); db.Add(value); }
    public void Add(PaperSimulation value) { unit.RequireActive(); db.Add(value); }
    public void Add(PaperPlanActivation value) { unit.RequireActive(); db.Add(value); }
    public void Add(PaperReservation value) { unit.RequireActive(); db.Add(value); }
    public void Add(PaperExecution value) { unit.RequireActive(); db.Add(value); }
    public void Add(PaperLedgerEntry value) { unit.RequireActive(); db.Add(value); }
    public void Add(PlayStatusChange value) { unit.RequireActive(); db.Add(value); }
}

public static class PaperServices
{
    /// <summary>Shared by the API and the opt-in paper worker; adds no hosted process or broker dependency.</summary>
    public static IServiceCollection AddPaperTrading(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPaperStore, PaperStore>();
        services.AddScoped<PaperTradingService>();
        services.AddScoped<IPaperTradingService>(sp => sp.GetRequiredService<PaperTradingService>());
        return services;
    }
}
