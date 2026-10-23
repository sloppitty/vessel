using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, D-19 (finding M2): the fingerprint of an undersized stop covers its quantities, so a stop that stayed smaller
/// than a position while either quantity changed restarted confirmation on every check and never opened. It now confirms on
/// the condition (this Play's linked stop covers less than its open quantity) and keeps the latest evidence. Interruptions,
/// a clearing observation, dismissal and every other kind behave as before.
/// </summary>
public sealed class MonitorDiscrepancyStopConfirmationTests
{
    [Fact] // M2 / D-19: confirms on the invariant.
    public async Task An_undersized_stop_whose_quantities_keep_changing_confirms_on_its_condition()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = .5m });
        var start = f.Clock.Now;
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        // Scaling in: every check sees a larger position and a larger stop, still smaller than the position.
        f.Advance(30); ScaleIn(f, .2m, .6m);
        Assert.Equal(start + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        Assert.Empty(await f.Service.ReadAsync(f.Account.Id, null, default));
        f.Advance(30); ScaleIn(f, .2m, .7m);
        await f.Evaluate();
        var row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default));
        Assert.Equal(("protection.short", "open", "warning", start), (row.Kind, row.State, row.Severity, row.OpenedAtUtc));
        Assert.Equal(("1.4", "0.7"), (row.Details.GetProperty("quantity").GetString(), row.Details.GetProperty("stopQuantity").GetString()));
        var opened = Assert.Single(f.Events.Items);
        Assert.Equal(("discrepancy.opened", "warning", row.Id), (opened.Type, opened.Severity, opened.Subject.DiscrepancyId));
        Assert.Equal("1.4", opened.Data.GetProperty("details").GetProperty("quantity").GetString());
        // An open warning keeps following its evidence silently.
        f.Advance(60); ScaleIn(f, .2m, .8m);
        await f.Evaluate();
        row = Assert.Single(await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default));
        Assert.Equal(("open", "1.6", "0.8"), (row.State, row.Details.GetProperty("quantity").GetString(),
            row.Details.GetProperty("stopQuantity").GetString()));
        Assert.Single(f.Events.Items);
    }

    [Fact]
    public async Task The_condition_ending_resolves_as_before()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = .5m });
        var start = f.Clock.Now;
        await f.Evaluate();
        // A candidate whose stop now covers the position resolves silently and keeps no work.
        f.Advance(30); f.Stop(o => o with { RemainingSize = 1 });
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.short");
        Assert.Equal(("resolved", (string?)null, false), (row.State, row.PendingFingerprint, row.Visible));
        // The condition returning is a new sequence: nothing carries over from before the clearing observation.
        f.Advance(30); f.Stop(o => o with { RemainingSize = .6m });
        Assert.Equal(f.Clock.Now + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        Assert.Equal(("resolved", (DateTimeOffset?)start.AddSeconds(60)), (row.State, row.PendingSinceUtc));
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
        // An open warning whose stop now covers the position resolves, announced once.
        f.Advance(60); f.Stop(o => o with { RemainingSize = 1 });
        await f.Evaluate();
        Assert.Equal("resolved", row.State);
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved"], f.Events.Items.Select(x => x.Type).ToArray());
    }

    [Theory]
    [InlineData("incomplete-book")]
    [InlineData("history-gap")]
    [InlineData("omitted-executions")]
    [InlineData("interrupted-check")]
    public async Task An_unknown_or_interrupted_observation_breaks_the_sequence_as_before(string interruption)
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = .5m });
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.short");
        f.Advance(30);
        if (interruption == "interrupted-check") await f.Service.SettleAsync(f.Account, Guid.NewGuid(), "scheduled", true, default);
        else
        {
            f.OrdersComplete = interruption != "incomplete-book";
            f.HistoryGap = interruption == "history-gap";
            f.Omitted = interruption == "omitted-executions" ? 1 : 0;
            Assert.True((await f.Evaluate()).Incomplete);
            f.OrdersComplete = true; f.HistoryGap = false; f.Omitted = 0;
        }
        // The work is kept, but the sequence restarts from the next fresh observation, whatever its evidence.
        Assert.Equal(("candidate", (DateTimeOffset?)null), (row.State, row.PendingSinceUtc));
        Assert.NotNull(row.PendingFingerprint);
        f.Advance(30); ScaleIn(f, .2m, .6m);
        var restart = f.Clock.Now;
        Assert.Equal(restart + DiscrepancyLifecycle.ConfirmationDelay, (await f.Evaluate()).NextConfirmationAtUtc);
        Assert.Equal(("candidate", (DateTimeOffset?)restart), (row.State, row.PendingSinceUtc));
        Assert.Empty(f.Events.Items);
        f.Advance(60); await f.Evaluate();
        Assert.Equal(("open", restart), (row.State, row.OpenedAtUtc));
    }

    [Fact]
    public async Task A_dismissed_record_stays_dismissed_until_changed_evidence_is_confirmed_as_before()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = .5m });
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.short");
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, "scaling in", default);
        var dismissed = row.Fingerprint;
        // The dismissed evidence returning stays dismissed and keeps no work.
        f.Advance(60); await f.Evaluate();
        Assert.Equal(("dismissed", false), (row.State, row.HasWork));
        // A moved quantity is pending work, not a reopening.
        f.Advance(60); f.Stop(o => o with { RemainingSize = .6m });
        await f.Evaluate();
        Assert.Equal(("dismissed", dismissed, true), (row.State, row.Fingerprint, row.HasWork));
        // The dismissed evidence returning ends that work.
        f.Advance(30); f.Stop(o => o with { RemainingSize = .5m });
        await f.Evaluate();
        Assert.Equal(("dismissed", (string?)null), (row.State, row.PendingFingerprint));
        // Changed evidence confirmed reopens it with a new revision.
        f.Advance(30); f.Stop(o => o with { RemainingSize = .6m });
        await f.Evaluate();
        Assert.Equal("dismissed", row.State);
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved", "discrepancy.opened"], f.Events.Items.Select(x => x.Type).ToArray());
        Assert.Equal("dismissed", f.Events.Items[1].Data.GetProperty("resolution").GetString());
    }

    [Fact] // D-19: the changed evidence of a dismissed record confirms on the condition too.
    public async Task A_dismissed_record_whose_quantities_keep_changing_reopens_with_its_latest_evidence()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = .5m });
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.short");
        await f.Service.DismissAsync(f.Account.Id, f.Play.Id, row.Id, null, default);
        f.Advance(60); ScaleIn(f, .2m, .6m);
        await f.Evaluate();
        Assert.Equal("dismissed", row.State);
        f.Advance(60); ScaleIn(f, .2m, .7m);
        await f.Evaluate();
        var dto = Assert.Single(await f.Service.ReadAsync(f.Account.Id, f.Play.Id, default));
        Assert.Equal(("open", "1.4", "0.7"), (dto.State, dto.Details.GetProperty("quantity").GetString(),
            dto.Details.GetProperty("stopQuantity").GetString()));
        Assert.Equal(["discrepancy.opened", "discrepancy.resolved", "discrepancy.opened"], f.Events.Items.Select(x => x.Type).ToArray());
    }

    [Fact] // D-19 changes the undersized stop only: an excess stop still confirms on its unchanged evidence.
    public async Task Other_kinds_still_confirm_on_their_evidence()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { RemainingSize = 2, ReduceOnly = false });
        await f.Evaluate();
        f.Advance(30); f.Stop(o => o with { RemainingSize = 2.5m });
        await f.Evaluate();
        f.Advance(30); f.Stop(o => o with { RemainingSize = 3 });
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.excess");
        Assert.Equal(("candidate", (DateTimeOffset?)f.Clock.Now), (row.State, row.PendingSinceUtc));
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
    }

    /// <summary>Adds an entry fill and the same venue position, and resizes the linked stop.</summary>
    internal static void ScaleIn(DiscrepancyFixture f, decimal added, decimal stop)
    {
        f.Fill("entry-fill-" + f.Store.Fills.Count, "entry", added, f.Clock.Now.AddSeconds(-1));
        f.Position += added;
        f.Stop(o => o with { RemainingSize = stop });
    }
}

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
