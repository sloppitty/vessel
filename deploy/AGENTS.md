# Deployment and release rules

The top-level [`AGENTS.md`](../AGENTS.md) applies first. The operator guide is
[`README.md`](README.md); releases follow the
[`vessel-release` skill](../.agents/skills/vessel-release/SKILL.md).

## Releases

- The Release workflow publishes from an annotated `vX.Y.Z` tag on `main`: both
  versioned images first, the `latest` tags only after both are verified. A
  version published from another revision is never overwritten.
- The Docker Hub token is the `DOCKERHUB_TOKEN` secret on the `docker-hub`
  environment. Never ask for, print or store it.
- Publishing never deploys. A release does not authorize deployment, a live
  migration or changes to the owner's database.
- Release checks: `scripts/release.py check`, and `scripts/verify-release.py` for
  S3 and `--storage local`.

## Installations

- Never bake secrets into images or expose plain HTTP to the internet.
- The API never migrates at startup; `migrate` is an explicit one-shot step.
- Upgrades stop every API and worker host and replica (including
  `worker-notifications`) and back up both schemas first. There is no rolling
  upgrade; rollback restores the pre-upgrade backup. A restore resets identity,
  reconciles trusted records and runs `rotate-event-epoch` before any host starts.
- Proxy trust: the default API trusts only the pinned web peer with one hop, and
  nginx overwrites forwarded claims. The Caddy TLS overlay unpublishes web and
  trusts only Caddy's pinned IP. Host-gateway trust is a weaker, documented opt-in.
- A reverse proxy in front of Vessel must not replace its
  `Permissions-Policy: camera=(self)` header.
- Registration stays `Closed` until the wallet acceptance checks pass and are
  recorded in [`multi-user-state.md`](../docs/architecture/multi-user-state.md).
- The paper worker is standalone and optional; never add it to the monitor or
  notifications module sets.

## Evidence storage and credential keys

- With no storage settings the API uses `Local` at its original root; only
  `Provider=S3` opts in, and invalid S3 settings or outages never fall back.
- Files are never moved automatically. `evidence-copy-to-s3` and
  `evidence-copy-to-local` dry-run by default and never delete originals; keep
  writers stopped through the copy and cutover.
- RustFS backups include its entire stopped data volume.
- Credential key-ring files are separate secret mounts, not S3 objects.
