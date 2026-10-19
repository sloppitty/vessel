# Activity and API performance PR verification

October 19, 2026. Scope: [PR #3](https://github.com/sloppitty/vessel/pull/3)
and [PR #6](https://github.com/sloppitty/vessel/pull/6). Three independent Sol
reviewers checked the feature, API performance/security, and their integration;
the parent contrasted findings and reviewed the fixes.

## Confirmed findings and fixes

- **Lost leverage after a snapshot/fill timing gap (#3).** A refresh can read the
  position before it reads executions. If that refresh imports a close/reopen
  after its snapshot, the next identical position snapshot must still create an
  observation, even though those executions are already retained. The refresh
  now checks retained execution timestamps since the previous observation, with
  native-market isolation and an inclusive lower boundary. Idle refreshes still
  stop adding observations once the observation advances past those executions.
- **Activity explanation (#3).** The tooltip now renders the API's notice
  verbatim, including gross-only fee subtraction, net-of-fee handling, nominal
  stablecoin scope and the estimates/not-advice notice.
- **Unbounded startup timeout (#6).** Waiting for cancellation callbacks before
  completing warm-up let blocked callbacks hold readiness beyond its timeout;
  throwing callbacks could fault the hosted service. Readiness now completes
  before detached, fault-observed cleanup. The cancellation source survives
  until both callbacks and work finish. The timeout also covers synchronous
  warm-up work before its first await.
- **Incomplete Activity version (#6).** The previous selected-field hash missed
  changed prices/results/fees and changed coverage even after cache invalidation.
  The version now hashes summary DTOs and coverage, plus retained funding details,
  once per aggregate, without materializing execution rows.
- **PR composition (#6).** The original branches conflict in `ActivityStore` and
  combine a static aggregate method with a request-clock reference. PR #6 now
  depends on #3 explicitly. Open-operation leverage uses retained observations,
  so aggregate results are clock-independent. The cache accounts for leverage
  observations and per-fill leverage allocations, and its revision migration
  runs after the leverage table exists and covers that table too.

Regression cases were added for each corrected behavior. Review also checked
owner/account/native-market isolation, exact entry-notional arithmetic,
missing-execution guards, gross/net fee treatment, session refusal ordering,
single-flight cancellation, transaction snapshots, transaction rollback and migration Down SQL.

## Validation

- PR #3 full backend baseline: 3,391 passed, zero failed, five opt-in live
  venue/S3 checks skipped. Final leverage/sizing suite: 34 passed, including two
  regressions first demonstrated failing against the original refresh code.
- PR #6 focused cache/startup suite before stacking: 32 passed, zero skipped.
  Six Activity version cases and three startup deadline cases first failed on
  the original code. The startup checks include Token and Wallet read-only
  database paths.
- Frontend: 2,447 tests passed; the final affected-file run passed 20 tests.
  Typecheck, lint and the production build passed after the tooltip change.
- Operator scripts: 78 tests passed on both branches. Prototype: 76 passed.
  `scripts/release.py check` passed. JavaScript checks use pinned pnpm 12.8.1;
  backend checks use the pinned .NET SDK 10.0.302 container.

The final combined Release build completed with zero warnings. Its suite passed
3,432 tests, with zero failures and five opt-in venue/S3 skips (3,437 total), using
locked restore and `dotnet test` against throwaway PostgreSQL 18 and RabbitMQ 4. Packaged verification builds the API and
worker from `backend/Dockerfile`; it never publishes an image.

The packaged one-shot migrator applied identity/journal migrations and messaging
provisioning to a new synthetic database. The Activity revision migration was
then explicitly downgraded to `LeverageObservations` and reapplied before the API
started; the seeded executions/observation survived and all 40 revision triggers
were restored. This is an isolated migration check, not an installation rollback
procedure.

Packaged HTTP smoke passed readiness, unauthenticated 401, no-store responses,
unchanged-fact version reuse, observation-only invalidation, corrected result and
version, Activity sizing fallback, Overview and Summary. Startup warm-up finished
with zero failed steps. These are loopback Kestrel/Podman checks; nginx aborts,
real Docker Engine health scheduling and remote-database cancellation are not
verified here.

## Delivery and limits

Merge #3 before #6. Apply journal migrations explicitly before starting upgraded
hosts; this review does not authorize deployment or migration of an existing
installation. The unshipped Activity revision migration has a new timestamp so
it follows `LeverageObservations`. Development databases that already applied its
old ID must be recreated; do not insert replacement migration-history records.

All database/broker verification uses disposable synthetic services. The owner's
database, `.env` and unrelated frontend work are excluded. Live venue access,
real-wallet signing and remote-database cancellation latency are separate checks.

Unnecessary structural changes (including a wider Activity sorting refactor)
were deferred. The fixes stay within the existing feature and module boundaries.

GitHub Actions is enabled, but the repository returned no workflow runs or PR
check results for these pushes at verification time. The checks above were run
locally in isolation; no GitHub CI pass is claimed.
