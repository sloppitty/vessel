using Vessel.Domain.Common;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6b: a backfill pages a gap's window from its start and proves it covered only when the venue still serves
/// the gap's own starting execution; an unknown start is proven only when the whole history fits in the venue's reach.
/// </summary>
public sealed class HistoryBackfillTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private const int Limit = 2000, Reach = 10_000;

    private static AccountHistoryGap Gap(DateTimeOffset? from, DateTimeOffset? to) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), AccountId = Guid.NewGuid(), FromUtc = from, ToUtc = to, OpenedAtUtc = At.AddHours(-1)
    };

    [Fact]
    public void A_backfill_reads_its_gaps_window_in_whole_milliseconds_and_only_an_open_gap_the_venue_may_serve()
    {
        var backfill = HistoryBackfill.For(Gap(At.AddDays(-2).AddTicks(5), At.AddDays(-1).AddTicks(5)), At);
        Assert.Equal((At.AddDays(-2), At.AddDays(-1).AddMilliseconds(1), At.AddDays(-2), true, HistoryBackfill.Queued),
            (backfill.FromUtc, backfill.ToUtc, backfill.CursorUtc, backfill.StartKnown, backfill.State));
        // A gap of unknown range reads the whole history up to now.
        var legacy = HistoryBackfill.For(Gap(null, null), At);
        Assert.Equal((DateTimeOffset.UnixEpoch, At, false), (legacy.FromUtc, legacy.ToUtc, legacy.StartKnown));
        var closed = Gap(At.AddDays(-2), At.AddDays(-1));
        closed.Close(Guid.NewGuid(), At);
        Assert.Throws<DomainRuleException>(() => HistoryBackfill.For(closed, At));
        Assert.Throws<DomainRuleException>(() => HistoryBackfill.For(new AccountHistoryGap { Id = Guid.NewGuid(), FromUtc = At.AddDays(-1),
            ToUtc = At, OpenedAtUtc = At, BeyondVenueReach = true }, At));
    }

    [Fact]
    public void A_window_is_exhausted_only_by_a_page_that_brings_nothing_newer_and_never_by_a_full_page_at_one_time()
    {
        var from = At.AddDays(-2);
        var backfill = HistoryBackfill.For(Gap(from, At.AddDays(-1)), At);
        // A short page still moves the cursor: only a page with nothing newer ends the window.
        Assert.False(backfill.Read(3, from, from.AddMinutes(5), 3, 0, Limit, Reach, At));
        Assert.Equal((from.AddMinutes(5), from), (backfill.CursorUtc, backfill.FirstRawAtUtc!.Value));
        Assert.True(backfill.Read(1, from.AddMinutes(5), from.AddMinutes(5), 0, 0, Limit, Reach, At));
        Assert.Equal((2, 4, 3), (backfill.Pages, backfill.RawRows, backfill.AddedFills));
        // A full page that cannot advance fails, as does reading past the venue's reach.
        var stuck = HistoryBackfill.For(Gap(from, At.AddDays(-1)), At);
        Assert.False(stuck.Read(Limit, from, from, 0, 0, Limit, Reach, At));
        Assert.Equal(HistoryBackfill.Failed, stuck.State);
        var endless = HistoryBackfill.For(Gap(from, At.AddDays(-1)), At);
        for (var i = 1; i <= 7; i++) endless.Read(Limit, from.AddSeconds(i - 1), from.AddSeconds(i), Limit, 0, Limit, Reach, At);
        Assert.Equal(HistoryBackfill.Failed, endless.State);
    }

    [Theory]
    // The venue still serves the gap's starting execution: the whole window is proven.
    [InlineData(0, false, 0, false)]
    // The oldest served row is later: proven from it, and the span before it is beyond the venue's reach.
    [InlineData(30, false, 30, true)]
    public void A_known_start_is_proven_when_the_venue_still_serves_it(int firstMinutes, bool complete, int coveredMinutes, bool narrowed)
    {
        var from = At.AddDays(-2);
        var backfill = HistoryBackfill.For(Gap(from, At.AddDays(-1)), At);
        backfill.Read(2, from.AddMinutes(firstMinutes), from.AddMinutes(60), 2, 0, Limit, Reach, At);
        backfill.Read(1, from.AddMinutes(60), from.AddMinutes(60), 0, 0, Limit, Reach, At);
        Assert.Equal((complete, (DateTimeOffset?)from.AddMinutes(coveredMinutes), narrowed), backfill.Conclude(Reach));
    }

    [Fact]
    public void An_unknown_start_is_proven_only_when_the_whole_history_fits_in_the_venues_reach()
    {
        var small = HistoryBackfill.For(Gap(null, null), At);
        small.Read(5, At.AddDays(-300), At.AddHours(-1), 5, 0, Limit, Reach, At);
        small.Read(1, At.AddHours(-1), At.AddHours(-1), 0, 0, Limit, Reach, At);
        Assert.Equal((true, (DateTimeOffset?)null, false), small.Conclude(Reach));
        var full = HistoryBackfill.For(Gap(null, null), At);
        for (var i = 0; i < 5; i++) full.Read(Limit, At.AddDays(-30 + i), At.AddDays(-29 + i), Limit, 0, Limit, Reach, At);
        full.Read(1, At.AddDays(-25), At.AddDays(-25), 0, 0, Limit, Reach, At);
        Assert.Equal((false, (DateTimeOffset?)At.AddDays(-30), true), full.Conclude(Reach));
        // A failure is counted; three in a row stop the backfill.
        for (var i = 0; i < HistoryBackfill.MaxFailures; i++) small.Failure("The venue read failed.", At);
        Assert.Equal(HistoryBackfill.Failed, small.State);
    }
}
