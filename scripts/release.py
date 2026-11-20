#!/usr/bin/env python3
"""Release metadata checks and image publication for Vessel.

`check` confirms that every packaged version and current-release docs example
agrees and that the version has a changelog entry, release notes and, from 0.4.3
on, an upgrade manifest (docs/releases/vX.Y.Z.upgrade.json, read by deploy/vessel
upgrade from the API image) whose migrations exist in the source. With `--tag` it
also refuses a changelog section that does not name, as `#N`, every pull request
merged on main since the previous release tag. `build` builds the Linux amd64 API and web
images once from one commit and saves them (`docker save`) with their config
digests. `identify` recomputes those digests from a saved pair without building,
so a later attempt of the same release run reuses the pair the first attempt
built. `load` loads that saved pair after checking it is the pair that was
built. `publish` loads the same pair, pushes both versioned tags, fails when a
pushed image is not the verified one, then copies each verified versioned
manifest to its latest tag and verifies all four remotely. It never overwrites a versioned tag published from another
revision. `notes` writes the GitHub Release body: the release notes plus both
published digests. Registry authentication is the caller's Docker login; this
script never reads, prints or stores credentials.

The verified identity of an image is its config digest: the SHA-256 of the image
configuration, which names every layer by the digest of its uncompressed
content. `docker save`, `docker load` and `docker push` keep it, whichever image
store the daemon uses, whereas the manifest digest is known only once a registry
holds the compressed layers.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile

ROOT = Path(__file__).resolve().parent.parent
IMAGE = 'skorcius/vessel'
DOCKERFILES = {'api': 'backend/Dockerfile', 'web': 'frontend/Dockerfile'}
SEMVER = re.compile(r'\d+\.\d+\.\d+')
DIGEST = re.compile(r'sha256:[0-9a-f]{64}')
# docker save names the config blob by its digest: blobs/sha256/<hex> (OCI layout) or <hex>.json (older daemons).
CONFIG_NAME = re.compile(r'(?:^|/)([0-9a-f]{64})(?:\.json)?$')
# The first release whose upgrade manifest is required; earlier upgrades are described in deploy/README.md.
FIRST_UPGRADE_MANIFEST = (0, 4, 3)
# Where each schema's migrations live, as deploy/vessel names the schemas.
MIGRATION_SOURCES = {'journal': 'backend/src/Vessel.Persistence', 'identity': 'backend/src/Vessel.Identity'}
MIGRATION_ATTRIBUTE = re.compile(r'\[Migration\("([^"]+)"\)\]')
# An API administrator command, as deploy/vessel accepts it.
COMMAND = re.compile(r'[a-z][a-z-]*')


class ReleaseError(Exception):
    pass


def packaged_versions(root):
    """Every place that states the release version, with what it states."""
    def matches(path, *patterns):
        text = (root / path).read_text()
        return [found for pattern in patterns for found in re.findall(pattern, text, re.MULTILINE)]

    found = {'frontend/package.json': [json.loads((root / 'frontend/package.json').read_text()).get('version')]}
    found['backend/Directory.Build.props'] = matches('backend/Directory.Build.props', r'<Version>([^<]*)</Version>')
    for file in DOCKERFILES.values():
        found[file] = matches(file, r'^ARG VERSION=(\S*)$')
    found['deploy/compose.yaml'] = matches('deploy/compose.yaml', r'vessel:(?:api|web)-\$\{VESSEL_VERSION:-([^}]*)\}')
    found['deploy/.env.example'] = matches('deploy/.env.example', r'^VESSEL_VERSION=(\S*)$')
    # Current-release examples in the docs. Older versions in upgrade history are left alone: only the
    # newest "From vA to vB" heading must name this version.
    found['README.md'] = matches('README.md', r'for the v(\S+) API and web', r'\[v(\S+) release notes\]',
                                 r'docs/releases/v(\d\S*)\.md')
    images = r'(?<![\w-])(?:api|web)-(\d+\.\d+\.\d+\S*?)[`\s]', r'--build-arg VERSION=(\S+)'
    found['deploy/README.md'] = matches('deploy/README.md', *images,
                                        r'\A(?:.*\n)*?### From v\S+ to v(\S+)$')
    found['docs/development.md'] = matches('docs/development.md', *images, r'git tag -a v(\S+)',
                                           r'git push origin v(\S+)', r'check --tag v(\S+?)`')
    return found


def pull_request(subject):
    """The pull request a first-parent commit on main merged, from its merge or squash subject, or None."""
    found = re.match(r'Merge pull request #(\d+) ', subject) or re.search(r'\(#(\d+)\)\s*$', subject)
    return int(found[1]) if found else None


def unlisted_pull_requests(root, version, tag):
    """Pull requests merged on main since the previous release tag that the version's changelog section does not
    name as #N, with that tag. The release pull request itself (its subject names the version) is exempt."""
    def git(*args):
        result = subprocess.run(['git', *args], cwd=root, text=True, capture_output=True)
        return result.stdout.strip() if result.returncode == 0 else None

    if git('rev-parse', '--git-dir') is None:
        raise ReleaseError(f'Cannot read the Git history of {root}; the changelog freeze check needs it')
    revision = f'refs/tags/{tag}' if git('rev-parse', '--verify', '--quiet', f'refs/tags/{tag}^{{commit}}') else 'HEAD'
    previous = git('describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*', '--exclude', tag, revision)
    if not previous:
        return None, []  # The first release: nothing to compare against.
    subjects = git('log', '--first-parent', '--format=%s', f'{previous}..{revision}')
    if subjects is None:
        raise ReleaseError(f'Cannot list the commits between {previous} and {tag}')
    own = re.compile(rf'(?<![\d.]){re.escape(version)}(?![\d.])')
    numbers = {pull_request(subject) for subject in subjects.splitlines() if not own.search(subject)} - {None}
    text = (root / 'CHANGELOG.md').read_text()
    section = re.search(rf'^## {re.escape(version)} - .*?(?=^## |\Z)', text, re.MULTILINE | re.DOTALL)[0]
    return previous, sorted(n for n in numbers if not re.search(rf'#{n}(?!\d)', section))


