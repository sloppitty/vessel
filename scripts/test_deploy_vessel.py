"""Unit tests for the deploy/vessel operator command. A fake Docker answers every call: no container, image, volume or
.env file outside a temporary directory is touched."""
import contextlib
import importlib.machinery
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
_loader = importlib.machinery.SourceFileLoader('vessel_cli', str(ROOT / 'deploy' / 'vessel'))
_spec = importlib.util.spec_from_loader('vessel_cli', _loader)
vessel = importlib.util.module_from_spec(_spec)
_loader.exec_module(vessel)

JOURNAL = ['20260930201658_InitialAccountPlayHeads', '20261001071844_CoreAccountPortfolio']
IDENTITY = ['20261017155708_InitialIdentity']
NEW = '20261201000000_NextRelease'


def assembly(*ids):
    """Bytes standing for an assembly: noise, the attribute strings and a longer identifier that is not an ID."""
    return b'\x00MZ\x90' + b''.join(b'\x1b' + i.encode() + b'\x00' for i in ids) + b'x20261001071844_NotAnId\x00'


def model(version='1.0.0', storage='rustfs', paper=True):
    api_image, web_image = f'skorcius/vessel:api-{version}', f'skorcius/vessel:web-{version}'
    services = {
        'postgres': {'image': 'postgres:18-alpine', 'environment': {'POSTGRES_USER': 'vessel', 'POSTGRES_DB': 'vessel'}},
        'rabbitmq': {'image': 'rabbitmq:4.3.6-management'},
        'migrate': {'image': api_image, 'environment': {}},
        'api': {'image': api_image, 'environment': {'Vessel__Evidence__Storage__Provider': 'S3'},
                'volumes': [{'type': 'volume', 'source': 'api-tmp', 'target': '/tmp'}]},
        'worker': {'image': api_image}, 'worker-notifications': {'image': api_image},
        'web': {'image': web_image},
    }
    volumes = {'postgres': {'name': 'p_postgres'}, 'api-tmp': {'name': 'p_api-tmp'}}
    if storage == 'rustfs':
        services['rustfs'] = {'image': 'rustfs/rustfs:1.0.1', 'volumes': [{'type': 'volume', 'source': 'objects', 'target': '/data'}]}
        services['storage-init'] = {'image': 'amazon/aws-cli:2.38.1'}
        volumes['objects'] = {'name': 'p_objects'}
    elif storage == 'local':
        services['api']['environment']['Vessel__Evidence__Storage__Provider'] = 'Local'
        services['api']['volumes'].append({'type': 'volume', 'source': 'evidence', 'target': '/var/lib/vessel/evidence'})
        volumes['evidence'] = {'name': 'p_evidence'}
    if paper:
        services['worker-paper'] = {'image': api_image}
    return {'name': 'p', 'services': services, 'volumes': volumes}


def inspect(service, state='running', oneoff=False, image='skorcius/vessel:api-1.0.0', number=1):
    return {'Id': f'{service}-{number}-id', 'Name': f'/p-{service}-{number}', 'Image': 'sha256:' + service,
            'Config': {'Image': image, 'Labels': {'com.docker.compose.service': service,
                                                  'com.docker.compose.oneoff': 'True' if oneoff else 'False'}},
            'State': {'Status': state}}


class Result:
    def __init__(self, returncode=0, stdout=''):
        self.returncode, self.stdout = returncode, stdout


