using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

public sealed class MonitorDiscrepancyStopConfirmationPostgresTests
{
    [PostgresFact] // M2 / D-19, through persisted records: confirms on the invariant, opens once, shows the latest quantities.
    public async Task An_undersized_stop_whose_quantities_change_confirms_once_with_its_latest_evidence()
    {
        await using var database = await DiscrepancyDatabase.Create();
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = .5m });
        await database.Seed(f);
        var start = f.Clock.Now;
        async Task<MonitorEvaluationResult> Check() { await using var scope = database.Scope(f); return await scope.Evaluate(f.Observation()); }
        async Task ScaleIn(decimal added, decimal stop)
        {
            MonitorDiscrepancyStopConfirmationTests.ScaleIn(f, added, stop);
            await using var scope = database.Scope(f);
            scope.Db.Add(f.Store.Fills[^1]);
            await scope.Db.SaveChangesAsync();
        }

        await Check();
        f.Advance(30); await ScaleIn(.2m, .6m);
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await Check()).NextConfirmationAtUtc);
        f.Advance(30); await ScaleIn(.2m, .7m);
        Assert.False((await Check()).Incomplete);
        await using (var read = database.Scope(f))
        {
            var row = Assert.Single(await read.Service.ReadAsync(f.Account.Id, f.Play.Id, default));
            Assert.Equal(("protection.short", "open", "warning", start), (row.Kind, row.State, row.Severity, row.OpenedAtUtc));
            Assert.Equal(("1.4", "0.7"), (row.Details.GetProperty("quantity").GetString(), row.Details.GetProperty("stopQuantity").GetString()));
            var opened = Assert.Single((await EventReads.ReadAsync(read.Db, 0, 100, default)).Events);
            Assert.Equal(("discrepancy.opened", "warning", row.Id), (opened.Type, opened.Severity, opened.Subject.DiscrepancyId));
            Assert.Equal("0.7", opened.Data.GetProperty("details").GetProperty("stopQuantity").GetString());
        }

        // Open: the details follow the evidence in place and nothing more is announced.
        f.Advance(60); await ScaleIn(.2m, .8m);
        await Check();
        await using (var read = database.Scope(f))
        {
            var row = Assert.Single(await read.Service.ReadAsync(f.Account.Id, f.Play.Id, default));
            Assert.Equal(("open", start, "1.6", "0.8"), (row.State, row.OpenedAtUtc, row.Details.GetProperty("quantity").GetString(),
                row.Details.GetProperty("stopQuantity").GetString()));
            Assert.Single((await EventReads.ReadAsync(read.Db, 0, 100, default)).Events);
        }

        // The stop now covers the position: resolved and announced, as before.
        f.Advance(60); f.Stop(o => o with { RemainingSize = 1.6m });
        await Check();
        await using (var read = database.Scope(f))
        {
            Assert.Equal("resolved", Assert.Single(await read.Service.ReadAsync(f.Account.Id, f.Play.Id, default)).State);
            Assert.Equal(["discrepancy.opened", "discrepancy.resolved"],
                (await EventReads.ReadAsync(read.Db, 0, 100, default)).Events.Select(x => x.Type).ToArray());
        }
    }
}
