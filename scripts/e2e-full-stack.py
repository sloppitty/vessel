#!/usr/bin/env python3
"""Full-stack end-to-end test of the packaged Vessel images with a fake Hyperliquid venue.

Runs the real Compose stack (postgres, rabbitmq, rustfs, storage-init, migrate, api, monitor and
notifications workers, a verifier worker, web behind the Caddy TLS overlay with `tls internal`) on an isolated project,
unused subnet and loopback ports, with generated secrets, a synthetic owner and synthetic venue data served by
scripts/e2e/fake_venue.py over TLS (a private CA trusted only by the API and worker containers). The owner signs in
with a publicly known synthetic EOA (scripts/wallet-test-signer.cjs, index 1) bound by `identity bind-wallet`; the
signer runs in the pinned Playwright image with @noble from NOBLE_MODULE_DIR or a run-owned install. It drives a
realistic monitor scenario through the public HTTPS API, the SSE stream and a Playwright browser, asserting at every
step, then cleans up everything it created.

Never reads the repository .env, never touches the owner's database or services, never pushes images.

    python3 scripts/e2e-full-stack.py [--keep] [--skip-build] [--until STEP] [--artifacts DIR]
"""
import argparse
import base64
import http.client
from http.cookies import SimpleCookie
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import uuid

from dev import verification_environment

ROOT = Path(__file__).resolve().parent.parent
IMAGES = {'api': 'vessel-e2e:api', 'web': 'vessel-e2e:web'}
FAKE_IMAGE = 'python:3.13-slim'
PLAYWRIGHT_VERSION = '1.63.0'
PLAYWRIGHT_IMAGE = f'mcr.microsoft.com/playwright:v{PLAYWRIGHT_VERSION}-noble'
NOBLE_VERSION = '2.4.0'
SIGNER = ROOT / 'scripts' / 'wallet-test-signer.cjs'
HEAD_PROBE = '/api/events?after=9223372036854775807&limit=1'
CURSOR = re.compile(r'[0-9a-f]{32}\.[1-9][0-9]*')
SUBJECT_KEYS = {'accountId', 'venueId', 'contractId', 'playId', 'entryId', 'levelId', 'discrepancyId'}
EVENT_KEYS = {'id', 'type', 'version', 'ownerId', 'sequence', 'runId', 'dedupKey', 'severity', 'occurredAt', 'detectedAt',
              'timeliness', 'relevance', 'expiresAt', 'subject', 'source', 'data'}
COOLDOWN = 15


MARKER = '.vessel-e2e-run'
MARKER_TOOL = 'vessel-e2e-full-stack'
RUN_NAME = re.compile(r'vessel-e2e-[0-9a-f]{8}')
RUN_LABEL = 'vessel-e2e.run'
# Every credential this run generates; all printed, stored or reported text passes through redact().
SECRETS = set()


class StepFailed(Exception):
    pass


class CommandFailed(Exception):
    """A failed external command, described by its operation and exit code only: argv can hold generated secrets."""


class ResumeRefused(Exception):
    pass


def redact(text):
    text = str(text)
    for secret in sorted(SECRETS, key=len, reverse=True):
        text = text.replace(secret, '[redacted]')
    return text


def operation(args):
    """The command's operation words (no option values, paths or payloads), e.g. 'docker compose exec rabbitmq rabbitmqctl add_user'."""
    words, skip = [], False
    for arg in map(str, args):
        if skip:
            skip = False
        elif arg in ('--project-directory', '-p', '-f', '-v', '-e', '-w', '--name', '--label', '--user', '--entrypoint', '--network', '-c'):
            skip = True
        elif not arg.startswith('-') and '/' not in arg and len(arg) <= 40:
            words.append(arg)
        if len(words) == 6:
            break
    return redact(' '.join(words))


def run(*args, check=True, timeout=None, **kwargs):
    try:
        result = subprocess.run(args, check=False, timeout=timeout, **kwargs)
    except subprocess.TimeoutExpired:
        raise CommandFailed(f'{operation(args)} timed out after {timeout}s') from None
    if check and result.returncode:
        raise CommandFailed(f'{operation(args)} exited with {result.returncode}')
    return result


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def seq(event):
    return int(event['sequence'])


def subscription_report(output):
    """The provision command reports inserted and already-present subscriptions separately."""
    match = re.search(r'Registered subscriptions: ([^;\r\n]+); already registered: ([^\r\n]+)\.$', output, re.M)
    if match is None:
        return None
    return tuple(set() if names == 'none' else set(names.split(', ')) for names in match.groups())


class SseReader(threading.Thread):
    """One long-lived authenticated SSE client through nginx, resuming from each frame's id like the browser."""

    def __init__(self, e2e, cursor):
        super().__init__(daemon=True)
        self.e2e, self.cursor = e2e, cursor
        self.frames, self.statuses, self.connects, self.errors = [], [], 0, []
        self.stop = threading.Event()
        self.lock = threading.Lock()
        self.connection = None

    def run(self):
        while not self.stop.is_set():
            try:
                self.connection = self.e2e.connect(timeout=40)
                # The current session on every connect: a restore's identity reset replaces it.
                self.connection.request('GET', '/api/events/stream?after=' + self.cursor,
                                        headers={**self.e2e.session_headers(), 'Accept': 'text/event-stream'})
                response = self.connection.getresponse()
                if response.status != 200:
                    self.errors.append(response.status)
                    response.read()
                    self.stop.wait(2)
                    continue
                with self.lock:
                    self.connects += 1
                frame = {}
                while not self.stop.is_set():
                    raw = response.readline()
                    if not raw:
                        break
                    line = raw.decode('utf-8').rstrip('\r\n')
                    if line == '':
                        self.dispatch(frame)
                        frame = {}
                    elif not line.startswith(':'):
                        key, _, value = line.partition(':')
                        frame[key] = frame.get(key, '') + (value[1:] if value.startswith(' ') else value)
            except (OSError, http.client.HTTPException, ValueError) as error:
                self.errors.append(type(error).__name__)
            finally:
                if self.connection:
                    self.connection.close()
            self.stop.wait(2)

    def dispatch(self, frame):
        if 'data' not in frame:
            return
        data = json.loads(frame['data'])
        with self.lock:
            if frame.get('event') == 'status':
                self.statuses.append(data)
                self.cursor = data['nextCursor']
            else:
                self.frames.append({'cursor': frame.get('id'), 'event': data, 'type': frame.get('event')})
                if frame.get('id'):
                    self.cursor = frame['id']

    def snapshot(self):
        with self.lock:
            return list(self.frames), list(self.statuses)

    def close(self):
        self.stop.set()
        try:
            if self.connection and self.connection.sock:
                self.connection.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass


