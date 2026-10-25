"""Safety tests for scripts/e2e-full-stack.py. Docker is mocked: nothing here starts, stops or removes anything real."""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parent / 'e2e-full-stack.py'
spec = importlib.util.spec_from_file_location('e2e_full_stack', SCRIPT)
harness = importlib.util.module_from_spec(spec)
spec.loader.exec_module(harness)

DESTRUCTIVE = ('down', 'rm', 'rmi', 'kill', 'stop', 'prune')


class Docker:
    """Records every command; answers `docker compose ls` with the configured projects and succeeds otherwise."""

    def __init__(self, projects=()):
        self.calls, self.projects = [], list(projects)

    def __call__(self, args, **kwargs):
        args = [str(a) for a in args]
        self.calls.append(args)
        stdout = json.dumps(self.projects) if args[:3] == ['docker', 'compose', 'ls'] else ''
        return subprocess.CompletedProcess(args, 0, stdout, '')

    def destructive(self):
        return [c for c in self.calls if any(word in c for word in DESTRUCTIVE) or c[:3] == ['docker', 'volume', 'rm']]


def arguments(**overrides):
    values = dict(artifacts=None, keep=False, resume=None, resume_from='', playwright_module=None, skip_build=True, until=None,
                  continue_on_failure=False)
    values.update(overrides)
    return argparse.Namespace(**values)


def snapshot(directory):
    return sorted((str(p.relative_to(directory)), p.read_bytes() if p.is_file() else None) for p in Path(directory).rglob('*'))


class ProvisioningReportTests(unittest.TestCase):
    def test_new_and_existing_reports_include_the_hub_and_do_not_depend_on_order(self):
        prefix = 'Ensured identity user 00000000-0000-4000-8000-000000000001.\nProvisioned envelope storage. '
        for report, expected in (
            ('Registered subscriptions: notifications, verification; already registered: none.',
             ({'notifications', 'verification'}, set())),
            ('Registered subscriptions: none; already registered: notifications, verification.',
             (set(), {'notifications', 'verification'})),
            ('Registered subscriptions: verification, notifications; already registered: none.',
             ({'notifications', 'verification'}, set())),
            ('Registered subscriptions: verification; already registered: none.', ({'verification'}, set())),
            ('not a provisioning report', None),
        ):
            with self.subTest(report=report):
                self.assertEqual(expected, harness.subscription_report(prefix + report + '\n'))


class ResumeSafetyTests(unittest.TestCase):
    def setUp(self):
        self.artifacts = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.artifacts, ignore_errors=True)

    def resume(self, directory, docker):
        with mock.patch.object(harness.subprocess, 'run', docker):
            return harness.E2E(arguments(resume=str(directory), artifacts=self.artifacts)).main()

    def owned_like(self, name='vessel-e2e-0123abcd', marker=True, state_name=None):
        directory = Path(tempfile.mkdtemp(prefix='vessel-e2e-'))
        self.addCleanup(shutil.rmtree, directory, ignore_errors=True)
        (directory / 'compose.yaml').write_text('services: {}\n')
        (directory / '.env').write_text('POSTGRES_PASSWORD=keep-me\n')
        if marker:
            (directory / harness.MARKER).write_text(json.dumps({'tool': harness.MARKER_TOOL, 'name': name}))
        state_name = state_name or name
        (directory / 'e2e-state.json').write_text(json.dumps({
            'name': state_name, 'project': state_name, 'projects': [state_name, state_name + '-restore'],
            'owner': '00000000-0000-4000-8000-000000000001', 'https_port': 1, 'control_port': 2, 'address': '0x' + '1' * 40,
            'account': 'a'}))
        return directory

    def assert_untouched(self, directory, before, docker, code):
        self.assertEqual(code, 2)
        self.assertTrue(Path(directory).is_dir())
        self.assertEqual(snapshot(directory), before)
        self.assertEqual(docker.destructive(), [])

    def test_a_repository_like_directory_is_refused_and_left_intact(self):
        with tempfile.TemporaryDirectory() as repo:
            (Path(repo) / '.env').write_text('POSTGRES_PASSWORD=owner-secret\n')
            (Path(repo) / 'scripts').mkdir()
            (Path(repo) / 'scripts' / 'keep.py').write_text('print(1)\n')
            before, docker = snapshot(repo), Docker()
            self.assert_untouched(repo, before, docker, self.resume(repo, docker))

    def test_missing_unreadable_or_foreign_state_is_refused(self):
        for directory in (self.owned_like(marker=False), self.owned_like(state_name='vessel-e2e-ffffffff')):
            before, docker = snapshot(directory), Docker()
            self.assert_untouched(directory, before, docker, self.resume(directory, docker))
        broken = self.owned_like()
        (broken / 'e2e-state.json').write_text('{not json')
        before, docker = snapshot(broken), Docker()
        self.assert_untouched(broken, before, docker, self.resume(broken, docker))

    def test_a_marked_directory_whose_project_runs_elsewhere_is_refused(self):
        directory = self.owned_like()
        before = snapshot(directory)
        docker = Docker([{'Name': 'vessel-e2e-0123abcd', 'ConfigFiles': '/elsewhere/compose.yaml'}])
        self.assert_untouched(directory, before, docker, self.resume(directory, docker))

    def test_a_symlink_to_a_kept_run_is_refused(self):
        directory = self.owned_like()
        link = Path(tempfile.gettempdir()) / ('vessel-e2e-' + directory.name[-8:][::-1])
        link.symlink_to(directory)
        self.addCleanup(link.unlink)
        docker = Docker([{'Name': 'vessel-e2e-0123abcd', 'ConfigFiles': str(directory / 'compose.yaml')}])
        before = snapshot(directory)
        self.assert_untouched(directory, before, docker, self.resume(link, docker))

    def test_a_kept_run_validates_before_adoption(self):
        directory = self.owned_like()
        docker = Docker([{'Name': 'vessel-e2e-0123abcd', 'ConfigFiles': f'{directory.resolve()}/compose.yaml,{directory.resolve()}/e2e.yaml'}])
        with mock.patch.object(harness.subprocess, 'run', docker):
            e2e = harness.E2E(arguments(resume=str(directory), artifacts=self.artifacts))
            candidate, state = e2e.validate_resume(directory)
        self.assertEqual((candidate, state['project']), (directory.resolve(), 'vessel-e2e-0123abcd'))
        self.assertIsNone(e2e.owned_temp)
        self.assertEqual(e2e.owned_projects, [])

    def test_cleanup_only_removes_what_the_run_owns(self):
        docker = Docker()
        with mock.patch.object(harness.subprocess, 'run', docker):
            e2e = harness.E2E(arguments(artifacts=self.artifacts))
            e2e.temp = Path(self.artifacts)  # a path the run did not create
            e2e.cleanup()
        self.assertTrue(Path(self.artifacts).is_dir())
        self.assertEqual([c for c in docker.destructive() if 'down' in c], [])


