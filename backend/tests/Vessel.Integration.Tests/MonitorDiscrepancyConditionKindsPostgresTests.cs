using System.Text.Json;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Numerics;
using Vessel.Domain.Plays;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyConditionKindsPostgresTests
{
    [PostgresTheory] // Through persisted records: confirms on the condition, opens once, shows the latest quantities.
    [InlineData("protection.missing")]
    [InlineData("protection.excess")]
    [InlineData("position.short")]
    [InlineData("position.excess")]
    public async Task Changing_quantities_confirm_once_with_the_latest_evidence(string kind)
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = MonitorDiscrepancyConditionKindsTests.Arrange(kind);
        await database.Seed(f);
        var start = f.Clock.Now;
        async Task<MonitorEvaluationResult> Check() { await using var scope = database.Scope(f); return await scope.Evaluate(f.Observation()); }
        async Task Change(int version)
        {
            var count = f.Store.Fills.Count;
            MonitorDiscrepancyConditionKindsTests.Evidence(f, kind, version);
            Assert.True(f.Store.Fills.Count >= count);
            await using var scope = database.Scope(f);
            scope.Db.AddRange(f.Store.Fills.Skip(count));
            await scope.Db.SaveChangesAsync();
        }
        async Task<(MonitorDiscrepancyDto Row, string[] Events)> Read()
        {
            await using var read = database.Scope(f);
            var row = Assert.Single(await read.Service.ReadAsync(f.Account.Id, null, default), x => x.Kind == kind);
            var events = (await EventReads.ReadAsync(read.Db, 0, 100, default)).Events.Where(x => x.Subject.DiscrepancyId == row.Id).ToArray();
            if (events.Length > 0)
                Assert.Equal(MonitorDiscrepancyConditionKindsTests.Latest(kind, 2),
                    MonitorDiscrepancyConditionKindsTests.Shown(kind, events[0].Data.GetProperty("details")));
            return (row, events.Select(x => x.Type).ToArray());
        }

        await Check();
        f.Advance(30); await Change(1);
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await Check()).NextConfirmationAtUtc);
        f.Advance(30); await Change(2);
        Assert.False((await Check()).Incomplete);
        var (row, events) = await Read();
        Assert.Equal(("open", start, MonitorDiscrepancyConditionKindsTests.Latest(kind, 2)),
            (row.State, row.OpenedAtUtc, MonitorDiscrepancyConditionKindsTests.Shown(kind, row.Details)));
        Assert.Equal(["discrepancy.opened"], events);

        // Open: the details follow the evidence in place and nothing more is announced.
        f.Advance(60); await Change(3);
        await Check();
        (row, events) = await Read();
        Assert.Equal(("open", start, MonitorDiscrepancyConditionKindsTests.Latest(kind, 3)),
            (row.State, row.OpenedAtUtc, MonitorDiscrepancyConditionKindsTests.Shown(kind, row.Details)));
        Assert.Equal(["discrepancy.opened"], events);

        // The condition clears: resolved and announced, as before.
        f.Advance(60); MonitorDiscrepancyConditionKindsTests.Clear(f, kind);
        await Check();
        (row, events) = await Read();
        Assert.Equal("resolved", row.State);
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved"], events);
    }
}