def check(root, tag=None):
    found = packaged_versions(root)
    missing = sorted(path for path, values in found.items() if not values)
    if missing:
        raise ReleaseError('No version found in ' + ', '.join(missing))
    distinct = {value for values in found.values() for value in values}
    if len(distinct) != 1:
        detail = '; '.join(f'{path}: {", ".join(map(str, values))}' for path, values in sorted(found.items()))
        raise ReleaseError('Packaged versions disagree: ' + detail)
    version = distinct.pop()
    if not isinstance(version, str) or not SEMVER.fullmatch(version):
        raise ReleaseError(f'Version {version!r} is not MAJOR.MINOR.PATCH')
    if tag is not None and tag != f'v{version}':
        raise ReleaseError(f'Tag {tag} does not match packaged version {version}')
    if not re.search(rf'^## {re.escape(version)} - ', (root / 'CHANGELOG.md').read_text(), re.MULTILINE):
        raise ReleaseError(f'CHANGELOG.md has no "## {version} - <date>" section')
    notes = root / 'docs' / 'releases' / f'v{version}.md'
    if not notes.is_file() or not notes.read_text().strip():
        raise ReleaseError(f'Release notes {notes.relative_to(root)} are missing or empty')
    manifests = upgrade_manifests(root)
    if release_key(version) >= FIRST_UPGRADE_MANIFEST and version not in manifests:
        raise ReleaseError(f'Upgrade manifest docs/releases/v{version}.upgrade.json is missing')
    if tag is not None:
        previous, unlisted = unlisted_pull_requests(root, version, tag)
        if unlisted:
            raise ReleaseError(f'CHANGELOG.md {version} does not mention pull requests merged since {previous}: '
                               + ', '.join(f'#{n}' for n in unlisted) + '. Name each as #N in the entry for its '
                               'change, or in a "Pull requests:" line when it changed nothing owner-facing.')
    return version


def release_key(version):
    return tuple(int(part) for part in version.split('.'))


