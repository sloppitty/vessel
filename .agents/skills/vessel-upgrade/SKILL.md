---
name: vessel-upgrade
description: Upgrade, back up, restore or roll back a running Vessel Docker Compose installation with the deploy/vessel operator command, and only when the owner explicitly asks for that deployment. Use for live installation upgrades, not for preparing or publishing a release.
---

# Vessel upgrade

Read `AGENTS.md` and the Upgrade, Backups and Isolated restore sections of
`deploy/README.md` first.

## Authorization

- Act only when the owner explicitly asks to upgrade, back up, restore or roll
  back a named installation. A release, a merged pull request, a published
  image or a request to "ship" does not authorize a deployment.
- The owner's database and evidence are touched only on that explicit request,
  and only through the commands below. Never run them, `migrate` or any
  database command against the owner's installation as a test, a check or a
  side effect of other work; use the isolated release verification for that.
- If the request names no installation or version, ask. Do not guess the
  operator directory or the target version.

## Only the script

Every step is `deploy/vessel` (copied as `./vessel` into the installation's
operator directory). Do not replace it with hand-written `docker compose`
commands: it stops every writer, including `worker-notifications` and
`worker-paper`, takes a backup that is never overwritten before any migration,
and runs the release manifests' post-steps. If the script refuses or fails,
stop and report its output; do not work around it.

1. In the operator directory, with its own `.env` and overlays (never print
   `.env` or any secret):

   ```sh
   ./vessel status
   ./vessel preflight --to X.Y.Z
   ```

   Use the target release's deploy files, `vessel` included. A failed
   preflight ends the task: report it.
2. Run the command without `--yes` and show the owner the plan it prints,
   including the release manifests' migrations, post-steps and configuration
   notes. Read the release notes of every release crossed and the README's
   per-version sections before v0.4.3.
3. Only after the owner confirms that plan, run the same command with `--yes`:

   ```sh
   ./vessel upgrade --to X.Y.Z --yes
   ./vessel backup --yes
   ./vessel restore --from DIR --yes [--keep-active GUID] [--start]
   ./vessel rollback --yes [--from DIR] [--keep-active GUID] [--start]
   ```

   The script cannot stop API or worker replicas on other Docker hosts; the
   owner stops those first.
4. After `restore` or `rollback` without `--start`, the hosts stay stopped for
   the trusted-record reconciliation in the README's Isolated restore section.
   That reconciliation is the owner's decision; do not run `--start` or the
   printed commands unless the owner says nothing needs reconciling.

## Report

Report the commands run, the plan, the backup directory, the version before
and after, the migrations applied, the post-steps run, `./vessel status`
afterwards and anything that failed or needs the owner, such as replicas on
other hosts, the credential key ring or an external S3 bucket, which backups do
not include.
