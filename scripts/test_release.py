import hashlib
import io
import json
import os
import importlib.util
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
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


def config_bytes(kind, version='1.2.3', revision=REVISION):
    return json.dumps({**image(version, revision), 'kind': kind}).encode()


def digest_of(data):
    return 'sha256:' + hashlib.sha256(data).hexdigest()


def write_archive(path, config, tags, name=None):
    """A minimal `docker save` archive: manifest.json naming one config blob, by its digest, and its tags."""
    name = name or 'blobs/sha256/' + hashlib.sha256(config).hexdigest()
    entries = {name: config,
               'manifest.json': json.dumps([{'Config': name, 'RepoTags': tags, 'Layers': []}]).encode()}
    with tarfile.open(path, 'w') as archive:
        for name, data in entries.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))


class ImageArchiveTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory)

    def test_the_identity_is_the_digest_of_the_saved_config(self):
        write_archive(self.directory / 'api.tar', config_bytes('api'), ['example/vessel:api-1.2.3'])
        saved = release.archive_identity(self.directory / 'api.tar')
        self.assertEqual(digest_of(config_bytes('api')), saved['config'])
        self.assertEqual(['example/vessel:api-1.2.3'], saved['tags'])
        self.assertEqual('linux', saved['image']['os'])
        # Older daemons name the config <hex>.json.
        legacy = hashlib.sha256(config_bytes('api')).hexdigest() + '.json'
        write_archive(self.directory / 'old.tar', config_bytes('api'), [], name=legacy)
        self.assertEqual(digest_of(config_bytes('api')), release.archive_identity(self.directory / 'old.tar')['config'])

    def test_a_config_that_does_not_hash_to_its_name_is_corrupted(self):
        named_for_web = 'blobs/sha256/' + hashlib.sha256(config_bytes('web')).hexdigest()
        for name in (named_for_web, 'blobs/sha256/config'):
            with self.subTest(name=name):
                write_archive(self.directory / 'api.tar', config_bytes('api'), [], name=name)
                with self.assertRaisesRegex(release.ReleaseError, 'the archive is corrupted'):
                    release.archive_identity(self.directory / 'api.tar')

    def test_anything_but_one_saved_image_is_rejected(self):
        (self.directory / 'api.tar').write_text('not a tar')
        with self.assertRaisesRegex(release.ReleaseError, 'not a docker save archive'):
            release.archive_identity(self.directory / 'api.tar')
        with self.assertRaisesRegex(release.ReleaseError, 'not a docker save archive'):
            release.archive_identity(self.directory / 'missing.tar')
        with tarfile.open(self.directory / 'two.tar', 'w') as archive:
            data = json.dumps([{'Config': 'a'}, {'Config': 'b'}]).encode()
            info = tarfile.TarInfo('manifest.json')
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
        with self.assertRaisesRegex(release.ReleaseError, 'holds 2 images, not one'):
            release.archive_identity(self.directory / 'two.tar')

    def test_the_local_config_digest_follows_the_image_store(self):
        classic = json.dumps([{'Id': 'sha256:config'}])
        containerd = json.dumps([{'Id': 'sha256:manifest', 'Descriptor': {'annotations': {'config.digest': 'sha256:config'}}}])
        for inspected in (classic, containerd):
            with patch.object(release, 'docker', return_value=inspected):
                self.assertEqual('sha256:config', release.local_config('ref'))


