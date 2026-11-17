#!/usr/bin/env python3
"""Pre-push checks for Vessel, run by scripts/hooks/pre-push.

Install once per clone with `git config core.hooksPath scripts/hooks`.
Git passes the remote name and URL as arguments and one line per pushed ref on
stdin: `<local ref> <local sha> <remote ref> <remote sha>`.

For branches it runs only the quick checks the changed files need (script
tests, frontend typecheck and lint, backend build) against the working tree. A
check whose tool is missing is skipped with the reason; a check that runs and
fails refuses the push. A pushed `v*` tag must pass
`scripts/release.py check --tag <tag>` on the tagged commit. `git push
--no-verify` is the only bypass.
"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent

# Order is the order the checks run in: fastest first.
CHECKS = {
    'scripts': ('Script tests', [
        [sys.executable, '-m', 'unittest', 'discover', '-s', 'scripts', '-p', 'test_*.py'],
    ]),
    'frontend': ('Frontend typecheck and lint', [
        ['pnpm', '--filter', 'vessel-frontend', 'typecheck'],
        ['pnpm', '--filter', 'vessel-frontend', 'lint'],
    ]),
    'backend': ('Backend build', [
        ['dotnet', 'build', 'backend/Vessel.slnx', '--configuration', 'Release',
         '-p:RestoreLockedMode=true'],
    ]),
}
PREFIXES = {'scripts/': 'scripts', 'deploy/': 'scripts', 'frontend/': 'frontend', 'backend/': 'backend'}


def select_checks(paths):
    """The checks the changed paths need, in running order."""
    wanted = {check for path in paths for prefix, check in PREFIXES.items() if path.startswith(prefix)}
    return [check for check in CHECKS if check in wanted]


def is_zero(sha):
    return set(sha) == {'0'}


def git(*args, root=ROOT):
    result = subprocess.run(['git', *args], cwd=root, text=True, capture_output=True)
    return result.stdout if result.returncode == 0 else None


def changed_paths(local_sha, remote_sha, remote, root=ROOT):
    """Files changed by pushing local_sha, or None when no base can be found."""
    if not is_zero(remote_sha) and git('cat-file', '-e', f'{remote_sha}^{{commit}}', root=root) is not None:
        out = git('diff', '--name-only', '--no-renames', remote_sha, local_sha, root=root)
        if out is not None:
            return out.splitlines()
    for base in (f'refs/remotes/{remote}/main', 'refs/remotes/origin/main'):
        out = git('diff', '--name-only', '--no-renames', f'{base}...{local_sha}', root=root)
        if out is not None:
            return out.splitlines()
    return None


def plan(lines, remote, root=ROOT):
    """Read git's pre-push lines into (checks, tags, notes)."""
    checks, tags, notes = set(), [], []
    for line in lines:
        fields = line.split()
        if len(fields) != 4:
            continue
        local_ref, local_sha, remote_ref, remote_sha = fields
        if is_zero(local_sha):
            continue  # deletion
        if remote_ref.startswith('refs/tags/'):
            tag = remote_ref.removeprefix('refs/tags/')
            if tag.startswith('v'):
                tags.append((tag, local_sha))
        elif remote_ref.startswith('refs/heads/'):
            paths = changed_paths(local_sha, remote_sha, remote, root)
            if paths is None:
                notes.append(f'No base found for {remote_ref}; running every check.')
                checks.update(CHECKS)
            else:
                checks.update(select_checks(paths))
            head = git('rev-parse', 'HEAD', root=root)
            if head is not None and head.strip() != local_sha:
                notes.append(f'{local_ref} is not the checked-out commit; checks run against the working tree.')
    return [check for check in CHECKS if check in checks], tags, notes


def missing_tool(check, root=ROOT):
    """Why a check cannot run on this machine, or None."""
    if check == 'frontend':
        for tool in ('node', 'pnpm'):
            if shutil.which(tool) is None:
                return f'{tool} is not on PATH'
        if not (root / 'frontend' / 'node_modules').is_dir():
            return 'frontend dependencies are not installed (run pnpm install --frozen-lockfile)'
    if check == 'backend':
        if shutil.which('dotnet') is None:
            return 'dotnet is not on PATH'
        probe = subprocess.run(['dotnet', '--version'], cwd=root, capture_output=True, text=True)
        if probe.returncode != 0:
            return 'the .NET SDK pinned in global.json is not installed'
    return None


def run_check(check, root=ROOT):
    """True when every command of the check passed."""
    for command in CHECKS[check][1]:
        print(f'pre-push: $ {" ".join(command)}', flush=True)
        if subprocess.run(command, cwd=root).returncode != 0:
            return False
    return True


def check_tag(tag, sha, root=ROOT):
    """Run release.py check against the tagged commit's own files."""
    print(f'pre-push: release check for {tag}', flush=True)
    with tempfile.TemporaryDirectory() as tree:
        archive = subprocess.run(['git', 'archive', '--format=tar', sha], cwd=root, capture_output=True)
        if archive.returncode != 0:
            print(f'pre-push: cannot read {tag} ({sha}): {archive.stderr.decode().strip()}', file=sys.stderr)
            return False
        subprocess.run(['tar', '-x', '-C', tree], input=archive.stdout, check=True)
        script = Path(tree) / 'scripts' / 'release.py'
        if not script.is_file():
            print(f'pre-push: {tag} has no scripts/release.py', file=sys.stderr)
            return False
        return subprocess.run([sys.executable, str(script), 'check', '--tag', tag], cwd=tree).returncode == 0


def main(argv, stdin, root=ROOT):
    remote = argv[0] if argv else 'origin'
    checks, tags, notes = plan(stdin.read().splitlines(), remote, root)
    for note in notes:
        print(f'pre-push: {note}')
    failed, skipped = [], []
    for tag, sha in tags:
        if not check_tag(tag, sha, root):
            failed.append(f'release check for {tag}')
    for check in checks:
        name = CHECKS[check][0]
        reason = missing_tool(check, root)
        if reason:
            skipped.append(f'{name}: {reason}')
        elif not run_check(check, root):
            failed.append(name)
    for line in skipped:
        print(f'pre-push: SKIPPED {line}', file=sys.stderr)
    if failed:
        print(f'pre-push: FAILED {", ".join(failed)}. Push refused '
              '(git push --no-verify bypasses this on purpose).', file=sys.stderr)
        return 1
    if checks or tags:
        print('pre-push: passed' + (' with skipped checks' if skipped else ''))
    return 0


if __name__ == '__main__':
    os.chdir(ROOT)
    sys.exit(main(sys.argv[1:], sys.stdin))
