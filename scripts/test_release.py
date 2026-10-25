import json
import os
import importlib.util
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

import release

spec = importlib.util.spec_from_file_location('verify_release', Path(__file__).with_name('verify-release.py'))
verify_release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_release)

REVISION = 'a' * 40


def write_release(root, version='1.2.3', **overrides):
    files = {
        'frontend/package.json': json.dumps({'name': 'vessel-frontend', 'version': version}),
        'backend/Directory.Build.props': f'<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>',
        'backend/Dockerfile': f'FROM a AS build\nARG VERSION={version}\nFROM b\nARG VERSION={version}\n',
        'frontend/Dockerfile': f'FROM c\nARG VERSION={version}\n',
        'deploy/compose.yaml': f'image: skorcius/vessel:api-${{VESSEL_VERSION:-{version}}}\n'
                               f'image: skorcius/vessel:web-${{VESSEL_VERSION:-{version}}}\n',
        'deploy/.env.example': f'VESSEL_VERSION={version}\n',
        'CHANGELOG.md': f'# Changelog\n\n## Unreleased\n\n## {version} - October 15, 2026\n',
        f'docs/releases/v{version}.md': 'Notes.\n',
    }
    files.update(overrides)
    for path, text in files.items():
        if text is None:
            continue
        (root / path).parent.mkdir(parents=True, exist_ok=True)
        (root / path).write_text(text)


def image(version='1.2.3', revision=REVISION, platform=('linux', 'amd64')):
    return {'os': platform[0], 'architecture': platform[1], 'config': {'Labels': {
        'org.opencontainers.image.version': version, 'org.opencontainers.image.revision': revision}}}


class ReleaseCheckTests(unittest.TestCase):
    def check(self, tag=None, **overrides):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_release(root, **overrides)
            return release.check(root, tag)

    def test_repository_versions_agree_and_have_notes(self):
        version = release.check(release.ROOT)
        self.assertRegex(version, r'^\d+\.\d+\.\d+$')

    def test_consistent_release_with_matching_tag(self):
        self.assertEqual('1.2.3', self.check(tag='v1.2.3'))

    def test_any_disagreeing_file_is_named(self):
        with self.assertRaisesRegex(release.ReleaseError, 'frontend/Dockerfile: 1.2.4'):
            self.check(**{'frontend/Dockerfile': 'ARG VERSION=1.2.4\n'})
        with self.assertRaisesRegex(release.ReleaseError, 'deploy/compose.yaml'):
            self.check(**{'deploy/compose.yaml': 'image: skorcius/vessel:api-${VESSEL_VERSION:-1.2.3}\n'
                                                 'image: skorcius/vessel:web-${VESSEL_VERSION:-1.2.2}\n'})

    def test_missing_version_is_reported(self):
        with self.assertRaisesRegex(release.ReleaseError, 'No version found in deploy/.env.example'):
            self.check(**{'deploy/.env.example': 'VESSEL_WEB_BIND=127.0.0.1:8080\n'})

    def test_tag_must_match_the_packaged_version(self):
        with self.assertRaisesRegex(release.ReleaseError, 'Tag v1.2.4 does not match'):
            self.check(tag='v1.2.4')

    def test_changelog_section_and_notes_are_required(self):
        with self.assertRaisesRegex(release.ReleaseError, 'CHANGELOG.md has no'):
            self.check(**{'CHANGELOG.md': '# Changelog\n\n## Unreleased\n\n- 1.2.3 soon\n'})
        with self.assertRaisesRegex(release.ReleaseError, 'v1.2.3.md are missing or empty'):
            self.check(**{'docs/releases/v1.2.3.md': None})
        with self.assertRaisesRegex(release.ReleaseError, 'v1.2.3.md are missing or empty'):
            self.check(**{'docs/releases/v1.2.3.md': '\n'})

    def test_non_semantic_versions_are_rejected(self):
        with self.assertRaisesRegex(release.ReleaseError, 'is not MAJOR.MINOR.PATCH'):
            self.check(version='1.2')


class ReleasePublishTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.registry = {}

    def fake_docker(self, *args, capture=False):
        self.calls.append((args[0], args[args.index('-t') + 1] if args[0] == 'build' else args[-1]))
        if args[0] == 'image':
            return json.dumps([{'Os': 'linux', 'Architecture': 'amd64', 'Config': image()['config']}])
        if args[0] == 'push':
            ref = args[-1]
            versioned = ref.replace('-latest', '-1.2.3')
            self.registry[ref] = {'digest': 'sha256:' + versioned.rsplit(':', 1)[1], 'image': image()}
        return None

    def publish(self):
        with patch.object(release, 'docker', self.fake_docker), \
                patch.object(release, 'remote', lambda ref: self.registry.get(ref)), \
                patch.dict('os.environ', {'GITHUB_STEP_SUMMARY': ''}), patch('builtins.print'):
            return release.publish(Path('/source'), '1.2.3', REVISION, 'example/vessel')

    def test_latest_moves_only_after_both_versioned_tags_are_pushed(self):
        digests = self.publish()
        self.assertEqual({'api': 'sha256:api-1.2.3', 'web': 'sha256:web-1.2.3'}, digests)
        pushes = [ref for command, ref in self.calls if command == 'push']
        self.assertEqual(['example/vessel:api-1.2.3', 'example/vessel:web-1.2.3',
                          'example/vessel:api-latest', 'example/vessel:web-latest'], pushes)

    def test_a_version_published_from_another_revision_is_never_overwritten(self):
        self.registry['example/vessel:web-1.2.3'] = {'digest': 'sha256:old', 'image': image(revision='b' * 40)}
        with self.assertRaisesRegex(release.ReleaseError, 'never overwritten'):
            self.publish()
        self.assertEqual([], [call for call in self.calls if call[0] in ('build', 'push')])

    def test_a_version_from_the_same_revision_is_reused_not_rebuilt(self):
        self.registry['example/vessel:api-1.2.3'] = {'digest': 'sha256:api-1.2.3', 'image': image()}
        self.publish()
        self.assertIn(('pull', 'example/vessel:api-1.2.3'), self.calls)
        self.assertNotIn(('build', 'example/vessel:api-1.2.3'), self.calls)
        self.assertIn(('build', 'example/vessel:web-1.2.3'), self.calls)

    def test_metadata_rejects_another_platform_or_version(self):
        with self.assertRaisesRegex(release.ReleaseError, 'linux/arm64, not linux/amd64'):
            release.expect_metadata('ref', image(platform=('linux', 'arm64')), '1.2.3')
        with self.assertRaisesRegex(release.ReleaseError, "labelled version '1.2.2'"):
            release.expect_metadata('ref', image(version='1.2.2'), '1.2.3')