class ReleaseImageTests(unittest.TestCase):
    """build, load and publish against a fake Docker daemon and registry that track images by config digest."""

    def setUp(self):
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory)
        self.calls = []
        self.local = {}
        self.registry = {}
        self.pushed_as = {}
        self.expected = {}
        for kind in ('api', 'web'):
            write_archive(self.directory / f'{kind}.tar', config_bytes(kind), [f'example/vessel:{kind}-1.2.3'])
            self.expected[kind] = digest_of(config_bytes(kind))

    def fake_docker(self, *args, capture=False):
        command = args[0]
        self.calls.append((command, args[args.index('-t') + 1] if command == 'build' else args[-1]))
        if command == 'build':
            ref = args[args.index('-t') + 1]
            self.local[ref] = config_bytes(ref.rsplit(':', 1)[1].split('-')[0])
        elif command == 'save':
            write_archive(args[args.index('--output') + 1], self.local[args[-1]], [args[-1]])
        elif command == 'load':
            saved = release.archive_identity(args[-1])
            for tag in saved['tags']:
                self.local[tag] = config_bytes(saved['image']['kind'])
        elif command == 'tag':
            self.local[args[2]] = self.local[args[1]]
        elif command == 'image':
            return json.dumps([{'Id': digest_of(self.local[args[-1]])}])
        elif command == 'push':
            ref = args[-1]
            config = self.pushed_as.get(ref, digest_of(self.local[ref]))
            self.registry[ref] = {'digest': 'sha256:manifest-' + config[-8:], 'config': config, 'image': image()}
        elif command == 'buildx':
            # imagetools create --prefer-index=false --tag <target> <source>@<digest>: a registry-side copy.
            self.assertEqual(('imagetools', 'create', '--prefer-index=false', '--tag'), args[1:5])
            source, digest = args[-1].split('@')
            self.assertEqual(digest, self.registry[source]['digest'])
            self.registry[args[5]] = dict(self.registry[source])
        return None

    def run_release(self, function, *args):
        with patch.object(release, 'docker', self.fake_docker), \
                patch.object(release, 'remote', lambda ref: self.registry.get(ref)), \
                patch.dict('os.environ', {'GITHUB_STEP_SUMMARY': '', 'GITHUB_OUTPUT': ''}), patch('builtins.print'):
            return function(*args)

    def publish(self):
        return self.run_release(release.publish, self.directory, '1.2.3', REVISION, self.expected, 'example/vessel')

    def load(self, expected=None, image='example/vessel'):
        return self.run_release(release.load, self.directory, '1.2.3', REVISION, expected or self.expected, image)

    def test_build_saves_each_image_once_and_reports_its_config_digest(self):
        output = self.directory / 'out'
        outputs = self.directory / 'github-output'
        with patch.object(release, 'docker', self.fake_docker), patch('builtins.print'), \
                patch.dict('os.environ', {'GITHUB_STEP_SUMMARY': '', 'GITHUB_OUTPUT': str(outputs)}):
            configs = release.build(Path('/source'), '1.2.3', REVISION, output, 'example/vessel')
        self.assertEqual(self.expected, configs)
        self.assertEqual([('build', 'example/vessel:api-1.2.3'), ('build', 'example/vessel:web-1.2.3')],
                         [call for call in self.calls if call[0] == 'build'])
        self.assertEqual(2, sum(call[0] == 'save' for call in self.calls))
        self.assertNotIn('push', [call[0] for call in self.calls])
        for kind in ('api', 'web'):
            self.assertEqual(self.expected[kind], release.archive_identity(output / f'{kind}.tar')['config'])
        self.assertEqual(f'api-config-digest={self.expected["api"]}\nweb-config-digest={self.expected["web"]}\n',
                         outputs.read_text())

    def test_build_refuses_a_saved_archive_that_is_not_the_built_image(self):
        def save_another(*args, capture=False):
            if args[0] == 'save':
                write_archive(args[args.index('--output') + 1], config_bytes('other'), [args[-1]])
                return None
            return self.fake_docker(*args, capture=capture)
        with patch.object(release, 'docker', save_another), patch('builtins.print'):
            with self.assertRaisesRegex(release.ReleaseError, 'is not the image that was built'):
                release.build(Path('/source'), '1.2.3', REVISION, self.directory / 'out', 'example/vessel')

    def test_load_checks_the_archive_before_loading_it(self):
        refs = self.load()
        self.assertEqual({'api': 'example/vessel:api-1.2.3', 'web': 'example/vessel:web-1.2.3'}, refs)
        with self.assertRaisesRegex(release.ReleaseError, 'not the built and verified'):
            self.load({**self.expected, 'web': 'sha256:' + 'f' * 64})
        # Both archives are checked before either is loaded.
        loads = [ref for command, ref in self.calls if command == 'load']
        self.assertEqual([str(self.directory / 'api.tar'), str(self.directory / 'web.tar')], loads)
        with self.assertRaisesRegex(release.ReleaseError, 'must be sha256:<64 hex>'):
            self.load({**self.expected, 'api': 'sha256:short'})

    def test_load_tags_the_image_for_another_repository(self):
        refs = self.load(image='127.0.0.1:5000/vessel')
        self.assertEqual('127.0.0.1:5000/vessel:api-1.2.3', refs['api'])
        self.assertIn(('tag', '127.0.0.1:5000/vessel:web-1.2.3'), self.calls)
        self.assertEqual(config_bytes('web'), self.local['127.0.0.1:5000/vessel:web-1.2.3'])

    def test_load_refuses_when_the_daemon_holds_another_image_under_the_tag(self):
        def load_another(*args, capture=False):
            result = self.fake_docker(*args, capture=capture)
            if args[0] == 'load':
                self.local['example/vessel:api-1.2.3'] = config_bytes('other')
            return result
        with patch.object(release, 'docker', load_another), patch('builtins.print'):
            with self.assertRaisesRegex(release.ReleaseError, 'is not the loaded image'):
                release.load(self.directory, '1.2.3', REVISION, self.expected, 'example/vessel')

    def test_publish_pushes_the_verified_images_and_moves_latest_only_after_both(self):
        digests = self.publish()
        self.assertEqual({kind: 'sha256:manifest-' + self.expected[kind][-8:] for kind in ('api', 'web')}, digests)
        self.assertNotIn('build', [call[0] for call in self.calls])
        moves = [(command, ref) for command, ref in self.calls if command in ('push', 'buildx')]
        self.assertEqual([('push', 'example/vessel:api-1.2.3'), ('push', 'example/vessel:web-1.2.3'),
                          ('buildx', f'example/vessel:api-1.2.3@{digests["api"]}'),
                          ('buildx', f'example/vessel:web-1.2.3@{digests["web"]}')], moves)
        for kind in ('api', 'web'):
            self.assertEqual(self.expected[kind], self.registry[f'example/vessel:{kind}-latest']['config'])

    def test_publish_fails_when_a_pushed_image_is_not_the_verified_one(self):
        self.pushed_as['example/vessel:web-1.2.3'] = 'sha256:' + 'e' * 64
        with self.assertRaisesRegex(release.ReleaseError, 'web-1.2.3 was pushed as sha256:e+, not the verified'):
            self.publish()
        self.assertNotIn('example/vessel:api-latest', self.registry)
        self.assertNotIn('example/vessel:web-latest', self.registry)

    def test_publish_refuses_an_archive_that_is_not_the_verified_one_before_any_push(self):
        self.expected['api'] = 'sha256:' + 'd' * 64
        with self.assertRaisesRegex(release.ReleaseError, 'not the built and verified'):
            self.publish()
        self.assertEqual([], [call for call in self.calls if call[0] == 'push'])

    def test_a_version_published_from_another_revision_is_never_overwritten(self):
        self.registry['example/vessel:web-1.2.3'] = {'digest': 'sha256:old', 'config': self.expected['web'],
                                                     'image': image(revision='b' * 40)}
        with self.assertRaisesRegex(release.ReleaseError, 'never overwritten'):
            self.publish()
        self.assertEqual([], [call for call in self.calls if call[0] in ('load', 'push')])

    def test_a_version_from_the_same_revision_and_verified_image_is_reused_not_pushed_again(self):
        self.registry['example/vessel:api-1.2.3'] = {'digest': 'sha256:manifest-' + self.expected['api'][-8:],
                                                     'config': self.expected['api'], 'image': image()}
        self.publish()
        pushes = [ref for command, ref in self.calls if command == 'push']
        self.assertNotIn('example/vessel:api-1.2.3', pushes)
        self.assertIn('example/vessel:web-1.2.3', pushes)
        self.assertNotIn('pull', [call[0] for call in self.calls])

    def test_latest_copies_an_earlier_attempts_manifest_even_when_another_encoding_has_the_same_config(self):
        # The registry holds api-1.2.3 as a manifest that a fresh push of the same config would not reproduce.
        self.registry['example/vessel:api-1.2.3'] = {'digest': 'sha256:alternate', 'config': self.expected['api'],
                                                     'image': image()}
        digests = self.publish()
        self.assertEqual('sha256:alternate', digests['api'])
        self.assertEqual('sha256:alternate', self.registry['example/vessel:api-latest']['digest'])
        self.assertEqual(digests['web'], self.registry['example/vessel:web-latest']['digest'])
        self.assertNotIn(('push', 'example/vessel:api-latest'), self.calls)

    def test_a_version_from_the_same_revision_but_another_build_is_never_overwritten(self):
        self.registry['example/vessel:api-1.2.3'] = {'digest': 'sha256:earlier', 'config': 'sha256:' + 'c' * 64,
                                                     'image': image()}
        with self.assertRaisesRegex(release.ReleaseError, 'already published from this revision as sha256:c+'):
            self.publish()
        self.assertEqual([], [call for call in self.calls if call[0] in ('load', 'push')])

    def identify(self, image='example/vessel'):
        outputs = self.directory / 'github-output'
        with patch('builtins.print'), patch.dict('os.environ', {'GITHUB_STEP_SUMMARY': '', 'GITHUB_OUTPUT': str(outputs)}):
            return release.identify(self.directory, '1.2.3', REVISION, image), outputs.read_text()

    def test_identify_recomputes_the_digests_build_recorded_without_building(self):
        output, recorded = self.directory / 'out', self.directory / 'build-output'
        with patch.object(release, 'docker', self.fake_docker), patch('builtins.print'), \
                patch.dict('os.environ', {'GITHUB_STEP_SUMMARY': '', 'GITHUB_OUTPUT': str(recorded)}):
            built = release.build(Path('/source'), '1.2.3', REVISION, output, 'example/vessel')
        self.calls.clear()
        with patch.object(release, 'docker', self.fake_docker):
            outputs = self.directory / 'identify-output'
            with patch('builtins.print'), patch.dict('os.environ', {'GITHUB_STEP_SUMMARY': '', 'GITHUB_OUTPUT': str(outputs)}):
                self.assertEqual(built, release.identify(output, '1.2.3', REVISION, 'example/vessel'))
        self.assertEqual(recorded.read_text(), outputs.read_text())
        self.assertEqual([], self.calls)

    def test_identify_refuses_a_corrupted_or_mismatched_pair(self):
        api = self.directory / 'api.tar'
        api.write_bytes(api.read_bytes()[:100])
        with self.assertRaisesRegex(release.ReleaseError, 'not a docker save archive'):
            self.identify()
        write_archive(api, config_bytes('api', revision='b' * 40), ['example/vessel:api-1.2.3'])
        with self.assertRaisesRegex(release.ReleaseError, 'was built from'):
            self.identify()
        write_archive(api, config_bytes('web'), ['example/vessel:web-1.2.3'])
        with self.assertRaisesRegex(release.ReleaseError, 'api.tar holds example/vessel:web-1.2.3, not example/vessel:api-1.2.3'):
            self.identify()
        write_archive(api, config_bytes('api'), ['example/vessel:api-1.2.3'])
        with self.assertRaisesRegex(release.ReleaseError, 'holds example/vessel:api-1.2.3, not other/vessel:api-1.2.3'):
            self.identify('other/vessel')

    def test_metadata_rejects_another_platform_or_version(self):
        with self.assertRaisesRegex(release.ReleaseError, 'linux/arm64, not linux/amd64'):
            release.expect_metadata('ref', image(platform=('linux', 'arm64')), '1.2.3')
        with self.assertRaisesRegex(release.ReleaseError, "labelled version '1.2.2'"):
            release.expect_metadata('ref', image(version='1.2.2'), '1.2.3')

    def test_remote_reads_the_config_digest_of_the_amd64_image(self):
        index = {'manifests': [{'digest': 'sha256:arm', 'platform': {'os': 'linux', 'architecture': 'arm64'}},
                               {'digest': 'sha256:amd', 'platform': {'os': 'linux', 'architecture': 'amd64'}}]}
        answers = {('r:1', '--format'): {'manifest': {'digest': 'sha256:index'}, 'image': {'linux/amd64': image()}},
                   ('r:1', '--raw'): index, ('r@sha256:amd', '--raw'): {'config': {'digest': 'sha256:config'}}}
        with patch.object(release, 'imagetools', lambda ref, *args: answers[(ref, args[0])]):
            found = release.remote('r:1')
        self.assertEqual(('sha256:index', 'sha256:config', 'amd64'),
                         (found['digest'], found['config'], found['image']['architecture']))


