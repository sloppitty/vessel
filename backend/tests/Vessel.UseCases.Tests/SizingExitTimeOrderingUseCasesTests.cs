using Vessel.Application.Sizing;

namespace Vessel.Tests;

/// <summary>
/// Phase 0 item 0.2 (D-05): a venue-linked Play's outcome closes at its latest valid linked exit, not when a check or
/// refresh detected the closure. One refresh can find several closures at once; their order must come from the fills,
/// never from random Play IDs. Genuine ties keep the Play-ID order and are disclosed.
/// </summary>
public sealed class SizingExitTimeOrderingUseCasesTests : SizingExitTimeOrderingTestsValues
{
    private readonly MemorySizingStore store = new();
    private SizingService Service => new(store, new CoreOwner(owner), TimeProvider.System, TestUnitOfWork.None);

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
