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
single-flight cancellation, transaction snapshots and migration rollback.

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
