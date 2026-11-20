# Backend rules

The top-level [`AGENTS.md`](../AGENTS.md) applies first. How to run, test and
configure: [`README.md`](README.md); structure: the
[backend guide](../docs/architecture/backend-guide.md).

## Boundaries

- Domain and Application never reference Wolverine, RabbitMQ or other concrete
  infrastructure. Wolverine and RabbitMQ code lives in `Vessel.Messaging`; feature
  modules register their own topology and hosted services.
- A bounded Application-owned `IUnitOfWork` owns saves and the one commit. Nested
  stores never commit or dispatch, and object-storage I/O refuses inside any unit
  or transaction.
- PostgreSQL is the record and RabbitMQ the transport. Events come from Vessel
  state changes, written in the same transaction, never from re-read rows.
- Use cases check venue capabilities, never venue names. Venue adapters translate
  their encoding into normalized facts; matching, lifecycle and sizing never read
  a venue's encoding, and net-of-fee P&L is never reduced by its fee twice.
- Shared strict JSON and bounded HTTP helpers live in `Venues/Common`; each venue
  registers its HTTP clients under its own names.

## Data

- Every query and write is owner-scoped; keep account and sync locking.
- Financial values are exact decimal strings. Unknown stays null, a missing fee
  is not a zero fee, and disabled accounts stay out of financial reports.
- A price touch, candle or stream update is an observation, never a fill.
  Discrepancies are flagged, never corrected.
- Fills belong to an order only through the shared `OrderKey`/`LinkedFills` rule,
  never the external order ID alone. Play order links reference `ImportedOrderId`.
- Only venue-reported leverage counts; never infer it from margin, notional, plan
  or venue maximum.
- Paper data never reaches real financial readers or actual facts; venue-fact
  endpoints answer paper requests with 409.

## Secrets and sign-in

- Venue credentials are encrypted with the server-only AES-GCM key ring, per
  owner, account and purpose, through a write-only API. Only verified read-only
  tokens are accepted. Verification runs outside database transactions and locks.
- Sign-in is by wallet only. API tests authenticate through the test-only header
  scheme; never add a bearer path to production code.

## Migrations

- Create migrations with the EF tools; apply them only through tests against the
  throwaway database. Tests migrate unique temporary schemas and drop only those.
- The API and workers never migrate or provision at startup; `migrate` and
  `provision-messaging` are explicit steps.
- A migration that needs an upgrade step says so in the pull request and the
  release notes.