class ReleaseNotesTests(unittest.TestCase):
    def test_the_release_body_is_the_notes_and_both_published_digests(self):
        digests = {'api': 'sha256:' + 'a' * 64, 'web': 'sha256:' + 'b' * 64}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_release(root, **{'docs/releases/v1.2.3.md': '# Vessel 1.2.3\n\nNotes.\n'})
            body = release.notes(root, '1.2.3', digests, 'example/vessel')
            self.assertTrue(body.startswith('# Vessel 1.2.3\n\nNotes.\n\n## Image digests\n'))
            self.assertIn(f'| `example/vessel:api-1.2.3` | `{digests["api"]}` |', body)
            self.assertIn(f'| `example/vessel:web-1.2.3` | `{digests["web"]}` |', body)
            with self.assertRaisesRegex(release.ReleaseError, 'web digest must be'):
                release.notes(root, '1.2.3', {**digests, 'web': ''}, 'example/vessel')

    def test_load_and_publish_require_both_verified_config_digests(self):
        for command in ('load', 'publish'):
            with self.subTest(command), patch('sys.stderr'), self.assertRaises(SystemExit) as error:
                release.main([command, '--revision', REVISION, '--input', '/tmp', '--api-config-digest', 'sha256:x'])
            self.assertEqual(2, error.exception.code)


