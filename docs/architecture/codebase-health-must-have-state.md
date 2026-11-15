# Codebase health must-haves

Triage recorded November 15, 2026 against main `20ec2a51` and issue #129.
This is a bounded first delivery, not completion of the whole index.

## First stack

Merge from top to bottom. Each PR targets the preceding branch so its diff
contains only its own fix. These fixes can be implemented independently.

| Order | Issue | Scope | Why now |
| --- | --- | --- | --- |
| 1 | #85 | Fail checks when database coverage is missing; explicit no-database mode | Every later fix needs trustworthy verification. |
| 2 | #86 | Refuse builder-blind history pages and continuity proofs | A successful partial read can permanently skip actual fills. |
| 3 | #87 | Exact-decimal price formatting across the journal | Rounded prices can hide entry/exit differences while returns remain visible. |
| 4 | #96 | Recover expired claimed monitor work at Refresh admission | A killed worker must not indefinitely block recovery; retain write fencing. |
| 5 | #97 | Real notification delivery while in Paper mode | With no external channel, the only open tab must still receive protection warnings. |

No financial calculation policy, schema migration, deployment or release is
part of this stack. Independent Sol reviews and isolated checks are required
before handoff. Test failures and skipped dependencies are reported, not counted
as passes.

## Next work

1. **#88, golden journal.** Establish hand-checked cross-reader identities before
   changing calculation or snapshot semantics. Known-defect assertions must name
   their issue rather than preserve a wrong number.
2. **#89, then #91.** Persist position completeness, then make snapshot and wallet
   reads consistent and distinguish an unread wallet from an empty one. These
   touch the same store and must not run concurrently.
3. **#94, then #95.** Restore missing-stop checks despite history gaps, then narrow
   incomplete-book coverage to the affected native contracts. These touch the
   same observation method and must not run concurrently. Any prerequisite
   extraction stays limited to what these fixes need.
4. **#92.** Refresh the Plays projection when missing-execution facts change and
   document the existing rebuild command for upgrade repair.
5. **#93.** Align Overview exclusions and make history/freshness claims explicit.
6. **#90 needs the owner's decision.** The recommendation is a remaining
   average-cost basis for unrealized P&L while retaining the all-entry display
   average. It is not approved by this triage. Do not silently choose it.

#90 can move forward once that decision is recorded. #88 should precede all
remaining numerical fixes. Keep #129 open until every must-have is resolved.
