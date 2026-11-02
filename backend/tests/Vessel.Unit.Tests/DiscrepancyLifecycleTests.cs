using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>The discrepancy state machine, table-driven over every record shape × input.</summary>
public sealed class DiscrepancyLifecycleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    private static readonly DateTimeOffset Historical = Now.AddDays(-9);

    /// <summary>Record shapes. All were last decided 8 days ago; baseline evidence is A unless noted.</summary>
    private static PlayDiscrepancy Record(string shape)
    {
        var row = new PlayDiscrepancy { Kind = "position.excess", SubjectKey = "s", Severity = "warning",
            Fingerprint = "A", Details = """{"q":"A"}""", LastObservedAtUtc = Now.AddDays(-8), Revision = 3 };
        void Pending(string fp, bool running) { row.PendingFingerprint = fp; row.PendingSinceUtc = running ? Now.AddSeconds(-60) : null; }
        switch (shape)
        {
            case "hidden-candidate": row.State = "candidate"; Pending("A", true); break;
            case "announced-candidate": row.State = "candidate"; Pending("A", false); Announced("unverifiable"); break;
            case "open": row.State = "open"; Announced("opened"); break;
            case "open-unverifiable": row.State = "open"; Announced("unverifiable"); break;
            case "resolved": row.State = "resolved"; row.ResolvedAtUtc = Historical; Announced("resolved"); row.Fingerprint = "Z"; break;
            case "resolved-pending": row.State = "resolved"; row.ResolvedAtUtc = Historical; Announced("resolved"); row.Fingerprint = "Z"; Pending("A", true); break;
            case "resolved-unverifiable": row.State = "resolved"; row.ResolvedAtUtc = Historical; Announced("unverifiable"); row.Fingerprint = "Z"; Pending("A", false); break;
            case "dismissed": row.State = "dismissed"; Announced("dismissed"); break;
            case "dismissed-unverifiable": row.State = "dismissed"; Announced("unverifiable"); Pending("B", false); break;
            case "hidden-resolved": row.State = "resolved"; row.ResolvedAtUtc = Historical; row.Fingerprint = "Z"; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
        return row;

        void Announced(string transition)
        {
            row.PublishedTransition = transition;
            row.PublishedRevision = row.Revision;
            if (transition == "unverifiable") row.UnverifiableAtUtc = Now.AddDays(-1);
        }
    }

    private static DiscrepancyInput Input(string input) => input switch
    {
        "present-A" => DiscrepancyInput.Present(Now, Now, "A", """{"q":"A"}""", []),
        "present-B" => DiscrepancyInput.Present(Now, Now, "B", """{"q":"B"}""", []),
        "absent" => DiscrepancyInput.Absent(Now, Now, "Z2", """{"q":"none"}""", []),
        "incomplete" => DiscrepancyInput.Unknown(Now),
        "expiry" => DiscrepancyInput.Expiry(Now, TimeSpan.FromDays(7)),
        "expiry-early" => DiscrepancyInput.Expiry(Now, TimeSpan.FromDays(30)),
        "dismiss" => DiscrepancyInput.Dismissal(Now, "accepted"),
        _ => throw new ArgumentOutOfRangeException(nameof(input))
    };

    /// <summary>state, published transition (or "-"), visible, has scheduling work, unverifiable outcome.</summary>
    [Theory]
    // Fresh present, matching the pending/baseline evidence A.
    [InlineData("hidden-candidate", "present-A", "open", "opened", true, true, false)]
    [InlineData("announced-candidate", "present-A", "candidate", "-", true, true, false)]
    [InlineData("open", "present-A", "open", "-", true, true, false)]
    [InlineData("open-unverifiable", "present-A", "open", "-", true, true, false)]
    [InlineData("resolved", "present-A", "resolved", "-", true, true, false)]
    [InlineData("resolved-pending", "present-A", "open", "opened", true, true, false)]
    [InlineData("resolved-unverifiable", "present-A", "resolved", "-", true, true, false)]
    [InlineData("dismissed", "present-A", "dismissed", "-", true, false, false)]
    [InlineData("dismissed-unverifiable", "present-A", "dismissed", "dismissed", true, false, false)]
    [InlineData("hidden-resolved", "present-A", "resolved", "-", false, true, false)]
    // Fresh present with different evidence B.
    [InlineData("hidden-candidate", "present-B", "candidate", "-", false, true, false)]
    [InlineData("open", "present-B", "open", "-", true, true, false)]
    [InlineData("dismissed", "present-B", "dismissed", "-", true, true, false)]
    [InlineData("dismissed-unverifiable", "present-B", "dismissed", "-", true, true, false)]
    // Fresh absent: every pending announcement gets its close.
    [InlineData("hidden-candidate", "absent", "resolved", "-", false, false, false)]
    [InlineData("announced-candidate", "absent", "resolved", "resolved", true, false, false)]
    [InlineData("open", "absent", "resolved", "resolved", true, false, false)]
    [InlineData("open-unverifiable", "absent", "resolved", "resolved", true, false, false)]
    [InlineData("resolved", "absent", "resolved", "-", true, false, false)]
    [InlineData("resolved-pending", "absent", "resolved", "-", true, false, false)]
    [InlineData("resolved-unverifiable", "absent", "resolved", "resolved", true, false, false)]
    [InlineData("dismissed", "absent", "resolved", "-", true, false, false)]
    [InlineData("dismissed-unverifiable", "absent", "resolved", "resolved", true, false, false)]
    [InlineData("hidden-resolved", "absent", "resolved", "-", false, false, false)]
    // Incomplete evidence changes nothing public and keeps outstanding work.
    [InlineData("hidden-candidate", "incomplete", "candidate", "-", false, true, false)]
    [InlineData("announced-candidate", "incomplete", "candidate", "-", true, false, true)]
    [InlineData("open", "incomplete", "open", "-", true, true, false)]
    [InlineData("open-unverifiable", "incomplete", "open", "-", true, false, true)]
    [InlineData("resolved", "incomplete", "resolved", "-", true, false, false)]
    [InlineData("resolved-pending", "incomplete", "resolved", "-", true, true, false)]
    [InlineData("resolved-unverifiable", "incomplete", "resolved", "-", true, false, true)]
    [InlineData("dismissed", "incomplete", "dismissed", "-", true, false, false)]
    [InlineData("dismissed-unverifiable", "incomplete", "dismissed", "-", true, false, true)]
    [InlineData("hidden-resolved", "incomplete", "resolved", "-", false, false, false)]
    // Expiry ends outstanding work without a decision, once.
    [InlineData("hidden-candidate", "expiry", "candidate", "unverifiable", true, false, true)]
    [InlineData("announced-candidate", "expiry", "candidate", "-", true, false, true)]
    [InlineData("open", "expiry", "open", "unverifiable", true, false, true)]
    [InlineData("open-unverifiable", "expiry", "open", "-", true, false, true)]
    [InlineData("resolved", "expiry", "resolved", "-", true, false, false)]
    [InlineData("resolved-pending", "expiry", "resolved", "unverifiable", true, false, true)]
    [InlineData("resolved-unverifiable", "expiry", "resolved", "-", true, false, true)]
    [InlineData("dismissed", "expiry", "dismissed", "-", true, false, false)]
    [InlineData("dismissed-unverifiable", "expiry", "dismissed", "-", true, false, true)]
    [InlineData("hidden-resolved", "expiry", "resolved", "-", false, false, false)]
    [InlineData("hidden-candidate", "expiry-early", "candidate", "-", false, true, false)]
    [InlineData("open", "expiry-early", "open", "-", true, true, false)]
    [InlineData("resolved-pending", "expiry-early", "resolved", "-", true, true, false)]
    // Dismissal closes any pending announcement.
    [InlineData("announced-candidate", "dismiss", "dismissed", "dismissed", true, false, true)]
    [InlineData("open", "dismiss", "dismissed", "dismissed", true, false, false)]
    [InlineData("open-unverifiable", "dismiss", "dismissed", "dismissed", true, false, true)]
    [InlineData("resolved-unverifiable", "dismiss", "dismissed", "dismissed", true, false, true)]
    [InlineData("dismissed", "dismiss", "dismissed", "-", true, false, false)]
    [InlineData("dismissed-unverifiable", "dismiss", "dismissed", "dismissed", true, false, true)]
    public void Every_state_and_input_has_one_defined_transition(string shape, string input, string state, string published,
        bool visible, bool work, bool unverifiable)
    {
        var row = Record(shape);
        var revision = row.Revision;
        var change = DiscrepancyLifecycle.Apply(row, Input(input));
        Assert.False(change.Rejected);
        Assert.Equal(state, row.State);
        Assert.Equal(published == "-" ? null : published, change.Publish);
        Assert.Equal(visible, row.Visible);
        Assert.Equal(work, row.HasWork);
        Assert.Equal(unverifiable, row.UnverifiableAtUtc is not null);
        if (change.Publish is not null)
        {
            Assert.Equal(revision + 1, row.Revision);
            Assert.Equal((change.Publish, row.Revision), (row.PublishedTransition!, row.PublishedRevision!.Value));
        }
        // The historical resolution survives a closing event on an already resolved record.
        if (shape.StartsWith("resolved") && input == "absent") Assert.Equal(Historical, row.ResolvedAtUtc);
    }

    [Theory]
    [InlineData("hidden-candidate")]
    [InlineData("resolved")]
    [InlineData("resolved-pending")]
    [InlineData("hidden-resolved")]
    public void Dismissal_without_a_pending_announcement_is_rejected(string shape)
    {
        var row = Record(shape);
        var before = (row.State, row.Revision);
        Assert.True(DiscrepancyLifecycle.Apply(row, Input("dismiss")).Rejected);
        Assert.Equal(before, (row.State, row.Revision));
    }

    [Fact]
    public void Dismissing_an_unverifiable_recurrence_dismisses_its_evidence()
    {
        var row = Record("resolved-unverifiable");
        DiscrepancyLifecycle.Apply(row, Input("dismiss"));
        Assert.Null(DiscrepancyLifecycle.Apply(row, Input("present-A")).Publish);
        Assert.Equal(("dismissed", false), (row.State, row.HasWork));
    }

    [Fact] // Required sequence: candidate → expiry → unverifiable → fresh present → dismissable → fresh absent closes.
    public void Announced_candidate_stays_visible_and_dismissable_and_gets_its_close()
    {
        var row = Record("hidden-candidate");
        Assert.Equal("unverifiable", DiscrepancyLifecycle.Apply(row, Input("expiry")).Publish);
        Assert.Null(DiscrepancyLifecycle.Apply(row, Input("present-A")).Publish);
        Assert.True(row.Visible && row.PublicOpen);
        var dismissable = Clone(row);
        Assert.Equal("dismissed", DiscrepancyLifecycle.Apply(dismissable, Input("dismiss")).Publish);
        Assert.Equal("resolved", DiscrepancyLifecycle.Apply(row, Input("absent")).Publish);
        Assert.True(row.Visible);
    }

    [Fact] // Required sequence: open → resolved → recurrence → expiry → unverifiable → fresh absent closes, history kept.
    public void Unverifiable_recurrence_is_closed_and_keeps_its_historical_resolution()
    {
        var row = Record("open");
        Assert.Equal("resolved", DiscrepancyLifecycle.Apply(row, Input("absent")).Publish);
        var resolvedAt = row.ResolvedAtUtc;
        row.LastObservedAtUtc = Now.AddDays(-8);
        Assert.Null(DiscrepancyLifecycle.Apply(row, Input("present-A")).Publish);
        row.LastObservedAtUtc = Now.AddDays(-8);
        Assert.Equal("unverifiable", DiscrepancyLifecycle.Apply(row, Input("expiry")).Publish);
        Assert.Equal("resolved", DiscrepancyLifecycle.Apply(row, Input("absent")).Publish);
        Assert.Equal(resolvedAt, row.ResolvedAtUtc);
    }

    [Fact] // Required sequence: dismissed announced candidate, unchanged evidence returns → stays dismissed and visible.
    public void Dismissed_announced_candidate_with_unchanged_evidence_stays_dismissed()
    {
        var row = Record("announced-candidate");
        DiscrepancyLifecycle.Apply(row, Input("dismiss"));
        Assert.Null(DiscrepancyLifecycle.Apply(row, Input("present-A")).Publish);
        Assert.Equal(("dismissed", true, false), (row.State, row.Visible, row.HasWork));
    }

    [Theory] // Phase 2, D-19: confirms on the invariant, and the evidence follows the latest observation.
    [InlineData("candidate")]
    [InlineData("resolved")]
    [InlineData("dismissed")]
    public void A_condition_confirmed_on_its_invariant_opens_while_its_evidence_changes(string state)
    {
        var row = Undersized(state);
        Assert.Equal(Now.AddSeconds(60), DiscrepancyLifecycle.Apply(row, Observed("B", 0)).ConfirmationDueAtUtc);
        Assert.Equal(Now.AddSeconds(60), DiscrepancyLifecycle.Apply(row, Observed("C", 30)).ConfirmationDueAtUtc);
        // A candidate's baseline follows its evidence; resolved and dismissed records keep theirs until confirmation.
        Assert.Equal(("C", state == "candidate" ? "C" : "Z"), (row.PendingFingerprint, row.Fingerprint));
        Assert.Equal("opened", DiscrepancyLifecycle.Apply(row, Observed("D", 60)).Publish);
        Assert.Equal(("open", Now, "D", """{"q":"D"}""", (string?)null), (row.State, row.OpenedAtUtc, row.Fingerprint, row.Details, row.PendingFingerprint));
    }

    [Fact] // Every other kind: changed evidence restarts confirmation, as before.
    public void Without_its_condition_changed_evidence_restarts_confirmation()
    {
        var row = Undersized("candidate");
        DiscrepancyLifecycle.Apply(row, Observed("B", 0, false));
        Assert.Equal(Now.AddSeconds(90), DiscrepancyLifecycle.Apply(row, Observed("C", 30, false)).ConfirmationDueAtUtc);
        Assert.Null(DiscrepancyLifecycle.Apply(row, Observed("D", 60, false)).Publish);
        Assert.Equal(("candidate", (DateTimeOffset?)Now.AddSeconds(60)), (row.State, row.PendingSinceUtc));
    }

    [Theory] // Phase 2, D-19: the invariant never bridges an interruption.
    [InlineData("incomplete")]
    [InlineData("expiry")]
    [InlineData("absent")]
    [InlineData("dismissed-evidence")]
    public void A_condition_confirmed_on_its_invariant_still_restarts_after_an_interruption(string interruption)
    {
        var row = Undersized(interruption == "dismissed-evidence" ? "dismissed" : "candidate");
        DiscrepancyLifecycle.Apply(row, Observed("B", 0));
        DiscrepancyLifecycle.Apply(row, interruption switch
        {
            "incomplete" => DiscrepancyInput.Unknown(Now.AddSeconds(30)),
            "expiry" => DiscrepancyInput.Expiry(Now.AddSeconds(30), TimeSpan.FromSeconds(30)),
            "absent" => DiscrepancyInput.Absent(Now.AddSeconds(30), Now.AddSeconds(30), "Y", """{"q":"none"}""", []),
            _ => Observed("Z", 30)
        });
        var restart = DiscrepancyLifecycle.Apply(row, Observed("C", 60));
        Assert.Equal(((string?)null, (DateTimeOffset?)Now.AddSeconds(120)), (restart.Publish, restart.ConfirmationDueAtUtc));
        Assert.Equal(Now.AddSeconds(60), row.PendingSinceUtc);
        Assert.Equal("opened", DiscrepancyLifecycle.Apply(row, Observed("D", 120)).Publish);
        Assert.Equal(("open", Now.AddSeconds(60)), (row.State, row.OpenedAtUtc));
    }

    [Fact] // Phase 2, D-19: the dismissed evidence is the latest pending one, so its return stays dismissed.
    public void Dismissing_an_unverifiable_recurrence_on_its_condition_dismisses_its_latest_evidence()
    {
        var row = Undersized("resolved");
        DiscrepancyLifecycle.Apply(row, Observed("B", 0));
        DiscrepancyLifecycle.Apply(row, Observed("C", 30));
        Assert.Equal("unverifiable", DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Expiry(Now.AddSeconds(60), TimeSpan.FromSeconds(30))).Publish);
        Assert.Equal("dismissed", DiscrepancyLifecycle.Apply(row, DiscrepancyInput.Dismissal(Now.AddSeconds(60), null)).Publish);
        Assert.Equal("C", row.Fingerprint);
        Assert.Null(DiscrepancyLifecycle.Apply(row, Observed("C", 90)).Publish);
        Assert.Equal(("dismissed", false), (row.State, row.HasWork));
    }

    /// <summary>An undersized-stop record whose baseline evidence is Z, last decided ten minutes ago.</summary>
    private static PlayDiscrepancy Undersized(string state)
    {
        var row = new PlayDiscrepancy { Kind = "protection.short", SubjectKey = "play:p", Severity = "warning", State = state,
            Fingerprint = "Z", Details = """{"q":"Z"}""", LastObservedAtUtc = Now.AddMinutes(-10), Revision = 3 };
        if (state != "candidate") (row.PublishedTransition, row.PublishedRevision) = (state, row.Revision);
        return row;
    }

    /// <summary>A fresh present observation <paramref name="seconds"/> after <see cref="Now"/>, confirmed on its condition by default.</summary>
    private static DiscrepancyInput Observed(string fingerprint, int seconds, bool onCondition = true) => DiscrepancyInput.Present(
        Now.AddSeconds(seconds), Now.AddSeconds(seconds), fingerprint, $$"""{"q":"{{fingerprint}}"}""", [], onCondition);

    [Fact] // Phase 2, item 6c: an observation from two reads confirms only when both are the delay after the first.
    public void An_observation_from_several_reads_confirms_only_when_its_earliest_source_is_the_delay_later()
    {
        var row = Undersized("candidate");
        // First: snapshot at 0 s, book at 19 s; the sequence starts at the later.
        Assert.Equal(Now.AddSeconds(79), DiscrepancyLifecycle.Apply(row, Observed("B", 19) with { EarliestSourceAtUtc = Now }).ConfirmationDueAtUtc);
        Assert.Null(DiscrepancyLifecycle.Apply(row, Observed("B", 80) with { EarliestSourceAtUtc = Now.AddSeconds(60) }).Publish);
        Assert.Equal("opened", DiscrepancyLifecycle.Apply(row, Observed("B", 90) with { EarliestSourceAtUtc = Now.AddSeconds(79) }).Publish);
        Assert.Equal(Now.AddSeconds(19), row.OpenedAtUtc);
    }

    private static PlayDiscrepancy Clone(PlayDiscrepancy x) => new()
    {
        Id = x.Id, State = x.State, Fingerprint = x.Fingerprint, Details = x.Details, Revision = x.Revision,
        LastObservedAtUtc = x.LastObservedAtUtc, PendingFingerprint = x.PendingFingerprint, PendingSinceUtc = x.PendingSinceUtc,
        UnverifiableAtUtc = x.UnverifiableAtUtc, PublishedTransition = x.PublishedTransition, PublishedRevision = x.PublishedRevision
    };
}