class SupplyChainPinningTests(unittest.TestCase):
    WORKFLOWS = sorted((release.ROOT / '.github' / 'workflows').glob('*.yml'))

    def test_every_action_is_pinned_to_a_commit_and_every_checkout_keeps_no_token(self):
        self.assertTrue(self.WORKFLOWS)
        for workflow in self.WORKFLOWS:
            text = workflow.read_text()
            for line in re.findall(r'^\s*(?:-\s*)?uses:\s*(.+)$', text, re.MULTILINE):
                with self.subTest(workflow=workflow.name, uses=line):
                    self.assertRegex(line, r'^(\./\S+|[\w.-]+/[\w.-]+@[0-9a-f]{40} # v\d+(\.\d+){2})$')
            checkouts = re.findall(r'uses: \S+/checkout@\S+.*\n\s+with:\n(?:\s+[\w-]+: .*\n)*', text)
            self.assertEqual(text.count('/checkout@'), len(checkouts), workflow.name)
            for step in checkouts:
                self.assertIn('persist-credentials: false', step, workflow.name)

    def test_no_branch_or_pull_request_event_starts_a_workflow(self):
        # Dependabot pull requests must not consume runner time either.
        for workflow in self.WORKFLOWS:
            text = workflow.read_text()
            self.assertNotIn('pull_request', text, workflow.name)
            self.assertNotRegex(text, r'^\s+branches', workflow.name)

    def test_every_base_image_is_pinned_by_digest(self):
        for file in release.DOCKERFILES.values():
            stages = set()
            for base, stage in re.findall(r'^FROM\s+(\S+)(?:\s+AS\s+(\S+))?', (release.ROOT / file).read_text(), re.MULTILINE):
                with self.subTest(file=file, base=base):
                    self.assertTrue(base in stages or re.fullmatch(r'\S+:\S+@sha256:[0-9a-f]{64}', base), base)
                stages.add(stage)

    def test_dependabot_watches_the_pins(self):
        text = (release.ROOT / '.github' / 'dependabot.yml').read_text()
        self.assertIn('package-ecosystem: github-actions', text)
        self.assertIn('package-ecosystem: docker', text)
        for directory in ('/backend', '/frontend'):
            self.assertIn(f'- {directory}\n', text)


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

    def hardened(self, service, **host):
        config = {'LogConfig': {'Type': 'json-file', 'Config': {'max-size': '10m', 'max-file': '5'}},
                  'SecurityOpt': ['no-new-privileges:true'], 'CapDrop': ['ALL'],
                  'CapAdd': [cap[4:] for cap in verify_release.CAPABILITIES.get(service, [])], 'ReadonlyRootfs': True, **host}
        return {'Id': service, 'Config': {'Labels': {'com.docker.compose.service': service}}, 'HostConfig': config,
                'State': {'Health': {'Status': 'healthy'}}}

    def run_hardening(self, containers):
        check = self.check()
        check.compose = Mock(return_value=Mock(stdout='\n'.join(c['Id'] for c in containers)))
        by_id = {c['Id']: c for c in containers}

        def inspect(*args, **kwargs):
            return Mock(stdout=json.dumps([by_id[name] for name in args[2:]]))
        with patch.object(verify_release, 'run', side_effect=inspect), patch('builtins.print'), \
                patch.object(verify_release.time, 'sleep'):
            check.hardening()
        return check

    def test_hardening_accepts_every_service_hardened_and_healthy(self):
        services = ['postgres', 'rabbitmq', 'rustfs', 'api', 'worker', 'worker-notifications', 'web', 'verifier', 'tls']
        check = self.run_hardening([self.hardened(service) for service in services])
        self.assertGreater(check.counts['hardening'], len(services) * 5)

    def test_hardening_rejects_uncapped_logs_privileges_capabilities_writable_roots_and_missing_services(self):
        services = ['postgres', 'rabbitmq', 'rustfs', 'api', 'worker', 'worker-notifications', 'web', 'verifier', 'tls']
        for broken in ({'LogConfig': {'Type': 'json-file', 'Config': {}}}, {'SecurityOpt': []}, {'CapDrop': []},
                       {'CapAdd': ['NET_ADMIN']}, {'ReadonlyRootfs': False}):
            with self.subTest(broken=broken), self.assertRaises(AssertionError):
                self.run_hardening([self.hardened(service, **(broken if service == 'web' else {})) for service in services])
        with self.assertRaisesRegex(AssertionError, 'every long-running service'):
            self.run_hardening([self.hardened(service) for service in services if service != 'tls'])
        unhealthy = [self.hardened(service) for service in services]
        unhealthy[6]['State']['Health']['Status'] = 'unhealthy'
        with self.assertRaisesRegex(AssertionError, 'web passes its health check'):
            self.run_hardening(unhealthy)

    def test_restore_extracts_with_only_the_capabilities_it_needs(self):
        self.assertEqual(('--cap-add', 'CHOWN', '--cap-add', 'DAC_OVERRIDE', '--cap-add', 'FOWNER'),
                         verify_release.RESTORE_CAPABILITIES)
        source = Path(verify_release.__file__).read_text()
        self.assertNotIn("'--user', '0', '--entrypoint', 'sh', 'api', '-c', 'cat", source)

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


