"""Pre-push hook: changed files to checks, ref handling and push refusal."""
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pre_push  # noqa: E402

# Under a hook git exports GIT_DIR and friends; the temporary repositories below
# must never resolve to the pushing repository.
pre_push.clear_git_environment()

ZERO = '0' * 40


class SelectChecksTests(unittest.TestCase):
    def test_each_area_selects_only_its_check(self):
        self.assertEqual(pre_push.select_checks(['scripts/release.py']), ['scripts'])
        self.assertEqual(pre_push.select_checks(['deploy/compose.yaml']), ['scripts'])
        self.assertEqual(pre_push.select_checks(['frontend/src/App.tsx']), ['frontend'])
        self.assertEqual(pre_push.select_checks(['backend/src/Vessel.Api/Program.cs']), ['backend'])

    def test_documentation_only_changes_select_nothing(self):
        self.assertEqual(pre_push.select_checks(['docs/development.md', 'AGENTS.md', 'CHANGELOG.md']), [])

    def test_mixed_changes_run_in_fixed_order_once(self):
        paths = ['backend/a.cs', 'frontend/b.ts', 'deploy/c', 'scripts/d.py', 'frontend/e.ts']
        self.assertEqual(pre_push.select_checks(paths), ['scripts', 'frontend', 'backend'])

    def test_prefix_must_be_a_directory(self):
        self.assertEqual(pre_push.select_checks(['frontendish.md', 'backend.txt']), [])

    def test_root_build_inputs_select_their_check(self):
        for path in ('package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml'):
            self.assertEqual(pre_push.select_checks([path]), ['frontend'], path)
        self.assertEqual(pre_push.select_checks(['global.json']), ['backend'])
        self.assertEqual(pre_push.select_checks(['docs/package.json', 'docs/global.json']), [])