def source_migrations(root):
    """Migration IDs per schema, from the [Migration] attributes compiled into each assembly."""
    return {schema: {match for file in sorted((root / path).rglob('*.cs'))
                     for match in MIGRATION_ATTRIBUTE.findall(file.read_text())}
            for schema, path in MIGRATION_SOURCES.items()}


def upgrade_manifests(root):
    """Every docs/releases/vX.Y.Z.upgrade.json by version, each checked: the fields deploy/vessel reads, migrations
    the source defines, and no migration listed by two releases."""
    manifests, listed, known = {}, {}, None
    for path in sorted((root / 'docs' / 'releases').glob('v*.upgrade.json')):
        name = path.relative_to(root)
        version = path.name[1:-len('.upgrade.json')]
        try:
            manifest = json.loads(path.read_text())
        except ValueError as error:
            raise ReleaseError(f'{name} is not JSON: {error}') from error
        if not SEMVER.fullmatch(version) or not isinstance(manifest, dict) or manifest.get('version') != version:
            raise ReleaseError(f'{name} must be a JSON object whose "version" is {version}')
        if set(manifest) != {'version', 'migrations', 'postSteps', 'configuration'}:
            raise ReleaseError(f'{name} must have exactly version, migrations, postSteps and configuration')
        migrations = manifest['migrations']
        if not isinstance(migrations, dict) or set(migrations) != set(MIGRATION_SOURCES):
            raise ReleaseError(f'{name}: migrations must list {" and ".join(MIGRATION_SOURCES)}, empty when none')
        known = known if known is not None else source_migrations(root)
        for schema, ids in migrations.items():
            if not isinstance(ids, list) or not all(isinstance(i, str) for i in ids) or len(set(ids)) != len(ids):
                raise ReleaseError(f'{name}: {schema} migrations must be a list of distinct IDs')
            for migration in ids:
                if migration not in known[schema]:
                    raise ReleaseError(f'{name}: {schema} migration {migration} is not in {MIGRATION_SOURCES[schema]}')
                if migration in listed:
                    raise ReleaseError(f'{name}: {migration} is already listed by {listed[migration]}')
                listed[migration] = version
        steps = manifest['postSteps']
        if not isinstance(steps, list) or not all(
                isinstance(step, list) and step and all(isinstance(a, str) for a in step) and COMMAND.fullmatch(step[0])
                for step in steps):
            raise ReleaseError(f'{name}: postSteps must be API command argument lists such as ["play-search-rebuild"]')
        notes = manifest['configuration']
        if not isinstance(notes, list) or not all(isinstance(note, str) and note.strip() for note in notes):
            raise ReleaseError(f'{name}: configuration must be a list of notes')
        manifests[version] = manifest
    return manifests


def docker(*args, capture=False):
    result = subprocess.run(['docker', *args], text=True, capture_output=capture)
    if result.returncode != 0:
        detail = (result.stderr or '').strip().splitlines()[-1:] if capture else []
        raise ReleaseError(f'docker {args[0]} {args[1] if len(args) > 1 else ""} failed' + (f': {detail[0]}' if detail else ''))
    return result.stdout if capture else None


def imagetools(ref, *args):
    result = subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', ref, *args], text=True, capture_output=True)
    if result.returncode != 0:
        if 'not found' in result.stderr:
            return None
        raise ReleaseError(f'Could not inspect {ref}: ' + (result.stderr.strip().splitlines() or ['unknown error'])[-1])
    return json.loads(result.stdout)


def remote(ref):
    """The manifest digest, config digest and linux/amd64 image config of a remote tag, or None when it does not exist."""
    info = imagetools(ref, '--format', '{{json .}}')
    if info is None:
        return None
    image = info.get('image') or {}
    if 'architecture' not in image:
        image = image.get('linux/amd64') or {}
    raw = imagetools(ref, '--raw')
    if raw is not None and 'manifests' in raw:
        child = next((entry['digest'] for entry in raw['manifests']
                      if (entry.get('platform') or {}).get('os') == 'linux'
                      and (entry.get('platform') or {}).get('architecture') == 'amd64'), None)
        raw = imagetools(f'{ref.rsplit(":", 1)[0]}@{child}', '--raw') if child else None
    config = ((raw or {}).get('config') or {}).get('digest')
    if config is None:
        raise ReleaseError(f'{ref} has no linux/amd64 image config')
    return {'digest': info['manifest']['digest'], 'config': config, 'image': image}


