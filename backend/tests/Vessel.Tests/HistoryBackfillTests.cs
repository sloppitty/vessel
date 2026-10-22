using Vessel.Domain.Common;
using Vessel.Domain.Monitoring;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 6b: a backfill pages from the retained execution at its gap's start to now. Continuity is proven by
/// witnesses: the first page must return that execution again, and every later page the rows the previous one ended on.
/// An unknown start is proven only when the whole history fits in the venue's reach.
/// </summary>
public sealed class HistoryBackfillTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private const int Limit = 3, Reach = 10;

    private static AccountHistoryGap Gap(DateTimeOffset? from, DateTimeOffset? to) => new()
    {
        Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), AccountId = Guid.NewGuid(), FromUtc = from, ToUtc = to, OpenedAtUtc = At.AddHours(-1)
    };

    private static List<(string Id, DateTimeOffset At)> Rows(params (string Id, int Minutes)[] rows) =>
        rows.Select(r => (r.Id, At.AddDays(-2).AddMinutes(r.Minutes))).ToList();

    private static DateTimeOffset T(int minutes) => At.AddDays(-2).AddMinutes(minutes);

    [Fact]
    public void A_backfill_reads_from_its_gaps_start_to_now_in_whole_milliseconds_and_only_an_open_gap_the_venue_may_serve()
    {
        var backfill = HistoryBackfill.For(Gap(At.AddDays(-2).AddTicks(5), At.AddDays(-1)), At.AddTicks(7));
        Assert.Equal((At.AddDays(-2), At, At.AddDays(-2), true, HistoryBackfill.Queued, At.AddTicks(7)),
            (backfill.FromUtc, backfill.ToUtc, backfill.CursorUtc, backfill.StartKnown, backfill.State, backfill.NextPageAtUtc));
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
    public void Pages_continue_from_their_witnesses_and_end_only_on_a_page_with_nothing_newer()
    {
        var backfill = HistoryBackfill.For(Gap(T(0), T(30)), At);
        // The first page starts at the retained execution and returns it again.
        Assert.False(backfill.Read(Rows(("a", 0), ("b", 5), ("c", 10)), T(0), 2, 0, ["a"], At, Limit, Reach, At));
        Assert.Equal((T(10), (bool?)true, T(0), At + HistoryBackfill.Pacing), (backfill.CursorUtc, backfill.StartServed,
            backfill.FirstRawAtUtc!.Value, backfill.NextPageAtUtc));
        Assert.Equal(["c"], backfill.Witnesses());
        // A later page returns the previous page's last row again; it is not counted twice.
        Assert.False(backfill.Read(Rows(("c", 10), ("d", 20), ("e", 20)), T(10), 2, 0, backfill.Witnesses(), At, Limit, Reach, At));
        Assert.Equal((5, T(20)), (backfill.RawRows, backfill.CursorUtc));
        Assert.Equal(["d", "e"], backfill.Witnesses());
        Assert.True(backfill.Read(Rows(("d", 20), ("e", 20)), T(20), 0, 0, backfill.Witnesses(), At, Limit, Reach, At));
        Assert.Equal((true, false, 3, 5, 4), (backfill.Exhausted, backfill.ContinuityLost, backfill.Pages, backfill.RawRows, backfill.AddedFills));
        Assert.Equal(new HistoryBackfill.Proof(false, T(0), At, false), backfill.Conclude(Reach));
    }

    [Fact]
    public void A_page_that_does_not_continue_the_backfill_or_strays_outside_its_window_is_refused()
    {
        var backfill = HistoryBackfill.For(Gap(T(0), T(30)), At);
        // The first page may start before the gap (at its witness), never after it.
        Assert.Throws<DomainRuleException>(() => backfill.Read(Rows(("a", 1)), T(1), 0, 0, [], At, Limit, Reach, At));
        Assert.Throws<DomainRuleException>(() => backfill.Read(Rows(("a", -5)), T(0), 0, 0, [], At, Limit, Reach, At));
        Assert.False(backfill.Read(Rows(("w", -5), ("a", 5)), T(-5), 1, 0, ["w"], At, Limit, Reach, At));
        Assert.Equal((T(-5), T(5)), (backfill.FirstRawAtUtc!.Value, backfill.CursorUtc));
        Assert.Throws<DomainRuleException>(() => backfill.Read(Rows(("a", 5)), T(4), 0, 0, ["a"], At, Limit, Reach, At));
    }

    [Fact]
    public void A_full_page_at_one_time_or_more_rows_than_the_venue_serves_fails_the_backfill()
    {
        var stuck = HistoryBackfill.For(Gap(T(0), T(30)), At);
        Assert.False(stuck.Read(Rows(("a", 0), ("b", 0), ("c", 0)), T(0), 3, 0, ["a"], At, Limit, Reach, At));
        Assert.Equal(HistoryBackfill.Failed, stuck.State);
        var endless = HistoryBackfill.For(Gap(T(0), T(30)), At);
        IReadOnlyCollection<string> witnesses = ["r0"];
        for (var i = 0; endless.IsActive && i < 10; i++)
        {
            endless.Read(Rows(($"r{2 * i}", 2 * i), ($"r{2 * i + 1}", 2 * i + 1), ($"r{2 * i + 2}", 2 * i + 2)), endless.CursorUtc, 2, 0,
                witnesses, At, Limit, Reach, At);
            witnesses = endless.Witnesses();
        }
        Assert.Equal(HistoryBackfill.Failed, endless.State);
    }

    [Fact]
    public void A_start_the_venue_no_longer_serves_is_proven_only_from_the_first_row_read()
    {
        var backfill = HistoryBackfill.For(Gap(T(0), T(30)), At);
        // The retained execution at the start is not returned: the reach has moved past it.
        Assert.False(backfill.Read(Rows(("b", 5), ("c", 10)), T(0), 2, 0, ["a"], At, Limit, Reach, At));
        Assert.True(backfill.Read(Rows(("c", 10)), T(10), 0, 0, backfill.Witnesses(), At, Limit, Reach, At));
        Assert.Equal(new HistoryBackfill.Proof(false, T(5), At, true), backfill.Conclude(Reach));
        // Nothing retained at or before the start: nothing witnesses it either.
        var unanchored = HistoryBackfill.For(Gap(T(0), T(30)), At);
        unanchored.Read(Rows(("b", 5)), T(0), 1, 0, [], At, Limit, Reach, At);
        unanchored.Read(Rows(("b", 5)), T(5), 0, 0, unanchored.Witnesses(), At, Limit, Reach, At);
        Assert.Equal(new HistoryBackfill.Proof(false, T(5), At, true), unanchored.Conclude(Reach));
        // An empty window proves nothing.
        var empty = HistoryBackfill.For(Gap(T(0), T(30)), At);
        empty.Read([], T(0), 0, 0, ["a"], At, Limit, Reach, At);
        Assert.Equal(new HistoryBackfill.Proof(false, null, At, true), empty.Conclude(Reach));
    }

    [Fact]
    public void A_later_page_without_its_witnesses_proves_nothing_after_the_cursor()
    {
        var backfill = HistoryBackfill.For(Gap(T(0), T(30)), At);
        backfill.Read(Rows(("a", 0), ("b", 5), ("c", 10)), T(0), 2, 0, ["a"], At, Limit, Reach, At);
        // The venue dropped "c" between pages: what followed it may be gone too.
        Assert.True(backfill.Read(Rows(("d", 20)), T(10), 1, 0, backfill.Witnesses(), At.AddMinutes(1), Limit, Reach, At));
        Assert.True(backfill.ContinuityLost);
        Assert.Equal(new HistoryBackfill.Proof(false, T(0), T(10), false), backfill.Conclude(Reach));
    }

    [Fact]
    public void An_unknown_start_is_proven_only_when_the_whole_history_fits_in_the_venues_reach_with_continuity_kept()
    {
        var small = HistoryBackfill.For(Gap(null, null), At);
        small.Read(Rows(("a", 0), ("b", 5)), DateTimeOffset.UnixEpoch, 2, 0, [], At, Limit, Reach, At);
        small.Read(Rows(("b", 5)), T(5), 0, 0, small.Witnesses(), At, Limit, Reach, At);
        Assert.Equal(new HistoryBackfill.Proof(true, null, At, false), small.Conclude(Reach));
        var full = HistoryBackfill.For(Gap(null, null), At);
        full.Read(Rows(("r0", 0), ("r1", 1), ("r2", 2)), DateTimeOffset.UnixEpoch, 3, 0, [], At, Limit, Reach, At);
        for (var i = 1; full.IsActive && !full.Exhausted && i < 5; i++)
            full.Read(Rows(($"r{2 * i}", 2 * i), ($"r{2 * i + 1}", 2 * i + 1), ($"r{2 * i + 2}", 2 * i + 2)), full.CursorUtc, 2, 0,
                full.Witnesses(), At, Limit, Reach, At);
        full.Read(Rows(("r10", 10)), full.CursorUtc, 0, 0, full.Witnesses(), At, Limit, Reach, At);
        Assert.Equal((true, 11), (full.Exhausted, full.RawRows));
        // Eleven rows reached the reach of ten: older executions may have been dropped.
        Assert.Equal(new HistoryBackfill.Proof(false, T(0), At, true), full.Conclude(Reach));
        var broken = HistoryBackfill.For(Gap(null, null), At);
        broken.Read(Rows(("a", 0), ("b", 5), ("c", 10)), DateTimeOffset.UnixEpoch, 3, 0, [], At, Limit, Reach, At);
        broken.Read(Rows(("d", 20)), T(10), 1, 0, broken.Witnesses(), At, Limit, Reach, At);
        Assert.Equal(new HistoryBackfill.Proof(false, T(0), T(10), true), broken.Conclude(Reach));
    }

    [Fact]
    public void Failures_are_paced_and_three_in_a_row_stop_the_backfill()
    {
        var backfill = HistoryBackfill.For(Gap(T(0), T(30)), At);
        backfill.Failure("The venue read failed.", At);
        Assert.Equal((1, At + HistoryBackfill.Pacing, HistoryBackfill.Queued), (backfill.Failures, backfill.NextPageAtUtc, backfill.State));
        // A page read resets the count.
        backfill.Read(Rows(("a", 0), ("b", 5)), T(0), 1, 0, ["a"], At, Limit, Reach, At);
        Assert.Equal((0, (string?)null), (backfill.Failures, backfill.Error));
        for (var i = 0; i < HistoryBackfill.MaxFailures; i++) backfill.Failure("The venue read failed.", At);
        Assert.Equal(HistoryBackfill.Failed, backfill.State);
    }
}
