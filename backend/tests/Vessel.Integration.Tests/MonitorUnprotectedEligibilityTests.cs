using Microsoft.EntityFrameworkCore;
using Vessel.Application.Monitoring;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6c: the kept-history warning is not protection work. It never brings back the full comparison on an
/// account without Plays, yet it keeps the account checked until it resolves, even after the opt-in is turned off.
/// </summary>
public sealed partial class MonitorPostgresTests
{
    [PostgresFact]
    public async Task Phase2_an_open_no_stop_warning_is_kept_history_work_not_protection_work()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid(); var id = (await AccountsAsync(database, owner, 1)).Single();
        var now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        await using var db = database.Context(owner);
        await KeepHistoryAsync(db, id, true);
        var warning = new PlayDiscrepancy
        {
            OwnerId = owner, AccountId = id, ContractId = "BTC", NativeContractId = "BTC", SubjectKey = "[\"BTC\",\"BTC\"]",
            Kind = MonitorDiscrepancyService.UnprotectedKind, Severity = "warning", State = DiscrepancyLifecycle.Open, OpenedAtUtc = now,
            LastObservedAtUtc = now
        };
        db.Add(warning);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = MonitorTestPaths.Store(db, TestUnitOfWork.For(db));
        Assert.False(await store.ProtectionEligibleAsync(id, now, default));
        await KeepHistoryAsync(db, id, false);
        // Opted out, the open warning still keeps the account checked, so its next run can resolve it.
        Assert.True(await store.EligibleAsync(id, now, default));
        await db.Set<PlayDiscrepancy>().ExecuteUpdateAsync(s => s.SetProperty(d => d.State, DiscrepancyLifecycle.Resolved));
        Assert.False(await store.EligibleAsync(id, now, default));
    }
}