def expect_metadata(ref, image, version, revision=None):
    labels = (image.get('config') or {}).get('Labels') or {}
    platform = f'{image.get("os")}/{image.get("architecture")}'
    if platform != 'linux/amd64':
        raise ReleaseError(f'{ref} is {platform}, not linux/amd64')
    if labels.get('org.opencontainers.image.version') != version:
        raise ReleaseError(f'{ref} is labelled version {labels.get("org.opencontainers.image.version")!r}, not {version}')
    if revision is not None and labels.get('org.opencontainers.image.revision') != revision:
        raise ReleaseError(f'{ref} was built from {labels.get("org.opencontainers.image.revision")!r}, not {revision}')
    return labels.get('org.opencontainers.image.revision')


def archive_identity(path):
    """The config digest, image config and tags of the single image in a `docker save` archive."""
    try:
        with tarfile.open(path) as archive:
            entries = json.load(archive.extractfile('manifest.json'))
            if len(entries) != 1:
                raise ReleaseError(f'{path} holds {len(entries)} images, not one')
            name = entries[0]['Config']
            config = archive.extractfile(name).read()
            image = json.loads(config)
    except (OSError, KeyError, TypeError, tarfile.TarError, json.JSONDecodeError, AttributeError) as error:
        raise ReleaseError(f'{path} is not a docker save archive: {error}') from error
    digest = 'sha256:' + hashlib.sha256(config).hexdigest()
    named = CONFIG_NAME.search(name) if isinstance(name, str) else None
    if named is None or f'sha256:{named[1]}' != digest:
        raise ReleaseError(f'{path} names its config {name!r} but the config hashes to {digest}; the archive is corrupted')
    return {'config': digest, 'image': image, 'tags': entries[0].get('RepoTags') or []}


def local_config(ref):
    """The config digest of a local image: the image ID with the classic store, an annotation with containerd's."""
    local = json.loads(docker('image', 'inspect', ref, capture=True))[0]
    return ((local.get('Descriptor') or {}).get('annotations') or {}).get('config.digest') or local['Id']


def github_output(**values):
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as handle:
            handle.writelines(f'{key.replace("_", "-")}={value}\n' for key, value in values.items())


def summarize(text):
    print(text, flush=True)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as handle:
            handle.write(text)


def build(root, version, revision, output, image=IMAGE):
    """Build both images once, save each to <output>/<kind>.tar and return their config digests."""
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    configs = {}
    for kind, file in DOCKERFILES.items():
        ref = f'{image}:{kind}-{version}'
        # Attestations would wrap the image in an index; keep the single amd64 manifest of earlier releases.
        docker('build', '--platform', 'linux/amd64', '--provenance=false', '--sbom=false', '-f', str(root / file),
               '--build-arg', f'VERSION={version}', '--build-arg', f'REVISION={revision}', '-t', ref, str(root))
        docker('save', '--output', str(output / f'{kind}.tar'), ref)
        saved = archive_identity(output / f'{kind}.tar')
        expect_metadata(ref, saved['image'], version, revision)
        if saved['config'] != local_config(ref):
            raise ReleaseError(f'The saved {ref} is not the image that was built')
        configs[kind] = saved['config']
    github_output(**{f'{kind}_config_digest': digest for kind, digest in configs.items()})
    summarize('\n'.join([f'### Built {image} {version}', '', '| Image | Config digest |', '| --- | --- |',
                          *[f'| `{image}:{kind}-{version}` | `{configs[kind]}` |' for kind in DOCKERFILES],
                          '', f'Revision `{revision}`, linux/amd64, saved once for verification and publication.', '']))
    return configs