class RedactionTests(unittest.TestCase):
    def setUp(self):
        self.artifacts = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.artifacts, ignore_errors=True)
        with mock.patch.object(harness.subprocess, 'run', Docker()):
            self.e2e = harness.E2E(arguments(artifacts=self.artifacts))
        self.e2e.temp = Path(self.artifacts)
        self.session = 'e2e-session-' + 'f' * 40
        self.e2e.session_cookie = '__Host-vessel-session=' + self.session
        self.e2e.register_secrets()

    def test_a_failed_broker_user_command_reports_operation_and_exit_code_only(self):
        calls = []

        def failing(args, **kwargs):
            # A tool that echoes everything it was given, argv and standard input alike.
            calls.append(list(map(str, args)))
            echoed = ' '.join(map(str, args)) + ' ' + (kwargs.get('input') or b'').decode()
            return subprocess.CompletedProcess(args, 70, echoed, 'Error: ' + echoed)
        with mock.patch.object(harness.subprocess, 'run', failing), mock.patch('builtins.print') as printed:
            with self.assertRaises(harness.CommandFailed) as caught:
                self.e2e.broker_app_user()
            message = str(caught.exception)
            self.e2e.step = 'x'
            self.e2e.expect(False, 'step completed', f'{type(caught.exception).__name__}: {harness.redact(caught.exception)}', soft=True)
        self.assertTrue(calls and all(self.e2e.app_password not in ' '.join(c) for c in calls), 'the password never travels in argv')
        self.assertIn('rabbitmqctl add_user', message)
        self.assertIn('exited with 70', message)
        for secret in (self.e2e.app_password, self.e2e.broker_password, self.e2e.database_password, self.session):
            self.assertNotIn(secret, message)
            self.assertNotIn(secret, json.dumps(self.e2e.results))
            self.assertNotIn(secret, ' '.join(str(c) for c in printed.call_args_list))

    def test_expect_and_notes_redact_generated_credentials(self):
        with mock.patch('builtins.print') as printed:
            self.e2e.step = 'x'
            self.e2e.expect(False, 'detail ' + self.e2e.session_cookie, {'password': self.e2e.app_password}, soft=True)
            self.e2e.note('s3 ' + self.e2e.s3_secret)
        text = json.dumps(self.e2e.results) + ' '.join(str(c) for c in printed.call_args_list)
        for secret in (self.session, self.e2e.app_password, self.e2e.s3_secret):
            self.assertNotIn(secret, text)
        self.assertIn('[redacted]', text)

    def test_timeouts_never_echo_argv(self):
        def timeout(args, **kwargs):
            raise subprocess.TimeoutExpired(args, kwargs.get('timeout'))
        with mock.patch.object(harness.subprocess, 'run', timeout):
            with self.assertRaises(harness.CommandFailed) as caught:
                harness.run('docker', 'exec', 'x', 'rabbitmqctl', 'add_user', 'vessel-app', self.e2e.app_password, timeout=1)
        self.assertNotIn(self.e2e.app_password, str(caught.exception))


