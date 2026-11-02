using Vessel.Application.Monitoring;
using Vessel.Worker;

namespace Vessel.Tests;

/// <summary>
/// Phase 2, item 9c (Sol round 1): the scheduler's background passes (backfill and funding) keep their place across ticks,
/// so the candidates they skip never hide the accounts after them.
/// </summary>
public sealed class DueQueueTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private sealed class Source(int count)
    {
        public List<DueMonitorCandidate> Rows { get; } = Enumerable.Range(0, count)
            .Select(i => new DueMonitorCandidate(Guid.NewGuid(), Guid.NewGuid(), "hyperliquid", At.AddSeconds(i))).ToList();
        public List<DueMonitorKey?> Calls { get; } = [];

        // Like the store: every row after the key, in key order, one page at a time.
        public Task<IReadOnlyList<DueMonitorCandidate>> Page(DueMonitorKey? after)
        {
            Calls.Add(after);
            return Task.FromResult<IReadOnlyList<DueMonitorCandidate>>(
                Rows.Where(r => after is null || r.DueAt > after.DueAt).Take(100).ToList());
        }
    }

    [Fact]
    public async Task Skipped_candidates_never_hide_an_account_after_a_full_page_of_them()
    {
        // A suspended owner's 250 accounts come first; the active owner's account sorts after them.
        var source = new Source(251);
        var active = source.Rows[^1];
        var queue = new DueQueue();
        DueMonitorCandidate? claimed = null;
        await queue.RunAsync(source.Page, () => claimed is not null, c =>
        {
            if (c == active) claimed = c;
            return Task.CompletedTask;
        });
        Assert.Equal(active, claimed);
        Assert.Equal(3, source.Calls.Count);
    }

    [Fact]
    public async Task A_full_tick_keeps_its_place_and_the_next_tick_continues_after_it()
    {
        var source = new Source(3);
        var queue = new DueQueue();
        var claimed = new List<DueMonitorCandidate>();
        var slots = 0;
        for (var tick = 0; tick < 3; tick++)
        {
            slots = 1;
            await queue.RunAsync(source.Page, () => slots == 0, c =>
            {
                claimed.Add(c);
                slots--;
                return Task.CompletedTask;
            });
        }
        // The rows stay due in this source, so only the continuation moves the pass on.
        Assert.Equal(source.Rows, claimed);
        // Full from the start: nothing is read.
        source.Calls.Clear();
        await queue.RunAsync(source.Page, () => true, _ => Task.CompletedTask);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task A_tick_that_began_part_way_starts_again_from_the_beginning_once_and_the_page_budget_bounds_a_tick()
    {
        var source = new Source(2);
        var queue = new DueQueue();
        var seen = new List<DueMonitorCandidate>();
        Task See(DueMonitorCandidate c) { seen.Add(c); return Task.CompletedTask; }
        // From the beginning: both rows, then the end, where it stops.
        await queue.RunAsync(source.Page, () => false, See);
        Assert.Equal(source.Rows, seen);
        Assert.Equal([null, source.Rows[1].Key], source.Calls);
        // From part way: the rest, the end, then once more from the beginning to the end.
        seen.Clear();
        await queue.RunAsync(source.Page, () => seen.Count == 1, See);
        source.Calls.Clear();
        seen.Clear();
        await queue.RunAsync(source.Page, () => false, See);
        Assert.Equal([source.Rows[1], source.Rows[0], source.Rows[1]], seen);
        Assert.Equal([source.Rows[0].Key, source.Rows[1].Key, null, source.Rows[1].Key], source.Calls);
        // Nothing claimable: a tick reads at most the page budget and the next one continues after the last row it examined.
        var many = new Source(MonitorScheduler.CandidatePagesPerTick * 100 + 50);
        var skipping = new DueQueue();
        await skipping.RunAsync(many.Page, () => false, _ => Task.CompletedTask);
        Assert.Equal(MonitorScheduler.CandidatePagesPerTick, many.Calls.Count);
        await skipping.RunAsync(many.Page, () => false, _ => Task.CompletedTask);
        Assert.Equal(many.Rows[MonitorScheduler.CandidatePagesPerTick * 100 - 1].Key, many.Calls[MonitorScheduler.CandidatePagesPerTick]);
    }
}
