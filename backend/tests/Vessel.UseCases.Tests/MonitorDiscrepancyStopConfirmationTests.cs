using static Vessel.Tests.StopConfirmationTestSupport;
using Vessel.Application.Monitoring;
using Vessel.Application.Venues;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Plays;

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

    // #140: an entry that keeps filling after its only stop was cancelled changes the fingerprint's quantity on every check.
    // With no linked stop resting, protection.cancelled confirms on its condition, so the critical it opens instead of
    // protection.missing is not delayed. Its fingerprint and details keep the quantity, and an open record follows it silently.
    [Fact]
    public async Task A_cancelled_stop_with_none_resting_confirms_while_the_entry_keeps_filling()
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "canceled" });
        PartlyFilledEntry(f);
        var start = f.Clock.Now;
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.cancelled");
        var quantities = new List<string?> { Quantity(row) };
        for (var i = 1; i <= 2; i++)
        {
            f.Advance(30); Grow(f, i);
            var result = await f.Evaluate();
            Assert.False(result.Incomplete);
            quantities.Add(Quantity(row));
            // Half a minute is not a confirmation.
            if (i == 1) Assert.Equal(("candidate", (DateTimeOffset?)start), (row.State, row.PendingSinceUtc));
        }
        Assert.Equal(["1", "1.5", "2"], quantities);
        Assert.Equal(("open", "critical", start), (row.State, row.Severity, row.OpenedAtUtc));
        // One cause, one record: the cancelled stop supersedes protection.missing.
        Assert.DoesNotContain(f.Store.Rows, x => x.Kind == "protection.missing" && x.HasWork);
        var opened = Assert.Single(f.Events.Items);
        Assert.Equal(("discrepancy.opened", "critical", row.Id), (opened.Type, opened.Severity, opened.Subject.DiscrepancyId));
        // The open critical shows the latest quantity without announcing it again.
        f.Advance(30); Grow(f, 3);
        await f.Evaluate();
        Assert.Equal(("open", "2.5"), (row.State, Quantity(row)));
        Assert.Single(f.Events.Items);
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("partial")]
    public async Task A_resting_replacement_stop_between_the_observations_breaks_the_confirmation(string replacement)
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "canceled" });
        PartlyFilledEntry(f);
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.cancelled");
        // A linked replacement stop rests at the second check.
        f.Advance(30); Grow(f, 1);
        f.AddOrder("replacement-stop", OrderLinkRole.Stop, replacement == "whole" ? 1.5m : .5m, 90);
        await f.Evaluate();
        var restart = f.Clock.Now;
        // Covering the position ends the candidate; a smaller one is about its size and confirms on its own evidence.
        if (replacement == "whole") Assert.Equal(("resolved", (DateTimeOffset?)null), (row.State, row.PendingSinceUtc));
        else Assert.Equal(("candidate", (DateTimeOffset?)restart), (row.State, row.PendingSinceUtc));
        // It is cancelled in turn: the first observation does not confirm the third.
        f.Advance(30); Grow(f, 2);
        var index = f.VenueOrders.FindIndex(x => x.OrderId == "replacement-stop");
        f.VenueOrders[index] = f.VenueOrders[index] with { Status = VenueOrderStatus.Canceled, VenueStatus = "canceled" };
        await f.Evaluate();
        Assert.NotEqual("open", row.State);
        Assert.Empty(f.Events.Items);
    }

    [Theory]
    [InlineData("incomplete-book")]
    [InlineData("stale")]
    public async Task An_unknown_observation_neither_confirms_nor_resolves_a_cancelled_stop(string reason)
    {
        var f = new DiscrepancyFixture();
        f.Stop(o => o with { Status = VenueOrderStatus.Canceled, VenueStatus = "canceled" });
        PartlyFilledEntry(f);
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "protection.cancelled");
        f.Advance(30); Grow(f, 1);
        if (reason == "incomplete-book") f.OrdersComplete = false; else f.ObservationOffset = TimeSpan.FromMinutes(-6);
        Assert.True((await f.Evaluate()).Incomplete);
        Assert.Equal(("candidate", (DateTimeOffset?)null), (row.State, row.PendingSinceUtc));
        (f.OrdersComplete, f.ObservationOffset) = (true, TimeSpan.Zero);
        f.Advance(30); Grow(f, 2);
        var restart = f.Clock.Now;
        await f.Evaluate();
        Assert.Equal(("candidate", (DateTimeOffset?)restart), (row.State, row.PendingSinceUtc));
        Assert.Empty(f.Events.Items);
        // An open critical stays open on an unknown read.
        f.Advance(30); Grow(f, 3); await f.Evaluate();
        f.Advance(30); Grow(f, 4); await f.Evaluate();
        Assert.Equal(("open", restart), (row.State, row.OpenedAtUtc));
        if (reason == "incomplete-book") f.OrdersComplete = false; else f.ObservationOffset = TimeSpan.FromMinutes(-6);
        f.VenueOrders.Clear();
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
        Assert.Equal(["discrepancy.opened"], f.Events.Items.Select(x => x.Type).ToArray());
    }

    // The entry order is partly filled, so its later fills are not missing executions.
    private static void PartlyFilledEntry(DiscrepancyFixture f)
    {
        var entry = f.Store.Orders.Single(x => x.ExternalOrderId == "entry");
        (entry.OriginalSize, entry.Status, entry.VenueStatus) = (5, "open", "open");
    }

    // One more entry fill, with the venue position following it.
    private static void Grow(DiscrepancyFixture f, int i)
    {
        f.Fill($"entry-fill-{i}", "entry", .5m, f.Clock.Now);
        f.Position += .5m;
    }

    private static string? Quantity(PlayDiscrepancy row) =>
        System.Text.Json.JsonDocument.Parse(row.Details).RootElement.GetProperty("quantity").GetString();

    [Theory] // D-19 and its follow-up change the size kinds only: every other kind still confirms on its unchanged evidence.
    // #140: protection.cancelled confirms on its condition only while no linked stop rests (see above); here a resting
    // replacement stop is too small, so the warning is about its size and its evidence must persist.
    [InlineData("protection.cancelled")]
    [InlineData("protection.orphaned")]
    [InlineData("protection.beyond-liquidation")]
    [InlineData("entry.orphaned")]
    [InlineData("level.drift")]
    [InlineData("position.missing")]
    [InlineData("position.opposite")]
    [InlineData("fill.unlinked")]
    public async Task Other_kinds_still_confirm_on_their_evidence(string kind)
    {
        var f = new DiscrepancyFixture();
        Fingerprinted(f, kind, 0);
        await f.Evaluate();
        f.Advance(30); Fingerprinted(f, kind, 1);
        await f.Evaluate();
        f.Advance(30); Fingerprinted(f, kind, 2);
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == kind);
        Assert.Equal(("candidate", (DateTimeOffset?)f.Clock.Now), (row.State, row.PendingSinceUtc));
        f.Advance(60); await f.Evaluate();
        Assert.Equal("open", row.State);
    }

    /// <summary>The kind's condition, with evidence version <paramref name="version"/>: each version changes its fingerprint.</summary>
    private static void Fingerprinted(DiscrepancyFixture f, string kind, int version)
    {
        void Grow() { f.Fill("entry-fill-" + f.Store.Fills.Count, "entry", .2m, f.Clock.Now.AddSeconds(-1)); f.Position += .2m; }
        switch (kind)
        {
            case "protection.cancelled":
                if (version == 0) { f.Stop(o => o with { Status = VenueOrderStatus.Canceled }); f.AddOrder("replacement-stop", OrderLinkRole.Stop, .5m, 90); }
                else Grow();
                break;
            case "protection.orphaned":
                if (version == 0) f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1)); else f.Stop(o => o with { RemainingSize = 1 - .2m * version });
                break;
            case "protection.beyond-liquidation":
                // Which stops sit beyond the liquidation price is its fingerprint.
                if (version == 0) f.Liquidation = 95; else f.AddOrder("stop-" + version, OrderLinkRole.Stop, .1m, 90 + version);
                break;
            case "entry.orphaned":
                if (version == 0)
                {
                    f.Position = 0;
                    f.Play.MarkClosed("fixture", f.Clock.Now.AddMinutes(-1));
                    f.VenueOrders.Clear();
                    f.Store.Links.Add(f.Link(f.Play, "late-entry", OrderLinkRole.Entry, "e", null, OrderLinkState.Linked, OrderLinkSource.Owner, f.Clock.Now.AddMinutes(-15)));
                    f.VenueOrders.Add(new("late-entry", "BTC", "buy", "Limit", 100, null, false, false, 1, 1, f.Clock.Now.AddMinutes(-15), VenueOrderStatus.Open, "open", f.Clock.Now));
                }
                else f.VenueOrders[0] = f.VenueOrders[0] with { RemainingSize = 1 - .2m * version };
                break;
            case "level.drift": f.Stop(o => o with { TriggerPrice = 80m - version }); break;
            case "position.missing":
                if (version == 0) f.Position = 0; else f.Fill("entry-fill-" + f.Store.Fills.Count, "entry", .2m, f.Clock.Now.AddSeconds(-1));
                break;
            case "position.opposite": f.Position = -1 - .5m * version; break;
            case "fill.unlinked":
                f.Fill("unlinked-" + version, "other-order-" + version, .2m, f.Clock.Now.AddMinutes(version == 0 ? -5 : 0).AddSeconds(-1));
                break;
        }
    }
}