class GitRepoTests(unittest.TestCase):
    """A throwaway repository with a fake origin/main."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.git('init', '-q', '-b', 'main')
        self.git('config', 'user.email', 'test@example.invalid')
        self.git('config', 'user.name', 'Test')
        self.git('config', 'commit.gpgsign', 'false')
        self.git('config', 'tag.gpgsign', 'false')
        self.base = self.commit('docs/readme.md')
        self.git('update-ref', 'refs/remotes/origin/main', self.base)

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.root, check=True, text=True,
                              capture_output=True).stdout.strip()

    def commit(self, path, text='x\n'):
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(text)
        self.git('add', path)
        self.git('commit', '-q', '-m', path)
        return self.git('rev-parse', 'HEAD')

    def plan(self, *lines):
        return pre_push.plan(list(lines), 'origin', self.root)

    def test_existing_branch_diffs_against_the_remote_ref(self):
        first = self.commit('frontend/a.ts')
        head = self.commit('backend/b.cs')
        self.assertEqual(self.plan(f'refs/heads/x {head} refs/heads/x {first}'), (['backend'], [], [], []))

    def test_new_branch_diffs_against_origin_main(self):
        self.commit('frontend/a.ts')
        head = self.commit('scripts/b.py')
        checks, _, _, refusals = self.plan(f'refs/heads/x {head} refs/heads/x {ZERO}')
        self.assertEqual((checks, refusals), (['scripts', 'frontend'], []))

    def test_unknown_base_runs_every_check(self):
        self.git('update-ref', '-d', 'refs/remotes/origin/main')
        head = self.commit('docs/other.md')
        checks, _, notes, _ = self.plan(f'refs/heads/x {head} refs/heads/x {"1" * 40}')
        self.assertEqual(checks, list(pre_push.CHECKS))
        self.assertIn('No base found', notes[0])

    def test_deletions_and_non_release_tags_are_ignored(self):
        head = self.commit('frontend/a.ts')
        checks, tags, _, _ = self.plan(f'(delete) {ZERO} refs/heads/gone {head}',
                                       f'refs/tags/backup {head} refs/tags/backup {ZERO}')
        self.assertEqual((checks, tags), ([], []))

    def test_release_tag_is_checked_without_branch_checks(self):
        head = self.commit('frontend/a.ts')
        _, tags, _, _ = self.plan(f'refs/tags/v1.2.3 {head} refs/tags/v1.2.3 {ZERO}')
        self.assertEqual(tags, [('v1.2.3', head)])

    def test_pushing_a_branch_that_needs_checks_but_is_not_checked_out_is_refused(self):
        other = self.commit('frontend/a.ts')
        self.commit('frontend/b.ts')
        _, _, _, refusals = self.plan(f'refs/heads/x {other} refs/heads/x {ZERO}')
        self.assertIn('refs/heads/x', refusals[0])
        self.assertIn('not the checked-out commit', refusals[0])

    def test_multi_ref_push_with_another_branch_is_refused(self):
        failing = self.commit('frontend/broken.ts')
        self.git('checkout', '-q', '-b', 'good', self.base)
        passing = self.commit('frontend/fine.ts')
        with mock.patch.object(pre_push, 'missing_tool', return_value=None), \
                mock.patch.object(pre_push, 'run_check', return_value=True) as run:
            code, stderr = self.main(f'refs/heads/main {failing} refs/heads/main {self.base}',
                                     f'refs/heads/good {passing} refs/heads/good {ZERO}')
        self.assertEqual(code, 1)
        run.assert_not_called()
        self.assertIn('refs/heads/main', stderr)
        self.assertIn('Commit or clean those changes and push from the checked-out branch', stderr)
        self.assertIn('git push --no-verify', stderr)

    def test_uncommitted_change_under_checked_paths_is_refused(self):
        head = self.commit('frontend/a.ts')
        (self.root / 'frontend' / 'a.ts').write_text('fix\n')
        _, _, _, refusals = self.plan(f'refs/heads/x {head} refs/heads/x {self.base}')
        self.assertIn('uncommitted changes', refusals[0])
        self.assertIn('frontend/a.ts', refusals[0])

    def test_staged_change_under_checked_paths_is_refused(self):
        head = self.commit('frontend/a.ts')
        (self.root / 'frontend' / 'new.ts').write_text('x\n')
        self.git('add', 'frontend/new.ts')
        _, _, _, refusals = self.plan(f'refs/heads/x {head} refs/heads/x {self.base}')
        self.assertIn('frontend/new.ts', refusals[0])

    def test_uncommitted_root_build_input_is_refused(self):
        self.commit('package.json')
        head = self.commit('frontend/a.ts')
        (self.root / 'package.json').write_text('{}\n')
        _, _, _, refusals = self.plan(f'refs/heads/x {head} refs/heads/x {self.base}')
        self.assertIn('package.json', refusals[0])

    def test_untracked_files_and_changes_elsewhere_are_allowed(self):
        head = self.commit('frontend/a.ts')
        (self.root / 'frontend' / 'untracked.ts').write_text('x\n')
        (self.root / 'docs' / 'readme.md').write_text('edited\n')
        (self.root / 'backend').mkdir()
        (self.root / 'backend' / 'tracked.cs').write_text('x\n')
        self.git('add', 'backend/tracked.cs')
        self.assertEqual(self.plan(f'refs/heads/x {head} refs/heads/x {self.base}'), (['frontend'], [], [], []))

    def test_branch_needing_no_checks_may_be_any_commit(self):
        other = self.commit('docs/other.md')
        self.commit('frontend/a.ts')
        (self.root / 'frontend' / 'a.ts').write_text('dirty\n')
        self.assertEqual(self.plan(f'refs/heads/x {other} refs/heads/x {self.base}'), ([], [], [], []))

    def main(self, *lines):
        stderr = io.StringIO()
        with mock.patch('sys.stderr', stderr), mock.patch('sys.stdout', io.StringIO()):
            code = pre_push.main(['origin'], io.StringIO(''.join(f'{line}\n' for line in lines)), self.root)
        return code, stderr.getvalue()

    def release_script(self, exit_code):
        return self.commit('scripts/release.py',
                           'import sys\n'
                           f'assert sys.argv[1:3] == ["check", "--tag"], sys.argv\n'
                           f'sys.exit({exit_code})\n')

    def test_failing_release_check_refuses_the_tag(self):
        head = self.release_script(1)
        code, stderr = self.main(f'refs/tags/v9.9.9 {head} refs/tags/v9.9.9 {ZERO}')
        self.assertEqual(code, 1)
        self.assertIn('release check for v9.9.9', stderr)

    def test_release_check_uses_the_tagged_commit_not_the_working_tree(self):
        tagged = self.release_script(0)
        self.release_script(1)
        code, _ = self.main(f'refs/tags/v9.9.9 {tagged} refs/tags/v9.9.9 {ZERO}')
        self.assertEqual(code, 0)

    def test_missing_tool_is_skipped_with_reason_and_does_not_refuse(self):
        head = self.commit('backend/a.cs')
        with mock.patch.object(pre_push, 'missing_tool', return_value='dotnet is not on PATH'), \
                mock.patch.object(pre_push, 'run_check') as run:
            code, stderr = self.main(f'refs/heads/x {head} refs/heads/x {self.base}')
        self.assertEqual(code, 0)
        run.assert_not_called()
        self.assertIn('SKIPPED Backend build: dotnet is not on PATH', stderr)

    def test_failed_check_refuses_the_push(self):
        head = self.commit('frontend/a.ts')
        with mock.patch.object(pre_push, 'missing_tool', return_value=None), \
                mock.patch.object(pre_push, 'run_check', return_value=False) as run:
            code, stderr = self.main(f'refs/heads/x {head} refs/heads/x {self.base}')
        self.assertEqual(code, 1)
        run.assert_called_once_with('frontend', self.root)
        self.assertIn('FAILED Frontend typecheck and lint', stderr)

    def test_missing_pinned_sdk_is_detected(self):
        (self.root / 'global.json').write_text('{"sdk": {"version": "0.0.1", "rollForward": "disable"}}')
        with mock.patch('shutil.which', return_value='/usr/bin/dotnet'), \
                mock.patch('subprocess.run', return_value=subprocess.CompletedProcess([], 155)):
            self.assertIn('not installed', pre_push.missing_tool('backend', self.root))

    def test_frontend_without_dependencies_is_skipped(self):
        with mock.patch('shutil.which', return_value='/usr/bin/tool'):
            self.assertIn('pnpm install', pre_push.missing_tool('frontend', self.root))
        (self.root / 'frontend' / 'node_modules').mkdir(parents=True)
        with mock.patch('shutil.which', return_value='/usr/bin/tool'):
            self.assertIsNone(pre_push.missing_tool('frontend', self.root))


class HookEnvironmentTests(unittest.TestCase):
    """The hook in a linked worktree of a throwaway repository, with git's variables set."""

    PROBE = """import pathlib, subprocess, tempfile, unittest


class Probe(unittest.TestCase):
    def test_git_in_another_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            for args in (['init', '-q'], ['config', 'user.email', 'probe@example.invalid'],
                         ['config', 'user.name', 'Probe'], ['config', 'commit.gpgsign', 'false']):
                subprocess.run(['git', *args], cwd=tmp, check=True)
            pathlib.Path(tmp, 'probe.txt').write_text('probe')
            subprocess.run(['git', 'add', 'probe.txt'], cwd=tmp, check=True)
            subprocess.run(['git', 'commit', '-q', '-m', 'probe'], cwd=tmp, check=True)
            subprocess.run(['git', 'update-ref', 'refs/remotes/origin/main', 'HEAD'], cwd=tmp, check=True)
"""

    def test_checks_cannot_touch_the_pushing_repository(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        main, worktree = Path(temporary.name, 'main'), Path(temporary.name, 'worktree')

        def git(*args, cwd=main):
            return subprocess.run(['git', *args], cwd=cwd, check=True, text=True,
                                  capture_output=True).stdout.strip()

        main.mkdir()
        git('init', '-q', '-b', 'main')
        git('config', 'user.email', 'owner@example.invalid')
        git('config', 'user.name', 'Owner')
        git('config', 'commit.gpgsign', 'false')
        (main / 'docs').mkdir()
        (main / 'docs' / 'readme.md').write_text('x\n')
        git('add', '.')
        git('commit', '-q', '-m', 'base')
        git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        scripts = main / 'scripts'
        (scripts / 'hooks').mkdir(parents=True)
        shutil.copy(Path(pre_push.__file__), scripts / 'pre_push.py')
        shutil.copy(Path(pre_push.__file__).parent / 'hooks' / 'pre-push', scripts / 'hooks' / 'pre-push')
        (scripts / 'test_probe.py').write_text(self.PROBE)
        git('add', '.')
        git('commit', '-q', '-m', 'scripts')
        git('worktree', 'add', '-q', '-b', 'feature', str(worktree))
        gitdir = Path(git('rev-parse', '--absolute-git-dir', cwd=worktree))

        def state():
            return (git('rev-parse', 'HEAD', cwd=worktree), git('for-each-ref', cwd=worktree),
                    (gitdir / 'index').read_bytes(), (main / '.git' / 'config').read_bytes(),
                    git('config', '--list', '--show-origin', cwd=worktree))

        before = state()
        head = before[0]
        env = {**os.environ, 'GIT_DIR': str(gitdir), 'GIT_WORK_TREE': str(worktree),
               'GIT_INDEX_FILE': str(gitdir / 'index')}
        result = subprocess.run(['sh', str(worktree / 'scripts' / 'hooks' / 'pre-push'), 'origin', 'unused'],
                                cwd=worktree, env=env, text=True, capture_output=True,
                                input=f'refs/heads/feature {head} refs/heads/feature {ZERO}\n')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Ran 1 test', result.stderr)
        self.assertEqual(state(), before)


if __name__ == '__main__':
    unittest.main()
