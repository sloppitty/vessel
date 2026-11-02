using Microsoft.EntityFrameworkCore;
using Vessel.Application.Events;

namespace Vessel.Tests;

/// <summary>Fourth review round for PR #46: real expiry of unobservable discrepancy work.</summary>
public sealed class MonitorDiscrepancyRound4Tests
{
    private static readonly int Lifetime = (int)TimeSpan.FromDays(7).TotalSeconds;

    [Fact] // Finding 2
    public async Task Unchanged_evidence_after_dismissing_an_unverifiable_candidate_stays_dismissed()
    {
        var f = Untracked();
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess" && x.State == "candidate");
        Assert.NotEmpty(row.Fingerprint);
        Assert.Contains("\"venueQuantity\":\"1\"", row.Details);
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        await f.Service.DismissAsync(f.Account.Id, null, row.Id, null, default);
        f.Retired = false;
        f.Advance(60); await f.Confirm();
        Assert.Equal("dismissed", row.State);
        Assert.DoesNotContain(f.Events.Items, x => x.Type == "discrepancy.opened");
        // Changed evidence still re-arms it.
        f.Position = 2; f.Advance(60); await f.Confirm();
        Assert.Equal("open", row.State);
    }

    [Fact] // Finding 3
    public async Task Expired_recurrence_of_a_resolved_record_is_unverifiable_and_keeps_its_resolution()
    {
        var f = Untracked();
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess" && x.State == "open");
        f.Position = 0; f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
        var resolvedAt = row.ResolvedAtUtc;
        f.Position = 1; f.Advance(60); await f.Evaluate();
        Assert.True(row.PendingFingerprint is not null);
        f.Retired = true; f.Advance(Lifetime + 60);
        Assert.False((await f.Evaluate()).Incomplete);
        Assert.Equal("resolved", row.State);
        Assert.Equal(resolvedAt, row.ResolvedAtUtc);
        Assert.NotNull(row.UnverifiableAtUtc);
        Assert.Equal("unverifiable", Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default)).Outcome);
        Assert.Single(f.Events.Items, x => x.Type == "discrepancy.unverifiable");
    }

    [Fact] // Finding 3: plain resolved history without pending work is untouched.
    public async Task Plain_resolved_history_is_not_made_unverifiable()
    {
        var f = Untracked();
        await f.Confirm();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess");
        f.Position = 0; f.Advance(60); await f.Evaluate();
        var revision = row.Revision;
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        Assert.Null(row.UnverifiableAtUtc);
        Assert.Equal(revision, row.Revision);
        Assert.Null(Assert.Single(await f.Service.ReadAsync(f.Account.Id, null, default)).Outcome);
        Assert.DoesNotContain(f.Events.Items, x => x.Type == "discrepancy.unverifiable");
    }

    [Fact] // Finding 4
    public async Task Unverifiable_event_is_emitted_once_per_transition_with_a_revision_key()
    {
        var f = Untracked();
        await f.Evaluate();
        var row = f.Store.Rows.Single(x => x.Kind == "position.excess");
        var lastObserved = row.LastObservedAtUtc;
        f.Retired = true; f.Advance(Lifetime + 60); await f.Evaluate();
        f.Advance(600); await f.Evaluate();
        var unverifiable = Assert.Single(f.Events.Items, x => x.Type == "discrepancy.unverifiable");
        Assert.Equal("warning", unverifiable.Severity);
        Assert.Equal($"discrepancy:{row.Id}:{row.Revision}:unverifiable", unverifiable.DedupKey);
        Assert.Equal("candidate", unverifiable.Data.GetProperty("state").GetString());
        Assert.Equal("unverifiable", unverifiable.Data.GetProperty("outcome").GetString());
        Assert.Equal(lastObserved, unverifiable.Data.GetProperty("lastObservedAtUtc").GetDateTimeOffset());
        Assert.Equal(row.UnverifiableAtUtc, unverifiable.Data.GetProperty("unverifiableAtUtc").GetDateTimeOffset());
        Assert.Contains("discrepancy.unverifiable", VesselEventValues.Types);
        // A fresh decision clears the announced candidate's outcome; its clearance is then published too.
        f.Retired = false; f.Position = 0; f.Advance(60); await f.Evaluate();
        Assert.Equal("resolved", row.State);
        Assert.Equal(["discrepancy.unverifiable", "discrepancy.resolved"], f.Events.Items.Select(x => x.Type).ToArray());
    }

    private static DiscrepancyFixture Untracked()
    {
        var f = new DiscrepancyFixture { Position = 1 };
        f.Store.Plays.Clear(); f.Store.Links.Clear(); f.Store.Orders.Clear(); f.Store.Fills.Clear(); f.VenueOrders.Clear();
        return f;
    }
}