class FakeStack(vessel.Stack):
    """Answers docker calls from in-memory state and records every call."""

    def __init__(self, directory, version='1.0.0', storage='rustfs', containers=None, applied=None, images=None,
                 known=None):
        super().__init__(directory)
        self.calls = []
        self.version = version
        self.storage_kind = storage
        self.state = containers if containers is not None else [
            inspect(s) for s in ('postgres', 'rabbitmq', 'rustfs', 'migrate', 'api', 'worker',
                                 'worker-notifications', 'worker-paper', 'web')]
        for item in self.state:
            if item['Config']['Labels']['com.docker.compose.service'] in ('migrate', 'storage-init'):
                item['State']['Status'] = 'exited'
        self.applied_ids = applied if applied is not None else {'journal': list(JOURNAL), 'identity': list(IDENTITY)}
        self.images = set(images if images is not None else {f'skorcius/vessel:api-{version}',
                                                             f'skorcius/vessel:web-{version}', 'postgres:18-alpine'})
        self.known_ids = known or {}
        self.volumes = {'p_postgres', 'p_objects', 'p_evidence'}

    def run(self, args, *, stdin=None, stdout=None, capture=False, binary=False, check=True, env=None):
        args = list(args)
        self.calls.append(args)
        result = self.answer(args, env or {})
        if check and result.returncode:
            raise vessel.Failure('failed: ' + ' '.join(args))
        if stdout is not None and hasattr(stdout, 'write') and result.stdout:
            stdout.write(result.stdout if isinstance(result.stdout, bytes) else result.stdout.encode())
        return result

    def answer(self, args, env):
        if args[:2] == ['docker', 'compose']:
            rest = args[args.index(str(self.directory)) + 1:]
            while rest and rest[0] in ('-p', '-f', '--profile'):
                rest = rest[2:]
            return self.compose_answer(rest, env)
        if args[:3] == ['docker', 'ps', '-aq']:
            return Result(stdout='\n'.join(item['Id'] for item in self.state))
        if args[:2] == ['docker', 'inspect']:
            return Result(stdout=json.dumps([item for item in self.state if item['Id'] in args[2:]]))
        if args[:3] == ['docker', 'image', 'inspect']:
            return Result(0 if args[-1] in self.images else 1)
        if args[:3] == ['docker', 'volume', 'inspect']:
            return Result(0 if args[3] in self.volumes else 1)
        if args[:3] == ['docker', 'volume', 'ls']:
            return Result(stdout='\n'.join(sorted(self.volumes)))
        if args[:2] == ['docker', 'info']:
            return Result(stdout=tempfile.gettempdir())
        if args[:2] == ['docker', 'run'] and 'cat' in args:
            image, path = args[-2], args[-1]
            schema = 'journal' if path.endswith('Persistence.dll') else 'identity'
            ids = self.known_ids.get(image, {'journal': JOURNAL, 'identity': IDENTITY})[schema]
            return Result(stdout=assembly(*ids))
        if args[:2] == ['docker', 'run'] and 'du' in args:
            return Result(stdout='2048\t/v\n')
        return self.other(args)

    def other(self, args):
        raise AssertionError('unexpected docker call: ' + ' '.join(args))

    def compose_answer(self, rest, env):
        if rest[:1] == ['config']:
            return Result(stdout=json.dumps(model(env.get('VESSEL_VERSION') or self.version, self.storage_kind)))
        if rest[:4] == ['exec', '-T', 'postgres', 'psql']:
            query = rest[-1]
            if 'to_regclass' in query:
                return Result(stdout='t\n')
            if 'pg_database_size' in query:
                return Result(stdout='4096\n')
            schema = 'identity' if '"identity"' in query else 'journal'
            return Result(stdout='\n'.join(self.applied_ids[schema]) + '\n')
        return self.compose_other(rest, env)

    def compose_other(self, rest, env):
        raise AssertionError('unexpected compose call: ' + ' '.join(rest))


