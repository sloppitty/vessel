"""Unit tests for the deploy/vessel operator command. A fake Docker answers every call: no container, image, volume or
.env file outside a temporary directory is touched."""
import contextlib
import importlib.machinery
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import stat
import tarfile
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


def model(version='1.0.0', storage='rustfs', paper=True, pins=None):
    api_image, web_image = f'skorcius/vessel:api-{version}', f'skorcius/vessel:web-{version}'
    services = {
        'postgres': {'image': 'postgres:18-alpine', 'environment': {'POSTGRES_USER': 'vessel', 'POSTGRES_DB': 'vessel'}},
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
    for service, image in (pins or {}).items():
        services[service]['image'] = image
    return {'name': 'p', 'services': services, 'volumes': volumes}


def inspect(service, state='running', oneoff=False, image='skorcius/vessel:api-1.0.0', number=1):
    return {'Id': f'{service}-{number}-id', 'Name': f'/p-{service}-{number}', 'Image': 'sha256:' + service,
            'Config': {'Image': image, 'Labels': {'com.docker.compose.service': service,
                                                  'com.docker.compose.oneoff': 'True' if oneoff else 'False'}},
            'State': {'Status': state}}


def release_archive(files):
    """What `tar -C /app/upgrade -cf - .` writes for these {file name: manifest}."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode='w') as archive:
        archive.addfile(_directory('.'))
        for name, manifest in files.items():
            data = json.dumps(manifest).encode()
            info = tarfile.TarInfo('./' + name)
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


def _directory(name):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    return info


class Result:
    def __init__(self, returncode=0, stdout=''):
        self.returncode, self.stdout = returncode, stdout


class FakeStack(vessel.Stack):
    """Answers docker calls from in-memory state and records every call."""

    def __init__(self, directory, version='1.0.0', storage='rustfs', containers=None, applied=None, images=None,
                 known=None, pins=None, **selectors):
        super().__init__(directory, **selectors)
        self.pins = pins or {}
        self.calls = []
        self.version = version
        self.storage_kind = storage
        self.state = containers if containers is not None else [
            inspect(s) for s in ('postgres', 'rustfs', 'migrate', 'api', 'worker',
                                 'worker-notifications', 'worker-paper', 'web')]
        for item in self.state:
            if item['Config']['Labels']['com.docker.compose.service'] in ('migrate', 'storage-init'):
                item['State']['Status'] = 'exited'
        self.applied_ids = applied if applied is not None else {'journal': list(JOURNAL), 'identity': list(IDENTITY)}
        self.images = set(images if images is not None else {f'skorcius/vessel:api-{version}',
                                                             f'skorcius/vessel:web-{version}', 'postgres:18-alpine'})
        self.known_ids = known or {}
        # Upgrade manifests each API image carries, by image: {file name: manifest}. An image not listed carries an
        # empty manifest for its own version; None means no manifest directory (releases before 0.4.3), and a
        # number is the exit code of a failing read.
        self.upgrade_files = {}
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
        if args[:2] == ['docker', 'run'] and args[args.index('--entrypoint') + 1] == 'tar' and vessel.UPGRADE_DIR in args:
            image = args[args.index('--entrypoint') + 2]
            version = image.rsplit(':api-', 1)[1]
            files = self.upgrade_files.get(image, {f'v{version}.upgrade.json': {'version': version}})
            if files is None or isinstance(files, int):
                return Result(files or 2, b'')
            return Result(stdout=release_archive(files))
        return self.other(args)

    def other(self, args):
        raise AssertionError('unexpected docker call: ' + ' '.join(args))

    def compose_answer(self, rest, env):
        if rest[:1] == ['config']:
            # As Compose: the shell overrides .env, which overrides the compose.yaml default.
            env_file = Path(self.directory, '.env')
            lines = env_file.read_text().splitlines() if env_file.exists() else []
            from_file = [line.split('=', 1)[1] for line in lines if line.startswith('VESSEL_VERSION=')]
            version = env.get('VESSEL_VERSION') or (from_file[-1] if from_file else self.version)
            return Result(stdout=json.dumps(model(version, self.storage_kind, pins=self.pins)))
        if rest[:4] == ['exec', '-T', 'postgres', 'psql'] and 'SELECT' in rest[-1]:
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
        return '--rm' in call and call[call.index('--network') + 1] == 'none' and (
            'cat' in call or 'du' in call or (vessel.UPGRADE_DIR in call and '-cf' in call))
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
                      ('postgres', 'rustfs', 'api', 'worker', 'worker-notifications', 'worker-paper',
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


def evidence_archive():
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode='w:gz') as archive:
        data = b'image bytes'
        info = tarfile.TarInfo('./owner/play/image.png')
        info.size = len(data)
        archive.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


TOC = ';\n215; 1259 16385 TABLE public __EFMigrationsHistory vessel\n216; 1259 16390 TABLE identity __EFMigrationsHistory vessel\n'


class ChangingStack(FakeStack):
    """Also performs the changing calls in memory and records them as events."""

    def __init__(self, directory, fail=(), **kwargs):
        super().__init__(directory, **kwargs)
        self.events = []
        self.fail = set(fail)
        # Services whose docker stop/start fails while the rest of the same call goes through, as Docker does.
        self.stuck = {'stop': set(), 'start': set()}

    def service_of(self, container_id):
        return next(i for i in self.state if i['Id'] == container_id)

    def other(self, args):
        if args[:2] in (['docker', 'stop'], ['docker', 'start']):
            items = [self.service_of(i) for i in args[2:]]
            done = [i for i in items if i['Config']['Labels']['com.docker.compose.service'] not in self.stuck[args[1]]]
            for item in done:
                item['State']['Status'] = 'exited' if args[1] == 'stop' else 'running'
            self.events.append((args[1], sorted(i['Config']['Labels']['com.docker.compose.service'] for i in done)))
            return Result(1 if len(done) < len(items) else 0)
        if args[:2] == ['docker', 'pull']:
            self.events.append(('pull', args[2]))
            self.images.add(args[2])
            return Result()
        return super().other(args)

    def version_in_env(self):
        lines = Path(self.directory, '.env').read_text().splitlines()
        return [line.split('=', 1)[1] for line in lines if line.startswith('VESSEL_VERSION=')][-1]

    def compose_other(self, rest, env):
        def event(*item):
            if item[0] in self.fail:
                self.events.append(('failed',) + item)
                return Result(1)
            self.events.append(item)
            return Result()
        if rest[:4] == ['exec', '-T', 'postgres', 'pg_dump']:
            result = event('dump')
            return Result(result.returncode, b'PGDMP fake dump' if not result.returncode else b'')
        if rest[:4] == ['exec', '-T', 'postgres', 'pg_restore'] and '-l' in rest:
            return Result(stdout=TOC)
        if rest[:4] == ['exec', '-T', 'postgres', 'pg_restore']:
            return event('restore-database')
        if rest[:4] == ['exec', '-T', 'postgres', 'psql'] and 'DROP DATABASE' in ' '.join(rest):
            return event('drop-database')
        if rest[:1] == ['run'] and '--entrypoint' in rest:
            entrypoint = rest[rest.index('--entrypoint') + 1]
            service = rest[rest.index('--entrypoint') + 2]
            if entrypoint == 'tar':
                result = event('archive', service)
                return Result(result.returncode, evidence_archive() if not result.returncode else b'')
            if entrypoint == 'sh':
                return event('restore-evidence', service, rest[-1])
            if entrypoint == 'dotnet':
                return event('dotnet', *rest[rest.index(vessel.API_DLL) + 1:])
        if rest == ['run', '--rm', 'migrate']:
            return event('migrate', self.version_in_env())
        if rest[:1] == ['up']:
            return event('up', *[a for a in rest[1:] if not a.startswith('-') and not a.isdigit()])
        if rest[:1] == ['stop']:
            return event('compose-stop', *rest[1:])
        return super().compose_other(rest, env)


class ChangingCommandTests(ReadOnlyCommandTests):
    def stack(self, **kwargs):
        return ChangingStack(self.directory.name, **kwargs)

    def env_text(self):
        return Path(self.directory.name, '.env').read_text()

    def pre_upgrade(self, stack, version='0.9.0'):
        """A backup as upgrade from version would have left it."""
        env_file = Path(self.directory.name, '.env')
        env_file.write_text(f'VESSEL_VERSION={version}\n')
        stack.images.update({f'skorcius/vessel:api-{version}', f'skorcius/vessel:web-{version}'})
        with contextlib.redirect_stdout(io.StringIO()):
            path = vessel.take_backup(stack, 'pre-upgrade', ['api', 'worker', 'worker-paper'], version,
                                      {'upgradeTo': '1.0.0'})
        env_file.write_text('VESSEL_VERSION=1.0.0\n')
        stack.forget()
        stack.events.clear()
        return path

    def test_changing_commands_without_yes_change_nothing(self):
        for command, args in ((vessel.backup, Args()), (vessel.upgrade, Args(to='1.1.0')),
                              (vessel.rollback, Args())):
            with self.subTest(command=command.__name__):
                stack = self.stack()
                stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
                if command is vessel.rollback:
                    self.pre_upgrade(stack)
                    stack.calls.clear()
                code, out = quietly(command, stack, args)
                self.assertEqual(2, code, out)
                self.assertIn('Run the same command with --yes', out)
                self.assertEqual([], stack.events)
                self.assertTrue(all(read_only(call) for call in stack.calls),
                                [c for c in stack.calls if not read_only(c)])
                self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())

    def test_backup_stops_every_writer_first_and_restarts_what_ran(self):
        stack = self.stack()
        code, out = quietly(vessel.backup, stack, Args(yes=True))
        self.assertEqual(0, code, out)
        hosts = ['api', 'worker', 'worker-notifications', 'worker-paper']
        self.assertEqual([('stop', ['web']), ('stop', hosts), ('dump',), ('stop', ['rustfs']), ('archive', 'rustfs'),
                          ('start', ['rustfs']), ('start', hosts), ('start', ['web'])], stack.events)
        [directory] = list(stack.backup_root.iterdir())
        _, manifest = vessel.load_backup(directory)
        self.assertEqual(('manual', '1.0.0', 'rustfs', 1), (manifest['kind'], manifest['version'],
                                                           manifest['storage']['kind'], manifest['storage']['entries']))
        self.assertEqual({'journal': JOURNAL, 'identity': IDENTITY}, manifest['migrations'])
        self.assertEqual(sorted(hosts + ['postgres', 'rustfs', 'web']), manifest['services'])
        self.assertEqual(0o700, stat.S_IMODE(directory.stat().st_mode))
        self.assertEqual(0o600, stat.S_IMODE((directory / 'vessel.dump').stat().st_mode))

    def test_a_backup_never_reuses_a_directory_or_a_file(self):
        root = Path(self.directory.name, 'backups')
        now = vessel.datetime.datetime(2026, 11, 20, 10, 0, 0, tzinfo=vessel.datetime.timezone.utc)
        first = vessel.new_backup_dir(root, now)
        (first / 'vessel.dump').write_bytes(b'first')
        second = vessel.new_backup_dir(root, now)
        self.assertEqual(('vessel-20261120T100000Z', 'vessel-20261120T100000Z-1'), (first.name, second.name))
        with self.assertRaises(FileExistsError):
            vessel.create(first / 'vessel.dump')
        self.assertEqual(b'first', (first / 'vessel.dump').read_bytes())

    def test_a_failed_backup_leaves_no_directory_and_restarts_the_writers(self):
        stack = self.stack(fail={'archive'})
        with self.assertRaises(vessel.Failure):
            quietly(vessel.backup, stack, Args(yes=True))
        self.assertEqual([], list(stack.backup_root.iterdir()))
        self.assertEqual(('start', ['web']), stack.events[-1])
        self.assertIn(('start', ['rustfs']), stack.events)

    def test_an_external_bucket_is_named_as_not_included(self):
        stack = self.stack(storage='external')
        code, out = quietly(vessel.backup, stack, Args(yes=True))
        self.assertEqual(0, code, out)
        self.assertNotIn('archive', [e[0] for e in stack.events])
        self.assertIn('external S3 bucket is NOT in this backup', out)
        [directory] = list(stack.backup_root.iterdir())
        _, manifest = vessel.load_backup(directory)
        self.assertIn("the external S3 bucket (use the provider's backup)", manifest['notIncluded'])

    def test_a_tampered_backup_is_refused(self):
        stack = self.stack()
        quietly(vessel.backup, stack, Args(yes=True))
        [directory] = list(stack.backup_root.iterdir())
        with open(directory / 'objects.tar.gz', 'ab') as archive:
            archive.write(b'x')
        with self.assertRaisesRegex(vessel.Failure, 'objects.tar.gz does not match'):
            vessel.load_backup(directory)

    def upgrade_manifest(self):
        path = Path(self.directory.name, 'v1.1.0.upgrade.json')
        path.write_text(json.dumps({'version': '1.1.0', 'migrations': {'journal': [NEW]},
                                    'postSteps': [['play-search-rebuild', '--apply']],
                                    'configuration': ['No new settings.']}))
        return str(path)

    def test_upgrade_backs_up_before_migrating_and_restarts_every_worker_on_the_target(self):
        stack = self.stack()
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        Path(self.directory.name, '.env').write_text('POSTGRES_PASSWORD=secret\nVESSEL_VERSION=1.0.0\nOTHER=x\n')
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True, manifest=self.upgrade_manifest()))
        self.assertEqual(0, code, out)
        kinds = [e[0] for e in stack.events]
        self.assertLess(kinds.index('dump'), kinds.index('migrate'))
        self.assertLess(kinds.index('archive'), kinds.index('migrate'))
        self.assertEqual(('migrate', '1.1.0'), stack.events[kinds.index('migrate')])
        self.assertEqual(('dotnet', 'play-search-rebuild', '--apply'), stack.events[kinds.index('migrate') + 1])
        self.assertEqual(('up', 'api', 'postgres', 'rustfs', 'web', 'worker', 'worker-notifications',
                          'worker-paper'), stack.events[-1])
        self.assertEqual('POSTGRES_PASSWORD=secret\nVESSEL_VERSION=1.1.0\nOTHER=x\n', self.env_text())
        self.assertNotIn('secret', out)
        [directory] = list(stack.backup_root.iterdir())
        _, manifest = vessel.load_backup(directory)
        self.assertEqual(('pre-upgrade', '1.0.0', '1.1.0'), (manifest['kind'], manifest['version'], manifest['upgradeTo']))

    def test_upgrade_never_migrates_when_the_backup_fails(self):
        stack = self.stack(fail={'dump'})
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        with self.assertRaisesRegex(vessel.Failure, 'nothing was migrated'):
            quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        self.assertNotIn('migrate', [e[0] for e in stack.events])
        self.assertEqual(('start', ['web']), stack.events[-1])
        self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())
        self.assertEqual([], list(stack.backup_root.iterdir()))

    def test_upgrade_pulls_missing_images_before_stopping_anything(self):
        stack = self.stack()
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        self.assertEqual(0, code, out)
        self.assertEqual([('pull', 'skorcius/vessel:api-1.1.0'), ('pull', 'skorcius/vessel:web-1.1.0'),
                          ('stop', ['web'])], stack.events[:3])
        Path(self.directory.name, '.env').write_text('VESSEL_VERSION=1.0.0\n')
        with self.assertRaisesRegex(vessel.Failure, '--no-pull'):
            quietly(vessel.upgrade, self.stack(), Args(to='1.2.0', yes=True, pull=False))

    def test_upgrade_runs_the_post_steps_of_every_release_it_crosses_from_the_target_image(self):
        stack = self.stack()
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        rebuild = ['play-search-rebuild', '--apply', '--all']
        stack.upgrade_files['skorcius/vessel:api-1.1.0'] = {
            'v1.0.0.upgrade.json': {'version': '1.0.0', 'postSteps': [['already-applied']]},
            'v1.0.10.upgrade.json': {'version': '1.0.10', 'postSteps': [rebuild, ['second']],
                                     'configuration': ['New optional setting.']},
            'v1.0.9.upgrade.json': {'version': '1.0.9', 'postSteps': [['first']]},
            'v1.1.0.upgrade.json': {'version': '1.1.0', 'migrations': {'journal': [NEW]}, 'postSteps': [rebuild]},
            'v1.2.0.upgrade.json': {'version': '1.2.0', 'postSteps': [['not-yet']]},
        }
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        self.assertEqual(0, code, out)
        steps = [e[1:] for e in stack.events if e[0] == 'dotnet']
        self.assertEqual([('first',), tuple(rebuild), ('second',)], steps)
        kinds = [e[0] for e in stack.events]
        self.assertLess(kinds.index('migrate'), kinds.index('dotnet'))
        self.assertLess(len(kinds) - 1 - kinds[::-1].index('dotnet'), kinds.index('up'))
        self.assertIn('Release upgrade manifests: 1.0.9, 1.0.10, 1.1.0', out)
        self.assertIn('1.0.10 configuration: New optional setting.', out)
        self.assertIn(f'1.1.0 journal migrations: {NEW}', out)

    def test_upgrade_reads_the_manifests_of_a_pulled_image_before_stopping_anything(self):
        stack = self.stack()
        stack.upgrade_files['skorcius/vessel:api-1.1.0'] = {
            'v1.1.0.upgrade.json': {'version': '1.1.0', 'postSteps': [['play-search-rebuild', '--apply']]}}
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0'))
        self.assertEqual(2, code, out)
        self.assertIn('Run the post-steps of the release upgrade manifests in the pulled 1.1.0 image.', out)
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        self.assertEqual(0, code, out)
        self.assertLess(out.index('Release upgrade manifests: 1.1.0'), out.index('Stopping web'))
        self.assertIn(('dotnet', 'play-search-rebuild', '--apply'), stack.events)

    def test_upgrade_to_a_release_before_manifests_runs_no_post_step(self):
        Path(self.directory.name, '.env').write_text('VESSEL_VERSION=0.4.1\n')
        stack = self.stack(version='0.4.1')
        stack.state = [inspect(s, image='skorcius/vessel:api-0.4.1') for s in ('postgres', 'api', 'worker', 'web')]
        stack.images.update({'skorcius/vessel:api-0.4.2', 'skorcius/vessel:web-0.4.2'})
        stack.upgrade_files['skorcius/vessel:api-0.4.2'] = None
        code, out = quietly(vessel.upgrade, stack, Args(to='0.4.2', yes=True))
        self.assertEqual(0, code, out)
        self.assertIn('carries no upgrade manifest', out)
        self.assertNotIn('dotnet', [e[0] for e in stack.events])
        self.assertIn(('migrate', '0.4.2'), stack.events)

    def test_upgrade_to_0_4_3_or_later_stops_before_anything_when_its_manifest_is_missing(self):
        image = 'skorcius/vessel:api-0.4.3'
        cases = {'no manifest directory': (None, 'cannot be read'),
                 'a failing read': (125, 'cannot be read'),
                 'no manifest for the target': ({'v0.4.2.upgrade.json': {'version': '0.4.2'}}, 'no upgrade manifest '
                                                                                                'for 0.4.3 itself')}
        for case, (files, message) in cases.items():
            with self.subTest(case):
                Path(self.directory.name, '.env').write_text('VESSEL_VERSION=0.4.2\n')
                stack = self.stack(version='0.4.2')
                stack.state = [inspect(s, image='skorcius/vessel:api-0.4.2') for s in ('postgres', 'api', 'worker', 'web')]
                stack.images.update({image, 'skorcius/vessel:web-0.4.3'})
                stack.upgrade_files[image] = files
                with self.assertRaisesRegex(vessel.Failure, message):
                    quietly(vessel.upgrade, stack, Args(to='0.4.3', yes=True))
                self.assertEqual([], stack.events)
                # Read once pulled: still stops before any writer.
                stack = self.stack(version='0.4.2')
                stack.state = [inspect(s, image='skorcius/vessel:api-0.4.2') for s in ('postgres', 'api', 'worker', 'web')]
                stack.upgrade_files[image] = files
                with self.assertRaisesRegex(vessel.Failure, message):
                    quietly(vessel.upgrade, stack, Args(to='0.4.3', yes=True))
                self.assertEqual({'pull'}, {e[0] for e in stack.events})
                self.assertEqual('VESSEL_VERSION=0.4.2\n', self.env_text())
                self.assertEqual([], list(stack.backup_root.iterdir()) if stack.backup_root.exists() else [])

    def test_an_invalid_manifest_or_one_for_another_version_stops_before_anything_changes(self):
        stack = self.stack()
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        stack.upgrade_files['skorcius/vessel:api-1.1.0'] = {
            'v1.1.0.upgrade.json': {'version': '1.1.0', 'postSteps': [['rm -rf /']]}}
        with self.assertRaisesRegex(vessel.Failure, 'Invalid post-step'):
            quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        path = Path(self.directory.name, 'other.json')
        path.write_text(json.dumps({'version': '1.0.5'}))
        with self.assertRaisesRegex(vessel.Failure, 'is for 1.0.5, not 1.1.0'):
            quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True, manifest=str(path)))
        self.assertEqual([], stack.events)
        self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())

    def test_a_manifest_file_replaces_the_images_own(self):
        stack = self.stack()
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        stack.upgrade_files['skorcius/vessel:api-1.1.0'] = {
            'v1.1.0.upgrade.json': {'version': '1.1.0', 'postSteps': [['from-image']]}}
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True, manifest=self.upgrade_manifest()))
        self.assertEqual(0, code, out)
        self.assertEqual([('dotnet', 'play-search-rebuild', '--apply')], [e for e in stack.events if e[0] == 'dotnet'])

    def test_upgrade_refuses_a_running_service_the_configuration_does_not_define(self):
        stack = self.stack()
        stack.state.append(inspect('verifier'))
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        with self.assertRaisesRegex(vessel.Failure, 'verifier'):
            quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        self.assertEqual([], stack.events)

    def test_upgrade_leaves_a_retired_broker_running_and_never_starts_it(self):
        # Issue #99: a RabbitMQ container from 0.4.2 or earlier is neither a writer nor a service to start again.
        stack = self.stack()
        stack.state.append(inspect('rabbitmq', image='rabbitmq:4.3.6-management'))
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        Path(self.directory.name, '.env').write_text('VESSEL_VERSION=1.0.0\n')
        report = vessel.Report()
        _, out = quietly(vessel.preflight_checks, stack, None, report)
        self.assertIn('warn  rabbitmq still runs but this configuration no longer uses it', out)
        self.assertFalse(report.failed)
        code, out = quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True, manifest=self.upgrade_manifest()))
        self.assertEqual(0, code, out)
        self.assertEqual(('up', 'api', 'postgres', 'rustfs', 'web', 'worker', 'worker-notifications',
                          'worker-paper'), stack.events[-1])
        self.assertFalse([e for e in stack.events if e[0] == 'stop' and 'rabbitmq' in e[1]])
        self.assertEqual('running', stack.service_of('rabbitmq-1-id')['State']['Status'])
        [directory] = list(stack.backup_root.iterdir())
        self.assertNotIn('rabbitmq', vessel.load_backup(directory)[1]['services'])

    def test_rollback_to_a_broker_release_needs_its_compose_files_back_first(self):
        stack = self.stack()
        self.pre_upgrade(stack, '0.4.2')
        with self.assertRaisesRegex(vessel.Failure, '0.4.2 needs RabbitMQ'):
            quietly(vessel.rollback, stack, Args(yes=True))
        self.assertEqual([], stack.events)
        self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())

    def test_set_env_version_changes_only_that_line(self):
        path = Path(self.directory.name, '.env')
        path.write_bytes(b'# VESSEL_VERSION=0.1.0 stays a comment\r\nA=1\r\nVESSEL_VERSION=1.0.0\r\nB=2')
        path.chmod(0o600)
        vessel.set_env_version(path, '1.1.0')
        self.assertEqual(b'# VESSEL_VERSION=0.1.0 stays a comment\r\nA=1\r\nVESSEL_VERSION=1.1.0\r\nB=2', path.read_bytes())
        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
        path.write_bytes(b'A=1')
        vessel.set_env_version(path, '1.2.0')
        self.assertEqual(b'A=1\nVESSEL_VERSION=1.2.0\n', path.read_bytes())
        with self.assertRaises(vessel.Failure):
            vessel.set_env_version(path, '1.2.0\nX=1')
        self.assertEqual([path.name], [p.name for p in path.parent.iterdir() if p.is_file()])

    def test_restore_resets_identity_and_rotates_the_epoch_before_any_host_starts(self):
        source = self.stack()
        quietly(vessel.backup, source, Args(yes=True))
        [backup] = list(source.backup_root.iterdir())
        for start in (False, True):
            with self.subTest(start=start):
                stack = self.stack(containers=[])
                stack.volumes = set()
                code, out = quietly(vessel.restore, stack, Args(source=str(backup), yes=True, start=start,
                                                                keep_active=['11111111-1111-1111-1111-111111111111']))
                self.assertEqual(0, code, out)
                expected = [('up', 'postgres'), ('restore-database',),
                            ('restore-evidence', 'rustfs', 'tar -xzf - -C /data && chown -R 10001:10001 /data'),
                            ('dotnet', 'identity', 'reset-after-restore', '--keep-active',
                             '11111111-1111-1111-1111-111111111111'),
                            ('dotnet', 'rotate-event-epoch')]
                if start:
                    expected += [('migrate', '1.0.0'), ('up', 'api', 'postgres', 'rustfs', 'web', 'worker',
                                                        'worker-notifications', 'worker-paper')]
                self.assertEqual(expected, stack.events)
                if not start:
                    self.assertIn('Hosts stay stopped', out)

    def test_restore_refuses_a_project_that_is_not_fresh_or_another_version(self):
        source = self.stack()
        quietly(vessel.backup, source, Args(yes=True))
        [backup] = list(source.backup_root.iterdir())
        with self.assertRaisesRegex(vessel.Failure, 'already has containers or volumes'):
            quietly(vessel.restore, self.stack(), Args(source=str(backup), yes=True))
        Path(self.directory.name, '.env').write_text('VESSEL_VERSION=1.1.0\n')
        other = self.stack(containers=[])
        other.volumes = set()
        with self.assertRaisesRegex(vessel.Failure, 'set VESSEL_VERSION=1.0.0'):
            quietly(vessel.restore, other, Args(source=str(backup), yes=True))
        self.assertEqual([], other.events)

    def test_rollback_saves_the_current_state_then_restores_the_pre_upgrade_backup_and_version(self):
        stack = self.stack()
        pre = self.pre_upgrade(stack, '0.9.0')
        code, out = quietly(vessel.rollback, stack, Args(yes=True, start=True))
        self.assertEqual(0, code, out)
        kinds = [e[0] for e in stack.events]
        order = [kinds.index(k) for k in ('dump', 'drop-database', 'restore-database', 'restore-evidence', 'dotnet',
                                          'migrate', 'up')]
        self.assertEqual(sorted(order), order)
        self.assertEqual(('stop', ['web']), stack.events[0])
        self.assertEqual(('migrate', '0.9.0'), stack.events[kinds.index('migrate')])
        self.assertIn('find /data -mindepth 1 -delete && tar -xzf', stack.events[kinds.index('restore-evidence')][2])
        self.assertEqual([('dotnet', 'identity', 'reset-after-restore'), ('dotnet', 'rotate-event-epoch')],
                         [e for e in stack.events if e[0] == 'dotnet'])
        self.assertEqual('VESSEL_VERSION=0.9.0\n', self.env_text())
        safety = [p for p in stack.backup_root.iterdir() if p != pre]
        self.assertEqual(1, len(safety))
        self.assertEqual(('pre-rollback', '1.0.0'), tuple(vessel.load_backup(safety[0])[1][k] for k in ('kind', 'version')))

    def test_rollback_restores_nothing_when_the_safety_backup_fails(self):
        stack = self.stack()
        self.pre_upgrade(stack, '0.9.0')
        stack.fail = {'dump'}
        with self.assertRaisesRegex(vessel.Failure, 'nothing was restored'):
            quietly(vessel.rollback, stack, Args(yes=True))
        self.assertFalse({'drop-database', 'restore-database'} & {e[0] for e in stack.events})
        self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())

    def test_rollback_after_a_failed_upgrade_starts_what_ran_before_the_upgrade(self):
        stack = self.stack()
        self.pre_upgrade(stack, '0.9.0')
        for item in stack.state:
            if item['Config']['Labels']['com.docker.compose.service'] not in vessel.DATA_SERVICES:
                item['State']['Status'] = 'exited'
        code, out = quietly(vessel.rollback, stack, Args(yes=True, start=True))
        self.assertEqual(0, code, out)
        self.assertEqual(('up', 'api', 'postgres', 'rustfs', 'worker', 'worker-paper'), stack.events[-1])

    def test_a_digest_pinned_service_is_refused_before_anything_changes(self):
        # S1: a pinned worker-paper or migrate would stay on the old image while the others upgrade.
        for service, pin in (('worker-paper', 'skorcius/vessel@sha256:' + 'a' * 64),
                             ('migrate', 'mirror.local/vessel@sha256:' + 'b' * 64),
                             ('worker', 'skorcius/vessel:custom-build')):
            with self.subTest(service=service):
                stack = self.stack(pins={service: pin})
                stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0', pin})
                code, out = quietly(vessel.preflight, stack, Args(to='1.1.0'))
                self.assertEqual(1, code, out)
                self.assertIn('not all release tags of one version', out)
                self.assertIn(service, quietly(vessel.status, stack, Args())[1])
                with self.assertRaisesRegex(vessel.Failure, 'Preflight failed'):
                    quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
                self.assertEqual([], stack.events)
                self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())

    def test_upgrade_checks_a_pulled_target_before_stopping_anything(self):
        # S2: the target's migrations are only readable after the pull; a downgrade must still be refused.
        applied = {'journal': JOURNAL + [NEW], 'identity': IDENTITY}
        stack = self.stack(applied=applied, known={'skorcius/vessel:api-1.0.0': applied,
                                                   'skorcius/vessel:api-1.1.0': {'journal': JOURNAL, 'identity': IDENTITY}})
        with self.assertRaisesRegex(vessel.Failure, 'does not know migrations the database has applied'):
            quietly(vessel.upgrade, stack, Args(to='1.1.0', yes=True))
        self.assertEqual([('pull', 'skorcius/vessel:api-1.1.0'), ('pull', 'skorcius/vessel:web-1.1.0')], stack.events)
        self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())
        self.assertFalse(stack.backup_root.exists() and list(stack.backup_root.iterdir()))

    def test_a_partly_failed_stop_starts_again_what_it_stopped(self):
        # S3: web and some hosts stop, worker-paper does not; every caller must get back a running installation.
        hosts = ['api', 'worker', 'worker-notifications']
        for command, args in ((vessel.backup, Args(yes=True)), (vessel.upgrade, Args(to='1.1.0', yes=True))):
            with self.subTest(command=command.__name__):
                stack = self.stack()
                stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
                stack.stuck['stop'] = {'worker-paper'}
                with self.assertRaisesRegex(vessel.Failure, 'Started again what had stopped: api, web, worker'):
                    quietly(command, stack, args)
                self.assertEqual([('stop', ['web']), ('stop', hosts), ('start', hosts), ('start', ['web'])],
                                 stack.events)
                self.assertTrue(all(i['State']['Status'] == 'running' for i in stack.state
                                    if i['Config']['Labels']['com.docker.compose.service'] != 'migrate'))
                self.assertEqual('VESSEL_VERSION=1.0.0\n', self.env_text())
        stack = self.stack()
        stack.stuck = {'stop': {'worker-paper'}, 'start': {'web'}}
        with self.assertRaises(vessel.Failure) as raised:
            quietly(vessel.backup, stack, Args(yes=True))
        self.assertIn('possibly still stopped: web. Nothing else was changed. Start them with: docker start p-web-1',
                      str(raised.exception))

    def test_printed_recovery_commands_keep_every_selector(self):
        # S4: the project directory, name, files and profiles a command ran with.
        selectors = {'project': 'vessel blue', 'files': ['compose.yaml', 'compose.paper.yaml'], 'profiles': ['paper']}
        files = [os.path.abspath(f) for f in selectors['files']]
        compose = ['docker', 'compose', '--project-directory', str(Path(self.directory.name).resolve()),
                   '-p', 'vessel blue', '-f', files[0], '-f', files[1], '--profile', 'paper']
        source = self.stack()
        quietly(vessel.backup, source, Args(yes=True))
        [backup] = list(source.backup_root.iterdir())
        stack = self.stack(containers=[], **selectors)
        stack.volumes = set()
        code, out = quietly(vessel.restore, stack, Args(source=str(backup), yes=True))
        self.assertEqual(0, code, out)
        self.assertIn('  ' + shlex.join(compose + ['run', '--rm', 'migrate']) + '\n', out)
        self.assertIn('  ' + shlex.join(compose + ['up', '-d', 'api', 'postgres', 'rustfs', 'web', 'worker',
                                                  'worker-notifications', 'worker-paper']) + '\n', out)
        stack = self.stack(fail={'migrate'}, backup_dir=str(Path(self.directory.name, 'elsewhere')), **selectors)
        stack.images.update({'skorcius/vessel:api-1.1.0', 'skorcius/vessel:web-1.1.0'})
        output = io.StringIO()
        with contextlib.redirect_stdout(output), self.assertRaises(vessel.Failure):
            vessel.upgrade(stack, Args(to='1.1.0', yes=True))
        [directory] = list(stack.backup_root.iterdir())
        expected = shlex.join([str(ROOT / 'deploy' / 'vessel'), 'rollback'] + compose[2:] + [
            '--backup-dir', str(stack.backup_root), '--from', str(directory), '--yes'])
        self.assertIn(expected, output.getvalue())


if __name__ == '__main__':
    unittest.main()