def identify(source, version, revision, image=IMAGE):
    """Recompute the config digests of a pair that build saved, without building; report them as build does."""
    configs = {}
    for kind in DOCKERFILES:
        archive, ref = Path(source) / f'{kind}.tar', f'{image}:{kind}-{version}'
        saved = archive_identity(archive)
        expect_metadata(ref, saved['image'], version, revision)
        if ref not in saved['tags']:
            raise ReleaseError(f'{archive} holds {", ".join(saved["tags"]) or "no tag"}, not {ref}')
        configs[kind] = saved['config']
    github_output(**{f'{kind}_config_digest': digest for kind, digest in configs.items()})
    summarize('\n'.join([f'### Reused {image} {version} from an earlier attempt', '', '| Image | Config digest |',
                          '| --- | --- |', *[f'| `{image}:{kind}-{version}` | `{configs[kind]}` |' for kind in DOCKERFILES],
                          '', f'Revision `{revision}`, saved by the first attempt of this run; nothing was rebuilt.', '']))
    return configs


def load(source, version, revision, expected, image=IMAGE):
    """Load the saved images after checking they are the expected ones; return the local reference of each."""
    saved = {}
    for kind in DOCKERFILES:
        if not DIGEST.fullmatch(expected.get(kind) or ''):
            raise ReleaseError(f'The expected {kind} config digest must be sha256:<64 hex>, not {expected.get(kind)!r}')
        archive = Path(source) / f'{kind}.tar'
        saved[kind] = archive_identity(archive)
        if saved[kind]['config'] != expected[kind]:
            raise ReleaseError(f'{archive} holds {saved[kind]["config"]}, not the built and verified {expected[kind]}')
        expect_metadata(f'{image}:{kind}-{version}', saved[kind]['image'], version, revision)
    refs = {}
    for kind in DOCKERFILES:
        archive, tags, ref = Path(source) / f'{kind}.tar', saved[kind]['tags'], f'{image}:{kind}-{version}'
        if not tags:
            raise ReleaseError(f'{archive} names no image')
        docker('load', '--quiet', '--input', str(archive))
        if ref not in tags:
            docker('tag', tags[0], ref)
        if local_config(ref) != expected[kind]:
            raise ReleaseError(f'{ref} is not the loaded image {expected[kind]}')
        refs[kind] = ref
    github_output(**{f'{kind}_image': ref for kind, ref in refs.items()})
    return refs


def publish(source, version, revision, expected, image=IMAGE):
    versioned = {kind: f'{image}:{kind}-{version}' for kind in DOCKERFILES}
    latest = {kind: f'{image}:{kind}-latest' for kind in DOCKERFILES}
    reused = []
    for kind, ref in versioned.items():
        existing = remote(ref)
        if existing is None:
            continue
        published = expect_metadata(ref, existing['image'], version)
        if published != revision:
            raise ReleaseError(f'{ref} already exists from revision {published}; a published version is never overwritten')
        if existing['config'] != expected.get(kind):
            raise ReleaseError(f'{ref} was already published from this revision as {existing["config"]}, not as the '
                               f'verified {expected.get(kind)}; a published version is never overwritten. Re-run the '
                               'release run that published it while its image artifact exists.')
        print(f'{ref} is already published from this revision and verified image; reusing it.', flush=True)
        reused.append(kind)

    load(source, version, revision, expected, image)
    for kind in DOCKERFILES:
        if kind not in reused:
            docker('push', '--quiet', versioned[kind])
    digests = {}
    for kind, ref in versioned.items():
        published = remote(ref)
        if published is None:
            raise ReleaseError(f'{ref} is missing after its push')
        expect_metadata(ref, published['image'], version, revision)
        if published['config'] != expected[kind]:
            raise ReleaseError(f'{ref} was pushed as {published["config"]}, not the verified {expected[kind]}')
        digests[kind] = published['digest']

    # Latest moves only after both versioned tags are verified remotely, and copies exactly the verified remote
    # manifest, also one an earlier attempt pushed. --prefer-index=false copies a single manifest as it is instead of
    # wrapping it in a new index, so latest keeps the versioned manifest digest.
    for kind in DOCKERFILES:
        docker('buildx', 'imagetools', 'create', '--prefer-index=false', '--tag', latest[kind],
               f'{versioned[kind]}@{digests[kind]}')
    for kind, ref in latest.items():
        moved = remote(ref)
        if moved is None or moved['digest'] != digests[kind] or moved['config'] != expected[kind]:
            raise ReleaseError(f'{ref} does not resolve to {versioned[kind]} ({digests[kind]})')

    lines = [f'| `{versioned[kind]}` and `{latest[kind]}` | `{digests[kind]}` | `{expected[kind]}` |' for kind in DOCKERFILES]
    summarize('\n'.join([f'### Published {image} {version}', '', '| Tags | Digest | Verified config digest |',
                          '| --- | --- | --- |', *lines, '', f'Revision `{revision}`, linux/amd64.', '']))
    github_output(**{f'{kind}_digest': digest for kind, digest in digests.items()})
    return digests


