using Vessel.Application.Plays;
using Vessel.Application.Plays.Execution;
using Vessel.Application.Sizing;
using Vessel.Domain.Accounts;
using Vessel.Domain.Markets;
using Vessel.Domain.Plays;
using Vessel.Domain.Sizing;
using Vessel.Domain.Workspace;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.2 (D-05): a venue-linked Play's outcome closes at its latest valid linked exit, not when a check or
/// refresh detected the closure. One refresh can find several closures at once; their order must come from the fills,
/// never from random Play IDs. Genuine ties keep the Play-ID order and are disclosed.
/// </summary>
public sealed class SizingExitTimeOrderingTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    /// <summary>The one refresh that found every closure below.</summary>
    private static readonly DateTimeOffset Detected = Planned.AddDays(1);
    private readonly Guid owner = Guid.NewGuid();
    private readonly MemorySizingStore store = new();
    private SizingService Service => new(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None);

    /// <summary>A long of one unit at 100 that exits <paramref name="exitMinutes"/> after planning with <paramref name="pnl"/>.</summary>
    private ClosedPlayFacts Closed(decimal pnl, int exitMinutes, DateTimeOffset? detected = null)
    {
        var account = new Account(Guid.NewGuid(), owner, "hyperliquid", "Venue");
        var plan = new PlayPlanDocument("long", "margin", "250", "3", null,
            [new PlanEntry("e", "e", "#b9c9e4", "100", "100", [new PlanExit("s", "price", "95", "100")], [new PlanExit("t", "price", "120", "100")])],
            new PlanNotes("", "", "", ""));
        var play = new Play(Guid.NewGuid(), account, new PerpetualInstrument("hyperliquid", "BTC"), InstrumentSource.Venue,
            PlayDocuments.Serialize(plan), Planned);
        var revision = play.MarkPlanned(Planned).Revision;
        play.MarkOpen("Filled.", Planned.AddMinutes(1));
        play.MarkClosed("Exited.", detected ?? Detected);
        ImportedFill Fill(string side, decimal price, decimal closed, int minutes) => new()
        {
            Id = Guid.NewGuid(), Price = price, Quantity = 1, ClosedPnlUsd = closed, Fee = 0, FeeToken = "USDC", PnlBasis = ExecutionFacts.PnlGross,
            OccurredAtUtc = Planned.AddMinutes(minutes), ExternalOrderId = "o" + minutes, ContractId = "BTC", SourceFillId = "f" + minutes, Side = side,
            PositionEffect = ExecutionFacts.Unknown, Direction = "", TransactionHash = "",
        };
        return new ClosedPlayFacts(play, [revision], [new(OrderLinkRole.Entry, Fill(ExecutionFacts.Buy, 100, 0, 1)),
            new(pnl > 0 ? OrderLinkRole.Target : OrderLinkRole.Stop, Fill(ExecutionFacts.Sell, 100 + pnl, pnl, exitMinutes))]);
    }

    /// <summary>
    /// The probe: results that really happened in this order, one hour apart, all found by the same refresh. Before the
    /// fix every outcome closed at the detection time and the ladder followed random Play IDs (Full or Half).
    /// </summary>
    [Theory]
    [InlineData("W,L,L,L", "half", 0, "Half size after 3 losses in a row.")]
    [InlineData("L,L,L,W", "half", 1, "Half size after 3 losses in a row. 1 of 2 wins toward stepping up.")]
    [InlineData("L,L,W,L", "full", 0, "Full size: no losing streak.")]
    public async Task Results_found_by_one_refresh_follow_their_exit_fills_whatever_the_play_ids(string sequence, string level, int wins, string reason)
    {
        var results = sequence.Split(',').Select(r => r == "W" ? 10m : -5m).ToArray();
        for (var round = 0; round < 25; round++)
        {
            store.Closed.Clear();
            // Fresh random Play IDs every round.
            for (var i = 0; i < results.Length; i++) store.Closed.Add(Closed(results[i], 60 * (i + 1)));
            var sizing = await Service.GetAsync(default);
            Assert.Equal((level, wins, reason), (sizing.Exposure.Level, sizing.Exposure.WinsSinceStepDown, sizing.Exposure.Reason));
            // The record lists the newest exit first, at the exit time, not at the shared detection time.
            Assert.Equal(Enumerable.Range(1, results.Length).Reverse().Select(i => Planned.AddMinutes(60 * i)),
                sizing.Record.Recent.Select(r => r.ClosedAtUtc));
            Assert.Equal(results.Reverse().Select(r => r > 0 ? "win" : "loss"), sizing.Record.Recent.Select(r => r.Outcome));
            // The detection time stays beside each outcome as audit data, and nothing is tied.
            Assert.All(sizing.Record.Recent, r => Assert.Equal(Detected, r.DetectedAtUtc));
            Assert.Equal(0, sizing.Exposure.TiedOutcomes);
        }
        // Play.EndedAtUtc keeps the detection time; monitor watches and list ordering read it.
        Assert.All(store.Closed, f => Assert.Equal(Detected, f.Play.EndedAtUtc));
    }

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

    [Fact]
    public async Task Results_sharing_an_exit_time_keep_the_play_id_order_and_are_disclosed()
    {
        store.Closed.Add(Closed(-5, exitMinutes: 60));
        store.Closed.Add(Closed(10, exitMinutes: 120));
        store.Closed.Add(Closed(-5, exitMinutes: 120));
        var sizing = await Service.GetAsync(default);
        Assert.Equal("Full size: no losing streak. 2 results share an exit time; their order is not known.", sizing.Exposure.Reason);
        Assert.Equal(2, sizing.Exposure.TiedOutcomes);
        // The tied pair keeps the stable Play-ID order; nothing economic is invented.
        var tied = store.Closed.Skip(1).Select(f => f.Play.Id).Order().ToList();
        Assert.Equal(tied, sizing.Record.Recent.Take(2).Select(r => r.PlayId!.Value));
    }

    [Fact]
    public async Task Distinct_exit_times_add_no_tie_notice()
    {
        store.Closed.Add(Closed(-5, exitMinutes: 60));
        store.Closed.Add(Closed(10, exitMinutes: 120));
        var sizing = await Service.GetAsync(default);
        Assert.Equal(("Full size: no losing streak.", 0), (sizing.Exposure.Reason, sizing.Exposure.TiedOutcomes));
    }
}