class PrebuiltImageVerificationTests(unittest.TestCase):
    def check(self, *args):
        check = verify_release.Check(verify_release.parse_args(list(args)))
        self.addCleanup(lambda: shutil.rmtree(check.temp, ignore_errors=True))
        return check

    def test_both_loaded_images_are_required_together(self):
        for args in (['--api-image', 'a'], ['--web-image', 'w']):
            with self.subTest(args=args), patch('sys.stderr'), self.assertRaises(SystemExit) as error:
                verify_release.parse_args(args)
            self.assertEqual(2, error.exception.code)

    def test_loaded_images_are_checked_as_they_are_not_rebuilt(self):
        check = self.check('--api-image', 'example/vessel:api-1.2.3', '--web-image', 'example/vessel:web-1.2.3')
        self.assertEqual({'api': 'example/vessel:api-1.2.3', 'web': 'example/vessel:web-1.2.3'}, check.images)
        check.wallet_signature = Mock()
        check.prepare = Mock()
        check.wallet_bootstrap = Mock(side_effect=RuntimeError('stop after the image checks'))
        check.expect = Mock()
        check.cleanup = Mock()
        with patch.object(verify_release, 'run', return_value=Mock(stderr='--with-http_realip_module')) as run, \
                patch.object(verify_release.subprocess, 'run', return_value=Mock(returncode=1)):
            with self.assertRaisesRegex(RuntimeError, 'stop after the image checks'):
                check.main()
        commands = [call.args[:2] for call in run.call_args_list]
        self.assertEqual(('docker', 'image'), commands[0])
        self.assertEqual(('example/vessel:api-1.2.3', 'example/vessel:web-1.2.3'), run.call_args_list[0].args[3:])
        self.assertNotIn(('docker', 'build'), commands)
        self.assertIn('example/vessel:web-1.2.3', run.call_args_list[1].args)

    def test_cleanup_leaves_loaded_images_for_publication(self):
        check = self.check('--api-image', 'example/vessel:api-1.2.3', '--web-image', 'example/vessel:web-1.2.3')
        with patch.object(verify_release, 'run') as run:
            check.cleanup()
        self.assertNotIn(('docker', 'image', 'rm'), [call.args[:3] for call in run.call_args_list])
        built = self.check()
        with patch.object(verify_release, 'run') as run:
            built.cleanup()
        self.assertEqual([('docker', 'image', 'rm', image) for image in built.images.values()],
                         [call.args for call in run.call_args_list])


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