def notes(root, version, digests, image=IMAGE):
    """The GitHub Release body: the committed release notes followed by both published image digests."""
    for kind in DOCKERFILES:
        if not DIGEST.fullmatch(digests.get(kind) or ''):
            raise ReleaseError(f'The {kind} digest must be sha256:<64 hex>, not {digests.get(kind)!r}')
    text = (root / 'docs' / 'releases' / f'v{version}.md').read_text().rstrip('\n')
    rows = [f'| `{image}:{kind}-{version}` | `{digests[kind]}` |' for kind in DOCKERFILES]
    return '\n'.join([text, '', '## Image digests', '', 'Both images were built once, verified, then pushed; '
                       f'pull `{image}@<digest>` to get exactly these bytes.', '', '| Image | Digest |',
                       '| --- | --- |', *rows, ''])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest='command', required=True)
    checking = commands.add_parser('check', help='Check packaged versions, changelog and release notes.')
    checking.add_argument('--tag', help='Also require this Git tag to be v<version>.')
    building = commands.add_parser('build', help='Build both images once and save them with their config digests.')
    loading = commands.add_parser('load', help='Load the saved images after checking their config digests.')
    identifying = commands.add_parser('identify', help='Report the config digests of a saved pair without building.')
    publishing = commands.add_parser('publish', help='Push the saved, verified images, then move both latest tags.')
    noting = commands.add_parser('notes', help='Write the GitHub Release body: release notes plus image digests.')
    noting.add_argument('--tag', help='Also require this Git tag to be v<version>.')
    for command in (building, identifying, loading, publishing):
        command.add_argument('--revision', required=True, help='Full commit SHA the images are built from.')
        command.add_argument('--image', default=IMAGE, help=f'Repository the images are tagged for (default {IMAGE}).')
    building.add_argument('--output', required=True, help='Directory for api.tar and web.tar.')
    identifying.add_argument('--input', required=True, help='Directory holding the api.tar and web.tar that build saved.')
    for command in (loading, publishing):
        command.add_argument('--input', required=True, help='Directory holding the api.tar and web.tar that build saved.')
        for kind in DOCKERFILES:
            command.add_argument(f'--{kind}-config-digest', required=True, help=f'The {kind} config digest build reported.')
    for kind in DOCKERFILES:
        noting.add_argument(f'--{kind}-digest', required=True, help=f'The published {kind} manifest digest.')
    noting.add_argument('--output', required=True, help='File to write the release body to.')
    args = parser.parse_args(argv)
    try:
        version = check(ROOT, getattr(args, 'tag', None))
        if args.command == 'check':
            print(version)
            github_output(version=version)
        elif args.command == 'notes':
            Path(args.output).write_text(notes(ROOT, version, {kind: getattr(args, f'{kind}_digest') for kind in DOCKERFILES}))
        else:
            if not re.fullmatch(r'[0-9a-f]{40}', args.revision):
                raise ReleaseError('--revision must be a full commit SHA')
            if args.command == 'build':
                build(ROOT, version, args.revision, args.output, args.image)
                return 0
            if args.command == 'identify':
                identify(args.input, version, args.revision, args.image)
                return 0
            expected = {kind: getattr(args, f'{kind}_config_digest') for kind in DOCKERFILES}
            if args.command == 'load':
                print('\n'.join(load(args.input, version, args.revision, expected, args.image).values()))
            else:
                publish(args.input, version, args.revision, expected, args.image)
    except ReleaseError as error:
        print(f'::error::{error}' if os.environ.get('GITHUB_ACTIONS') else f'error: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