def quietly(function, *args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
        code = function(*args)
    return code, out.getvalue()


class Args:
    def __init__(self, **values):
        self.__dict__.update({'to': None, 'yes': False, 'keep_active': [], 'start': False, 'pull': True,
                              'manifest': None, 'source': None, 'wait_timeout': 300}, **values)


# Read-only commands may only inspect: the Compose model, containers, volumes, images, the migration history (psql
# SELECTs) and an image's files from a network-less throwaway container.
def read_only(call):
    if call[:2] == ['docker', 'compose']:
        return 'config' in call or ('exec' in call and 'psql' in call and 'SELECT' in call[-1])
    if call[:2] == ['docker', 'run']:
        return '--rm' in call and call[call.index('--network') + 1] == 'none' and ('cat' in call or 'du' in call)
    return call[:3] in (['docker', 'ps', '-aq'], ['docker', 'image', 'inspect'], ['docker', 'volume', 'inspect'],
                        ['docker', 'volume', 'ls']) or call[:2] in (['docker', 'inspect'], ['docker', 'info'])


class HelperTests(unittest.TestCase):
    def test_migration_ids_come_from_attribute_strings_only(self):
        self.assertEqual(JOURNAL + [NEW], vessel.migration_ids(assembly(NEW, *JOURNAL)))

    def test_pending_and_unknown_per_schema(self):
        known = {'journal': JOURNAL + [NEW], 'identity': IDENTITY}
        applied = {'journal': JOURNAL[:1] + ['29990101000000_Future'], 'identity': IDENTITY}
        result = vessel.compare_migrations(known, applied)
        self.assertEqual([JOURNAL[1], NEW], result['journal']['pending'])
        self.assertEqual(['29990101000000_Future'], result['journal']['unknown'])
        self.assertEqual(([], []), (result['identity']['pending'], result['identity']['unknown']))
        self.assertIsNone(vessel.compare_migrations(None, applied)['journal']['pending'])

    def test_image_version_reads_release_tags_only(self):
        self.assertEqual('0.4.2', vessel.image_version('skorcius/vessel:api-0.4.2'))
        self.assertEqual('1.0.1', vessel.image_version('registry.local:5000/vessel:web-1.0.1'))
        self.assertIsNone(vessel.image_version('skorcius/vessel@sha256:abc'))

    def test_writers_are_every_running_host_but_the_data_stores_front_first(self):
        containers = [vessel.Container(inspect(s)) for s in
                      ('postgres', 'rabbitmq', 'rustfs', 'api', 'worker', 'worker-notifications', 'worker-paper',
                       'verifier', 'web', 'tls')]
        containers.append(vessel.Container(inspect('migrate', oneoff=True)))
        containers.append(vessel.Container(inspect('worker', state='exited', number=2)))
        front, hosts = vessel.writers(containers)
        self.assertEqual(['web', 'tls'], [c.service for c in front])
        self.assertEqual(['api', 'worker', 'worker-notifications', 'worker-paper', 'verifier', 'migrate'],
                         [c.service for c in hosts])


class ReadOnlyCommandTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        Path(self.directory.name, '.env').write_text('VESSEL_VERSION=1.0.0\n')
        patcher = mock.patch.dict(os.environ)
        patcher.start()
        self.addCleanup(patcher.stop)
        os.environ.pop('VESSEL_VERSION', None)

    def stack(self, **kwargs):
        return FakeStack(self.directory.name, **kwargs)

    def test_status_reports_every_worker_migrations_and_the_last_backup(self):
        stack = self.stack(applied={'journal': JOURNAL[:1], 'identity': IDENTITY})
        backup = stack.backup_root / 'vessel-20261120T100000Z'
        backup.mkdir(parents=True)
        (backup / vessel.MANIFEST).write_text(json.dumps({'kind': 'manual', 'createdAt': '2026-11-20T10:00:00Z',
                                                          'version': '1.0.0'}))
        code, out = quietly(vessel.status, stack, Args())
        self.assertEqual(0, code)
        for text in ('worker-paper', 'worker-notifications', 'Configured version: 1.0.0', 'embedded RustFS',
                     'journal: 1 applied, 1 pending, 0 unknown', JOURNAL[1], 'vessel-20261120T100000Z (manual'):
            self.assertIn(text, out)
        self.assertTrue(all(read_only(call) for call in stack.calls), [c for c in stack.calls if not read_only(c)])

    def test_preflight_passes_on_a_current_stack_and_only_reads(self):
        stack = self.stack()
        code, out = quietly(vessel.preflight, stack, Args())
        self.assertEqual(0, code, out)
        self.assertIn('api, web, worker, worker-notifications, worker-paper', out)
        self.assertTrue(all(read_only(call) for call in stack.calls), [c for c in stack.calls if not read_only(c)])

    def test_preflight_fails_on_migrations_the_image_does_not_know(self):
        stack = self.stack(applied={'journal': JOURNAL + ['29990101000000_Future'], 'identity': IDENTITY})
        code, out = quietly(vessel.preflight, stack, Args())
        self.assertEqual(1, code)
        self.assertIn('29990101000000_Future', out)

    def test_preflight_for_a_target_lists_its_migrations_and_missing_images(self):
        stack = self.stack()
        stack.images.add('skorcius/vessel:api-1.1.0')
        stack.known_ids['skorcius/vessel:api-1.1.0'] = {'journal': JOURNAL + [NEW], 'identity': IDENTITY}
        code, out = quietly(vessel.preflight, stack, Args(to='1.1.0'))
        self.assertEqual(0, code, out)
        self.assertIn(f'1.1.0 applies 1 journal migration(s): {NEW}', out)
        self.assertIn('upgrade` pulls: skorcius/vessel:web-1.1.0', out)
        self.assertTrue(all(read_only(call) for call in stack.calls))

    def test_preflight_refuses_a_downgrade_and_a_shell_override(self):
        stack = self.stack(applied={'journal': JOURNAL + [NEW], 'identity': IDENTITY},
                           known={'skorcius/vessel:api-1.0.0': {'journal': JOURNAL + [NEW], 'identity': IDENTITY}})
        stack.images.update({'skorcius/vessel:api-0.9.0', 'skorcius/vessel:web-0.9.0'})
        os.environ['VESSEL_VERSION'] = '0.9.0'
        code, out = quietly(vessel.preflight, stack, Args(to='0.9.0'))
        self.assertEqual(1, code)
        self.assertIn('VESSEL_VERSION is set in this shell', out)
        self.assertIn('needs `rollback`', out)

    def test_preflight_warns_that_an_external_bucket_is_not_backed_up(self):
        code, out = quietly(vessel.preflight, self.stack(storage='external'), Args())
        self.assertEqual(0, code, out)
        self.assertIn('external S3 bucket, which backups do not include', out)


if __name__ == '__main__':
    unittest.main()
