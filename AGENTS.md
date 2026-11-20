# Vessel

Vessel is a self-hosted trading diary. It links a trader's plans (Plays) to
read-only execution facts imported from venues (Hyperliquid, RISEx, Lighter) or
entered by hand, and to later review. It never places orders or gives advice.
Read `docs/project-brief.md` and `CONTEXT.md` before changing product terminology
or scope; `docs/app-status.md` describes what the app does today.

## Hard rules

Never:

1. Read, print or expose `.env` files, secrets (tokens, passwords, credential
   keys) or the owner's database values, and never put secrets or keys in Vite,
   request URLs, logs, browser storage, images or chat.
2. Apply migrations to the owner's database or a live one. Tests use only the
   throwaway test database; migrations are explicit one-shot steps, never run at
   API or worker startup.
3. Push a tag, publish images, create a GitHub Release or deploy unless the owner
   asks. A release does not authorize deployment or changes to the owner's database.
4. Use npm or yarn. pnpm is the only JavaScript package manager.
5. Add CI triggers on branches or pull requests. CI runs only on release tags.
6. Treat a price touch or market observation as a fill.
7. Invent a thesis, intent, initial risk or leverage for imported history, or
   turn imported operations into Plays or links without the owner. Unknown values
   stay null (never zero or guessed) and exact decimal strings stay intact.
8. Make Domain or Application reference concrete infrastructure (such as
   Wolverine or RabbitMQ), or put business-facing contracts in infrastructure.
9. Enforce a unique active Play per account and instrument.
10. Move the approved layout's sections. Themes change colours and radius only.
11. Accept a trading-capable venue credential, or place or sign orders at a
    venue. Venue access is read-only.
12. Reintroduce API-token or bearer sign-in. Vessel signs in with wallets only.

## Layout

| Path | Contents |
| --- | --- |
| `backend/` | ASP.NET Core 10 solution `Vessel.slnx`: `src/` (Domain, Application, Infrastructure, Persistence, Identity, Messaging, Api, Worker) and `tests/`. Rules: [`backend/AGENTS.md`](backend/AGENTS.md) |
| `frontend/` | React, Vite and TypeScript app (`src/features/*`, `src/api`, `src/components`). Rules: [`frontend/AGENTS.md`](frontend/AGENTS.md) |
| `deploy/` | Compose files and the operator guide. Rules: [`deploy/AGENTS.md`](deploy/AGENTS.md) |
| `scripts/` | Dev runner, checks, release and verification scripts with their Python tests |
| `docs/architecture/` | Feature contracts (`*-contract.md`, plans) and verified progress (`*-state.md`) |
| `docs/decisions.md` | Owner decisions, newest first, with what superseded what |
| `docs/design/`, `prototype/` | Design records and the disposable prototype; prototype interactions are not production features |
| `.agents/skills/` | The `vessel-release` skill |

## Build, run and test

Tooling: Node 24 with full ICU, pnpm (version pinned in `package.json`, install
with Corepack), .NET SDK pinned in `global.json`, Docker. Setup and the dev
servers are in [`docs/development.md`](docs/development.md).

```sh
pnpm install --frozen-lockfile
dotnet build backend/Vessel.slnx -c Release          # must stay at 0 warnings
pnpm dev                                             # API, workers and frontend (needs the private .env)
```

One backend test, one frontend test, the scripts:

```sh
dotnet test backend/tests/Vessel.Unit.Tests -c Release --filter "FullyQualifiedName~CsvTests"
pnpm --filter vessel-frontend test src/features/plays/ExecutionPanel.test.tsx
python3 -m unittest discover -s scripts -p 'test_*.py'
```

Test projects: `Vessel.Unit.Tests`, `Vessel.UseCases.Tests`,
`Vessel.Venues.Tests`, `Vessel.Api.Tests`, `Vessel.Integration.Tests` (see
`backend/README.md` for what goes where). Database tests need
`Vessel_TEST_POSTGRES`: `bash scripts/test-db.sh` starts a throwaway PostgreSQL
and prints the export and its cleanup command. Without it they skip. Frontend
checks: `pnpm --filter vessel-frontend typecheck`, `lint`, `test`, `build`.
Everything at once: `pnpm check`.

Without the pinned SDK or a full-ICU Node, run the same commands in the official
images from the repository root (add `--security-opt label=disable` on SELinux):

```sh
docker run --rm --network host -e Vessel_TEST_POSTGRES -v "$PWD":"$PWD" -w "$PWD" \
  mcr.microsoft.com/dotnet/sdk:10.0.302 \
  dotnet test backend/tests/Vessel.Unit.Tests -c Release --filter "FullyQualifiedName~CsvTests"
docker run --rm -e COREPACK_ENABLE_DOWNLOAD_PROMPT=0 -v "$PWD":"$PWD" -w "$PWD" node:24-bookworm-slim bash -c \
  'corepack enable && pnpm install --frozen-lockfile && pnpm --filter vessel-frontend test src/features/plays/ExecutionPanel.test.tsx'
```

Use the SDK tag that matches `global.json`. `--network host` lets the container
reach the database that `scripts/test-db.sh` publishes on loopback.

## Workflow

- Track work in the repository only: issues and pull requests on
  `sloppitty/vessel`, plans and state documents under `docs/`.
- Branch from `main`; keep pull requests small; use conventional commit messages.
- Before a feature, read its contract in `docs/architecture/` and follow it.
  Record verified progress in the matching `*-state.md`.
- Record a new owner decision at the top of `docs/decisions.md`, and add
  "Superseded by" to any entry it replaces. Change this file only for rules that
  hold now.
- Install the pre-push hook once per clone: `git config core.hooksPath scripts/hooks`.
- Releases follow the [`vessel-release` skill](.agents/skills/vessel-release/SKILL.md):
  a release pull request, then an annotated `vX.Y.Z` tag on `main` that CI publishes.

## Definition of done

- The behaviour matches the contract, and the state document records what was verified.
- Focused tests cover the change.
- Local checks pass for every area touched (backend build and all test projects
  with the database; frontend typecheck, lint, tests and build; script tests).
  CI runs only on release tags, so these are the checks before merging. A skipped
  check is not a pass.
- After a style change, built-in themes still pass the contrast test and the
  rendered controls are checked.
- New migrations are applied only through tests, and the pull request states the
  upgrade step.
- User-visible changes have a line under Unreleased in `CHANGELOG.md`.
- The pull request has had one adversarial Sol (Codex) review.

## Where decisions live

- `docs/decisions.md`: every owner decision with its date, newest first.
- `docs/architecture/*-contract.md` and plans: the detailed rules of each feature.
- `docs/architecture/*-state.md`: what is implemented and verified.
- `docs/design/approved-baseline.md`: the approved layout.