class E2E:
    def __init__(self, args):
        self.args = args
        self.started = time.monotonic()
        # The working directory and Compose projects this run may remove: set only when this run creates them, or after a
        # --resume candidate passed every ownership check. A candidate path is never cleaned up on its own.
        self.temp, self.owned_temp, self.owned_projects = None, None, []
        self.name = 'vessel-e2e-' + secrets.token_hex(4)
        self.project = self.name
        self.projects = [self.name, self.name + '-restore']
        self.owner = str(uuid.uuid4())
        self.database_password = secrets.token_hex(24)
        self.broker_password, self.app_password = secrets.token_hex(24), secrets.token_hex(24)
        self.s3_access, self.s3_secret = secrets.token_hex(16), secrets.token_hex(32)
        # Wallet sign-in: the synthetic operator EOA's public address and the current session cookie (a secret).
        self.wallet_address, self.session_cookie, self.tool_paths = None, None, None
        self.register_secrets()
        self.https_port, self.redirect_port = free_port(), free_port()
        self.broker_port, self.control_port = free_port(), free_port()
        stamp = time.strftime('%Y%m%d-%H%M%S')
        self.artifacts = Path(args.artifacts or Path(tempfile.gettempdir()) / f'vessel-e2e-artifacts-{stamp}')
        self.artifacts.mkdir(parents=True, exist_ok=True)
        self.results = []
        self.step = None
        self.built = False
        self.sse = None
        self.playwright_volume = None
        self.address = '0x' + secrets.token_hex(20)
        self.ids = {}

    # ---- reporting -----------------------------------------------------------------------------------

    def register_secrets(self):
        SECRETS.update(value for value in (self.database_password, self.broker_password, self.app_password,
                                           self.s3_access, self.s3_secret) if value)
        if self.session_cookie:
            SECRETS.add(self.session_cookie.split('=', 1)[1])

    def expect(self, condition, label, detail='', soft=False):
        label, detail = redact(label), redact(detail)
        self.results.append((self.step, label, bool(condition), '' if condition else detail[:300]))
        print(f"{'PASS' if condition else 'FAIL'} [{self.step}] {label}" + ('' if condition else f' -- {detail}'), flush=True)
        if not condition and not soft:
            raise StepFailed(label)
        return condition

    def note(self, message):
        print(f'      [{self.step}] {redact(message)}', flush=True)

    # ---- stack ---------------------------------------------------------------------------------------

    def prepare(self):
        self.temp = Path(tempfile.mkdtemp(prefix='vessel-e2e-'))
        self.owned_temp, self.owned_projects = self.temp, list(self.projects)
        # The ownership marker a later --resume must find before it adopts this directory and its projects.
        (self.temp / MARKER).write_text(json.dumps({'tool': MARKER_TOOL, 'name': self.name}))
        (self.temp / MARKER).chmod(0o600)
        for file in ('compose.yaml', 'compose.tls.yaml', 'Caddyfile'):
            shutil.copyfile(ROOT / 'deploy' / file, self.temp / file)
        shutil.copyfile(ROOT / 'scripts' / 'e2e' / 'fake_venue.py', self.temp / 'fake_venue.py')
        networks = [json.loads(line) for line in run('docker', 'network', 'ls', '--format', 'json', capture_output=True, text=True).stdout.splitlines()]
        inspected = json.loads(run('docker', 'network', 'inspect', *[n['ID'] for n in networks], capture_output=True, text=True).stdout or '[]')
        used = [ipaddress.ip_network(c['Subnet']) for n in inspected for c in (n['IPAM'].get('Config') or []) if c.get('Subnet')]
        for octet in range(120, 250):
            subnet = ipaddress.ip_network(f'172.28.{octet}.0/24')
            if not any(subnet.version == other.version and subnet.overlaps(other) for other in used):
                break
        else:
            raise RuntimeError('No free end-to-end subnet.')
        self.network = f'172.28.{octet}'
        self.certificates()
        (self.temp / '.env').write_text(f'''POSTGRES_PASSWORD={self.database_password}
Vessel__Auth__OwnerId={self.owner}
Vessel__Auth__OwnerName=E2E
VESSEL_IDENTITY_ORIGIN={self.base}
VESSEL_S3_ACCESS_KEY_ID={self.s3_access}
VESSEL_S3_SECRET_ACCESS_KEY={self.s3_secret}
RABBITMQ_USER=vessel
RABBITMQ_PASSWORD={self.broker_password}
RABBITMQ_APP_USER=vessel-app
RABBITMQ_APP_PASSWORD={self.app_password}
VESSEL_RABBITMQ_MANAGEMENT_BIND=127.0.0.1:{self.broker_port}
VESSEL_NETWORK_SUBNET={self.network}.0/24
VESSEL_NETWORK_GATEWAY={self.network}.1
VESSEL_WEB_IP={self.network}.10
VESSEL_TLS_PROXY_IP={self.network}.20
VESSEL_TLS_HOST=localhost
VESSEL_TLS_HTTP_BIND=127.0.0.1:{self.redirect_port}
VESSEL_TLS_HTTPS_BIND=127.0.0.1:{self.https_port}
VESSEL_TLS_DIRECTIVE=tls internal
VESSEL_MONITOR_INTERVAL=00:01:00
VESSEL_MONITOR_PROTECTION_GRACE=00:00:00
VESSEL_MONITOR_MANUAL_COOLDOWN=00:00:{COOLDOWN:02d}
E2E_FAKE_CONTROL_BIND=127.0.0.1:{self.control_port}
''')
        (self.temp / '.env').chmod(0o600)
        api, web = IMAGES['api'], IMAGES['web']
        # Shortest allowed lifetimes and retention; the private CA is trusted only by these containers.
        (self.temp / 'e2e.yaml').write_text(f'''x-venue: &venue
  Vessel__Venues__Hyperliquid__BaseUrl: https://fake-venue:8443/
  Vessel__Venues__Hyperliquid__StreamUrl: wss://fake-venue:8443/ws
  SSL_CERT_FILE: /run/e2e/ca.crt
  Vessel__Monitor__WatchLifetime: "01:00:00"
  Vessel__Monitor__UnobservableLifetime: "01:00:00"
  Vessel__Events__RetentionDays: "1"
x-ca: &ca
  - ./certs/ca.crt:/run/e2e/ca.crt:ro
services:
  migrate:
    image: {api}
    pull_policy: never
    environment:
      Vessel__Messaging__Verification: "true"
  api:
    image: {api}
    pull_policy: never
    environment: *venue
    volumes: *ca
  worker:
    image: {api}
    pull_policy: never
    environment: *venue
    volumes: *ca
  worker-notifications:
    image: {api}
    pull_policy: never
  verifier:
    image: {api}
    pull_policy: never
    entrypoint: ["/app/worker"]
    environment:
      DOTNET_ENVIRONMENT: Production
      Vessel__Worker__Modules__0: verification
      Vessel__Worker__Deployment: verification
      Vessel__Messaging__Verification: "true"
      Vessel__Events__RetentionDays: "1"
      ConnectionStrings__Vessel: "Host=postgres;Database=vessel;Username=vessel;Password=${{POSTGRES_PASSWORD}}"
      # Every worker reads the identity owner directory, so it needs the same sign-in settings as the API.
      Vessel__Auth__OwnerId: ${{Vessel__Auth__OwnerId}}
      Vessel__Identity__Origins__0: ${{VESSEL_IDENTITY_ORIGIN}}
      Vessel__Messaging__RabbitMqUri: "amqp://${{RABBITMQ_APP_USER}}:${{RABBITMQ_APP_PASSWORD}}@rabbitmq:5672/"
    depends_on:
      migrate:
        condition: service_completed_successfully
    # `compose up --wait` rejects a service without a healthcheck; the verifier reports only that its process is alive.
    healthcheck:
      test: ["CMD-SHELL", "kill -0 1"]
      interval: 5s
      timeout: 3s
      start_period: 10s
      retries: 6
    restart: unless-stopped
  rabbitmq:
    environment:
      RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS: "+S 2:2"
      RABBITMQ_CTL_ERL_ARGS: "+S 1:1"
  web:
    image: {web}
    pull_policy: never
  fake-venue:
    image: {FAKE_IMAGE}
    command: ["python", "-I", "/fake/fake_venue.py", "--cert", "/certs/venue.crt", "--key", "/certs/venue.key"]
    volumes:
      - ./fake_venue.py:/fake/fake_venue.py:ro
      - ./certs:/certs:ro
    ports:
      - "${{E2E_FAKE_CONTROL_BIND}}:8080"
    healthcheck:
      test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health')"]
      interval: 2s
      timeout: 5s
      retries: 30
''')

    def certificates(self):
        # Generated inside the fake venue's own image, so the host needs only Docker and Python; files stay host-owned.
        script = '''set -e; cd /tmp
openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.crt -days 2 -subj "/CN=Vessel E2E synthetic CA" \\
  -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign,cRLSign 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout venue.key -out venue.csr -subj /CN=fake-venue 2>/dev/null
printf 'subjectAltName=DNS:fake-venue\\nextendedKeyUsage=serverAuth\\nbasicConstraints=CA:FALSE\\n' > ext.cnf
openssl x509 -req -in venue.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out venue.crt -days 2 -extfile ext.cnf 2>/dev/null
for f in ca.crt venue.crt venue.key; do echo "==> $f"; cat "$f"; done'''
        output = self.standalone('certs', ['--entrypoint', 'sh', FAKE_IMAGE, '-ec', script], timeout=180)
        parts = re.split(r'^==> (\S+)\n', output, flags=re.M)[1:]
        files = dict(zip(parts[::2], parts[1::2]))
        if set(files) != {'ca.crt', 'venue.crt', 'venue.key'}:
            raise StepFailed('certificate generation returned incomplete output')
        certs = self.temp / 'certs'
        certs.mkdir()
        for name, content in files.items():
            (certs / name).write_text(content)
            (certs / name).chmod(0o644)
        certs.chmod(0o755)

    def standalone(self, purpose, arguments, timeout, input=None):
        """A run-owned, labelled `docker run --rm` container, removed explicitly if it outlives its timeout."""
        name = f'{self.name}-{purpose}'
        self.last_standalone = None
        try:
            result = subprocess.run(['docker', 'run', '--rm', '--name', name, '--label', f'{RUN_LABEL}={self.name}', *arguments],
                                    capture_output=True, text=True, timeout=timeout, input=input,
                                    env=getattr(self, 'standalone_env', None))
        except subprocess.TimeoutExpired:
            # Killing the client does not stop the daemon-side container; remove it before reporting.
            removed = subprocess.run(['docker', 'rm', '-f', name], capture_output=True, text=True).returncode == 0
            raise CommandFailed(f'container {purpose} timed out after {timeout}s' + ('' if removed else '; removing it failed')) from None
        self.last_standalone = result
        if result.returncode:
            raise CommandFailed(f'container {purpose} exited with {result.returncode}: {redact((result.stderr or result.stdout)[-300:])}')
        return result.stdout

    def compose(self, *args, project=None, **kwargs):
        # Wallet sign-in needs HTTPS: every command keeps the Caddy overlay, as an installation's COMPOSE_FILE does.
        cmd = ['docker', 'compose', '--project-directory', str(self.temp), '-p', project or self.project,
               '-f', str(self.temp / 'compose.yaml'), '-f', str(self.temp / 'compose.tls.yaml'), '-f', str(self.temp / 'e2e.yaml')]
        # The private synthetic .env must win over the caller's deployment settings.
        return run(*cmd, *args, env=verification_environment(), **kwargs)

    def quiet(self, *args, **kwargs):
        return self.compose(*args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **kwargs)

    def container(self, service, index=None):
        ids = self.compose('ps', '-q', service, capture_output=True, text=True).stdout.split()
        return ids if index is None else ids[index]

    def sql(self, query):
        # Only this run's generated Compose database; never a supplied DSN.
        return self.compose('exec', '-T', 'postgres', 'psql', '-U', 'vessel', '-d', 'vessel', '-qAt', '-F', '\t', '-c', query,
                            capture_output=True, text=True).stdout.strip()

    def rabbit(self, *args):
        # rabbitmqctl can answer 69 (node temporarily unavailable) on a busy host; retry briefly before failing.
        for attempt in range(6):
            result = self.compose('exec', '-T', 'rabbitmq', 'rabbitmqctl', '--quiet', *args, '--formatter', 'json',
                                  capture_output=True, text=True, check=False)
            if result.returncode == 0:
                return json.loads(result.stdout or '[]')
            time.sleep(3)
        raise StepFailed(f'rabbitmqctl {args[0]} failed ({result.returncode}): {result.stderr[-200:]}')

    def broker_app_user(self):
        # Least privilege, as documented: the hosts can only use vessel.* names and declare nothing they need.
        # rabbitmqctl reads the password from standard input, so it never appears in argv or process listings.
        self.compose('exec', '-T', 'rabbitmq', 'rabbitmqctl', 'add_user', 'vessel-app', input=self.app_password.encode(),
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.compose('exec', '-T', 'rabbitmq', 'rabbitmqctl', 'set_permissions', '-p', '/', 'vessel-app', '^vessel\\..*',
                     '^(vessel\\..*|amq\\.default)$', '^vessel\\..*', stdout=subprocess.DEVNULL)

    def topology(self):
        queues = {q['name']: {**q, 'arguments': {a[0]: a[2] for a in q['arguments']}}
                  for q in self.rabbit('list_queues', 'name', 'type', 'durable', 'arguments')}
        exchanges = {x['name']: x for x in self.rabbit('list_exchanges', 'name', 'type', 'durable') if x['name'].startswith('vessel')}
        bindings = sorted((b['source_name'], b['destination_name'], b['routing_key'])
                          for b in self.rabbit('list_bindings', 'source_name', 'destination_name', 'routing_key') if b['source_name'])
        return queues, exchanges, bindings

    # ---- HTTP ----------------------------------------------------------------------------------------

    @property
    def base(self):
        return f'https://localhost:{self.https_port}'

    def connect(self, timeout=30):
        # Caddy's `tls internal` certificate: an isolated loopback run, so the chain is not verified.
        return http.client.HTTPSConnection('localhost', self.https_port, context=ssl._create_unverified_context(), timeout=timeout)

    def session_headers(self, auth=True):
        """What the browser sends: the accepted origin, and with a session its cookie and the tab's user binding."""
        headers = {'Origin': self.base, 'X-Vessel-Request': '1'}
        if auth and self.session_cookie:
            headers.update({'Cookie': self.session_cookie, 'X-Vessel-User': self.owner})
        return headers

    def request(self, path, method='GET', body=None, auth=True, headers=None):
        headers = {'Host': f'localhost:{self.https_port}', **self.session_headers(auth), **(headers or {})}
        if body is not None:
            headers['Content-Type'] = 'application/json'
            body = json.dumps(body).encode()
        connection = self.connect()
        try:
            connection.request(method, path, body, headers)
            response = connection.getresponse()
            data = response.read()
            raw = response.getheaders()
            answer = {k.lower(): v for k, v in raw}
            answer['set-cookie'] = [v for k, v in raw if k.lower() == 'set-cookie']
            return response.status, answer, data
        finally:
            connection.close()

    def api(self, path, body=None, method=None, expect=(200, 201)):
        status, _, data = self.request(path, method or ('POST' if body is not None else 'GET'), body)
        if status not in expect:
            raise StepFailed(f'{method or "GET"} {path} returned {status}: {data[:300]!r}')
        return json.loads(data) if data else None

    # ---- wallet sign-in --------------------------------------------------------------------------------

    def sign(self, message=None):
        """The synthetic operator EOA (index 1), signed in a network-less pinned Playwright container."""
        mounts, _, noble = self.tools()
        arguments = ['-i', '--network', 'none', '-e', 'NOBLE_MODULE_DIR=' + noble, '-e', 'VESSEL_RELEASE_SYNTHETIC=1',
                     '-v', f'{SIGNER}:/wallet-test-signer.cjs:ro']
        for mount in mounts:
            arguments += ['-v', mount]
        # A fresh name per call: a removed --rm container's name can briefly stay taken.
        output = self.standalone('sign-' + secrets.token_hex(3), [*arguments, PLAYWRIGHT_IMAGE, 'node', '/wallet-test-signer.cjs'], timeout=120,
                                 input=json.dumps({'wallet': 1, 'message': message}))
        return json.loads(output)

    def operator_address(self):
        if not self.wallet_address:
            self.wallet_address = self.sign()['address']
        return self.wallet_address

    def cookie(self, headers, name):
        values = [value for value in headers['set-cookie'] if value.startswith(name + '=')]
        self.expect(len(values) == 1, name + ' issued exactly once', f'{len(values)} issued')
        jar = SimpleCookie()
        jar.load(values[0])
        cookie = jar[name]
        self.expect(bool(cookie['secure']) and bool(cookie['httponly']) and cookie['samesite'].lower() == 'strict' and
                    cookie['path'] == '/' and not cookie['domain'], name + ' is Secure, HttpOnly, SameSite=Strict, host-only, Path=/')
        return name + '=' + cookie.value

    def sign_in(self):
        """Challenge, EOA signature and verify through Caddy and nginx, like the browser; keeps the session cookie."""
        status, headers, data = self.request('/api/auth/challenge', 'POST', {'address': self.operator_address()}, auth=False)
        self.expect(status == 200, 'the API issues the operator EOA a sign-in challenge', data[:200])
        binding = self.cookie(headers, '__Host-vessel-signin')
        challenge = json.loads(data)
        signature = self.sign(challenge['message'])['signature']
        status, headers, data = self.request('/api/auth/verify', 'POST', {'challengeId': challenge['challengeId'],
                                             'signature': signature, 'remember': False}, auth=False, headers={'Cookie': binding})
        body = json.loads(data) if status == 200 else {}
        self.expect(body.get('status') == 'signed-in', 'the synthetic operator wallet signs in over HTTPS', (status, data[:200]))
        self.session_cookie = self.cookie(headers, '__Host-vessel-session')
        self.register_secrets()
        self.expect(body['session']['user']['id'] == self.owner, 'the operator session keeps the journal owner GUID')

    def control(self, path, body=None):
        connection = http.client.HTTPConnection('127.0.0.1', self.control_port, timeout=30)
        try:
            connection.request('POST' if body is not None else 'GET', '/control/' + path,
                               json.dumps(body).encode() if body is not None else None, {'Content-Type': 'application/json'})
            response = connection.getresponse()
            data = json.loads(response.read())
            if response.status != 200:
                raise StepFailed(f'fake venue {path}: {data}')
            return data
        finally:
            connection.close()

    def order(self, coin, side, px, sz, kind='limit', **extra):
        return self.control('order', {'address': self.address, 'coin': coin, 'side': side, 'px': px, 'sz': sz, 'kind': kind,
                                      'trigger': px if kind != 'limit' else None, **extra})['oid']

    def wait(self, predicate, label, timeout=120, interval=2):
        deadline = time.monotonic() + timeout
        while True:
            value = predicate()
            if value:
                return value
            if time.monotonic() > deadline:
                raise StepFailed('Timed out waiting for ' + label)
            time.sleep(interval)

    def ready(self):
        def ok():
            try:
                return self.request('/health/ready', auth=False)[0] == 200
            except (OSError, http.client.HTTPException):
                return False
        self.wait(ok, 'API readiness', 120)

    # ---- events --------------------------------------------------------------------------------------

    def head(self):
        page = self.api(HEAD_PROBE)
        assert page['resetRequired'] and page['events'] == []
        return page['nextCursor']

    def events(self, after):
        found, cursor = [], after
        while True:
            page = self.api(f'/api/events?after={cursor}&limit=200')
            if page['resetRequired']:
                raise StepFailed('Unexpected event reset after ' + cursor)
            found += page['events']
            cursor = page['nextCursor']
            if len(page['events']) < 200:
                return found

    def wait_events(self, after, predicate, label, timeout=150):
        return self.wait(lambda: (lambda found: found if predicate(found) else None)(self.events(after)), label, timeout)

    def check(self, account=None, timeout=120):
        """An owner Check, waiting out admission (cooldown or a scheduled run) as the browser button does."""
        account = account or self.account
        deadline = time.monotonic() + timeout
        while True:
            status, headers, data = self.request(f'/api/accounts/{account}/monitor/run', 'POST')
            if status == 202:
                return json.loads(data)
            if status != 429 or time.monotonic() > deadline:
                raise StepFailed(f'Check returned {status}: {data[:200]!r}')
            time.sleep(min(int(headers.get('retry-after', '5')), 30) + 0.5)

    def cooled(self, account=None):
        monitor = self.api(f'/api/accounts/{account or self.account}/monitor')
        if monitor['nextManualAllowedAtUtc']:
            from datetime import datetime
            until = datetime.fromisoformat(monitor['nextManualAllowedAtUtc'].replace('Z', '+00:00')).timestamp()
            time.sleep(max(0.0, until - time.time()) + 0.5)

    def checked(self, after, account=None, timeout=150):
        """Check, then wait for its monitor.run-completed; returns (accepted, completion, events since `after`)."""
        accepted = self.check(account)
        request = accepted['requestId']
        found = self.wait_events(after, lambda es: any(e['type'] == 'monitor.run-completed' and e['runId'] == request for e in es),
                                 'monitor.run-completed for ' + request, timeout)
        return accepted, next(e for e in found if e['type'] == 'monitor.run-completed' and e['runId'] == request), found

    def until(self, after, predicate, label, timeout=240, gap=0):
        """Owner Checks (and scheduled runs) until the stored state satisfies `predicate`."""
        deadline = time.monotonic() + timeout
        while True:
            if gap:
                time.sleep(gap)
            self.checked(after)
            value = predicate()
            if value:
                return value
            if time.monotonic() > deadline:
                raise StepFailed('Timed out waiting for ' + label)

    def receipts(self):
        rows = self.sql("SELECT event_id, state, attempts FROM event_receipts WHERE subscription = 'verification' AND owner_id = '" +
                        self.owner + "'")
        return {line.split('\t')[0]: line.split('\t')[1:] for line in rows.splitlines() if line}

    def wait_delivered(self, events, label, timeout=150):
        ids = {e['id'] for e in events}
        self.wait(lambda: all(self.receipts().get(i, [''])[0] == 'delivered' for i in ids), label, timeout)
        return True

    def outbox(self):
        return int(self.sql('SELECT (SELECT count(*) FROM wolverine_api.wolverine_outgoing_envelopes) + '
                            '(SELECT count(*) FROM wolverine_monitor.wolverine_outgoing_envelopes)') or '0')

    def execution(self, play):
        return self.api(f'/api/plays/{play}/execution')

    def play(self, play):
        return self.api(f'/api/plays/{play}')

    def status(self, play, status, reason=None):
        current = self.play(play)
        body = {'expectedVersion': current['summary']['version'], 'status': status}
        if reason:
            body['reason'] = reason
        return self.api(f'/api/plays/{play}/status', body)

    def create_play(self, title, coin, direction, entry, stops, targets, size='1000', leverage='3'):
        plan = {'direction': direction, 'sizingMode': 'margin', 'size': size, 'leverage': leverage,
                'entries': [{'id': 'entry-1', 'name': 'Entry 1', 'color': '#b9c9e4', 'share': '100', 'price': entry,
                             'stops': [{'id': f'stop-{i + 1}', 'unit': 'price', 'value': v, 'share': s} for i, (v, s) in enumerate(stops)],
                             'targets': [{'id': f'target-{i + 1}', 'unit': 'price', 'value': v, 'share': s} for i, (v, s) in enumerate(targets)]}],
                'notes': {'thesis': 'Synthetic end-to-end scenario', 'invalidation': '', 'strategy': '', 'evidence': ''}}
        return self.api('/api/plays', {'accountId': self.account, 'instrument': coin, 'instrumentSource': 'venue',
                                       'title': title, 'plan': plan})['summary']['id']

    def discrepancies(self, play, kind=None):
        return [d for d in self.execution(play)['discrepancies'] if kind is None or d['kind'] == kind]

    def stored(self, kind, play):
        rows = self.sql(f'SELECT "State" FROM play_discrepancies WHERE "PlayId" = \'{play}\' AND "Kind" = \'{kind}\'')
        return rows.splitlines()

    def explicit(self, events):
        bad = [e['type'] for e in events if set(e) != EVENT_KEYS or set(e['subject']) != SUBJECT_KEYS or
               set(e['source']) != {'trigger', 'orderIds', 'fillIds'}]
        return not bad, bad

    def ordered(self, events, first, then):
        a = [seq(e) for e in events if first(e)]
        b = [seq(e) for e in events if then(e)]
        return bool(a) and bool(b) and max(a) < min(b)

    # ---- scenario ------------------------------------------------------------------------------------

    def step_deploy(self):
        if not self.args.skip_build:
            for kind, file in (('api', 'backend/Dockerfile'), ('web', 'frontend/Dockerfile')):
                self.note(f'building {IMAGES[kind]}')
                run('docker', 'build', '--platform', 'linux/amd64', '-f', str(ROOT / file), '-t', IMAGES[kind], str(ROOT),
                    stdout=subprocess.DEVNULL)
            self.built = True
        for image in (*IMAGES.values(),):
            self.expect(run('docker', 'image', 'inspect', image, check=False, capture_output=True).returncode == 0,
                        'packaged image available: ' + image)
        for image in (FAKE_IMAGE, PLAYWRIGHT_IMAGE):
            if run('docker', 'image', 'inspect', image, check=False, capture_output=True).returncode:
                run('docker', 'pull', image, stdout=subprocess.DEVNULL)
        self.compose('config', '-q')
        self.quiet('up', '-d', '--wait', 'postgres', 'rabbitmq', 'fake-venue')
        queues, _, _ = self.topology()
        self.expect(not any(name.startswith('vessel') for name in queues), 'empty broker before the explicit provision step')
        migrate = self.compose('run', '--rm', 'migrate', capture_output=True, text=True, check=False)
        output = migrate.stdout + migrate.stderr
        self.expect(migrate.returncode == 0, 'migrate runs the EF bundle and provision-messaging', output[-400:])
        self.expect(all(schema in output for schema in ('wolverine_api', 'wolverine_monitor',
                                                       'wolverine_notifications', 'wolverine_verification')),
                    'provisioning prepares every deployment envelope schema', output[-300:])
        self.expect(subscription_report(output) == ({'notifications', 'verification'}, set()),
                    'notifications and verification subscriptions registered insert-only', output[-300:])
        self.expect(f'Ensured identity user {self.owner}.' in output, 'migrate ensures the configured owner as the operator', output[-300:])
        # Nobody can sign in, and readiness stays 503, until the operator has a wallet: bound explicitly, never by migrate.
        bound = self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate', '/app/Vessel.Api.dll', 'identity',
                             'bind-wallet', '--user', self.owner, '--address', self.operator_address(), '--label', 'E2E operator',
                             capture_output=True, text=True, check=False)
        self.expect(bound.returncode == 0, 'identity bind-wallet binds the synthetic operator EOA', (bound.stdout + bound.stderr)[-300:])
        queues, exchanges, bindings = self.topology()
        expected = {'vessel.commands.monitor', 'vessel.commands.monitor.dlq', 'vessel.events.sub.verification',
                    'vessel.events.sub.verification.dlq', 'vessel.events.sub.notifications',
                    'vessel.events.sub.notifications.dlq'}
        self.expect(expected <= set(queues), 'provisioned queues exist', sorted(queues))
        for name in ('vessel.commands.monitor', 'vessel.events.sub.verification', 'vessel.events.sub.notifications'):
            arguments = queues[name]['arguments']
            self.expect(queues[name]['type'] == 'quorum' and queues[name]['durable'], f'{name} is a durable quorum queue', queues[name])
            self.expect(arguments.get('x-delivery-limit') == 20, f'{name} has delivery limit 20', arguments)
            self.expect(bool(arguments.get('x-dead-letter-exchange')), f'{name} dead-letters to its DLX', arguments)
        self.expect(exchanges.get('vessel.events', {}).get('type') == 'topic' and exchanges['vessel.events']['durable'],
                    'vessel.events is a durable topic exchange', exchanges)
        self.expect(('vessel.events', 'vessel.events.sub.verification', '#') in bindings,
                    'verification subscription bound to every event type', bindings)
        self.expect(('vessel.events', 'vessel.events.sub.notifications', '#') in bindings,
                    'notifications subscription bound to every event type', bindings)
        dead = {(s, d) for s, d, _ in bindings}
        self.expect({('vessel.commands.monitor.dlx', 'vessel.commands.monitor.dlq'),
                     ('vessel.events.sub.notifications.dlx', 'vessel.events.sub.notifications.dlq'),
                     ('vessel.events.sub.verification.dlx', 'vessel.events.sub.verification.dlq')} <= dead,
                    'each dead-letter exchange routes to its dead-letter queue', bindings)
        before = self.topology()
        self.broker_app_user()
        self.quiet('up', '-d', '--wait')
        self.ready()
        self.expect(self.request('/api/system', auth=False, headers={'Authorization': 'Bearer ' + secrets.token_hex(32)})[0] == 401,
                    'a bearer token authenticates nobody')
        self.sign_in()
        summary = self.wait(lambda: (lambda s: s if s['workerOnline'] else None)(self.api('/api/monitor')), 'worker heartbeat', 120)
        self.expect(summary['workerOnline'] and summary['heartbeatAtUtc'], 'worker heartbeat online in /api/monitor')
        worker = json.loads(run('docker', 'inspect', self.container('worker', 0), capture_output=True, text=True).stdout)[0]
        self.expect(worker['State']['Health']['Status'] == 'healthy', 'worker container healthy (heartbeat file)')
        notifications = json.loads(run('docker', 'inspect', self.container('worker-notifications', 0),
                                       capture_output=True, text=True).stdout)[0]
        self.expect(notifications['State']['Health']['Status'] == 'healthy',
                    'notifications worker healthy (module-aware heartbeat file)')
        self.wait(lambda: self.api('/api/notifications/head')['workerOnline'], 'notifications heartbeat', 120)
        self.expect(self.sql("SELECT count(*) FROM server_notification_heartbeats WHERE deployment='notifications'") == '1',
                    'notifications worker records per-deployment health')
        time.sleep(3)
        self.expect(self.topology() == before, 'API and workers declared nothing at runtime (topology unchanged)')
        self.expect(self.listening('listeners'), 'the worker consumes the command queue and the verifier its subscription', self.consumers())
        deployments = self.sql('SELECT schema_name FROM messaging_deployments ORDER BY 1')
        self.expect(deployments.split() == ['wolverine_api', 'wolverine_monitor', 'wolverine_notifications', 'wolverine_verification'],
                    'messaging deployments recorded per schema', deployments)
        self.expect(self.request('/api/monitor', auth=False)[0] == 401, 'monitor API requires authentication')
        self.head_cursor = self.head()
        self.expect(self.head_cursor == '0' or CURSOR.fullmatch(self.head_cursor), 'head probe returns an opaque cursor', self.head_cursor)
        self.sse = SseReader(self, self.head_cursor)
        self.sse.start()
        self.wait(lambda: self.sse.statuses, 'SSE ready status', 30)
        self.expect(self.sse.statuses[0]['state'] == 'ready', 'SSE stream through nginx starts with a ready status', self.sse.statuses)

    def step_account(self):
        seed = self.control('reset', {})
        self.control('seed-fill', {'address': self.address, 'coin': 'SOL', 'side': 'B', 'px': '140', 'sz': '3', 'agoMs': 3_600_000})
        self.control('seed-fill', {'address': self.address, 'coin': 'SOL', 'side': 'A', 'px': '150', 'sz': '1', 'agoMs': 1_800_000})
        self.expect(seed['ok'], 'fake venue reset with synthetic history')
        self.portfolio = self.api('/api/portfolios', {'name': 'E2E synthetic', 'icon': 'folder'})['id']
        account = self.api('/api/accounts', {'name': 'E2E Hyperliquid', 'venueId': 'hyperliquid', 'address': self.address,
                                             'portfolioId': self.portfolio})
        self.account = account['id']
        self.expect(account['venueId'] == 'hyperliquid' and account['portfolioId'] == self.portfolio, 'Hyperliquid account created in portfolio')
        status, _, data = self.request(f'/api/accounts/{self.account}/monitor/run', 'POST')
        self.expect(status == 409, 'Check is refused (409) for an account without eligible Plays', status)
        refreshed = self.api(f'/api/accounts/{self.account}/sync', {})
        self.expect(refreshed['syncStatus'] != 'failed' and refreshed['lastSyncedAtUtc'], 'Refresh reads the fake venue over trusted TLS',
                    refreshed.get('lastSyncError'))
        self.expect(refreshed['positionCount'] == 1, 'Refresh imports the open SOL position', refreshed)
        snapshot = self.api(f'/api/accounts/{self.account}/snapshot')
        positions = snapshot.get('positions') or []
        self.expect([(p['contractId'], p['signedQuantity']) for p in positions] == [('SOL', '2')],
                    'snapshot holds the SOL position with its exact quantity', positions)
        fills = self.api(f'/api/accounts/{self.account}/fills')
        rows = fills if isinstance(fills, list) else fills.get('fills', [])
        self.expect(len(rows) == 2, 'Refresh imports both historical fills', len(rows))
        monitor = self.api(f'/api/accounts/{self.account}/monitor')
        self.expect(monitor['state'] == 'idle' and monitor['manualState'] == 'idle', 'account monitor idle without Plays', monitor)
        requests = self.control('summary')['requests']
        self.expect(requests.get('clearinghouseState') == 1 and requests.get('frontendOpenOrders') == 1,
                    'one account read and one order read per Refresh', requests)

    def step_link(self):
        mark = self.head()
        self.p1 = self.create_play('E2E P1 BTC long', 'BTC', 'long', '100', [('95', '50'), ('94', '50')], [('110', '100')])
        oids = {'entry': self.order('BTC', 'B', '100', '1'), 'stop1': self.order('BTC', 'A', '95', '0.5', 'stop'),
                'stop2': self.order('BTC', 'A', '94', '0.5', 'stop'), 'target': self.order('BTC', 'A', '110', '1', 'tp')}
        self.p1_orders = oids
        # The Refresh started the manual cooldown; plan only once a Check can be admitted at once, so the Check
        # (not the next scheduled discovery) performs the first run.
        self.cooled()
        planned = self.status(self.p1, 'planned')
        self.expect(planned['summary']['status'] == 'planned', 'P1 planned')
        accepted = self.check()
        self.expect(accepted['monitor']['manualState'] == 'pending' and accepted['monitor']['runTrigger'] == 'owner',
                    'Check answers 202 with a pending owner run', accepted['monitor'])
        request = accepted['requestId']
        found = self.wait_events(mark, lambda es: any(e['type'] == 'monitor.run-completed' and e['runId'] == request for e in es),
                                 'P1 Check completion')
        links = [e for e in found if e['type'] == 'execution.link-changed' and e['subject']['playId'] == self.p1]
        done = next(e for e in found if e['type'] == 'monitor.run-completed' and e['runId'] == request)
        self.expect(len(links) == 4, 'the worker run links entry, both stops and the target automatically', [e['data'] for e in links])
        self.expect(all(e['runId'] == request and e['source']['trigger'] == 'monitor' for e in links), 'link events belong to the Check run')
        self.expect(all(seq(e) < seq(done) for e in links), 'link events precede monitor.run-completed')
        self.expect(done['data'] == {'trigger': 'manual', 'outcome': 'succeeded', 'state': 'live'},
                    'monitor.run-completed data {trigger, outcome, state}', done['data'])
        status = next(e for e in found if e['type'] == 'play.status-changed' and e['subject']['playId'] == self.p1)
        self.expect(seq(status) < min(seq(e) for e in links), 'owner planning event precedes the worker links')
        ok, bad = self.explicit(found)
        self.expect(ok, 'every event serializes all fields with explicit nulls', bad)
        self.expect(all(e['subject']['discrepancyId'] is None for e in links), 'absent subject fields are explicit null')
        execution = self.execution(self.p1)
        roles = sorted(l['role'] for l in execution['links'] if l['state'] == 'linked')
        self.expect(roles == ['entry', 'stop', 'stop', 'target'], 'P1 execution shows four linked orders', roles)
        self.expect(execution['monitor']['state'] == 'live', 'P1 execution embeds a live account monitor', execution['monitor'])
        status, headers, _ = self.request(f'/api/accounts/{self.account}/monitor/run', 'POST')
        self.expect(status == 429 and 0 < int(headers.get('retry-after', '0')) <= COOLDOWN, 'immediate second Check is cooling down (429, Retry-After)',
                    (status, headers.get('retry-after')))
        status, headers, _ = self.request(f'/api/plays/{self.p1}/execution/check', 'POST')
        self.expect(status == 429, 'execution/check alias shares the same admission', status)
        self.wait(lambda: any(f['event']['id'] == done['id'] for f in self.sse.snapshot()[0]), 'SSE delivery of the run', 30)
        frames = [f for f in self.sse.snapshot()[0] if f['event']['id'] in {e['id'] for e in found}]
        self.expect([f['event']['id'] for f in frames] == [e['id'] for e in found], 'SSE delivers the same events in commit order')
        self.expect(all(f['type'] == 'event' and CURSOR.fullmatch(f['cursor'] or '') and f['cursor'].split('.')[1] == f['event']['sequence']
                        for f in frames), 'SSE id lines are {epoch}.{sequence} resume cursors')
        self.wait_delivered(found, 'verification receipts for the link run')
        self.expect(True, 'verification subscription receipts reach delivered')

    def step_open(self):
        mark = self.head()
        fill = self.control('fill', {'address': self.address, 'oid': self.p1_orders['entry']})
        _, done, found = self.checked(mark)
        filled = [e for e in found if e['type'] == 'execution.filled' and e['subject']['playId'] == self.p1]
        opened = [e for e in found if e['type'] == 'play.opened' and e['subject']['playId'] == self.p1]
        if not opened:  # a scheduled run may have imported it just before the Check
            found = self.events(mark)
            filled = [e for e in found if e['type'] == 'execution.filled' and e['subject']['playId'] == self.p1]
            opened = [e for e in found if e['type'] == 'play.opened' and e['subject']['playId'] == self.p1]
        self.expect(len(filled) == 1 and len(opened) == 1, 'one execution.filled and one play.opened', [e['type'] for e in found])
        data = filled[0]['data']
        self.expect((data['role'], data['filledQuantity'], data['cumulativeQuantity'], data['complete']) == ('entry', '1', '1', True),
                    'execution.filled reports the exact entry quantity', data)
        self.expect(filled[0]['source']['fillIds'] == [str(fill['tid'])], 'execution.filled names the venue fill', filled[0]['source'])
        self.expect(seq(filled[0]) < seq(opened[0]) and filled[0]['runId'] == opened[0]['runId'], 'cause (fill) before effect (play.opened) in one run')
        self.expect(self.play(self.p1)['summary']['status'] == 'open', 'P1 is Open')
        execution = self.execution(self.p1)
        self.expect(execution['totals']['openQuantity'] == '1' and not execution['discrepancies'], 'P1 open quantity 1 with stops resting, no discrepancy',
                    execution['totals'])
        positions = self.api(f'/api/accounts/{self.account}/snapshot').get('positions') or []
        self.expect(('BTC', '1') in [(p['contractId'], p['signedQuantity']) for p in positions], 'the run imports the BTC position', positions)

    def step_discrepancy(self):
        mark = self.head()
        self.p2 = self.create_play('E2E P2 ETH long', 'ETH', 'long', '2000', [('1900', '100')], [('2200', '100')])
        self.p2_entry = self.order('ETH', 'B', '2000', '1')
        self.status(self.p2, 'planned')
        self.until(mark, lambda: any(l['role'] == 'entry' and l['state'] == 'linked' for l in self.execution(self.p2)['links']), 'P2 entry link')
        self.control('fill', {'address': self.address, 'oid': self.p2_entry})
        # P1 loses half its stop protection at the venue.
        self.control('cancel', {'address': self.address, 'oid': self.p1_orders['stop1']})
        self.until(mark, lambda: self.play(self.p2)['summary']['status'] == 'open' and self.stored('protection.missing', self.p2), 'P2 open with a candidate')
        self.expect(self.stored('protection.missing', self.p2) == ['candidate'], 'P2 missing stop is a candidate first (not public)')
        self.expect(not self.discrepancies(self.p2), 'an unconfirmed candidate is not shown')
        self.expect(self.stored('protection.cancelled', self.p1) == ['candidate'], 'P1 cancelled stop is a candidate',
                    self.sql(f'SELECT "Kind", "State" FROM play_discrepancies WHERE "PlayId" = \'{self.p1}\''))
        self.expect(not self.stored('protection.missing', self.p1), 'protection.cancelled supersedes missing for the same orders')
        kinds = lambda es: {e['data']['kind'] for e in es if e['type'] == 'discrepancy.opened'}
        found = self.until(mark, lambda: (lambda es: es if {'protection.missing', 'protection.cancelled'} <= kinds(es) else None)(self.events(mark)),
                           'confirmed discrepancies', timeout=240, gap=20)
        opened = {e['data']['kind']: e for e in found if e['type'] == 'discrepancy.opened'}
        self.expect(set(opened) == {'protection.missing', 'protection.cancelled', 'position.excess'},
                    'confirmation runs open both protection discrepancies (and the untracked SOL exposure)', sorted(opened))
        self.expect(opened['protection.missing']['severity'] == opened['protection.cancelled']['severity'] == 'critical', 'protection discrepancies are critical')
        self.expect(opened['protection.missing']['subject']['playId'] == self.p2 and opened['protection.cancelled']['subject']['playId'] == self.p1,
                    'discrepancy events name their Play')
        excess = opened['position.excess']
        self.expect(excess['severity'] == 'warning' and excess['subject']['contractId'] == 'SOL' and excess['subject']['playId'] is None and
                    (excess['data']['details']['expectedQuantity'], excess['data']['details']['venueQuantity']) == ('0', '2'),
                    'a venue position without any Play is flagged as untracked exposure (position.excess)', excess['data'])
        self.expect(all(seq(e) > seq(f) for e in opened.values() for f in found if f['type'] == 'execution.filled'),
                    'discrepancies follow the facts they describe')
        missing = self.discrepancies(self.p2, 'protection.missing')
        cancelled = self.discrepancies(self.p1, 'protection.cancelled')
        self.expect(len(missing) == 1 and missing[0]['state'] == 'open' and missing[0]['confirmedAtUtc'] and missing[0]['outcome'] is None,
                    'P2 execution lists the open protection.missing', missing)
        self.expect(len(cancelled) == 1 and cancelled[0]['state'] == 'open', 'P1 execution lists the open protection.cancelled', cancelled)
        self.expect(cancelled[0]['details'].get('quantity') == '1' and cancelled[0]['details'].get('stopQuantity') == '0.5',
                    'discrepancy details keep exact decimal strings', cancelled[0]['details'])
        account = self.api(f'/api/accounts/{self.account}/monitor')
        self.expect({d['id'] for d in account['discrepancies']} >= {missing[0]['id'], cancelled[0]['id']},
                    'account monitor lists both discrepancies')
        self.ids.update(p2_missing=missing[0]['id'], p1_cancelled=cancelled[0]['id'])
        self.wait_delivered(found, 'receipts for the discrepancy events')
        # The owner acknowledges the untracked SOL position on the account route; checks continue.
        status, _, _ = self.request(f'/api/accounts/{self.account}/discrepancies/{excess["subject"]["discrepancyId"]}/dismiss', 'POST',
                                    {'note': 'Held outside Vessel Plays'})
        self.expect(status == 204, 'account-level dismissal answers 204', status)
        before = self.head()
        status, _, _ = self.request(f'/api/accounts/{self.account}/discrepancies/{excess["subject"]["discrepancyId"]}/dismiss', 'POST',
                                    {'note': 'Second note'})
        record = next(d for d in self.api(f'/api/accounts/{self.account}/monitor')['discrepancies'] if d['id'] == excess['subject']['discrepancyId'])
        # Contract: repeating a dismissal is a 204 no-op that keeps the original note; other non-open states are 409.
        self.expect(status == 204 and record['note'] == 'Held outside Vessel Plays' and not self.events(before),
                    'repeating a dismissal is an idempotent no-op: no event, note kept', (status, record['note']))

    def step_browser_live(self):
        self.mark_browser = self.head()
        self.browser('live')
        found = self.events(self.mark_browser)
        resolved = {e['subject']['discrepancyId']: e for e in found if e['type'] == 'discrepancy.resolved'}
        for key in ('p1_cancelled', 'p2_missing'):
            event = resolved.get(self.ids[key])
            self.expect(event is not None and event['data'].get('resolution') == 'dismissed', f'{key} dismissal raised discrepancy.resolved (dismissed)',
                        event and event['data'])
            self.expect(event is not None and event['source']['trigger'] == 'owner', f'{key} dismissal is an owner event')
        cancelled, missing = self.discrepancies(self.p1, 'protection.cancelled')[0], self.discrepancies(self.p2, 'protection.missing')[0]
        # P1 closed on screen afterwards, so fresh evidence may already have resolved its dismissed record.
        self.expect(cancelled['state'] in ('dismissed', 'resolved') and cancelled['dismissedAtUtc'] and cancelled['note'] == 'Dismissed from a second client' and
                    missing['state'] == 'dismissed' and missing['note'] == 'Reviewed in the end-to-end browser test',
                    'dismissals persist with their notes', (cancelled, missing))

    def watch(self, play, order):
        return [w for w in self.execution(play)['watches'] if w['orderId'] == str(order)]

    def step_close(self):
        mark = getattr(self, 'mark_browser', None) or self.head()
        if self.play(self.p1)['summary']['status'] != 'closed':
            # The browser phase closes P1 on screen; without it the harness fills the target itself.
            if self.p1_orders['target'] in self.control('summary')['accounts'][self.address.lower()]['open']:
                self.control('fill', {'address': self.address, 'oid': self.p1_orders['target']})
            self.until(mark, lambda: self.play(self.p1)['summary']['status'] == 'closed', 'P1 closed')
        found = self.events(mark)
        target = [e for e in found if e['type'] == 'execution.filled' and e['subject']['playId'] == self.p1]
        closed = [e for e in found if e['type'] == 'play.closed' and e['subject']['playId'] == self.p1]
        self.expect(len(target) == 1 and target[0]['data']['role'] == 'target' and len(closed) == 1, 'target fill then one play.closed',
                    [e['type'] for e in found])
        self.expect(seq(target[0]) < seq(closed[0]) and target[0]['runId'] == closed[0]['runId'], 'execution.filled precedes play.closed in one run')
        data = closed[0]['data']
        self.expect(data['enteredQuantity'] == '1' and data['exitedQuantity'] == '1' and data['closedPnlUsd'] == '10',
                    'play.closed carries exact entered/exited quantity and PnL', data)
        self.expect(data['fees'] and all(f['token'] == 'USDC' for f in data['fees']), 'play.closed reports fees by token', data.get('fees'))
        watched = self.watch(self.p1, self.p1_orders['stop2'])
        self.expect([(w['role'], w['state']) for w in watched] == [('stop', 'watching')], 'the leftover stop of the closed Play is watched',
                    self.execution(self.p1)['watches'])
        # The closing run's own order read predates the closure; the next fresh read ends the other watches.
        others = lambda: [w for w in self.execution(self.p1)['watches'] if w['orderId'] != str(self.p1_orders['stop2'])]
        self.until(mark, lambda: (lambda ws: ws if len(ws) == 3 and all(w['state'] == 'resolved' for w in ws) else None)(others()),
                   'resolved watches of filled and cancelled orders')
        self.expect(True, 'the next fresh read ends the watches of filled and cancelled linked orders')
        # P2 exits without a planned level: an unplanned reduce-only exit links automatically.
        self.p2_exit = self.order('ETH', 'A', '2100', '1', reduceOnly=True)
        self.until(mark, lambda: any(l['role'] == 'exit' and l['state'] == 'linked' for l in self.execution(self.p2)['links']), 'P2 unplanned exit link')
        self.control('fill', {'address': self.address, 'oid': self.p2_exit})
        self.until(mark, lambda: self.play(self.p2)['summary']['status'] == 'closed', 'P2 closed')
        self.expect(True, 'P2 closes through its unplanned exit')
        found = self.until(mark, lambda: (lambda es: es if any(e['type'] == 'discrepancy.opened' and e['data']['kind'] == 'protection.orphaned'
                                                               for e in es) else None)(self.events(mark)),
                           'protection.orphaned for the leftover stop', timeout=240, gap=20)
        orphan = next(e for e in found if e['type'] == 'discrepancy.opened' and e['data']['kind'] == 'protection.orphaned')
        self.expect(orphan['severity'] == 'warning' and orphan['subject']['playId'] == self.p1, 'protection.orphaned (warning) for closed P1')
        missing = self.discrepancies(self.p2, 'protection.missing')
        self.expect(missing[0]['state'] in ('dismissed', 'resolved'), 'P2 dismissed discrepancy stays closed after the exit', missing)
        # A one-hour WatchLifetime is the shortest allowed; age only this watch's start (synthetic time control).
        # A watch identifies its order through its link; the link references the order by internal ID.
        changed = self.sql('UPDATE monitor_exit_watches w SET "StartedAtUtc" = w."StartedAtUtc" - interval \'2 hours\' '
                           'FROM play_order_links l JOIN imported_orders o ON o."OwnerId" = l."OwnerId" '
                           'AND o."AccountId" = l."AccountId" AND o."Id" = l."ImportedOrderId" '
                           f'WHERE l."Id" = w."LinkId" AND w."PlayId" = \'{self.p1}\' '
                           f'AND o."ExternalOrderId" = \'{self.p1_orders["stop2"]}\' RETURNING w."LinkId"')
        self.expect(len(changed.split()) == 1, 'synthetic time control ages the stop watch past WatchLifetime', changed)
        self.until(mark, lambda: [w['state'] for w in self.watch(self.p1, self.p1_orders['stop2'])] == ['unverifiable'], 'unverifiable watch')
        self.expect(self.watch(self.p1, self.p1_orders['stop2'])[0]['endedAtUtc'], 'the unverifiable watch is persistent and ended')
        self.expect(self.discrepancies(self.p1, 'protection.orphaned')[0]['state'] == 'open', 'unverifiable watch never claims the order is gone')

    def step_orphan_entry(self):
        mark = self.head()
        self.p3 = self.create_play('E2E P3 AVAX long', 'AVAX', 'long', '20', [('19', '100')], [('22', '100')])
        self.p3_entry = self.order('AVAX', 'B', '20', '5')
        self.status(self.p3, 'planned')
        self.until(mark, lambda: any(l['role'] == 'entry' and l['state'] == 'linked' for l in self.execution(self.p3)['links']), 'P3 entry link')
        cancelled = self.status(self.p3, 'cancelled', 'invalidated')
        self.expect(cancelled['summary']['status'] == 'cancelled', 'owner cancels P3 with its entry still resting')
        found = self.until(mark, lambda: (lambda es: es if any(e['type'] == 'discrepancy.opened' and e['data']['kind'] == 'entry.orphaned'
                                                               for e in es) else None)(self.events(mark)),
                           'entry.orphaned', timeout=240, gap=20)
        orphan = next(e for e in found if e['type'] == 'discrepancy.opened' and e['data']['kind'] == 'entry.orphaned')
        self.expect(orphan['severity'] == 'critical' and orphan['subject']['playId'] == self.p3, 'entry.orphaned (critical) for cancelled P3')
        watches = self.execution(self.p3)['watches']
        self.expect([(w['role'], w['state']) for w in watches] == [('entry', 'watching')], 'the resting entry is watched', watches)
        self.ids['p3_orphan'] = orphan['subject']['discrepancyId']
        # An execution on a market the catalogue does not list yet: Vessel leaves it out and says so.
        self.control('raw-fill', {'address': self.address, 'coin': 'NEWCOIN', 'side': 'B', 'px': '1', 'sz': '10'})
        monitor = self.until(mark, lambda: (lambda m: m if m['state'] == 'degraded' else None)(self.api(f'/api/accounts/{self.account}/monitor')),
                             'degraded monitor')
        self.expect('could not verify' in (monitor['error'] or ''), 'an omitted execution degrades the monitor with a safe reason', monitor['error'])
        change = [e for e in self.events(mark) if e['type'] == 'monitor.state-changed' and e['data']['state'] == 'degraded']
        self.expect(change and change[-1]['severity'] == 'warning' and change[-1]['data']['previous'] == 'live',
                    'monitor.state-changed live -> degraded (warning)', [e['data'] for e in change])
        # Unobservable beyond UnobservableLifetime (one hour minimum): order reads fail at the venue and the record's last
        # decisive observation is aged (synthetic time control). The record turns unverifiable, never resolved.
        self.control('fail', {'type': 'frontendOpenOrders', 'count': 1000, 'status': 500})
        aged = self.sql('UPDATE play_discrepancies SET "LastObservedAtUtc" = "LastObservedAtUtc" - interval \'2 hours\' '
                        f'WHERE "Id" = \'{self.ids["p3_orphan"]}\' RETURNING "Id"')
        self.expect(aged == self.ids['p3_orphan'], 'synthetic time control ages the orphaned entry record')
        found = self.until(mark, lambda: (lambda es: es if any(e['type'] == 'discrepancy.unverifiable' for e in es) else None)(self.events(mark)),
                           'discrepancy.unverifiable', timeout=180)
        event = next(e for e in found if e['type'] == 'discrepancy.unverifiable')
        self.expect(event['severity'] == 'warning' and event['subject']['discrepancyId'] == self.ids['p3_orphan'] and
                    event['data'].get('outcome') == 'unverifiable', 'discrepancy.unverifiable announces the undecidable record', event['data'])
        record = self.discrepancies(self.p3, 'entry.orphaned')[0]
        self.expect(record['outcome'] == 'unverifiable' and record['unverifiableAtUtc'] and record['state'] != 'resolved',
                    'the record stays visible as unverifiable, not resolved', record)
        failed = self.api(f'/api/accounts/{self.account}/monitor')
        self.expect(failed['state'] in ('degraded', 'stale') and failed['error'] == 'The venue check failed. Monitoring will retry.',
                    'failed order reads leave a safe, payload-free error', failed['error'])

    def step_browser_ended(self):
        self.browser('ended')

    def step_recover(self):
        mark = self.head()
        self.control('fail', {'type': 'frontendOpenOrders', 'count': 0})
        # The owner closes the NEWCOIN exposure, then the venue lists the market: the omitted fills become importable.
        self.control('raw-fill', {'address': self.address, 'coin': 'NEWCOIN', 'side': 'A', 'px': '1.1', 'sz': '10'})
        self.control('market', {'name': 'NEWCOIN', 'szDecimals': 0, 'maxLeverage': 3, 'px': '1'})
        self.until(mark, lambda: self.api(f'/api/accounts/{self.account}/monitor')['state'] == 'live', 'live again', gap=5)
        self.expect(True, 'reads recover and a newly listed market makes the omitted execution importable: monitor live')
        record = self.discrepancies(self.p3, 'entry.orphaned')[0]
        self.expect(record['outcome'] is None and record['state'] == 'open', 'a fresh decision clears the unverifiable outcome', record)
        self.control('cancel', {'address': self.address, 'oid': self.p3_entry})
        self.control('cancel', {'address': self.address, 'oid': self.p1_orders['stop2']})
        self.until(mark, lambda: [w['state'] for w in self.execution(self.p3)['watches']] == ['resolved'], 'P3 watch resolved')
        self.expect(True, 'the entry watch resolves once the venue reports the order cancelled')
        found = self.wait_events(mark, lambda es: sum(e['type'] == 'discrepancy.resolved' and e['data'].get('resolution') == 'resolved'
                                                      for e in es) >= 2, 'orphan discrepancies resolved', 150)
        kinds = sorted(e['data']['kind'] for e in found if e['type'] == 'discrepancy.resolved' and e['data'].get('resolution') == 'resolved')
        self.expect(kinds == ['entry.orphaned', 'protection.orphaned'], 'fresh evidence resolves both orphan discrepancies', kinds)
        resolved = next(e for e in found if e['type'] == 'discrepancy.resolved' and e['data']['kind'] == 'entry.orphaned')
        status, _, _ = self.request(f'/api/plays/{self.p3}/discrepancies/{resolved["subject"]["discrepancyId"]}/dismiss', 'POST', {})
        self.expect(status == 409, 'a resolved discrepancy cannot be dismissed (409)', status)
        self.expect([w['state'] for w in self.watch(self.p1, self.p1_orders['stop2'])] == ['unverifiable'], 'the unverifiable watch stays unverifiable')
        monitor = self.wait(lambda: (lambda m: m if m['state'] == 'idle' else None)(self.api(f'/api/accounts/{self.account}/monitor')),
                            'idle account', 150)
        self.expect(monitor['nextRunAtUtc'] is None, 'the account idles with no scheduled run when nothing is left', monitor)
        status, _, _ = self.request(f'/api/accounts/{self.account}/monitor/run', 'POST')
        self.expect(status == 409, 'Check on the idle account is 409', status)

    def consumers(self):
        return {q['name']: q['consumers'] for q in self.rabbit('list_queues', 'name', 'consumers')}

    def listening(self, label, timeout=90):
        queues = ('vessel.commands.monitor', 'vessel.events.sub.verification', 'vessel.events.sub.notifications')
        try:
            self.wait(lambda: all(self.consumers().get(q, 0) >= 1 for q in queues), label, timeout, 3)
            return True
        except StepFailed:
            return False

    def step_broker(self):
        mark = self.head()
        self.p4 = self.create_play('E2E P4 BTC long', 'BTC', 'long', '100', [('95', '100')], [('110', '100')])
        self.p4_orders = {'entry': self.order('BTC', 'B', '100', '1'), 'stop': self.order('BTC', 'A', '95', '1', 'stop'),
                          'target': self.order('BTC', 'A', '110', '1', 'tp')}
        self.quiet('stop', 'rabbitmq')
        self.status(self.p4, 'planned')
        accepted = self.check()
        request = accepted['requestId']
        found = self.wait_events(mark, lambda es: any(e['type'] == 'monitor.run-completed' and e['runId'] == request for e in es),
                                 'run without the broker (command recovered from monitor_accounts)', 120)
        self.expect(sum(e['type'] == 'execution.link-changed' for e in found) == 3, 'the worker linked P4 while RabbitMQ was down')
        self.expect(True, 'the Check runs without the broker: the pending request is recovered from monitor_accounts')
        self.expect(self.outbox() > 0, 'the outboxes hold the publications during the outage', self.outbox())
        self.expect(self.request('/health/ready', auth=False)[0] == 200, 'API stays ready without the broker')
        self.quiet('up', '-d', '--wait', 'rabbitmq')
        self.wait(lambda: self.outbox() == 0, 'outbox drain', 150)
        self.expect(True, 'the outboxes drain once the broker returns')
        self.wait_delivered(found, 'receipts after the broker returned')
        self.expect(True, 'every event of the outage reaches the verification subscription')
        self.expect(self.listening('listeners after broker restart'), 'command and subscription listeners reconnect after a broker restart',
                    self.consumers())
        # Broker volume loss: an empty RabbitMQ without topology or the application user.
        mark = self.head()
        self.quiet('rm', '-sfv', 'rabbitmq')
        run('docker', 'volume', 'rm', f'{self.project}_rabbitmq', stdout=subprocess.DEVNULL)
        self.quiet('up', '-d', '--wait', 'rabbitmq')
        self.broker_app_user()
        self.expect(not any(q.startswith('vessel') for q in self.topology()[0]), 'the recreated broker has no topology')
        self.control('fill', {'address': self.address, 'oid': self.p4_orders['entry']})
        self.wait(lambda: self.play(self.p4)['summary']['status'] == 'open', 'P4 open on a scheduled run', 150, 5)
        lost = self.events(mark)
        self.expect(any(e['type'] == 'play.opened' for e in lost), 'events commit while the broker has no topology')
        migrate = self.compose('run', '--rm', 'migrate', capture_output=True, text=True, check=False)
        self.expect(migrate.returncode == 0 and
                    subscription_report(migrate.stdout) == (set(), {'notifications', 'verification'}),
                    're-provisioning restores the topology; registration stays insert-only', migrate.stdout[-300:])
        self.wait_delivered(lost, 'catch-up after broker volume loss', 180)
        self.expect(True, 'subscription catch-up from PostgreSQL delivers the events the empty broker missed')
        self.wait(lambda: self.outbox() == 0, 'outbox drain after volume loss', 150)
        self.expect(True, 'the outboxes drain after re-provisioning')
        self.expect(self.listening('listeners after broker volume loss'), 'listeners resume on the re-provisioned queues without a host restart',
                    self.consumers(), soft=True)
        accepted, done, _ = self.checked(self.head())
        self.expect(done['data']['outcome'] == 'succeeded', 'Checks complete after broker volume loss')

    def step_crash(self):
        self.quiet('up', '-d', '--wait', '--scale', 'worker=2', '--no-recreate', 'worker')
        workers = self.container('worker')
        self.expect(len(workers) == 2, 'two monitor worker replicas', workers)
        mark = self.head()
        ips = {next(iter(json.loads(run('docker', 'inspect', '--format', '{{json .NetworkSettings.Networks}}', c, capture_output=True,
                                        text=True).stdout).values()))['IPAddress']: c for c in workers}
        self.control('fill', {'address': self.address, 'oid': self.p4_orders['target']})
        self.cooled()
        self.wait(lambda: self.api(f'/api/accounts/{self.account}/monitor')['manualState'] == 'idle', 'no run in progress', 90, 1)
        self.control('gate', {'type': 'frontendOpenOrders'})
        status, _, data = self.request(f'/api/accounts/{self.account}/monitor/run', 'POST')
        # 429 only if a scheduled run started in between; then that run hits the gate instead.
        request = json.loads(data)['requestId'] if status == 202 else None
        self.expect(status in (202, 429), 'Check admitted (or a scheduled run already holds the account)', status)
        gate = self.wait(lambda: (lambda g: g if g.get('entered') else None)(self.control('gate')), 'gated venue read', 120, 1)
        victim = ips.get(gate['peer'])
        self.expect(victim is not None, 'the gated order read comes from a worker replica', (gate, ips))
        killed_at = time.monotonic()
        run('docker', 'kill', victim, stdout=subprocess.DEVNULL)
        self.control('release', {})
        running = self.api(f'/api/accounts/{self.account}/monitor')
        self.expect(running['manualState'] == 'running', 'the crashed run keeps its lease (running)', running['manualState'])
        self.wait(lambda: self.play(self.p4)['summary']['status'] == 'closed', 'P4 closed by the recovered run', 480, 5)
        waited = time.monotonic() - killed_at
        self.expect(waited >= 240, f'recovery waits for the 5-minute lease ({waited:.0f}s after the kill)', waited)
        found = self.wait_events(mark, lambda es: request is None or any(e['type'] == 'monitor.run-completed' and e['runId'] == request for e in es),
                                 'completion of the recovered Check', 60)
        if request:
            done = [e for e in found if e['type'] == 'monitor.run-completed' and e['runId'] == request]
            self.expect(len(done) == 1 and done[0]['data']['outcome'] == 'succeeded', 'the recovered Check completes exactly once', done)
        self.expect(sum(e['type'] == 'execution.filled' and e['subject']['playId'] == self.p4 for e in found) == 1 and
                    sum(e['type'] == 'play.closed' and e['subject']['playId'] == self.p4 for e in found) == 1,
                    'the recovered run applies the target fill exactly once')
        survivors = [c for c in self.container('worker')]
        self.note(f'worker containers after the kill: {len(survivors)} (restart policy may restart the killed one)')
        keys = [e['dedupKey'] for e in self.events(self.head_cursor)]
        self.expect(len(keys) == len(set(keys)), 'no duplicate event identities across the whole run')
        self.quiet('up', '-d', '--wait', '--scale', 'worker=1', 'worker')

    def step_disable(self):
        # P4's resting stop keeps the account monitored after its closure.
        mark = self.head()
        self.quiet('stop', 'worker')
        self.cooled()
        accepted = self.check()
        self.expect(accepted['monitor']['manualState'] == 'pending', 'Check accepted while no worker runs')
        account = self.api(f'/api/accounts/{self.account}')
        self.api(f'/api/accounts/{self.account}', {'name': account['name'], 'portfolioId': account['portfolioId'], 'isEnabled': False,
                                                   'expectedRevision': account['settingsRevision']}, method='PUT')
        reads = self.control('summary')['requests'].get('clearinghouseState', 0)
        self.quiet('up', '-d', '--wait', 'worker')
        found = self.wait_events(mark, lambda es: any(e['type'] == 'monitor.run-completed' and e['runId'] == accepted['requestId'] for e in es),
                                 'settled Check', 120)
        done = next(e for e in found if e['type'] == 'monitor.run-completed' and e['runId'] == accepted['requestId'])
        self.expect(done['data']['outcome'] == 'superseded', 'discovery settles the stranded Check as superseded', done['data'])
        rows = self.sql('SELECT "ManualState", "NextManualAllowedAtUtc" IS NOT NULL, "NextRunAtUtc" IS NULL FROM monitor_accounts '
                        f'WHERE "AccountId" = \'{self.account}\'').split('\t')
        self.expect(rows == ['idle', 't', 't'], 'disabled account: idle, no scheduled run, cooldown recorded', rows)
        self.expect(self.control('summary')['requests'].get('clearinghouseState', 0) == reads, 'settling the disabled account read no venue data')
        account = self.api(f'/api/accounts/{self.account}')
        self.api(f'/api/accounts/{self.account}', {'name': account['name'], 'portfolioId': account['portfolioId'], 'isEnabled': True,
                                                   'expectedRevision': account['settingsRevision']}, method='PUT')
        status, headers, _ = self.request(f'/api/accounts/{self.account}/monitor/run', 'POST')
        self.expect(status == 429 and 0 < int(headers.get('retry-after', '0')) <= COOLDOWN,
                    'the cooldown started by the settled Check applies after re-enabling', (status, headers.get('retry-after')))
        accepted, done, _ = self.checked(self.head())
        self.expect(done['data']['outcome'] == 'succeeded', 'monitoring resumes on the re-enabled account')

    def step_admin(self):
        frames, _ = self.sse.snapshot()
        epoch = frames[0]['cursor'].split('.')[0]
        # retry-event-receipts: two settled receipts are marked failed (synthetic), then returned to pending.
        delivered = [k for k, v in self.receipts().items() if v[0] == 'delivered'][:2]
        self.sql("UPDATE event_receipts SET state = 'failed' WHERE subscription = 'verification' AND event_id IN ('" + "','".join(delivered) + "')")
        retried = self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate', '/app/Vessel.Api.dll', 'retry-event-receipts',
                               'verification', '--owner', self.owner, '--limit', '10', '--attempts', '2', capture_output=True, text=True, check=False)
        self.expect(retried.returncode == 0 and 'Returned 2 failed receipt(s)' in retried.stdout, 'retry-event-receipts returns failed receipts to pending',
                    retried.stdout[-200:])
        self.wait(lambda: all(self.receipts()[i][0] == 'delivered' for i in delivered), 'retried receipts delivered', 120)
        self.expect(True, 'catch-up redelivers the retried receipts')
        # Retention: RetentionDays=1 is the minimum; age the oldest events (synthetic) and restart a sweeping worker.
        events = self.retained()
        dedup = self.sql('SELECT count(*) FROM event_dedup')
        old = events[:5]
        self.sql(f"UPDATE events SET detected_at = detected_at - interval '3 days' WHERE owner_id = '{self.owner}' AND sequence <= {old[-1]['sequence']}")
        self.quiet('restart', 'verifier')
        floor = self.wait(lambda: (lambda f: f if f and int(f) >= int(old[-1]['sequence']) else None)(
            self.sql(f"SELECT retention_floor FROM event_cursors WHERE owner_id = '{self.owner}'")), 'retention floor', 120)
        self.expect(int(floor) == int(old[-1]['sequence']), 'retention prunes exactly the aged contiguous prefix', floor)
        self.wait(lambda: self.sql(f"SELECT count(*) FROM events WHERE owner_id = '{self.owner}' AND sequence <= {floor}") == '0', 'physical deletion', 60)
        self.expect(True, 'events below the floor are deleted')
        page = self.api(f'/api/events?after={epoch}.{old[1]["sequence"]}&limit=5')
        self.expect(page['resetRequired'] and not page['events'], 'a cursor below the retention floor requires a reset', page)
        self.expect(self.sql('SELECT count(*) FROM event_dedup') == dedup, 'dedup identities survive pruning')
        page = self.api('/api/events?after=0&limit=1')
        self.expect(not page['resetRequired'] and page['events'] and seq(page['events'][0]) == int(floor) + 1,
                    'a fresh after=0 cursor pages retained history from the oldest retained event', page)
        retained = self.retained()
        self.expect(retained and seq(retained[0]) == int(floor) + 1, 'retained history starts right above the floor')

    def step_restore(self):
        frames, _ = self.sse.snapshot()
        old_cursor = frames[-1]['cursor']
        self.quiet('stop', 'web', 'api', 'worker', 'worker-notifications', 'verifier', 'rustfs')
        with (self.temp / 'vessel.dump').open('wb') as out:
            self.compose('exec', '-T', 'postgres', 'pg_dump', '-U', 'vessel', '-d', 'vessel', '-Fc', stdout=out)
        with (self.temp / 'objects.tar.gz').open('wb') as out:
            self.compose('run', '--rm', '--no-deps', '-T', '--entrypoint', 'tar', 'rustfs', '-C', '/data', '-czf', '-', '.', stdout=out)
        self.quiet('down', '-v', '--remove-orphans')
        self.project = self.projects[1]
        self.quiet('up', '-d', '--wait', 'postgres', 'rabbitmq', 'fake-venue')
        with (self.temp / 'vessel.dump').open('rb') as source:
            self.compose('exec', '-T', 'postgres', 'pg_restore', '-U', 'vessel', '-d', 'vessel', '--no-owner', '--no-privileges',
                         '--exit-on-error', stdin=source)
        with (self.temp / 'objects.tar.gz').open('rb') as source:
            self.compose('run', '--rm', '--no-deps', '-T', '--user', '0', '--entrypoint', 'sh', 'rustfs', '-c',
                         'tar -xzf - -C /data && chown -R 10001:10001 /data', stdin=source, stdout=subprocess.DEVNULL)
        # Mandatory before any host starts: the backup's sessions are revoked and only the confirmed operator stays active.
        reset = self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate', '/app/Vessel.Api.dll', 'identity',
                             'reset-after-restore', '--keep-active', self.owner, capture_output=True, text=True, check=False)
        self.expect(reset.returncode == 0 and self.sql('SELECT count(*) FROM identity.sessions WHERE revoked_at IS NULL') == '0',
                    'identity reset-after-restore revokes every restored session before any host starts', reset.stdout[-300:])
        rotated = self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate', '/app/Vessel.Api.dll', 'rotate-event-epoch',
                               capture_output=True, text=True)
        self.expect('Rotated the event stream epoch for 1 owner' in rotated.stdout, 'rotate-event-epoch before any host starts', rotated.stdout)
        self.broker_app_user()
        self.quiet('run', '--rm', 'migrate')
        self.quiet('up', '-d', '--wait')
        self.ready()
        self.expect(self.request('/api/auth/session')[0] == 401, 'the restored, reset session no longer authenticates')
        self.sign_in()
        self.expect(self.play(self.p1)['summary']['status'] == 'closed' and self.play(self.p4)['summary']['status'] == 'closed',
                    'restored Plays keep their lifecycle')
        page = self.api(f'/api/events?after={old_cursor}&limit=10')
        self.expect(page['resetRequired'] and page['nextCursor'].split('.')[0] != old_cursor.split('.')[0],
                    'an old-epoch cursor gets a reset with the new epoch', page)
        self.wait(lambda: any(s['state'] == 'reset' for s in self.sse.snapshot()[1]), 'SSE reset after restore', 60)
        self.expect(True, 'the long-lived SSE client reconnects with its old cursor and receives a reset status')
        summary = self.wait(lambda: (lambda s: s if s['workerOnline'] else None)(self.api('/api/monitor')), 'restored worker heartbeat', 120)
        self.expect(summary['workerOnline'], 'restored worker heartbeat online without the old broker volume')
        mark = self.head()
        self.expect(mark.split('.')[0] == page['nextCursor'].split('.')[0], 'new events use the rotated epoch')
        self.p5 = self.create_play('E2E P5 after restore', 'SOL', 'long', '150', [('140', '100')], [('170', '100')])
        self.status(self.p5, 'planned')
        found = self.wait_events(mark, lambda es: any(e['type'] == 'play.status-changed' for e in es), 'post-restore event', 60)
        self.wait(lambda: any(f['event']['id'] == found[-1]['id'] for f in self.sse.snapshot()[0]), 'SSE after reset', 60)
        self.expect(True, 'SSE resumes in the new epoch after the reset')
        self.wait_delivered(found, 'post-restore delivery')
        self.expect(True, 'delivery works after restore')

    def step_streams(self):
        frames, statuses = self.sse.snapshot()
        history = self.retained()
        streamed = [f['event']['id'] for f in frames if f['type'] == 'event']
        self.expect(len(streamed) == len(set(streamed)), 'SSE never delivered an event twice', len(streamed) - len(set(streamed)))
        resets = [s for s in statuses if s['state'] == 'reset']
        old_epoch = frames[0]['cursor'].split('.')[0]
        old_max = max(int(f['cursor'].split('.')[1]) for f in frames if f['cursor'].split('.')[0] == old_epoch)
        reset_at = int(resets[-1]['nextCursor'].split('.')[1]) if resets else old_max
        # Between the backup and the client's reconnection the restored hosts may commit events; a reset means
        # "reload stored state", so those are deliberately not replayed.
        gap = [e for e in history if old_max < seq(e) <= reset_at]
        missing = [e['type'] for e in history if e['id'] not in set(streamed) and e not in gap]
        self.expect(not missing, 'SSE delivered every retained event outside the reset window', missing)
        self.note(f'SSE: {len(streamed)} event frames, {len(statuses)} status frames ({len(resets)} reset), {self.sse.connects} connections, '
                  f'{len(gap)} events in the reset window')

    def retained(self):
        # A fresh '0' cursor pages retained history from the oldest retained event, also after pruning.
        return self.events('0')

    # ---- browser -------------------------------------------------------------------------------------

    def tools(self):
        """The (mounts, playwright-core path, @noble directory) for the pinned Playwright image.

        A supplied --playwright-module and an absolute NOBLE_MODULE_DIR (holding @noble/curves and @noble/hashes) are
        mounted read-only. Whatever is missing is installed once per run with the repository's pinned pnpm (through
        Corepack inside the same image) into a run-owned Docker volume, so a fresh machine needs only Docker and Python.
        The volume is removed with the run.
        """
        if self.tool_paths:
            return self.tool_paths
        mounts, packages = [], []
        if self.args.playwright_module:
            module = Path(self.args.playwright_module)
            self.expect(module.exists(), 'supplied playwright-core module exists', module)
            mounts.append(f'{module.parent}:/playwright:ro')
            playwright = f'/playwright/{module.name}'
        else:
            packages.append(f'playwright-core@{PLAYWRIGHT_VERSION}')
            playwright = '/tools/node_modules/playwright-core'
        noble = os.environ.get('NOBLE_MODULE_DIR')
        if noble:
            self.expect(Path(noble).is_absolute() and (Path(noble) / '@noble' / 'curves').is_dir() and (Path(noble) / '@noble' / 'hashes').is_dir(),
                        'supplied NOBLE_MODULE_DIR holds @noble/curves and @noble/hashes', noble)
            mounts.append(f'{noble}:/noble:ro')
            noble = '/noble'
        else:
            packages += [f'@noble/curves@{NOBLE_VERSION}', f'@noble/hashes@{NOBLE_VERSION}']
            noble = '/tools/node_modules'
        if packages:
            pnpm = json.loads((ROOT / 'package.json').read_text())['packageManager']
            volume = self.name + '-playwright'
            run('docker', 'volume', 'create', '--label', f'{RUN_LABEL}={self.name}', volume, stdout=subprocess.DEVNULL)
            self.playwright_volume = volume
            try:
                self.standalone('tools-install', ['-e', 'HOME=/tmp', '-e', 'COREPACK_ENABLE_DOWNLOAD_PROMPT=0', '-v', f'{volume}:/cache',
                                                  '-w', '/cache', PLAYWRIGHT_IMAGE, 'sh', '-ec',
                                                  f'echo \'{{"private":true}}\' > package.json && '
                                                  f'corepack {pnpm} add --save-exact {" ".join(packages)}'], timeout=600)
                installed = ''
            except CommandFailed as error:
                installed = str(error)
            self.expect(not installed, f'{", ".join(packages)} installed with {pnpm} for the pinned image', installed)
            mounts.append(f'{volume}:/tools:ro')
        self.tool_paths = (mounts, playwright, noble)
        return self.tool_paths

    def browser(self, phase):
        mounts, module, noble = self.tools()
        # The page signs in with the same synthetic operator EOA; E2E_SESSION is the harness's own second client.
        env = {'E2E_URL': self.base, 'E2E_SESSION': self.session_cookie, 'E2E_USER': self.owner, 'E2E_PHASE': phase,
               'NOBLE_MODULE_DIR': noble, 'VESSEL_RELEASE_SYNTHETIC': '1',
               'E2E_CONTROL': f'http://127.0.0.1:{self.control_port}', 'E2E_OUT': '/artifacts', 'PLAYWRIGHT_MODULE': module,
               'E2E_IDS': json.dumps({'account': self.account, 'accountName': 'E2E Hyperliquid', 'address': self.address,
                                      'p1': self.p1, 'p2': self.p2, 'p3': getattr(self, 'p3', None), 'p1_target': self.p1_orders['target'],
                                      'p1_stop2': self.p1_orders['stop2'], 'p3_entry': getattr(self, 'p3_entry', None), **self.ids})}
        arguments = ['--network', 'host', '--ipc=host', '-v', f'{ROOT / "scripts" / "e2e"}:/e2e:ro',
                     '-v', f'{SIGNER}:/wallet-test-signer.cjs:ro', '-v', f'{self.artifacts}:/artifacts']
        for mount in mounts:
            arguments += ['-v', mount]
        for key in env:
            arguments += ['-e', key]
        arguments += [PLAYWRIGHT_IMAGE, 'node', '/e2e/browser-e2e.cjs']
        # Values travel in the client environment (-e KEY), never in argv.
        self.standalone_env = {**os.environ, **env}
        try:
            self.standalone(f'browser-{phase}', arguments, timeout=600)
            result = self.last_standalone
        except CommandFailed as error:
            result = self.last_standalone
            if result is None or 'timed out' in str(error):
                self.expect(False, f'browser phase {phase} completed', error)
        finally:
            self.standalone_env = None
        (self.artifacts / f'browser-{phase}.log').write_text(redact(result.stdout + result.stderr))
        for line in result.stdout.splitlines():
            match = re.match(r'(PASS|FAIL) \[browser\] (.*)', line)
            if match:
                self.expect(match.group(1) == 'PASS', match.group(2), soft=True)
        self.expect(result.returncode == 0, f'browser phase {phase} completed', (result.stderr or result.stdout)[-400:])

    # ---- driver --------------------------------------------------------------------------------------

    STATE = ('name', 'project', 'projects', 'owner', 'database_password', 'broker_password', 'app_password', 's3_access',
             's3_secret', 'https_port', 'redirect_port', 'broker_port', 'control_port', 'network', 'address', 'wallet_address',
             'account', 'portfolio', 'p1', 'p2', 'p3',
             'p4', 'p5', 'p1_orders', 'p2_entry', 'p2_exit', 'p3_entry', 'p4_orders', 'ids', 'head_cursor', 'mark_browser')

    def save_state(self):
        # Development aid for --keep runs: lets --resume continue a kept stack from a later step.
        state = {key: getattr(self, key) for key in self.STATE if hasattr(self, key)}
        (self.temp / 'e2e-state.json').write_text(json.dumps(state))
        (self.temp / 'e2e-state.json').chmod(0o600)

    @staticmethod
    def regular_json(path):
        if path.is_symlink() or not path.is_file():
            raise ResumeRefused(f'{path.name} is missing or not a regular file')
        try:
            value = json.loads(path.read_text())
        except (OSError, ValueError):
            raise ResumeRefused(f'{path.name} is unreadable') from None
        if not isinstance(value, dict):
            raise ResumeRefused(f'{path.name} is not an object')
        return value

    def validate_resume(self, directory):
        """The (directory, state) of a kept run of this harness, or ResumeRefused. Reads only; adopts nothing."""
        candidate = Path(directory)
        if candidate.is_symlink() or not candidate.is_dir():
            raise ResumeRefused('not a real directory (missing or a symlink)')
        candidate = candidate.resolve()
        if candidate.parent != Path(tempfile.gettempdir()).resolve() or not re.fullmatch(r'vessel-e2e-[A-Za-z0-9_]{8}', candidate.name):
            raise ResumeRefused('not a working directory created by this harness')
        marker = self.regular_json(candidate / MARKER)
        if marker.get('tool') != MARKER_TOOL or not RUN_NAME.fullmatch(str(marker.get('name'))):
            raise ResumeRefused('ownership marker does not belong to this harness')
        state = self.regular_json(candidate / 'e2e-state.json')
        name = marker['name']
        if state.get('name') != name or state.get('projects') != [name, name + '-restore'] or state.get('project') not in state['projects']:
            raise ResumeRefused('saved state does not match the ownership marker')
        missing = [key for key in ('owner', 'https_port', 'control_port', 'address', 'account') if key not in state]
        if missing:
            raise ResumeRefused('saved state is incomplete: ' + ', '.join(missing))
        projects = run('docker', 'compose', 'ls', '-a', '--format', 'json', capture_output=True, text=True, check=False)
        try:
            listed = {p['Name']: p.get('ConfigFiles', '') for p in json.loads(projects.stdout or '[]')}
        except ValueError:
            raise ResumeRefused('docker compose ls returned no project list') from None
        configs = listed.get(state['project'])
        if configs is None or str(candidate / 'compose.yaml') not in configs.split(','):
            raise ResumeRefused('the saved Compose project is not running from this directory')
        return candidate, state

    def load_state(self, directory):
        candidate, state = self.validate_resume(directory)
        # Validated: from here this run owns the directory and its two projects.
        for key, value in state.items():
            if key in self.STATE:
                setattr(self, key, value)
        self.register_secrets()
        self.temp = self.owned_temp = candidate
        self.owned_projects = list(state['projects'])
        # Sessions are never saved: sign in again with the synthetic operator wallet.
        self.step = '0-resume'
        self.sign_in()
        self.sse = SseReader(self, self.head())
        self.sse.start()

    def cleanup(self):
        if self.sse:
            self.sse.close()
        failures = []
        # Standalone containers never outlive the run, even with --keep: they hold the session and host networking.
        listed = run('docker', 'ps', '-aq', '--filter', f'label={RUN_LABEL}={self.name}', capture_output=True, text=True, check=False)
        for container in listed.stdout.split():
            if run('docker', 'rm', '-f', container, capture_output=True, check=False).returncode:
                failures.append('container ' + container)
        if self.args.keep:
            print(f'Kept: projects {", ".join(self.owned_projects)}, directory {self.owned_temp}, web {self.base} '
                  f'(sign in with synthetic wallet index 1), fake control http://127.0.0.1:{self.control_port}', flush=True)
        else:
            if self.owned_temp and self.temp == self.owned_temp:
                for project in self.owned_projects:
                    if self.compose('down', '-v', '--remove-orphans', project=project, capture_output=True, check=False).returncode:
                        failures.append('project ' + project)
                shutil.rmtree(self.owned_temp, ignore_errors=True)
                if self.owned_temp.exists():
                    failures.append('directory ' + str(self.owned_temp))
            if self.playwright_volume and run('docker', 'volume', 'rm', self.playwright_volume, capture_output=True,
                                              check=False).returncode:
                failures.append('volume ' + self.playwright_volume)
            if self.built:
                for image in IMAGES.values():
                    if run('docker', 'image', 'rm', image, capture_output=True, check=False).returncode:
                        failures.append('image ' + image)
        if failures:
            self.cleanup_failures = failures
            print('Cleanup failed for: ' + ', '.join(failures), flush=True)

    def report(self, steps, status):
        width = max(len(label) for _, label, _, _ in self.results) if self.results else 20
        print('\n' + '=' * (width + 24))
        print(f'{"Step":<18} {"Result":<6} Check')
        print('-' * (width + 24))
        for step, label, ok, detail in self.results:
            print(f'{step:<18} {"PASS" if ok else "FAIL":<6} {label}' + (f'\n{"":<25} {detail}' if detail else ''))
        print('-' * (width + 24))
        print(f'{"Step":<18} {"State":<8} {"Checks":>6} {"Failed":>6} {"Seconds":>8}')
        for name, _ in steps:
            state, seconds = status.get(name, ('not run', 0))
            rows = [r for r in self.results if r[0] == name]
            print(f'{name:<18} {state:<8} {len(rows):>6} {sum(not r[2] for r in rows):>6} {seconds:>8.0f}')
        passed = sum(r[2] for r in self.results)
        print(f'\nTotal: {passed} passed, {len(self.results) - passed} failed, runtime {time.monotonic() - self.started:.0f}s; '
              f'artifacts {self.artifacts}')

    def main(self):
        steps = [('1-deploy', self.step_deploy), ('2-account', self.step_account), ('3-link', self.step_link),
                 ('4-open', self.step_open), ('5-discrepancy', self.step_discrepancy), ('11a-browser-live', self.step_browser_live),
                 ('6-close', self.step_close), ('7-orphan-entry', self.step_orphan_entry), ('11b-browser-end', self.step_browser_ended),
                 ('7b-recover', self.step_recover), ('8-broker', self.step_broker), ('9a-crash', self.step_crash),
                 ('9b-disable', self.step_disable), ('10a-admin', self.step_admin), ('10b-restore', self.step_restore),
                 ('10c-streams', self.step_streams)]
        status, failed = {}, False
        resuming = bool(self.args.resume)
        if resuming:
            try:
                self.validate_resume(self.args.resume)
            except ResumeRefused as error:
                # Nothing was adopted, so nothing (directory, projects, volumes) is cleaned up.
                print(f'Refusing to resume {self.args.resume}: {error}. Nothing was changed or removed.', flush=True)
                return 2
        try:
            if resuming:
                self.load_state(self.args.resume)
            else:
                self.prepare()
            for name, step in steps:
                if resuming and not name.startswith(self.args.resume_from):
                    continue
                resuming = False
                self.step = name
                started = time.monotonic()
                aborted = False
                try:
                    step()
                    state = 'passed' if all(r[2] for r in self.results if r[0] == name) else 'failed'
                except Exception as error:  # noqa: BLE001 - every failure is reported in the table
                    if not isinstance(error, StepFailed) or not any(r[0] == name and not r[2] for r in self.results):
                        self.expect(False, 'step completed', f'{type(error).__name__}: {redact(error)}', soft=True)
                    state, aborted = 'failed', True
                status[name] = (state, time.monotonic() - started)
                if self.args.keep:
                    self.save_state()
                if state == 'failed':
                    failed = True
                    # A soft (recorded) failure leaves the scenario consistent; an aborted step does not.
                    if aborted and not self.args.continue_on_failure:
                        break
                if self.args.until and name.startswith(self.args.until):
                    break
        finally:
            if (failed or self.args.keep) and self.owned_temp:
                self.logs()
            self.cleanup()
            self.report(steps, status)
        return 1 if failed or getattr(self, 'cleanup_failures', None) or not all(r[2] for r in self.results) else 0

    def logs(self):
        try:
            logs = self.compose('logs', '--no-color', 'api', 'worker', 'worker-notifications', 'verifier', 'fake-venue', capture_output=True, text=True, check=False)
            (self.artifacts / 'compose.log').write_text(redact(logs.stdout + logs.stderr))
        except (OSError, CommandFailed):
            pass


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--keep', action='store_true', help='Keep the stack, volumes, temp directory and images afterwards.')
    parser.add_argument('--skip-build', action='store_true', help=f'Reuse existing {IMAGES["api"]} and {IMAGES["web"]} images.')
    parser.add_argument('--until', help='Stop after the step whose name starts with this prefix (development aid).')
    parser.add_argument('--resume', metavar='DIR', help='Development aid: continue a --keep run from its temp directory.')
    parser.add_argument('--resume-from', metavar='STEP', default='', help='With --resume: the first step to run (name prefix).')
    parser.add_argument('--continue-on-failure', action='store_true', help='Run later steps after a failed one.')
    parser.add_argument('--artifacts', help='Directory for screenshots and logs (default: a new temp directory).')
    parser.add_argument('--playwright-module', help='Optional host path of a playwright-core 1.63 package. By default it is installed '
                        'with the pinned pnpm into a temporary Docker volume; the browser always runs in the pinned Playwright image.')
    sys.exit(E2E(parser.parse_args()).main())