class WalletReleaseTests(unittest.TestCase):
    def test_browser_console_allows_only_the_expected_401_before_sign_in(self):
        result = subprocess.run(['node', '-e', '''
const assert = require('node:assert/strict')
const { unexpectedConsoleError } = require('./scripts/release-browser-smoke.cjs')
const message = (text, type = 'error') => ({ type: () => type, text: () => text })
for (const reason of ['Unauthorized', '']) {
  const probe = message(`Failed to load resource: the server responded with a status of 401 (${reason})`)
  assert.equal(unexpectedConsoleError(probe, false), false)
  assert.equal(unexpectedConsoleError(probe, true), true)
}
for (const text of [
  'Failed to load resource: the server responded with a status of 403 (Forbidden)',
  'Failed to load resource: the server responded with a status of 500 ()',
  'Application error: status of 401 (Unauthorized)',
  'Failed to load resource: the server responded with a status of 401 () unexpected error',
  'Unexpected application error',
]) {
  for (const signedIn of [false, true]) {
    assert.equal(unexpectedConsoleError(message(text), signedIn), true, text)
  }
}
assert.equal(unexpectedConsoleError(message('Informational message', 'log'), false), false)
assert.equal(unexpectedConsoleError(message('Informational message', 'log'), true), false)
'''], cwd=release.ROOT, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def check(self, *args):
        check = verify_release.Check(verify_release.parse_args(list(args)))
        self.addCleanup(shutil.rmtree, check.temp)
        return check

    def test_the_removed_token_option_is_rejected(self):
        with patch('sys.stderr'), self.assertRaises(SystemExit) as error:
            verify_release.parse_args(['--auth', 'token'])
        self.assertEqual(2, error.exception.code)

    def test_headers_never_send_a_bearer_and_bind_the_owner(self):
        check = self.check()
        check.session_cookie = '__Host-vessel-session=synthetic'
        headers = check.auth_headers()
        self.assertNotIn('Authorization', headers)
        self.assertEqual(check.owner, headers['X-Vessel-User'])
        self.assertEqual(check.session_cookie, headers['Cookie'])
        self.assertEqual(check.url, headers['Origin'])
        self.assertTrue(check.url.startswith('https://localhost:'))
        self.assertEqual('1', headers['X-Vessel-Request'])
        self.assertNotIn('Cookie', check.auth_headers(False))
        self.assertNotIn('Authorization', check.auth_headers(False))

    def test_compose_applies_the_tls_overlay_to_every_command_and_ignores_caller_secrets(self):
        check = self.check()
        with patch.object(verify_release, 'run') as run, patch.dict('os.environ', {'Vessel__Auth__Token': 'caller-secret'}):
            check.compose('config')
        args = run.call_args.args
        self.assertIn(str(check.temp / 'compose.tls.yaml'), args)
        self.assertFalse(any(str(arg).endswith('compose.wallet.yaml') for arg in args))
        self.assertNotIn('Vessel__Auth__Token', run.call_args.kwargs['env'])

    def test_the_generated_environment_has_identity_settings_and_no_token_settings(self):
        check = self.check()
        check.write_env('172.29.200')
        env = (check.temp / '.env').read_text()
        self.assertIn(f'VESSEL_IDENTITY_ORIGIN=https://localhost:{check.https_port}\n', env)
        self.assertIn('VESSEL_IDENTITY_REGISTRATION=Invitation\n', env)
        self.assertNotIn('Vessel__Auth__Token', env)
        self.assertNotIn('AUTH_MODE', env)
        self.assertEqual(0o600, (check.temp / '.env').stat().st_mode & 0o777)

    def test_notification_stream_uses_the_session_transport(self):
        check = self.check()
        check.session_cookie = '__Host-vessel-session=synthetic'
        connection = Mock()
        connection.getresponse.return_value.readline.side_effect = [
            b'event: notification\n', b'data: {"notification":{"kind":"test","state":"unread"}}\n']
        check.connect = Mock(return_value=connection)
        check.request = Mock(side_effect=[
            (401, [], b''), (401, [], b''), (401, [], b''),
            (200, [('Cache-Control', 'no-store')], b''),
            (202, [], b''), (429, [], b''), (202, [], b''),
        ])
        check.json = Mock(side_effect=[
            {'nextCursor': '0.0.0'}, {'popups': [{'id': 'test', 'content': {'title': 'Test notification'}}]},
            {'popups': []}, {'state': 'read'}, {'unreadCount': 0},
        ])
        check.compose = Mock()
        check.wait_notifications = Mock()
        with patch.object(verify_release.time, 'sleep'), patch('builtins.print'):
            check.notifications()
        connection.request.assert_called_once_with(
            'GET', '/api/events/stream?after=0&notificationsAfter=0.0.0', headers=check.auth_headers())
        self.assertNotIn('Authorization', connection.request.call_args.kwargs['headers'])
        connection.close.assert_called_once()

    def test_cookie_assertions_require_every_host_cookie_attribute(self):
        check = self.check()
        name = '__Host-vessel-session'
        value = name + '=synthetic; Path=/; Secure; HttpOnly; SameSite=Strict'
        with patch('builtins.print'):
            self.assertEqual(name + '=synthetic', check.cookie([('Set-Cookie', value)], name))
            for missing in ('; Path=/', '; Secure', '; HttpOnly', '; SameSite=Strict'):
                with self.assertRaises(AssertionError, msg=missing):
                    check.cookie([('Set-Cookie', value.replace(missing, ''))], name)
            with self.assertRaises(AssertionError):
                check.cookie([('Set-Cookie', value + '; Domain=localhost')], name)

    def test_bootstrap_requires_no_wallet_readiness_before_binding_synthetic_operator(self):
        check = self.check()
        check.compose = Mock(return_value=Mock(stdout='{"reason":"no-operator-wallet"}'))
        check.identity = Mock()
        check.wallet_signature = Mock(return_value={'address': '0x' + '1' * 40})
        calls = Mock()
        calls.attach_mock(check.compose, 'compose')
        calls.attach_mock(check.identity, 'identity')
        with patch('builtins.print'):
            check.wallet_bootstrap()
        self.assertIn(('run', '--rm', 'migrate'), [call.args for call in check.compose.call_args_list])
        check.identity.assert_called_once_with('bind-wallet', '--user', check.owner, '--address', '0x' + '1' * 40)
        probe = next(index for index, call in enumerate(calls.mock_calls)
                     if call[0] == 'compose' and call.args[:3] == ('exec', '-T', 'api'))
        binding = next(index for index, call in enumerate(calls.mock_calls) if call[0] == 'identity')
        self.assertLess(probe, binding)

    def test_bootstrap_does_not_bind_if_no_operator_wallet_readiness_never_appears(self):
        check = self.check()
        check.compose = Mock(return_value=Mock(stdout='{"status":"unhealthy"}'))
        check.identity = Mock()
        with patch.object(verify_release.time, 'sleep'), patch('builtins.print'):
            with self.assertRaisesRegex(AssertionError, 'readiness blocks'):
                check.wallet_bootstrap()
        check.identity.assert_not_called()
        self.assertEqual(60, sum(call.args[:3] == ('exec', '-T', 'api')
                                 for call in check.compose.call_args_list))

    def test_browser_image_requires_a_browser_module(self):
        for args in (['--wallet-browser-image', 'image'],
                     ['--wallet-browsers', 'chromium', '--wallet-browser-image', 'image']):
            with self.subTest(args=args), patch('sys.stderr'):
                with self.assertRaises(SystemExit) as error:
                    verify_release.parse_args(args)
                self.assertEqual(2, error.exception.code)

    def remote_check(self):
        return self.check('--browser-module', '/tmp/vessel-pw/node_modules/playwright',
                          '--wallet-browser-image', 'mcr.microsoft.com/playwright:v1.58.2-noble')

    def test_browser_server_uses_isolated_network_loopback_resource_limits_and_only_readonly_mounts(self):
        check = self.remote_check()
        connection = Mock()
        connection.getresponse.return_value.status = 200
        connection.getresponse.return_value.read.return_value = b'{"wsEndpointPath":"/test-path"}'
        with patch.object(verify_release, 'run') as run, \
                patch.object(verify_release.http.client, 'HTTPConnection', return_value=connection), \
                patch.object(verify_release.secrets, 'token_hex', return_value='test-path'), \
                patch.dict('os.environ', {'VESSEL_RELEASE_INVITATION': 'private-invite',
                                          'Vessel__Auth__Token': 'private-token'}):
            endpoint = check.start_browser_server()
        args, env = run.call_args.args, run.call_args.kwargs['env']
        self.assertEqual(('docker', 'run', '-d', '--rm', '--init', '--user', 'pwuser'), args[:7])
        for flag, value in (('--memory', '2g'), ('--cpus', '2'), ('--network', check.name + '_default')):
            self.assertEqual(value, args[args.index(flag) + 1])
        self.assertRegex(args[args.index('-p') + 1], r'^127\.0\.0\.1:\d+:3000$')
        mounts = [args[index + 1] for index, value in enumerate(args) if value == '-v']
        self.assertEqual(['/tmp/vessel-pw:/tmp/vessel-pw:ro',
                          f'{check.temp}/browser-forwarder.cjs:/vessel-browser-forwarder.cjs:ro'], mounts)
        self.assertEqual(0o644, (check.temp / 'browser-forwarder.cjs').stat().st_mode & 0o777)
        forwarder = (check.temp / 'browser-forwarder.cjs').read_text()
        self.assertIn("['127.0.0.1', '::1']", forwarder)
        self.assertIn("net.connect(443, 'tls')", forwarder)
        self.assertIn(f'.listen({check.https_port}, host)', forwarder)
        self.assertIn('run-server --host 0.0.0.0 --port 3000 --path /', args[-1])
        self.assertIn('--max-clients 1', args[-1])
        self.assertRegex(endpoint, r'^ws://127\.0\.0\.1:\d+/test-path$')
        connection.request.assert_called_once_with('GET', '/json')
        connection.close.assert_called_once()
        self.assertNotIn('-e', args)
        for secret in ('private-invite', 'private-token'):
            self.assertNotIn(secret, ' '.join(args))
            self.assertNotIn(secret, env.values())

    def test_release_smoke_then_wallet_smokes_and_only_firefox_gets_our_endpoint(self):
        check = self.remote_check()
        check.chart_account = 'synthetic'
        check.identity = Mock(return_value=Mock(stdout='private-invitation\n'))
        check.start_browser_server = Mock(return_value='ws://127.0.0.1:3000/synthetic')
        check.stop_browser_server = Mock()
        with patch.dict('os.environ', {'PLAYWRIGHT_FIREFOX_WS_ENDPOINT': 'ws://foreign/firefox',
                                      'PLAYWRIGHT_CHROMIUM_WS_ENDPOINT': 'ws://foreign/chromium'}), \
                patch.object(verify_release, 'run') as run:
            check.browser_smokes()
        release, chromium, firefox = [call.kwargs['env'] for call in run.call_args_list]
        self.assertEqual(('node', str(verify_release.ROOT / 'scripts/release-browser-smoke.cjs')), run.call_args_list[0].args)
        self.assertEqual({'node'}, {call.args[0] for call in run.call_args_list})
        self.assertTrue(all(call.args[1].endswith('wallet-browser-smoke.cjs') for call in run.call_args_list[1:]))
        for env in (release, chromium, firefox):
            self.assertEqual('1', env['VESSEL_RELEASE_SYNTHETIC'])
        self.assertNotIn('VESSEL_RELEASE_INVITATION', release)
        self.assertFalse(any(key.endswith('_WS_ENDPOINT') for key in release))
        self.assertFalse(any(key.endswith('_WS_ENDPOINT') for key in chromium))
        self.assertEqual('ws://127.0.0.1:3000/synthetic', firefox['PLAYWRIGHT_FIREFOX_WS_ENDPOINT'])
        self.assertNotIn('PLAYWRIGHT_CHROMIUM_WS_ENDPOINT', firefox)
        check.stop_browser_server.assert_called_once()

    def test_failed_browser_smoke_still_removes_remote_container(self):
        check = self.remote_check()
        check.args.wallet_browsers = 'firefox'
        check.chart_account = 'synthetic'
        check.identity = Mock(return_value=Mock(stdout='private-invitation\n'))
        check.start_browser_server = Mock(return_value='ws://127.0.0.1:3000/synthetic')
        check.stop_browser_server = Mock()
        with patch.object(verify_release, 'run', side_effect=subprocess.CalledProcessError(1, 'node')):
            with self.assertRaises(subprocess.CalledProcessError):
                check.browser_smokes()
        check.stop_browser_server.assert_called_once()

    def test_cleanup_removes_browser_before_compose_network_and_images(self):
        check = self.remote_check()
        check.browser_container = check.name + '-browser'
        (check.temp / 'local.yaml').touch()
        calls = []
        def run(*args, **kwargs):
            calls.append(args)
            return Mock(returncode=0)
        check.compose = Mock(side_effect=lambda *args, **kwargs: run('compose', *args, **kwargs))
        with patch.object(verify_release, 'run', side_effect=run), patch.object(verify_release.shutil, 'rmtree'):
            check.cleanup()
        self.assertEqual(('docker', 'rm', '-f', check.name + '-browser'), calls[0])
        self.assertEqual(('compose', 'down', '-v', '--remove-orphans'), calls[1])
        self.assertIsNone(check.browser_container)

    def test_browser_image_is_inspected_before_build_or_prepare(self):
        check = self.remote_check()
        check.prepare = Mock(side_effect=RuntimeError('stop before build'))
        check.wallet_signature = Mock()
        check.cleanup = Mock()
        with patch.object(verify_release, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'stop before build'):
                check.main()
        self.assertEqual(('docker', 'image', 'inspect', check.args.wallet_browser_image), run.call_args.args)
        check.cleanup.assert_called_once()

    def test_cleanup_accepts_browser_already_removed_by_docker(self):
        check = self.remote_check()
        check.browser_container = check.name + '-browser'
        with patch.object(verify_release, 'run', return_value=Mock(returncode=1, stderr='Error: No such container')):
            check.stop_browser_server()
        self.assertIsNone(check.browser_container)

    def test_browser_server_waits_for_its_http_response_not_only_the_docker_tcp_proxy(self):
        check = self.remote_check()
        connection = Mock()
        connection.getresponse.side_effect = [
            verify_release.http.client.RemoteDisconnected('not listening yet'),
            Mock(status=200, read=Mock(return_value=b'{"wsEndpointPath":"/test-path"}')),
        ]
        with patch.object(verify_release, 'run'), \
                patch.object(verify_release.http.client, 'HTTPConnection', return_value=connection), \
                patch.object(verify_release.secrets, 'token_hex', return_value='test-path'), \
                patch.object(verify_release.time, 'sleep') as sleep:
            self.assertTrue(check.start_browser_server().endswith('/test-path'))
        self.assertEqual(2, connection.request.call_count)
        sleep.assert_called_once_with(1)


class MigrationPreflightTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('docker'), 'Docker Compose is required to resolve deployment overlays')
    def test_compose_sign_in_settings_reach_every_host_and_fail_closed_without_an_origin(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            env = root / 'synthetic.env'
            common = ('POSTGRES_PASSWORD=synthetic\nRABBITMQ_USER=synthetic\nRABBITMQ_PASSWORD=synthetic\n'
                      'Vessel__Auth__OwnerId=11111111-1111-4111-8111-111111111111\nVESSEL_TLS_HOST=localhost\n'
                      # Leftovers of the removed Token sign-in in an old private .env reach every host, which refuses them.
                      'VESSEL_AUTH_MODE=Token\nVessel__Auth__Token=synthetic-leftover\n')
            files = ':'.join(str(verify_release.ROOT / 'deploy' / name) for name in ('compose.yaml', 'compose.tls.yaml'))
            for persisted in (True, False):
                with self.subTest(persisted_compose_file=persisted):
                    env.write_text(common + 'VESSEL_IDENTITY_ORIGIN=https://localhost\n' + (f'COMPOSE_FILE={files}\n' if persisted else ''))
                    command = ['docker', 'compose', '--project-directory', directory, '--env-file', str(env)]
                    if not persisted:
                        command += ['-f', str(verify_release.ROOT / 'deploy/compose.yaml')]
                    result = subprocess.run([*command, 'config', '--format', 'json'], check=True,
                                            env=verify_release.verification_environment(), capture_output=True, text=True)
                    services = json.loads(result.stdout)['services']
                    for service in ('migrate', 'api', 'worker', 'worker-notifications'):
                        values = services[service]['environment']
                        self.assertEqual(('Token', 'synthetic-leftover'),
                                         (values['Vessel__Auth__Mode'], values['Vessel__Auth__Token']))
                        self.assertEqual('11111111-1111-4111-8111-111111111111', values['Vessel__Auth__OwnerId'])
                        self.assertEqual('https://localhost', values['Vessel__Identity__Origins__0'])
                        self.assertEqual('Closed', values['Vessel__Identity__Registration'])
                        self.assertEqual('false', values['Vessel__Identity__AllowLegacyDefaultOwner'])
                    self.assertEqual(['CMD', '/app/worker-health'],
                                     services['worker-notifications']['healthcheck']['test'])
                    if persisted:
                        self.assertFalse(services['web'].get('ports'))
            env.write_text(common + f'COMPOSE_FILE={files}\n')
            missing = subprocess.run(['docker', 'compose', '--project-directory', directory, '--env-file', str(env), 'config', '--quiet'],
                                     env=verify_release.verification_environment(), capture_output=True, text=True)
            self.assertNotEqual(0, missing.returncode)
            self.assertIn('VESSEL_IDENTITY_ORIGIN', missing.stderr)

    @staticmethod
    def stub_bundles(root):
        # Stub bundles and dotnet log each call; the stub check-config exits CHECK_CONFIG_EXIT, standing in for the
        # real command (covered by ConfigurationCheckCommandTests) rejecting what the application rejects.
        for name in ('migrate-identity-ef', 'migrate-ef', 'dotnet'):
            path = root / name
            path.write_text('#!/bin/sh\nprintf "%s\\n" "' + name + ' $*" >> "$CALL_LOG"\n' +
                            ('case "$*" in *check-config) exit "${CHECK_CONFIG_EXIT:-0}" ;; esac\n' if name == 'dotnet' else ''))
            path.chmod(0o755)
        return (verify_release.ROOT / 'deploy/migrate').read_text().replace('/app', str(root))

    @staticmethod
    def run_stubbed(root, script, log, settings):
        environment = verify_release.verification_environment()
        # Prepend: replacing PATH hides tr/printf on hosts such as NixOS that keep them outside /usr/bin.
        return subprocess.run(['/bin/sh'], input=script, capture_output=True, text=True,
                              env={**environment, 'PATH': str(root) + os.pathsep + environment.get('PATH', os.defpath),
                                   'CALL_LOG': str(log), **settings})

    def test_valid_configuration_keeps_identity_journal_bootstrap_provision_order(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = self.stub_bundles(root)
            log = root / 'valid'
            result = self.run_stubbed(root, script, log, {'Vessel__Identity__Origins__0': 'https://localhost'})
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual([f'dotnet {root}/Vessel.Api.dll check-config', 'migrate-identity-ef ', 'migrate-ef ',
                              f'dotnet {root}/Vessel.Api.dll identity ensure-owner',
                              f'dotnet {root}/Vessel.Api.dll provision-messaging'], log.read_text().splitlines())

    def test_configuration_the_application_rejects_runs_neither_bundle(self):
        # deploy/migrate has no shell preflight of its own: check-config, the application's own validation, decides.
        valid = {'Vessel__Identity__Origins__0': 'https://localhost'}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = self.stub_bundles(root)
            for name, settings in (
                ('removed token', {**valid, 'Vessel__Auth__Token': 'synthetic'}),
                ('removed token mode', {**valid, 'Vessel__Auth__Mode': 'Token'}),
                ('lower-case mode', {**valid, 'Vessel__Auth__Mode': 'wallet'}),
                ('http origin in Production', {'Vessel__Identity__Origins__0': 'http://diary.example.com'}),
                ('misspelt registration', {**valid, 'Vessel__Identity__Registration': 'Clsoed'}),
            ):
                with self.subTest(name):
                    log = root / name.replace(' ', '-')
                    result = self.run_stubbed(root, script, log, {**settings, 'CHECK_CONFIG_EXIT': '1'})
                    self.assertEqual(1, result.returncode, result.stderr)
                    self.assertEqual([f'dotnet {root}/Vessel.Api.dll check-config'], log.read_text().splitlines())
                    self.assertIn('no migration was applied', result.stderr)
                    self.assertNotIn('synthetic', result.stderr)


if __name__ == '__main__':
    unittest.main()
