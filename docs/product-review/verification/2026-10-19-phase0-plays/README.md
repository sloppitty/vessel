# Phase 0 Plays captures (0.1, 0.5a, 0.5b)

October 19, 2026. Captured by `scripts/e2e-full-stack.py` on branch
`t3/phase0-managed-stops` at `bc652f8`. That branch includes 0.1 sizing truth,
0.5a plan-number grammar and 0.5b managed stops. The run used the isolated
packaged stack: generated secrets, a synthetic owner and the fake Hyperliquid
venue over TLS. All 177 checks passed, and the stack was removed afterwards.
The monitor panel here predates 0.4a, which is on a separate branch.

| File | Shows |
| --- | --- |
| `live-p1-execution.png` | An Open Play with a filled entry. The capital context shows "Available" as the same reported figure and as-of time as the Budget. Suggestions collapse to "An entry has filled". The ratios are labelled "Margin / <account> balance". The Summary's signed "All stops" row reads "−165 USDC at risk · From the average fill", and risk is measured against the account balance. |
| `live-p2-mobile.png` | The same workspace at 390px. |
