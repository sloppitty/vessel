---
name: vessel-release
description: Prepare and deliver a Vessel release - version bump, changelog and release notes in a pull request, then an annotated vX.Y.Z tag on main that the Release workflow verifies and publishes as Linux amd64 API/web images in skorcius/vessel plus a GitHub Release. Use for Vessel version bumps, release checks and publication, not live installation upgrades.
---

# Vessel release

Read `AGENTS.md`, `CHANGELOG.md`, `deploy/README.md` and the Release images
section of `docs/development.md` first.

## Release destination and scope

- Docker Hub repository `skorcius/vessel` is the image destination, managed at
  <https://hub.docker.com/repository/docker/skorcius/vessel/general>.
- One version produces annotated Git tag `vX.Y.Z`, two Linux amd64 images,
  `skorcius/vessel:api-X.Y.Z` and `skorcius/vessel:web-X.Y.Z`, the moving tags
  `api-latest` and `web-latest`, and a GitHub Release whose body is
  `docs/releases/vX.Y.Z.md`.
- October 15, 2026 (v0.1.2): the owner asked for automated publishing.
  `.github/workflows/release.yml` publishes when the tag is pushed. A request to
  create or deliver a release includes pushing that tag and confirming the
  workflow published everything; do not stop at a prepared branch.
- Honor explicit prepare-only, local-only or no-push instructions: stop before
  the tag. A release request does not authorize deployment.
- Never apply migrations to the owner's database, load the development `.env`
  into release checks, or change running installations for a release.

## Prepare the source

1. Inspect the worktree, fetch origin and tags, and confirm the intended commit
   and version. Preserve unrelated work. Check local and remote tags before
   creating one. Never move or overwrite an existing release tag or published
   versioned image.
2. Read changes since the preceding release tag. Move `## Unreleased` items
   into a dated `## X.Y.Z - <Month D, YYYY>` changelog section with migration
   names, upgrade requirements and known limits. Preserve historical entries.
3. Write `docs/releases/vX.Y.Z.md`: owner-facing highlights, upgrade steps,
   image tags and known limits. It becomes the GitHub Release body, so link
   repository files with absolute `https://github.com/<owner>/<repo>/blob/vX.Y.Z/...`
   URLs for the active release repository (currently `sloppitty/vessel`), not
   relative paths or links to a tag that exists only in another repository.
4. Keep the version in step across:
   - `frontend/package.json`
   - `backend/Directory.Build.props`
   - all `ARG VERSION` defaults in both Dockerfiles
   - `deploy/compose.yaml` image fallbacks and `deploy/.env.example`
   - current release examples in `README.md`, `deploy/README.md` and
     `docs/development.md`

   `python3 scripts/release.py check --tag vX.Y.Z` checks the packaged files,
   the changelog section and the notes; the docs examples are yours to check.
5. Do not change dependencies, regenerate lockfiles unnecessarily or expand
   product scope. Use the root-pinned pnpm through Corepack if needed.

## Verify locally

- Finish version and source edits before running `scripts/check.sh` with pinned
  pnpm on `PATH`. Rerun affected checks after any later source change. Set
  `Vessel_TEST_POSTGRES` to a disposable synthetic PostgreSQL 18 instance so
  database tests run. Never use the owner's connection. Report opt-in live-test
  skips separately.
- Run `python3 scripts/verify-release.py` (and `--storage local` when storage or
  Compose changed). Let its cleanup finish. If a check fails, stop and report it.
- When an existing Playwright installation is available, use the documented
  `--browser-module` option for the production-image CSP smoke check. Do not
  install a new browser dependency just for a release.

## Release pull request

Commit the release changes on a branch and open a pull request. CI runs the
foundation checks; when packaging files change, the Release workflow also runs
both verifications and a publish rehearsal against a throwaway local registry.
The owner merges it.

## Tag and publish

After the merge, tag the merge commit on `main` and push only that tag:

```sh
git fetch origin --tags
git tag -a "v$VERSION" -m "Vessel v$VERSION" origin/main
git push origin "v$VERSION"
```

Do not use force or `--tags`. The Release workflow then:

1. rejects lightweight tags, tags not on `main` and version/notes mismatches;
2. runs the foundation checks and `verify-release.py` for S3 and local storage;
3. runs `scripts/release.py publish` in the `docker-hub` environment: builds both
   images from the tagged commit, pushes both versioned tags, verifies them
   remotely, then tags and pushes both latest tags and verifies they resolve to
   the same digests. A versioned tag already published from another revision
   stops the job; one from the same revision is reused;
4. creates or updates the GitHub Release from `docs/releases/vX.Y.Z.md`.

Watch the run (`gh run watch`) and confirm the job summary's digests. Verify
remote metadata with `docker buildx imagetools inspect` if needed; never dump
environments or Docker credential configuration.

## Credentials and failures

- Publication uses the `DOCKERHUB_TOKEN` secret on the `docker-hub` GitHub
  environment (a Docker Hub access token with Read & Write scope) and the
  optional `DOCKERHUB_USERNAME` variable (default `skorcius`). Only `v*` tags
  may deploy to that environment. Never ask for the token in chat, print it or
  store it in the repository; the owner sets it with
  `gh secret set DOCKERHUB_TOKEN --env docker-hub` or in repository settings.
- A missing secret fails before any push. After it is set, re-run the failed
  jobs, or dispatch the workflow on the tag ("Use workflow from" the tag).
- If a versioned push fails, latest tags are untouched. If a latest push fails,
  report the partial publication; a re-run reuses the versioned images.
- If a correction is needed after a tag was pushed, report it and obtain the
  owner's direction rather than moving the tag.

## Manual fallback

Only when the owner asks or CI is unavailable, and with the owner's existing
Docker login on this machine, publish from the clean tagged checkout:

```sh
python3 scripts/release.py publish --revision "$(git rev-parse HEAD)"
gh release create "v$VERSION" --verify-tag --title "Vessel v$VERSION" \
  --notes-file "docs/releases/v$VERSION.md"
```

If authentication is missing, report publication as blocked, not completed.

Report the source commit, Git tag and remote status, workflow run, tests and
skips, image tags and digests, and the operator's next step. Release
publication never means a live installation was upgraded.
