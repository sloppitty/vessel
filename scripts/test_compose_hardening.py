"""Issue #105: every service in every documented overlay combination keeps capped logs and the hardening flags.

Resolves the repository Compose files with synthetic values only; never reads an .env file or starts a container."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent

# The combinations deploy/README.md documents, each also with the paper overlay.
COMBINATIONS = [
    [], ['compose.tls.yaml'], ['compose.storage-local.yaml'], ['compose.storage-external.yaml'],
    ['compose.credentials.yaml'], ['compose.tls.yaml', 'compose.credentials.yaml'],
    ['compose.tls.yaml', 'compose.storage-local.yaml', 'compose.credentials.yaml'],
    ['compose.tls.yaml', 'compose.storage-external.yaml', 'compose.credentials.yaml'],
]
# Capabilities a service demonstrably needs: their entrypoints start as root, fix the data volume's ownership and
# switch to their own user. Every other service runs with none.
CAPABILITIES = {'postgres': ['CHOWN', 'DAC_OVERRIDE', 'SETUID', 'SETGID']}
ONE_SHOT = {'migrate', 'storage-init'}


@unittest.skipUnless(shutil.which('docker'), 'docker compose is not installed')
class ComposeHardeningTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix='vessel-compose-hardening-'))
        self.addCleanup(shutil.rmtree, self.directory)
        self.ring = self.directory / 'ring.json'
        self.ring.write_text('{}')

    def config(self, files, **overrides):
        command = ['docker', 'compose', '--project-directory', str(self.directory), '--env-file', '/dev/null']
        for file in ['compose.yaml', *files]:
            command += ['-f', str(ROOT / 'deploy' / file)]
        if 'compose.paper.yaml' in files:
            command += ['--profile', 'paper']
        env = {'PATH': os.environ['PATH'], 'HOME': str(self.directory), 'POSTGRES_PASSWORD': 'synthetic',
               'Vessel__Auth__OwnerId': '00000000-0000-4000-8000-000000000001',
               'VESSEL_IDENTITY_ORIGIN': 'https://vessel.invalid', 'VESSEL_TLS_HOST': 'vessel.invalid',
               'VESSEL_VERSION': '0.0.0', 'VESSEL_CREDENTIAL_KEYS_FILE': str(self.ring), **overrides}
        result = subprocess.run([*command, 'config', '--format', 'json'], cwd=self.directory, env=env,
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout)['services']

    def test_every_service_of_every_combination_is_capped_and_hardened(self):
        for combination in COMBINATIONS:
            for files in (combination, [*combination, 'compose.paper.yaml']):
                services = self.config(files)
                if 'compose.tls.yaml' in files:
                    self.assertIn('tls', services)
                if 'compose.paper.yaml' in files:
                    self.assertIn('worker-paper', services)
                for name, service in services.items():
                    with self.subTest(files=files, service=name):
                        self.assertEqual({'driver': 'json-file', 'options': {'max-size': '10m', 'max-file': '5'}},
                                         service.get('logging'))
                        self.assertEqual(['no-new-privileges:true'], service.get('security_opt'))
                        self.assertEqual(['ALL'], service.get('cap_drop'))
                        self.assertEqual(CAPABILITIES.get(name), service.get('cap_add'))
                        self.assertTrue(service.get('read_only'))
                        if name in ONE_SHOT:
                            continue
                        self.assertEqual('unless-stopped', service.get('restart'))
                        healthcheck = service.get('healthcheck') or {}
                        self.assertFalse(healthcheck.get('disable', False))
                        self.assertTrue(healthcheck.get('test') or name == 'api',
                                        'a long-running service has a health check (the API image has its own)')

    def test_log_limits_are_configurable(self):
        services = self.config([], VESSEL_LOG_MAX_SIZE='50m', VESSEL_LOG_MAX_FILE='2')
        self.assertTrue(all(service['logging']['options'] == {'max-size': '50m', 'max-file': '2'}
                            for service in services.values()))

    def test_web_and_tls_health_checks_probe_their_own_listeners(self):
        services = self.config(['compose.tls.yaml'])
        self.assertEqual(['CMD', 'wget', '-q', '-O', '/dev/null', 'http://127.0.0.1:8080/index.html'],
                         services['web']['healthcheck']['test'])
        self.assertEqual(['CMD', 'wget', '-q', '-O', '/dev/null', 'http://127.0.0.1:2019/config/'],
                         services['tls']['healthcheck']['test'])

    def test_read_only_services_keep_the_paths_they_write(self):
        services = self.config(['compose.storage-local.yaml', 'compose.credentials.yaml', 'compose.paper.yaml'])
        tmpfs = {name: [entry.split(':')[0] for entry in service.get('tmpfs', [])] for name, service in services.items()}
        targets = {name: [volume['target'] for volume in service.get('volumes', [])] for name, service in services.items()}
        self.assertIn('/etc/nginx/conf.d', tmpfs['web'])
        self.assertIn('/var/run/postgresql', tmpfs['postgres'])
        for name in ('worker', 'worker-notifications', 'worker-paper', 'migrate', 'web', 'postgres'):
            self.assertIn('/tmp', tmpfs[name], name)
        # The API stages exports and uploads in /tmp on disk, beside its evidence and key ring.
        self.assertEqual({'/tmp', '/var/lib/vessel/evidence', '/run/secrets/vessel-credential-keys.json'}, set(targets['api']))
        self.assertIn('/var/lib/postgresql', targets['postgres'])
        # Issue #99: no broker service, volume or connection setting is left.
        self.assertNotIn('rabbitmq', services)
        self.assertFalse([(name, key) for name, service in services.items() for key in service.get('environment') or {}
                          if 'rabbit' in key.casefold()])
        # RustFS logs to stdout (the capped Docker log), not to a file in its read-only image.
        self.assertEqual('', self.config([])['rustfs']['environment']['RUSTFS_OBS_LOG_DIRECTORY'])


if __name__ == '__main__':
    unittest.main()
