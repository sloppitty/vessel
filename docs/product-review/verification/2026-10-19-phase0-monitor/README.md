# Phase 0 monitor captures (0.4a–0.4c)

October 19, 2026. Captured by `scripts/e2e-full-stack.py` on branch
`t3/phase0-monitor-coverage` at `787dee3`. That branch includes 0.4a incident
visibility, 0.4b Refresh continuity and 0.4c coverage and evidence. The run
used the isolated packaged stack: generated secrets, a synthetic owner and
the fake Hyperliquid venue over TLS. All 177 checks passed, and the stack was
removed afterwards. No owner data is shown.

| File | Shows |
| --- | --- |
| `live-p1-execution.png` | An Open Play with a cancelled stop. It shows the Plays navigation badge, the "Needs attention · 1 critical" header button and panel heading, the fact table with the next action, and the chart stop labelled "resting as of … UTC". |
| `live-account-detail.png` | Account detail with two critical records and a dismissed warning. Each record has a fact table, its orders by level and "Recorded details". |
| `ended-p3-cancelled.png` | A cancelled Play whose entry order still rests: an unverifiable critical record. |
| `ended-account-degraded.png` | A degraded account monitor after an incomplete venue read. |
| `live-p2-mobile.png` | The 390px layout: the chart stop labelled "not seen at the venue" and the degraded monitor panel. |
