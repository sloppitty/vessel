using Microsoft.EntityFrameworkCore;
using Vessel.Application.Sizing;
using Vessel.Domain.Sizing;
using Vessel.Persistence;

namespace Vessel.Tests;

/// <summary>Two first-time settings writes for one owner share one row; neither fails on the primary key.</summary>
public sealed class SizingSettingsFirstWriteRaceTests
{
    private static SizingService Service(VesselDbContext db, Guid owner) =>
        new(new SizingStore(db, TestUnitOfWork.For(db)), new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.For(db));

    [PostgresFact]
    public async Task A_first_write_waiting_on_another_uncommitted_first_write_updates_the_same_row()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await using (var other = database.Context(foreign))
            Assert.Equal("3", (await Service(other, foreign).UpdateSettingsAsync(new("3"), default)).Settings.RiskPercent);

        await using var first = database.Context(owner);
        await using var second = database.Context(owner);
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = new SizingStore(first, TestUnitOfWork.For(first));
        var firstWrite = TestUnitOfWork.For(first).ExecuteAsync(async () =>
        {
            var settings = await firstStore.SettingsForUpdateAsync(new SizingSettings(owner, 1.5m, DateTimeOffset.UtcNow), default);
            settings.SetRisk(1.5m, DateTimeOffset.UtcNow);
            await firstStore.SaveAsync(default);
            inserted.SetResult();
            await release.Task;
            return true;
        }, default);
        await inserted.Task;
        // The first write's row is inserted but uncommitted: the second sees no row yet and must wait on the conflict.
        var secondWrite = Service(second, owner).UpdateSettingsAsync(new("2.5"), default);
        await Task.Delay(300);
        Assert.False(secondWrite.IsCompleted);
        release.SetResult();
        await firstWrite;
        Assert.Equal("2.5", (await secondWrite).Settings.RiskPercent);

        await using var verify = database.Context(owner);
        var row = Assert.Single(await verify.SizingSettings.IgnoreQueryFilters().Where(s => s.OwnerId == owner).ToListAsync());
        Assert.Equal(2.5m, row.RiskPercent);
        var foreignRow = Assert.Single(await verify.SizingSettings.IgnoreQueryFilters().Where(s => s.OwnerId == foreign).ToListAsync());
        Assert.Equal(3m, foreignRow.RiskPercent);
        Assert.Equal(2, await verify.SizingSettings.IgnoreQueryFilters().CountAsync());
    }

    [PostgresFact]
    public async Task Concurrent_first_writes_both_succeed_with_one_row()
    {
        await using var database = await CoreDatabase.CreateAsync();
        for (var round = 0; round < 10; round++)
        {
            var owner = Guid.NewGuid();
            await using var a = database.Context(owner);
            await using var b = database.Context(owner);
            var results = await Task.WhenAll(Service(a, owner).UpdateSettingsAsync(new("1.5"), default),
                Service(b, owner).UpdateSettingsAsync(new("2"), default));
            Assert.Equal(["1.5", "2"], results.Select(r => r.Settings.RiskPercent));
            await using var verify = database.Context(owner);
            var row = Assert.Single(await verify.SizingSettings.ToListAsync());
            Assert.Contains(row.RiskPercent, new[] { 1.5m, 2m });
        }
    }

    [PostgresFact]
    public async Task Invalid_first_write_is_refused_before_any_row_exists_and_later_updates_keep_one_row()
    {
        await using var database = await CoreDatabase.CreateAsync();
        var owner = Guid.NewGuid();
        await using var db = database.Context(owner);
        await Assert.ThrowsAsync<Vessel.Domain.Common.DomainValidationException>(() => Service(db, owner).UpdateSettingsAsync(new("9"), default));
        await using (var check = database.Context(owner))
            Assert.Empty(await check.SizingSettings.ToListAsync());
        await using (var one = database.Context(owner))
            await Service(one, owner).UpdateSettingsAsync(new("1.5"), default);
        await using (var two = database.Context(owner))
            await Service(two, owner).UpdateSettingsAsync(new("2.25"), default);
        await using var verify = database.Context(owner);
        Assert.Equal(2.25m, Assert.Single(await verify.SizingSettings.ToListAsync()).RiskPercent);
    }
}
