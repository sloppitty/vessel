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

## Delivery status, November 15, 2026

#85 and #86 are merged through PRs #130 and #131. The remaining stack is:

1. PR #132 for #87, based on `main`.
2. PR #133 for #96, based on `fix/health-87-prices`.
3. PR #134 for #97, based on `fix/health-96-monitor-admission`.

After each squash merge, rebase the next branch onto updated main and retarget
its PR. Only each PR's own issue has an automatic closing reference.

Each layer received an independent adversarial Sol review. #87's missing tick
propagation and #97's pending-claim unmount race are corrected with regressions;
#96's review found no blockers.

The rebased combined `scripts/check.sh` passed with SDK 10.0.302, full-ICU Node,
disposable PostgreSQL 18 and RabbitMQ, with build/test concurrency capped at two.
It passed 133 operator tests, 76 prototype tests, 4,459 backend tests and 4,457
frontend tests, plus typecheck, lint and production build. The seven explicit
skips are five opt-in live-venue cases and two opt-in S3 cases, not database or
broker tests. The corrected script reported seven skips after backend execution
and on its final line.

The separate end-to-end `--no-db` rerun remains outstanding; the earlier stopped
run is not a pass. The post-correction #86 independent re-check failed with a
provider rate limit, not an approval. Neither is represented as completed by
these three PRs. No release, deployment or owner's database change occurred.

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
