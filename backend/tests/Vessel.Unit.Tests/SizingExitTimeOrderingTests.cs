using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.2 (D-05): a venue-linked Play's outcome closes at its latest valid linked exit, not when a check or
/// refresh detected the closure. One refresh can find several closures at once; their order must come from the fills,
/// never from random Play IDs. Genuine ties keep the Play-ID order and are disclosed.
/// </summary>
public sealed class SizingExitTimeOrderingTests : SizingExitTimeOrderingTestsValues
{

    [Fact]
    public void An_exit_reported_after_its_detection_closes_at_the_detection_time()
    {
        var before = Closed(10, exitMinutes: 120);
        Assert.Equal(Planned.AddMinutes(120), PlayOutcomeCalculator.Outcome(before)!.ClosedAtUtc);
        // A fill timestamp after the check that saw it is not evidence that the Play closed later than it was seen.
        var late = Closed(10, exitMinutes: 60, detected: Planned.AddMinutes(30));
        Assert.Equal(Planned.AddMinutes(30), PlayOutcomeCalculator.Outcome(late)!.ClosedAtUtc);
        Assert.Equal(Planned.AddMinutes(30), PlayExitTime.LatestValidExit(late, Planned.AddMinutes(30)));
        Assert.Equal(Planned.AddMinutes(60), PlayExitTime.LatestExit(late.Fills));
    }

    [Fact]
    public void Fills_that_cannot_settle_the_play_keep_the_detection_time()
    {
        // Linked exits exceed the entry: no valid exit exists, so the close stays at detection (sizing does not score it).
        var facts = Closed(10, exitMinutes: 60);
        var unsettled = facts with { Fills = [.. facts.Fills, facts.Fills[1]] };
        Assert.Null(PlayExitTime.LatestValidExit(unsettled, Detected));
        Assert.Equal(Detected, PlayOutcomeCalculator.CloseTime(unsettled));
        var incomplete = facts with { IncompleteOrders = ["o1"] };
        Assert.Equal(Detected, PlayOutcomeCalculator.CloseTime(incomplete));
    }

    [Fact]
    public void Manual_and_retrospective_closes_keep_their_stored_exit()
    {
        var facts = Closed(10, exitMinutes: 60);
        // A manual ledger's EndedAtUtc is the owner-entered exit; nothing was detected.
        var manual = facts with { ManualSource = true };
        Assert.Equal((Detected, (DateTimeOffset?)null), (PlayOutcomeCalculator.CloseTime(manual), PlayOutcomeCalculator.DetectedAt(manual)));
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var retrospective = Play.Retrospective(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), facts.Play.Plan,
            Planned.AddMinutes(90), Detected, "Retro");
        Assert.Equal(Planned.AddMinutes(90), PlayOutcomeCalculator.CloseTime(new ClosedPlayFacts(retrospective, [], facts.Fills)));
    }

    [Fact]
    public void Ties_count_decided_outcomes_sharing_an_exact_close_time()
    {
        PlayOutcome At(int minutes, decimal net) => new(Guid.NewGuid(), Planned.AddMinutes(minutes), net, 100, null, true);
        Assert.Equal(new CloseTimeTies(0, 0), CloseTimeTies.From([At(1, 1), At(2, -1), At(2, 0)]));
        Assert.Null(CloseTimeTies.From([]).Reason);
        var one = CloseTimeTies.From([At(1, 1), At(1, -1), At(1, 2), At(3, 0)]);
        Assert.Equal((3, 1, "3 results share an exit time; their order is not known."), (one.Outcomes, one.Times, one.Reason));
        // The same instant at another offset is the same exit time.
        var offset = new PlayOutcome(Guid.NewGuid(), Planned.AddMinutes(5).ToOffset(TimeSpan.FromHours(2)), 1, 100, null, true);
        var two = CloseTimeTies.From([At(1, 1), At(1, -1), At(5, -1), offset, At(9, 1)]);
        Assert.Equal((4, 2, "4 results share 2 exit times; their order is not known."), (two.Outcomes, two.Times, two.Reason));
    }
}
