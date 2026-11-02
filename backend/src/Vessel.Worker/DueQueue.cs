using Vessel.Application.Monitoring;

namespace Vessel.Worker;

/// <summary>
/// Phase 2, item 9c (Sol round 1): one background pass of the scheduler (backfill or funding) pages on from the last
/// candidate it examined, across ticks, so the candidates it skips (suspended owners, capped owners or venues, refused claims)
/// never hide the accounts after them. A tick that began part way starts again from the beginning at the end, once; one
/// that began at the beginning stops there. It stops once the slots are full or after the page budget, keeping its place.
/// </summary>
internal sealed class DueQueue
{
    private DueMonitorKey? continuation;

    public async Task RunAsync(Func<DueMonitorKey?, Task<IReadOnlyList<DueMonitorCandidate>>> page, Func<bool> full,
        Func<DueMonitorCandidate, Task> claim)
    {
        var wrap = continuation is not null;
        for (var pages = 0; !full() && pages < MonitorScheduler.CandidatePagesPerTick; pages++)
        {
            var due = await page(continuation);
            if (due.Count == 0)
            {
                continuation = null;
                if (!wrap) return;
                wrap = false;
                continue;
            }
            foreach (var candidate in due)
            {
                if (full()) return;
                continuation = candidate.Key;
                await claim(candidate);
            }
        }
    }
}