class WalletSessionTests(unittest.TestCase):
    def setUp(self):
        self.artifacts = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.artifacts, ignore_errors=True)
        with mock.patch.object(harness.subprocess, 'run', Docker()):
            self.e2e = harness.E2E(arguments(artifacts=self.artifacts))

    def test_requests_carry_the_session_binding_and_origin_never_a_bearer(self):
        self.assertEqual({'Origin': self.e2e.base, 'X-Vessel-Request': '1'}, self.e2e.session_headers())
        self.e2e.session_cookie = '__Host-vessel-session=synthetic'
        headers = self.e2e.session_headers()
        self.assertEqual(('__Host-vessel-session=synthetic', self.e2e.owner), (headers['Cookie'], headers['X-Vessel-User']))
        self.assertNotIn('Authorization', headers)
        self.assertNotIn('Cookie', self.e2e.session_headers(False))
        self.assertTrue(self.e2e.base.startswith('https://localhost:'))

    def test_the_signer_runs_networkless_with_its_input_on_stdin(self):
        calls = []

        def docker(args, **kwargs):
            calls.append((list(map(str, args)), kwargs.get('input')))
            return subprocess.CompletedProcess(args, 0, '{"address": "0x' + '1' * 40 + '"}', '')
        self.e2e.tool_paths = (['/host/noble:/noble:ro'], '/playwright/playwright-core', '/noble')
        with mock.patch.object(harness.subprocess, 'run', docker):
            self.assertEqual('0x' + '1' * 40, self.e2e.operator_address())
            self.assertEqual('0x' + '1' * 40, self.e2e.operator_address())
        self.assertEqual(1, len(calls), 'the address is read once')
        args, stdin = calls[0]
        self.assertEqual(['--network', 'none'], args[args.index('--network'):args.index('--network') + 2])
        self.assertIn(f'{harness.SIGNER}:/wallet-test-signer.cjs:ro', args)
        self.assertIn('NOBLE_MODULE_DIR=/noble', args)
        self.assertEqual({'wallet': 1, 'message': None}, json.loads(stdin))

    def test_session_cookie_must_be_a_secure_host_cookie(self):
        name = '__Host-vessel-session'
        good = name + '=synthetic; path=/; secure; samesite=strict; httponly'
        self.e2e.step = 'x'
        with mock.patch('builtins.print'):
            self.assertEqual(name + '=synthetic', self.e2e.cookie({'set-cookie': [good, 'other=1; path=/']}, name))
            for missing in ('; secure', '; httponly', '; samesite=strict'):
                with self.assertRaises(harness.StepFailed, msg=missing):
                    self.e2e.cookie({'set-cookie': [good.replace(missing, '')]}, name)
            with self.assertRaises(harness.StepFailed):
                self.e2e.cookie({'set-cookie': [good + '; domain=localhost']}, name)


class StandaloneContainerTests(unittest.TestCase):
    def setUp(self):
        self.artifacts = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.artifacts, ignore_errors=True)
        with mock.patch.object(harness.subprocess, 'run', Docker()):
            self.e2e = harness.E2E(arguments(artifacts=self.artifacts))

    def test_a_timed_out_container_is_removed_by_name(self):
        calls = []

        def docker(args, **kwargs):
            calls.append(list(args))
            if args[:2] == ['docker', 'run']:
                raise subprocess.TimeoutExpired(args, kwargs['timeout'])
            return subprocess.CompletedProcess(args, 0, '', '')
        with mock.patch.object(harness.subprocess, 'run', docker):
            with self.assertRaises(harness.CommandFailed) as caught:
                self.e2e.standalone('browser-live', ['image', 'node', 'x.cjs'], timeout=1)
        name = f'{self.e2e.name}-browser-live'
        self.assertIn(['--name', name], [calls[0][i:i + 2] for i in range(len(calls[0]))])
        self.assertIn(f'{harness.RUN_LABEL}={self.e2e.name}', calls[0])
        self.assertEqual(calls[-1], ['docker', 'rm', '-f', name])
        self.assertIn('timed out', str(caught.exception))

    def test_cleanup_removes_labelled_containers_and_reports_volume_failures(self):
        calls = []

        def docker(args, **kwargs):
            args = [str(a) for a in args]
            calls.append(args)
            if args[:2] == ['docker', 'ps']:
                return subprocess.CompletedProcess(args, 0, 'abc123\n', '')
            if args[:3] == ['docker', 'volume', 'rm']:
                return subprocess.CompletedProcess(args, 1, '', 'in use')
            return subprocess.CompletedProcess(args, 0, '', '')
        self.e2e.playwright_volume = self.e2e.name + '-playwright'
        with mock.patch.object(harness.subprocess, 'run', docker), mock.patch('builtins.print') as printed:
            self.e2e.cleanup()
        self.assertIn(['docker', 'ps', '-aq', '--filter', f'label={harness.RUN_LABEL}={self.e2e.name}'], calls)
        self.assertIn(['docker', 'rm', '-f', 'abc123'], calls)
        self.assertEqual(self.e2e.cleanup_failures, ['volume ' + self.e2e.playwright_volume])
        self.assertIn('Cleanup failed', ' '.join(str(c) for c in printed.call_args_list))


if __name__ == '__main__':
    unittest.main()
