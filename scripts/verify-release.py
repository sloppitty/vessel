#!/usr/bin/env python3
"""Local-only release checks. Own temp files/projects/images; never load repository .env.

Sign-in is by wallet only. The check runs over the Caddy TLS overlay, plus one pass of the default plain-HTTP web
behind an operator's own HTTPS proxy. The synthetic operator EOA is signed by scripts/wallet-test-signer.cjs, whose
@noble dependencies come from an external NOBLE_MODULE_DIR."""
import argparse
import base64
import gzip
import http.client
from http.cookies import SimpleCookie
import json
import os
from pathlib import Path
import re
import secrets
import select
import shlex
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import uuid
from dev import verification_environment

ROOT = Path(__file__).resolve().parent.parent

# A local transport fixture, not a venue or production fallback. It reports only
# synthetic headers and emits two delayed SSE frames to catch nginx buffering.
FIXTURE = r"""
const http = require('http'), zlib = require('zlib');
http.createServer((req, res) => {
  if (req.url === '/api/stream') {
    res.writeHead(200, {'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store'});
    res.write('event: first\ndata: 1\n\n');
    setTimeout(() => res.end('event: second\ndata: 2\n\n'), 800);
  } else if (req.url === '/api/compressed') {
    res.writeHead(200, {'Content-Type': 'application/json', 'Content-Encoding': 'gzip'});
    res.end(zlib.gzipSync(JSON.stringify({backend: true})));
  } else {
    res.writeHead(200, {'Content-Type': 'application/json'});
    res.end(JSON.stringify({peer: req.socket.remoteAddress, headers: req.headers}));
  }
}).listen(8080, '0.0.0.0');
"""


def run(*args, **kwargs):
    return subprocess.run(args, check=kwargs.pop('check', True), **kwargs)


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class Check:
    def __init__(self, args):
        self.args = args
        self.temp = Path(tempfile.mkdtemp(prefix='vessel-release-check-'))
        self.name = 'vessel-check-' + secrets.token_hex(5)
        self.images = {kind: f'{self.name}:{kind}' for kind in ('api', 'web')}
        self.s3_access, self.s3_secret = secrets.token_hex(16), secrets.token_hex(32)
        self.database_password = secrets.token_hex(24)
        self.broker_password = secrets.token_hex(24)
        self.owner = str(uuid.uuid4())
        self.http_port, self.https_port, self.redirect_port = port(), port(), port()
        self.broker_port = port()
        self.projects = [self.name, self.name + '-restore']
        self.counts = {}
        self.tls = True
        self.fixture = False
        self.session_cookie = None
        self.session = None
        self.browser_container = None

    def prepare(self):
        for file in ('compose.yaml', 'compose.tls.yaml', 'compose.credentials.yaml', 'compose.storage-local.yaml', 'Caddyfile'):
            shutil.copyfile(ROOT / 'deploy' / file, self.temp / file)
        # Pick an unused subnet, not the development or deployment defaults.
        networks = [json.loads(line) for line in run('docker', 'network', 'ls', '--format', 'json', capture_output=True, text=True).stdout.splitlines()]
        occupied = run('docker', 'network', 'inspect', *[n['ID'] for n in networks], capture_output=True, text=True).stdout if networks else ''
        import ipaddress
        subnets = [ipaddress.ip_network(c['Subnet']) for n in json.loads(occupied or '[]') for c in (n['IPAM'].get('Config') or []) if c.get('Subnet')]
        for octet in range(100, 250):
            subnet = ipaddress.ip_network(f'172.29.{octet}.0/24')
            if not any(subnet.version == other.version and subnet.overlaps(other) for other in subnets):
                break
        else:
            raise RuntimeError('No free verification subnet.')
        self.network = f'172.29.{octet}'
        self.write_env(self.network)
        (self.temp / 'local.yaml').write_text(f'''services:
  api:
    image: {self.images['api']}
    pull_policy: never
  migrate:
    image: {self.images['api']}
    pull_policy: never
    environment:
      # Release checks only: also provision the verification subscription and its deployment.
      Vessel__Messaging__Verification: "true"
  worker:
    image: {self.images['api']}
    pull_policy: never
  worker-notifications:
    image: {self.images['api']}
    pull_policy: never
  # Runs only the verification subscription, whose receipts prove delivery and catch-up.
  verifier:
    image: {self.images['api']}
    pull_policy: never
    entrypoint: ["/app/worker"]
    environment:
      DOTNET_ENVIRONMENT: Production
      Vessel__Worker__Modules__0: verification
      Vessel__Worker__Deployment: verification
      Vessel__Messaging__Verification: "true"
      ConnectionStrings__Vessel: "Host=postgres;Database=vessel;Username=vessel;Password=${{POSTGRES_PASSWORD}}"
      # Every worker reads the identity owner directory, so it needs the same sign-in settings as the API.
      Vessel__Auth__OwnerId: ${{Vessel__Auth__OwnerId}}
      Vessel__Identity__Origins__0: ${{VESSEL_IDENTITY_ORIGIN}}
      Vessel__Messaging__RabbitMqUri: "amqp://${{RABBITMQ_USER}}:${{RABBITMQ_PASSWORD}}@rabbitmq:5672/"
    depends_on:
      migrate:
        condition: service_completed_successfully
    # The heartbeat file belongs to the monitor module, so this host reports only that its process is alive.
    # `compose up --wait` rejects a service without a healthcheck, so it cannot simply be disabled.
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
    image: {self.images['web']}
    pull_policy: never
  fixture:
    image: node:24-bookworm-slim
    entrypoint: [node, /fixture.cjs]
    volumes: ["./fixture.cjs:/fixture.cjs:ro"]
''')
        (self.temp / 'fixture.cjs').write_text(FIXTURE)
        (self.temp / 'fixture.yaml').write_text('services:\n  web:\n    environment:\n      VESSEL_API_UPSTREAM: http://fixture:8080\n')
        self.key = self.temp / 'secrets' / 'ring.json'
        self.key.parent.mkdir(mode=0o700)
        self.key.write_text(json.dumps({'ActiveKeyId': 'test', 'Keys': {'test': base64.b64encode(secrets.token_bytes(32)).decode()}}))
        self.key.chmod(0o600)
        # Rootful Docker permissions, without requiring the invoking user to be root.
        run('docker', 'run', '--rm', '--user', '0', '-v', f'{self.key.parent}:/keys', 'node:24-bookworm-slim', 'chown', '1654:1654', '/keys/ring.json', stdout=subprocess.DEVNULL)

    def write_env(self, network):
        (self.temp / '.env').write_text(f'''POSTGRES_PASSWORD={self.database_password}
VESSEL_IDENTITY_REGISTRATION=Invitation
Vessel__Auth__OwnerId={self.owner}
VESSEL_S3_ACCESS_KEY_ID={self.s3_access}
VESSEL_S3_SECRET_ACCESS_KEY={self.s3_secret}
RABBITMQ_USER=vessel
RABBITMQ_PASSWORD={self.broker_password}
VESSEL_RABBITMQ_MANAGEMENT_BIND=127.0.0.1:{self.broker_port}
VESSEL_WEB_BIND=127.0.0.1:{self.http_port}
VESSEL_NETWORK_SUBNET={network}.0/24
VESSEL_NETWORK_GATEWAY={network}.1
VESSEL_WEB_IP={network}.10
VESSEL_TLS_PROXY_IP={network}.20
VESSEL_TLS_HOST=localhost
VESSEL_TLS_HTTP_BIND=127.0.0.1:{self.redirect_port}
VESSEL_TLS_HTTPS_BIND=127.0.0.1:{self.https_port}
VESSEL_TLS_DIRECTIVE=tls internal
VESSEL_IDENTITY_ORIGIN=https://localhost:{self.https_port}
VESSEL_CREDENTIAL_KEYS_FILE={self.temp}/secrets/ring.json
''')
        (self.temp / '.env').chmod(0o600)

    def compose(self, *args, project=None, **kwargs):
        files = ['compose.yaml', 'local.yaml', 'compose.credentials.yaml']
        if self.args.storage == 'local':
            files.append('compose.storage-local.yaml')
        # Wallet sign-in needs HTTPS: the Caddy overlay, except for the pass that stands for the default plain-HTTP
        # web behind an operator's own HTTPS proxy.
        if self.tls:
            files.append('compose.tls.yaml')
        if self.fixture:
            files.append('fixture.yaml')
        cmd = ['docker', 'compose', '--project-directory', str(self.temp), '-p', project or self.name]
        for file in files:
            cmd += ['-f', str(self.temp / file)]
        # The private synthetic .env must win over the caller's deployment/S3 settings.
        return run(*cmd, *args, env=verification_environment(), **kwargs)

    def expect(self, condition, label, group=None):
        if not condition:
            raise AssertionError(label)
        group = group or ('TLS' if self.tls else 'HTTP')
        self.counts[group] = self.counts.get(group, 0) + 1
        print(f'PASS [{group}] {label}', flush=True)

    @property
    def url(self):
        return f'https://localhost:{self.https_port}' if self.tls else f'http://127.0.0.1:{self.http_port}'

    @property
    def origin(self):
        """The public HTTPS origin a browser sends, also when web itself is reached over plain HTTP behind a proxy."""
        return f'https://localhost:{self.https_port}'

    def connect(self):
        if self.tls:
            return http.client.HTTPSConnection('localhost', self.https_port, context=ssl._create_unverified_context(), timeout=15)
        return http.client.HTTPConnection('127.0.0.1', self.http_port, timeout=15)

    def request(self, path, method='GET', body=None, headers=None, auth=True):
        headers = {**self.auth_headers(auth), **(headers or {})}
        headers['Host'] = f'localhost:{self.https_port if self.tls else self.http_port}'
        if isinstance(body, dict):
            headers['Content-Type'] = 'application/json'
            body = json.dumps(body).encode()
        conn = self.connect()
        try:
            conn.request(method, path, body, headers)
            response = conn.getresponse()
            return response.status, response.getheaders(), response.read()
        finally:
            conn.close()

    def auth_headers(self, auth=True):
        headers = {'Origin': self.origin, 'Sec-Fetch-Site': 'same-origin', 'X-Vessel-Request': '1'}
        if auth and self.session_cookie:
            headers.update({'Cookie': self.session_cookie, 'X-Vessel-User': self.owner})
        return headers

    def identity(self, *args, project=None):
        return self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate',
                            '/app/Vessel.Api.dll', 'identity', *args, project=project,
                            capture_output=True, text=True)

    def wallet_signature(self, message=None):
        result = run('node', str(ROOT / 'scripts/wallet-test-signer.cjs'),
                     input=json.dumps({'wallet': 1, 'message': message}),
                     env={**os.environ, 'VESSEL_RELEASE_SYNTHETIC': '1'}, capture_output=True, text=True)
        return json.loads(result.stdout)

    def wallet_sign_in(self):
        address = self.wallet_signature()['address']
        status, headers, data = self.request('/api/auth/challenge', 'POST', {'address': address}, auth=False)
        self.expect(status == 200, 'real API issues an EOA sign-in challenge', 'wallet')
        binding = self.cookie(headers, '__Host-vessel-signin')
        challenge = json.loads(data)
        signature = self.wallet_signature(challenge['message'])['signature']
        status, headers, data = self.request('/api/auth/verify', 'POST',
            {'challengeId': challenge['challengeId'], 'signature': signature, 'remember': False},
            {'Cookie': binding}, auth=False)
        self.expect(status == 200 and json.loads(data)['status'] == 'signed-in',
                    'external noble EOA signature signs in through the API', 'wallet')
        self.session_cookie = self.cookie(headers, '__Host-vessel-session')
        self.session = json.loads(data)['session']
        self.expect(self.session['user']['id'] == self.owner, 'operator preserves the journal owner GUID', 'wallet')

    def cookie(self, headers, name):
        values = [value for key, value in headers if key.lower() == 'set-cookie' and value.startswith(name + '=')]
        self.expect(len(values) == 1, name + ' issued exactly once', 'wallet')
        jar = SimpleCookie()
        jar.load(values[0])
        cookie = jar[name]
        self.expect(bool(cookie['secure']) and bool(cookie['httponly']) and
                    cookie['samesite'].lower() == 'strict' and cookie['path'] == '/' and not cookie['domain'],
                    name + ' is Secure, HttpOnly, SameSite=Strict, host-only, Path=/', 'wallet')
        return name + '=' + cookie.value

    def wallet_bootstrap(self):
        self.compose('up', '-d', '--wait', 'postgres', 'rabbitmq', stdout=subprocess.DEVNULL)
        self.compose('run', '--rm', 'migrate', stdout=subprocess.DEVNULL)
        self.compose('up', '-d', 'api', stdout=subprocess.DEVNULL)
        for _ in range(60):
            result = self.compose('exec', '-T', 'api', 'curl', '--silent', 'http://localhost:8080/health/ready',
                                  capture_output=True, text=True, check=False)
            if 'no-operator-wallet' in result.stdout:
                break
            time.sleep(1)
        self.expect('no-operator-wallet' in result.stdout,
                    'migrate ensures owner; readiness blocks until an operator wallet is bound', 'wallet')
        self.identity('bind-wallet', '--user', self.owner, '--address', self.wallet_signature()['address'])

    def oversize_upload(self, path, length):
        # Send only while no answer is waiting. The proxy answers once the limit is passed and closes; writing on
        # after a TLS peer closed raises SSLEOFError with OpenSSL 3.0 and loses that answer.
        port = self.https_port if self.tls else self.http_port
        raw = socket.create_connection(('localhost' if self.tls else '127.0.0.1', port), timeout=15)
        if self.tls:
            raw = ssl._create_unverified_context().wrap_socket(raw, server_hostname='localhost')
        session = ''.join(f'{name}: {value}\r\n' for name, value in self.auth_headers().items())
        with raw:
            raw.sendall(f'POST {path} HTTP/1.1\r\nHost: localhost:{port}\r\n{session}'
                        f'Content-Type: application/octet-stream\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n'.encode())
            raw.setblocking(False)
            chunk, sent, data, deadline = b'x' * 16384, 0, b'', time.monotonic() + 60
            while b'\r\n\r\n' not in data:
                if self.tls and raw.pending():
                    readable, writable = [raw], []
                else:
                    readable, writable, _ = select.select([raw], [raw] if sent < length else [], [], max(deadline - time.monotonic(), 0))
                if not readable and not writable:
                    raise TimeoutError(f'{path} did not answer an oversize upload')
                if readable:
                    try:
                        part = raw.recv(65536)
                    except (ssl.SSLWantReadError, BlockingIOError):
                        continue  # TLS 1.3 session tickets, not an answer
                    if not part:
                        break
                    data += part
                    continue
                try:
                    sent += raw.send(chunk[:length - sent])
                except (ssl.SSLWantWriteError, ssl.SSLWantReadError, BlockingIOError):
                    continue
                except (BrokenPipeError, ConnectionResetError, ssl.SSLEOFError):
                    sent = length  # Closed early; read whatever answer arrived.
        head = data.split(b'\r\n\r\n', 1)[0].decode('latin-1').split('\r\n')
        status = int(head[0].split()[1]) if head and len(head[0].split()) > 1 else 0
        return status, [tuple(part.strip() for part in line.split(':', 1)) for line in head[1:] if ':' in line]

    def malformed_chunked(self, path):
        raw = socket.create_connection(('localhost' if self.tls else '127.0.0.1', self.https_port if self.tls else self.http_port), timeout=15)
        if self.tls:
            raw = ssl._create_unverified_context().wrap_socket(raw, server_hostname='localhost')
        with raw:
            host = f'localhost:{self.https_port if self.tls else self.http_port}'
            raw.sendall(f'POST {path} HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\nzz\r\n'.encode())
            data = b''
            while chunk := raw.recv(65536):
                data += chunk
        head = data.split(b'\r\n\r\n', 1)[0].decode('latin-1').split('\r\n')
        status = int(head[0].split()[1]) if head and len(head[0].split()) > 1 else 0
        return status, [tuple(part.strip() for part in line.split(':', 1)) for line in head[1:] if ':' in line]

    def json(self, path, body=None, method='GET'):
        status, _, data = self.request(path, method, body)
        if status not in (200, 201):
            raise AssertionError(f'{method} {path} returned {status}')
        return json.loads(data)

    def ready(self):
        for _ in range(90):
            try:
                if self.request('/health/ready')[0] == 200:
                    return
            except (OSError, http.client.HTTPException):
                pass
            time.sleep(1)
        raise AssertionError('Isolated deployment did not become ready.')

    def monitor(self):
        """Exercise the packaged worker and event API without contacting a venue."""
        for _ in range(60):
            summary = self.json('/api/monitor')
            if summary['workerOnline']:
                break
            time.sleep(1)
        self.expect(summary['workerOnline'], 'packaged worker publishes its heartbeat', 'monitor')
        for path in ('/api/monitor', '/api/events', '/api/events/stream'):
            self.expect(self.request(path, auth=False)[0] == 401,
                        'monitor route requires authentication: ' + path, 'monitor')
        foreign = str(uuid.uuid4())
        self.expect(self.request('/api/accounts/' + foreign + '/monitor')[0] == 404,
                    'unknown account monitor is hidden', 'monitor')
        self.expect(self.request('/api/accounts/' + self.account + '/monitor/run', 'POST')[0] == 409,
                    'manual account cannot start venue monitoring', 'monitor')
        self.expect(self.request('/api/events?after=not-a-cursor')[0] == 400,
                    'invalid event cursor is rejected', 'monitor')
        before = self.json('/api/events?limit=200')
        self.expect(isinstance(before['nextCursor'], str) and re.fullmatch(r'0|[0-9a-f]{32}\.(0|[1-9][0-9]*)', before['nextCursor']) is not None,
                    'event cursor is an opaque {epoch}.{sequence} string', 'monitor')
        self.expect(all(set(event['subject']) == {'accountId', 'venueId', 'contractId', 'playId', 'entryId', 'levelId', 'discrepancyId'}
                        for event in before['events']), 'event subjects keep explicit nulls', 'monitor')
        play = self.json('/api/plays/' + self.play)
        request = {'expectedVersion': play['summary']['version'], 'status': 'planned'}
        planned = self.json('/api/plays/' + self.play + '/status', request, 'POST')
        self.expect(planned['summary']['status'] == 'planned',
                    'owner action persists with event emission enabled', 'monitor')
        after = self.json('/api/events?after=' + before['nextCursor'] + '&limit=200')
        events = [event for event in after['events']
                  if event['subject']['playId'] == self.play and event['type'] == 'play.status-changed']
        self.expect(len(events) == 1, 'one saved status change produces one event', 'monitor')
        self.expect(self.request('/api/plays/' + self.play + '/status', 'POST', request)[0] == 409,
                    'stale owner mutation is rejected', 'monitor')
        again = self.json('/api/events?after=' + before['nextCursor'] + '&limit=200')
        self.expect([event['id'] for event in again['events']
                     if event['subject']['playId'] == self.play] == [event['id'] for event in events],
                    'rejected retry does not duplicate events', 'monitor')
        status, headers, _ = self.request('/api/events')
        self.expect(status == 200 and 'no-store' in dict((key.lower(), value) for key, value in headers).get('cache-control', ''),
                    'event history is not cacheable', 'monitor')
        self.monitor_event_id = events[0]['id']
        self.event_stream(before['nextCursor'], self.monitor_event_id)
        self.wait_event_delivery(self.monitor_event_id)
        self.compose('stop', 'rabbitmq', stdout=subprocess.DEVNULL)
        event_id = self.change_status_event('paused')
        self.expect(self.event_pending(event_id), 'broker outage leaves a durable pending publication in the outbox', 'monitor')
        self.compose('up', '-d', '--wait', 'rabbitmq', stdout=subprocess.DEVNULL)
        self.wait_event_delivery(event_id)
        self.wait_outbox_drained()
        self.expect(self.json('/api/plays/' + self.play)['summary']['status'] == 'paused',
                    'broker recovery does not replay the owner mutation', 'monitor')

    def change_status_event(self, status):
        before = self.json('/api/events?limit=200')
        play = self.json('/api/plays/' + self.play)
        self.json('/api/plays/' + self.play + '/status',
                  {'expectedVersion': play['summary']['version'], 'status': status}, 'POST')
        after = self.json('/api/events?after=' + before['nextCursor'] + '&limit=200')
        events = [event for event in after['events']
                  if event['subject']['playId'] == self.play and event['type'] == 'play.status-changed']
        self.expect(len(events) == 1, 'status event commits while broker is unavailable', 'monitor')
        return events[0]['id']

    def event_stream(self, after, expected_id):
        connection = self.connect()
        try:
            connection.request('GET', '/api/events/stream?after=' + after,
                               headers=self.auth_headers())
            response = connection.getresponse()
            self.expect(response.status == 200 and
                        response.getheader('Content-Type', '').startswith('text/event-stream'),
                        'authenticated event stream passes through nginx', 'monitor')
            received, resume = [], None
            for _ in range(40):
                line = response.readline().decode('utf-8').strip()
                if line.startswith('id: '):
                    resume = line[4:]
                if line.startswith('data: '):
                    received.append(json.loads(line[6:]))
                    if received[-1].get('id') == expected_id:
                        break
            self.expect(any(item.get('id') == expected_id for item in received),
                        'SSE catch-up delivers the committed event without a venue read', 'monitor')
            # A fresh owner starts from cursor 0; once events exist every resume cursor names the stream epoch.
            self.expect(resume is not None and re.fullmatch(r'[0-9a-f]{32}\.[1-9][0-9]*', resume) is not None and
                        (after == '0' or resume.split('.')[0] == after.split('.')[0]),
                        'SSE event frames carry their resume cursor in the stream epoch', 'monitor')
        finally:
            connection.close()

    def sql(self, query, project=None):
        # Only this check's generated Compose project/database; never a supplied DSN.
        result = self.compose('exec', '-T', 'postgres', 'psql', '-U', 'vessel', '-d', 'vessel', '-At', '-c', query,
                              project=project, capture_output=True, text=True)
        return result.stdout.strip()

    def outbox_count(self, project=None):
        # The API publishes through its own Wolverine envelope schema; a broker outage leaves envelopes here.
        return int(self.sql('SELECT count(*) FROM wolverine_api.wolverine_outgoing_envelopes', project) or '0')

    def event_pending(self, event_id, project=None):
        # The committed event is the record; its publication waits in the outbox until the broker returns.
        # The verification subscription may still catch up from the database on its own, which is the design.
        return self.outbox_count(project) > 0 and self.event_delivery_state(event_id, project) != 'failed'

    def event_delivery_state(self, event_id, project=None):
        event_id = str(uuid.UUID(event_id))
        return self.sql("SELECT state FROM event_receipts WHERE owner_id = '" + self.owner + "' AND event_id = '" + event_id +
                        "' AND subscription = 'verification'", project)

    def wait_event_delivery(self, event_id, project=None):
        for _ in range(120):
            if self.event_delivery_state(event_id, project) == 'delivered':
                self.expect(True, 'verification subscription settled event ' + event_id + ' as delivered', 'monitor')
                return
            time.sleep(1)
        raise AssertionError('Durable event delivery did not recover: ' + event_id)

    def notifications(self):
        """A test notification travels API, event, broker or catch-up, hub, feed and stream, even through a broker outage."""
        for path in ('/api/notifications', '/api/notifications/head', '/api/notifications/preferences'):
            self.expect(self.request(path, auth=False)[0] == 401, 'notification route requires authentication: ' + path, 'notifications')
        status, headers, _ = self.request('/api/notifications/head')
        self.expect(status == 200 and 'no-store' in dict((key.lower(), value) for key, value in headers).get('cache-control', ''),
                    'notification head is not cacheable', 'notifications')
        head = self.json('/api/notifications/head')
        self.expect(re.fullmatch(r'(0|[0-9a-f]{32})\.[0-9]+\.[0-9]+', head['nextCursor']) is not None,
                    'notification cursor is an opaque {epoch}.{reset}.{sequence} string', 'notifications')
        connection = self.connect()
        try:
            connection.request('GET', '/api/events/stream?after=0&notificationsAfter=' + head['nextCursor'],
                               headers=self.auth_headers())
            response = connection.getresponse()
            self.expect(self.request('/api/notifications/test', 'POST')[0] == 202, 'test notification requested', 'notifications')
            self.expect(self.request('/api/notifications/test', 'POST')[0] == 429, 'test notification cooldown refuses a repeat', 'notifications')
            frame, received = None, None
            for _ in range(400):
                line = response.readline().decode('utf-8').strip()
                if line.startswith('event: '):
                    frame = line[7:]
                elif line.startswith('data: ') and frame == 'notification':
                    received = json.loads(line[6:])
                    break
            self.expect(received is not None and received['notification']['kind'] == 'test' and received['notification']['state'] == 'unread',
                        'the stream delivers the test notification frame', 'notifications')
        finally:
            connection.close()
        claim = self.json('/api/notifications/claim', None, 'POST')
        self.expect(len(claim['popups']) == 1 and claim['popups'][0]['content']['title'] == 'Test notification',
                    'one claim wins the popup', 'notifications')
        self.expect(self.json('/api/notifications/claim', None, 'POST')['popups'] == [], 'a second claim wins nothing', 'notifications')
        read = self.json('/api/notifications/' + claim['popups'][0]['id'] + '/read', None, 'POST')
        self.expect(read['state'] == 'read' and self.json('/api/notifications/head')['unreadCount'] == 0,
                    'reading clears the unread count', 'notifications')
        # Broker outage: the event commits; the hub's catch-up reads it from PostgreSQL.
        time.sleep(11)
        self.compose('stop', 'rabbitmq', stdout=subprocess.DEVNULL)
        self.expect(self.request('/api/notifications/test', 'POST')[0] == 202, 'test request commits while the broker is unavailable', 'notifications')
        self.wait_notifications(2)
        self.compose('up', '-d', '--wait', 'rabbitmq', stdout=subprocess.DEVNULL)
        self.notification_count = 2

    def wait_notifications(self, count, project=None):
        for _ in range(120):
            if self.json('/api/notifications?tab=all')['allTotal'] >= count:
                self.expect(True, 'notifications reached ' + str(count), 'notifications')
                return
            time.sleep(1)
        raise AssertionError('The Notification Hub did not deliver ' + str(count) + ' notifications.')

    def wait_outbox_drained(self, project=None):
        for _ in range(120):
            if self.outbox_count(project) == 0:
                self.expect(True, 'the outbox drains once the broker is reachable', 'monitor')
                return
            time.sleep(1)
        raise AssertionError('The event outbox did not drain after the broker returned.')

    def private_logs(self, project=None):
        logs = self.compose('logs', '--no-color', 'api', 'worker', 'worker-notifications', 'verifier', 'migrate',
                            project=project, capture_output=True, text=True).stdout
        generated = [self.database_password, self.broker_password, self.s3_secret]
        if self.session_cookie:
            generated.append(self.session_cookie.split('=', 1)[1])
        for secret in generated:
            self.expect(secret not in logs, 'application logs omit a synthetic secret', 'security')

    def matrix(self):
        status, _, _ = self.request('/api/accounts/00000000-0000-0000-0000-000000000001/credential', 'PUT', {}, {'X-Forwarded-Proto': 'https', 'X-Forwarded-For': '198.51.100.77', 'Forwarded': 'proto=https'})
        self.expect(status == (400 if self.tls else 403), 'credential HTTPS guard cannot be bypassed on HTTP; TLS reaches validation')
        # Regression for the removed Token sign-in: no bearer value authenticates anyone.
        self.expect(self.request('/api/system', auth=False, headers={'Authorization': 'Bearer ' + secrets.token_hex(32)})[0] == 401,
                    'the API never accepts a bearer token', 'wallet')
        self.expect(self.request('/api/portfolios', 'POST', {'name': 'Rejected'}, {'Origin': 'https://evil.invalid'})[0] == 403,
                    'guarded write rejects an untrusted origin', 'wallet')
        self.expect(self.request('/api/portfolios', 'POST', {'name': 'Rejected'}, {'X-Vessel-User': str(uuid.uuid4())})[0] == 401,
                    'guarded write rejects a stale user header', 'wallet')
        for path in ('/api/accounts', '/health/ready'):
            status, headers, _ = self.request(path, headers={'Accept-Encoding': 'gzip'})
            self.expect(status == 200 and not any(k.lower() == 'content-encoding' for k, _ in headers), path + ' not gzip')
            for name in ('X-Content-Type-Options', 'X-Frame-Options', 'Referrer-Policy'):
                self.expect(sum(k.lower() == name.lower() for k, _ in headers) == 1, path + ' has exactly one ' + name)
        _, _, html = self.request('/')
        asset = re.search(rb'src="(/assets/[^\"]+\.js)"', html).group(1).decode()
        for path, expected in (('/', 200), ('/plays/deep-link', 200), (asset, 200), ('/assets/missing.js', 404)):
            status, headers, _ = self.request(path)
            self.expect(status == expected, path + ' status')
            for name, value in (('X-Content-Type-Options', 'nosniff'), ('X-Frame-Options', 'DENY'), ('Referrer-Policy', 'no-referrer')):
                self.expect([v for k, v in headers if k.lower() == name.lower()] == [value], path + ' ' + name)
            self.expect(sum(k.lower() == 'content-security-policy' for k, _ in headers) == 1, path + ' CSP')
        _, headers, data = self.request(asset, headers={'Accept-Encoding': 'gzip'})
        self.expect(any(k.lower() == 'content-encoding' and v == 'gzip' for k, v in headers) and len(gzip.decompress(data)) > len(data), 'static JS gzip')
        status, headers = self.oversize_upload('/api/plays/00000000-0000-0000-0000-000000000001/evidence', 10551296 + 1)
        self.expect(status == 413, 'upload body limit is enforced by nginx')
        self.expect(any(k.lower() == 'x-content-type-options' and v == 'nosniff' for k, v in headers) and any(k.lower() == 'content-security-policy' for k, _ in headers), 'nginx upload error has security headers')
        for path in ('/api/test', '/health/ready'):
            status, headers = self.malformed_chunked(path)
            self.expect((status >= 400 if self.tls else status == 400) and any(k.lower() == 'content-security-policy' for k, _ in headers) and any(k.lower() == 'x-content-type-options' and v == 'nosniff' for k, v in headers), path + f' proxy-generated error has security headers (got {status} {[k for k, _ in headers]})')
        boundary = 'vessel-release-limit'
        image = self.png + b'x' * (10 * 1024 * 1024 + 1 - len(self.png))
        body = f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="large.png"\r\nContent-Type: image/png\r\n\r\n'.encode() + image + f'\r\n--{boundary}--\r\n'.encode()
        status, _, _ = self.request('/api/plays/' + self.play + '/evidence', 'POST', body, {'Content-Type': 'multipart/form-data; boundary=' + boundary})
        self.expect(status == 413, 'API rejects image above 10 MiB within nginx multipart allowance')
        if self.tls:
            web_id = self.compose('ps', '-q', 'web', capture_output=True, text=True).stdout.strip()
            published = json.loads(run('docker', 'inspect', '--format', '{{json .HostConfig.PortBindings}}', web_id, capture_output=True, text=True).stdout)
            self.expect(not published, 'TLS web has no host port')
            try:
                http.client.HTTPConnection('127.0.0.1', self.http_port, timeout=1).request('GET', '/')
            except OSError:
                self.expect(True, 'old direct web port is closed')
            else:
                raise AssertionError('TLS web remains directly reachable')

    def ports(self, service):
        container = self.compose('ps', '-q', service, capture_output=True, text=True).stdout.strip()
        return json.loads(run('docker', 'inspect', '--format', '{{json .HostConfig.PortBindings}}', container, capture_output=True, text=True).stdout) or {}

    def worker(self):
        # The worker ships in the API image with its own entrypoint and a heartbeat healthcheck.
        container = self.compose('ps', '-q', 'worker', capture_output=True, text=True).stdout.strip()
        inspected = json.loads(run('docker', 'inspect', container, capture_output=True, text=True).stdout)[0]
        self.expect(inspected['Config']['Entrypoint'] == ['/app/worker'], 'worker uses the /app/worker entrypoint', 'worker')
        self.expect(inspected['Config']['Healthcheck']['Test'] == ['CMD', '/app/worker-health'], 'worker healthcheck reads its heartbeat', 'worker')
        self.expect(inspected['State']['Health']['Status'] == 'healthy', 'worker is healthy after a productive pass', 'worker')
        self.expect(inspected['Config']['User'] == 'app', 'worker runs as the non-root app user', 'worker')
        self.expect(not self.ports('worker'), 'worker publishes no port', 'worker')
        broker = self.ports('rabbitmq')
        self.expect('5672/tcp' not in broker, 'RabbitMQ AMQP is not published', 'worker')
        self.expect([b['HostIp'] for b in broker.get('15672/tcp', [])] == ['127.0.0.1'], 'RabbitMQ management is bound to loopback only', 'worker')
        for _ in range(60):
            if self.json('/api/monitor')['workerOnline']:
                break
            time.sleep(1)
        self.expect(self.json('/api/monitor')['workerOnline'], 'API reports the worker heartbeat online', 'worker')
        # The worker refuses a module set its deployment schema was not provisioned for.
        wrong = self.compose('run', '--rm', '--no-deps', '-e', 'Vessel__Worker__Modules__0=verification',
                             '-e', 'Vessel__Worker__Deployment=monitor', 'worker', check=False, capture_output=True, text=True)
        self.expect(wrong.returncode != 0 and 'Password' not in wrong.stderr + wrong.stdout, 'worker rejects an unprovisioned module set without printing secrets', 'worker')
        unknown = self.compose('run', '--rm', '--no-deps', '-e', 'Vessel__Worker__Modules__0=nonexistent', 'worker',
                               check=False, capture_output=True, text=True)
        self.expect(unknown.returncode != 0, 'worker rejects an unknown module', 'worker')
        # The Notification Hub runs as its own deployment, with its own heartbeat file and row.
        container = self.compose('ps', '-q', 'worker-notifications', capture_output=True, text=True).stdout.strip()
        inspected = json.loads(run('docker', 'inspect', container, capture_output=True, text=True).stdout)[0]
        self.expect(inspected['Config']['Entrypoint'] == ['/app/worker'], 'notifications worker uses the /app/worker entrypoint', 'notifications')
        self.expect(inspected['Config']['Healthcheck']['Test'] == ['CMD', '/app/worker-health'],
                    'notifications worker uses the module-aware heartbeat healthcheck', 'notifications')
        self.expect(inspected['State']['Health']['Status'] == 'healthy', 'notifications worker is healthy after a productive maintenance pass', 'notifications')
        environment = dict(value.split('=', 1) for value in inspected['Config']['Env'])
        self.expect('Vessel__Auth__Mode' not in environment and 'Vessel__Auth__Token' not in environment,
                    'notifications worker receives no setting of the removed Token sign-in', 'notifications')
        self.expect(environment['Vessel__Identity__Origins__0'] == self.url and environment['Vessel__Auth__OwnerId'] == self.owner and
                    environment['Vessel__Identity__Registration'] == 'Invitation',
                    'notifications worker shares the API sign-in settings for the identity owner directory', 'wallet')
        self.expect(inspected['Config']['User'] == 'app', 'notifications worker runs as the non-root app user', 'notifications')
        self.expect(not self.ports('worker-notifications'), 'notifications worker publishes no port', 'notifications')
        mounts = [mount.get('Destination', '') for mount in inspected.get('Mounts', [])]
        self.expect(not any('vessel-credential-keys' in mount for mount in mounts), 'notifications worker has no credential key ring', 'notifications')
        for _ in range(60):
            if self.json('/api/notifications/head')['workerOnline']:
                break
            time.sleep(1)
        self.expect(self.json('/api/notifications/head')['workerOnline'], 'API reports the notifications heartbeat online', 'notifications')
        self.expect(self.sql("SELECT COUNT(*) FROM server_notification_heartbeats WHERE deployment='notifications'") == '1',
                    'notification health is stored per deployment, not per owner', 'notifications')

    def transport_fixture(self):
        self.fixture = True
        self.compose('up', '-d', '--wait', 'fixture', 'web', stdout=subprocess.DEVNULL)
        response = self.json('/api/inspect')
        _, _, data = self.request('/api/inspect', headers={'X-Forwarded-Proto': 'https' if not self.tls else 'http', 'X-Forwarded-For': '203.0.113.99', 'X-Forwarded-Host': 'evil.invalid', 'Forwarded': 'for=203.0.113.99;proto=https'})
        spoof = json.loads(data)
        self.expect(spoof['headers']['x-forwarded-proto'] == ('https' if self.tls else 'http'), 'immediate-peer scheme policy')
        self.expect(spoof['headers']['x-forwarded-for'] == response['headers']['x-forwarded-for'] == self.network + '.1', 'proxy preserves observed NAT client, not forged address')
        self.expect('forwarded' not in spoof['headers'] and 'x-forwarded-host' not in spoof['headers'], 'other forwarded claims cleared')
        # A non-Caddy container can reach unpublished web, but cannot forge trust.
        js = "fetch('http://web:8080/api/inspect',{headers:{'X-Forwarded-For':'203.0.113.99','X-Forwarded-Proto':'https'}}).then(r=>r.text()).then(console.log)"
        direct = json.loads(self.compose('exec', '-T', 'fixture', 'node', '-e', js, capture_output=True, text=True).stdout)
        self.expect(direct['headers']['x-forwarded-proto'] == 'http' and direct['headers']['x-forwarded-for'] != '203.0.113.99', 'untrusted internal peer cannot spoof TLS or address')
        if self.tls:
            # A separate client peer reaches Caddy, not web. Its real address must
            # survive both proxies even when it sends forged forwarded claims.
            fixture_id = self.compose('ps', '-q', 'fixture', capture_output=True, text=True).stdout.strip()
            networks = json.loads(run('docker', 'inspect', '--format', '{{json .NetworkSettings.Networks}}', fixture_id, capture_output=True, text=True).stdout)
            client_ip = next(iter(networks.values()))['IPAddress']
            js = "require('https').get({hostname:'tls',servername:'localhost',rejectUnauthorized:false,path:'/api/inspect',headers:{Host:'localhost','X-Forwarded-For':'203.0.113.99','X-Forwarded-Proto':'http'}},r=>{let b='';r.on('data',v=>b+=v);r.on('end',()=>console.log(b))})"
            through_tls = json.loads(self.compose('exec', '-T', 'fixture', 'node', '-e', js, capture_output=True, text=True).stdout)
            self.expect(through_tls['headers']['x-forwarded-for'] == client_ip and through_tls['headers']['x-forwarded-proto'] == 'https', 'distinct client IP and HTTPS survive Caddy/web, forged claims do not')
        _, headers, data = self.request('/api/compressed', headers={'Accept-Encoding': 'gzip'})
        self.expect(any(k.lower() == 'content-encoding' and v == 'gzip' for k, v in headers) and json.loads(gzip.decompress(data)) == {'backend': True}, 'backend-selected gzip passes unchanged')
        conn = self.connect()
        try:
            conn.request('GET', '/api/stream', headers={'Host': 'localhost'})
            stream = conn.getresponse()
            started = time.monotonic()
            first = stream.readline()
            elapsed = time.monotonic() - started
            rest = stream.read()
            self.expect(stream.status == 200 and first == b'event: first\n' and elapsed < .5 and b'event: second' in rest and time.monotonic() - started > .65, 'SSE first frame arrives before delayed final frame')
        finally:
            conn.close()
        self.fixture = False
        self.compose('up', '-d', '--wait', 'web', stdout=subprocess.DEVNULL)

    def seed(self):
        portfolio = self.json('/api/portfolios', {'name': 'Release synthetic', 'icon': 'folder'}, 'POST')
        account = self.json('/api/accounts', {'name': 'Synthetic manual', 'venueId': 'manual', 'portfolioId': portfolio['id'], 'manualAccountValueUsd': '10000'}, 'POST')
        plan = {'direction': 'long', 'sizingMode': 'margin', 'size': '250', 'leverage': '3', 'entries': [{'id': 'entry-1', 'name': 'Entry 1', 'color': '#b9c9e4', 'share': '100', 'price': '100', 'stops': [{'id': 'stop-1', 'unit': 'price', 'value': '95', 'share': '100'}], 'targets': [{'id': 'target-1', 'unit': 'price', 'value': '110', 'share': '100'}]}], 'notes': {'thesis': 'Synthetic release check', 'invalidation': '', 'strategy': '', 'evidence': ''}}
        play = self.json('/api/plays', {'accountId': account['id'], 'instrument': 'SYNTH', 'instrumentSource': 'manual', 'title': 'Synthetic restore play', 'plan': plan}, 'POST')
        # Valid 1x1 PNG, enough to verify original evidence bytes survive restore.
        self.png = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
        boundary = 'vessel-release-synthetic'
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="synthetic.png"\r\nContent-Type: image/png\r\n\r\n'.encode() + self.png + f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="note"\r\n\r\nSynthetic restore evidence\r\n--{boundary}--\r\n'.encode())
        status, _, data = self.request('/api/plays/' + play['summary']['id'] + '/evidence', 'POST', body, {'Content-Type': 'multipart/form-data; boundary=' + boundary})
        self.expect(status == 201, 'synthetic saved Play evidence uploaded', 'restore')
        self.evidence = json.loads(data)['id']
        self.markup = {'width': 1, 'height': 1, 'shapes': [{'id': 'box-1', 'kind': 'box', 'color': '#ff5c5c', 'width': 1, 'from': {'x': 0, 'y': 0}, 'to': {'x': 1, 'y': 1}}]}
        self.markup = self.json('/api/evidence/' + self.evidence + '/markup', self.markup, 'PUT')['markup']
        self.account = account['id']
        self.play = play['summary']['id']
        # Synthetic chart account, never refreshed. Browser intercepts its public reads.
        self.credential_account = self.json('/api/accounts', {'name': 'Synthetic credential', 'venueId': 'lighter', 'sourceId': '123'}, 'POST')['id']
        self.chart_account = self.json('/api/accounts', {'name': 'Synthetic chart', 'venueId': 'hyperliquid', 'address': '0x' + '1' * 40}, 'POST')['id']

    def restore(self):
        # Backup while an event is pending, then discard RabbitMQ's volume along
        # with the old deployment. Recovery must come from PostgreSQL.
        self.compose('stop', 'rabbitmq', stdout=subprocess.DEVNULL)
        self.restore_event_id = self.change_status_event('planned')
        self.expect(self.event_pending(self.restore_event_id),
                    'database backup includes pending event work', 'restore')
        self.private_logs()
        # Every host stops before a backup: web, api and the workers (README upgrade/backup procedure).
        self.compose('stop', 'web', 'api', 'worker', 'worker-notifications', 'verifier', stdout=subprocess.DEVNULL)
        service, directory, uid = ('rustfs', '/data', '10001') if self.args.storage == 's3' else ('api', '/var/lib/vessel/evidence', '1654')
        if self.args.storage == 's3':
            # RustFS stores object metadata too; take its complete volume offline.
            self.compose('stop', 'rustfs', stdout=subprocess.DEVNULL)
        self.expect(self.sql(f"SELECT count(*) FROM identity.sessions WHERE id = '{self.session['session']['id']}' AND revoked_at IS NULL") == '1',
                    'backup starts with the operator session live', 'wallet')
        with (self.temp / 'vessel.dump').open('wb') as out:
            self.compose('exec', '-T', 'postgres', 'pg_dump', '-U', 'vessel', '-d', 'vessel', '-Fc', stdout=out)
        self.restored_revoked_cookie = self.session_cookie
        self.identity('revoke-sessions', '--user', self.owner)
        self.expect(self.sql('SELECT count(*) FROM identity.sessions WHERE revoked_at IS NULL') == '0',
                    'session revoked AFTER backup, before restoring the older database', 'wallet')
        with (self.temp / 'evidence.tar.gz').open('wb') as out:
            self.compose('run', '--rm', '--no-deps', '-T', '--entrypoint', 'tar', service, '-C', directory, '-czf', '-', '.', stdout=out)
        # Copy the same ring bytes to a separate restoration location and mount it.
        with (self.temp / 'ring.backup').open('wb') as out:
            self.compose('run', '--rm', '--no-deps', '-T', '--user', '0', '--entrypoint', 'sh', 'api', '-c', 'cat /run/secrets/vessel-credential-keys.json', stdout=out)
        # Original network must be removed before restore, not merely stopped.
        self.compose('down', '-v', '--remove-orphans', stdout=subprocess.DEVNULL)
        project = self.projects[1]
        self.write_env(self.network)
        run('docker', 'run', '--rm', '--user', '0', '-v', f'{self.temp}:/backup', 'node:24-bookworm-slim', 'sh', '-c', 'cp /backup/ring.backup /backup/secrets/restored.json && chown 1654:1654 /backup/secrets/restored.json && chmod 600 /backup/secrets/restored.json', stdout=subprocess.DEVNULL)
        env = self.temp / '.env'
        env.write_text(env.read_text().replace('/secrets/ring.json', '/secrets/restored.json'))
        self.compose('up', '-d', '--wait', 'postgres', project=project, stdout=subprocess.DEVNULL)
        with (self.temp / 'vessel.dump').open('rb') as source:
            self.compose('exec', '-T', 'postgres', 'pg_restore', '-U', 'vessel', '-d', 'vessel', '--no-owner', '--no-privileges', '--exit-on-error', project=project, stdin=source)
        with (self.temp / 'evidence.tar.gz').open('rb') as source:
            self.compose('run', '--rm', '--no-deps', '-T', '--user', '0', '--entrypoint', 'sh', service, '-c',
                         f'tar -xzf - -C {directory} && chown -R {uid}:{uid} {directory}', project=project, stdin=source, stdout=subprocess.DEVNULL)
        # Reset restored identities before any host starts, then rotate the event epoch.
        self.expect(self.sql(f"SELECT count(*) FROM identity.sessions WHERE id = '{self.session['session']['id']}' AND revoked_at IS NULL", project) == '1',
                    'restored pre-revocation backup resurrects the live operator session before reset', 'wallet')
        self.identity('reset-after-restore', '--keep-active', self.owner, project=project)
        self.expect(self.sql('SELECT count(*) FROM identity.sessions WHERE revoked_at IS NULL', project) == '0',
                    'restore reset revokes sessions resurrected by the pre-revocation backup', 'wallet')
        self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate', '/app/Vessel.Api.dll', 'rotate-event-epoch',
                     project=project, stdout=subprocess.DEVNULL)
        self.compose('run', '--rm', 'migrate', project=project, stdout=subprocess.DEVNULL)
        self.compose('up', '-d', '--wait', project=project, stdout=subprocess.DEVNULL)
        self.ready()
        self.expect(self.request('/api/auth/session', headers={'Cookie': self.restored_revoked_cookie})[0] == 401,
                    'restored previously revoked session cannot authenticate after reset', 'wallet')
        self.wallet_sign_in()
        self.wait_event_delivery(self.restore_event_id, project)
        self.wait_outbox_drained(project)
        restored_events = self.json('/api/events?limit=200')
        self.expect(any(event['id'] == self.restore_event_id for event in restored_events['events']),
                    'event identity survives database restore and broker volume loss', 'restore')
        self.private_logs(project)
        self.expect(self.request('/health/ready')[0] == 200, 'restored database/migrations ready', 'restore')
        self.expect(self.compose('ps', '--format', 'json', 'worker', project=project, capture_output=True, text=True).stdout.count('"healthy"') == 1,
                    'restored worker healthy without the broker volume', 'restore')
        self.expect(self.compose('ps', '--format', 'json', 'worker-notifications', project=project, capture_output=True, text=True).stdout.count('"healthy"') == 1,
                    'restored notifications worker healthy without the broker volume', 'restore')
        restored = self.json('/api/notifications?tab=all')
        self.expect(restored['allTotal'] == self.notification_count and any(item['state'] == 'read' for item in restored['items']),
                    'notifications and their read state survive the restore', 'restore')
        self.expect(re.fullmatch(r'[0-9a-f]{32}\.[0-9]+\.[0-9]+', self.json('/api/notifications/head')['nextCursor']) is not None,
                    'the notification head names the rotated epoch', 'restore')
        self.expect(self.json('/api/accounts/' + self.account)['name'] == 'Synthetic manual', 'account visible under preserved owner', 'restore')
        self.expect(self.json('/api/plays/' + self.play)['summary']['title'] == 'Synthetic restore play', 'saved Play restored', 'restore')
        self.expect(self.request('/api/evidence/' + self.evidence + '/content')[2] == self.png, 'original evidence bytes restored', 'restore')
        item = self.json('/api/plays/' + self.play + '/evidence')[0]
        self.expect(item['note'] == 'Synthetic restore evidence' and item['markup'] == self.markup, 'evidence note and vector markup restored', 'restore')
        self.expect(self.json('/api/accounts/' + self.credential_account + '/credential')['storageConfigured'], 'restored mode-600 ring loaded by UID 1654', 'restore')
        self.expect(run('docker', 'run', '--rm', '-v', f'{self.temp}:/backup:ro', 'node:24-bookworm-slim', 'sh', '-c', 'cmp /backup/ring.backup /backup/secrets/restored.json', stdout=subprocess.DEVNULL).returncode == 0, 'restored key IDs/bytes unchanged', 'restore')
        # The verification subscription is not a production consumer: stop its host, then deregister it.
        self.compose('stop', 'verifier', project=project, stdout=subprocess.DEVNULL)
        removed = self.compose('run', '--rm', '--no-deps', '--entrypoint', 'dotnet', 'migrate', '/app/Vessel.Api.dll',
                               'deregister-event-subscription', 'verification', project=project, check=False, capture_output=True, text=True)
        self.expect(removed.returncode == 0 and self.sql("SELECT count(*) FROM event_subscriptions WHERE name = 'verification'", project) == '0' and
                    self.sql("SELECT count(*) FROM event_receipts WHERE subscription = 'verification'", project) == '0',
                    'verification subscription deregistered with its receipts', 'restore')

    def start_browser_server(self):
        # Mount only the external tooling and this one public forwarder file,
        # never the private temp directory containing .env and the key ring.
        module = Path(self.args.browser_module)
        if not module.is_absolute() or module.name != 'playwright' or module.parent.name != 'node_modules':
            raise ValueError('--wallet-browser-image requires an absolute node_modules/playwright module path')
        forwarder = self.temp / 'browser-forwarder.cjs'
        forwarder.write_text(f"""const net = require('node:net');
for (const host of ['127.0.0.1', '::1']) {{
  net.createServer(client => {{
    const upstream = net.connect(443, 'tls');
    client.on('error', () => upstream.destroy());
    upstream.on('error', () => client.destroy());
    client.pipe(upstream).pipe(client);
  }}).listen({self.https_port}, host);
}}
""")
        forwarder.chmod(0o644)
        server_port, path = port(), secrets.token_hex(24)
        self.browser_container = self.name + '-browser'
        run('docker', 'run', '-d', '--rm', '--init', '--user', 'pwuser',
            '--memory', '2g', '--cpus', '2', '--network', self.name + '_default',
            '-p', f'127.0.0.1:{server_port}:3000', '--name', self.browser_container,
            '-v', f'{module.parent.parent}:{module.parent.parent}:ro',
            '-v', f'{forwarder}:/vessel-browser-forwarder.cjs:ro',
            '--entrypoint', '/bin/sh', self.args.wallet_browser_image, '-c',
            f'node /vessel-browser-forwarder.cjs & exec node {shlex.quote(str(module / "cli.js"))} '
            f'run-server --host 0.0.0.0 --port 3000 --path /{path} --max-clients 1',
            stdout=subprocess.DEVNULL, env=verification_environment())
        for _ in range(60):
            connection = http.client.HTTPConnection('127.0.0.1', server_port, timeout=1)
            try:
                # Rootless Docker's port proxy accepts TCP before Node listens.
                # Require the server's own endpoint response, not just a socket.
                connection.request('GET', '/json')
                response = connection.getresponse()
                if response.status == 200 and json.loads(response.read()).get('wsEndpointPath') == '/' + path:
                    return f'ws://127.0.0.1:{server_port}/{path}'
            except (OSError, http.client.HTTPException, ValueError):
                pass
            finally:
                connection.close()
            time.sleep(1)
        logs = run('docker', 'logs', self.browser_container, capture_output=True, text=True, check=False)
        raise RuntimeError('Isolated Playwright server did not become ready: ' + logs.stdout + logs.stderr)

    def stop_browser_server(self):
        if self.browser_container:
            result = run('docker', 'rm', '-f', self.browser_container, capture_output=True, text=True, check=False)
            if result.returncode and 'No such container' not in result.stderr:
                raise RuntimeError('Cannot remove isolated browser server: ' + result.stderr.strip())
            self.browser_container = None

    def browser_smokes(self):
        # Never connect to a caller's browser server, even for the local engine.
        env = {key: value for key, value in os.environ.items()
               if not re.fullmatch(r'PLAYWRIGHT_.*_WS_ENDPOINT', key)}
        env.update({'VESSEL_RELEASE_URL': self.url, 'VESSEL_RELEASE_CHART_ACCOUNT': self.chart_account,
                    'PLAYWRIGHT_MODULE': self.args.browser_module, 'VESSEL_RELEASE_SYNTHETIC': '1'})
        try:
            # The production SPA (CSP, chart, evidence, themes, monitor UI), signed in as the bound operator wallet.
            run('node', str(ROOT / 'scripts/release-browser-smoke.cjs'), env=dict(env))
            env['VESSEL_RELEASE_INVITATION'] = self.identity('invite', '--max-uses', '2').stdout.strip().splitlines()[-1]
            for browser in self.args.wallet_browsers.split(','):
                browser_env = {**env, 'VESSEL_BROWSER': browser}
                if browser == 'firefox' and self.args.wallet_browser_image:
                    browser_env['PLAYWRIGHT_FIREFOX_WS_ENDPOINT'] = self.start_browser_server()
                run('node', str(ROOT / 'scripts/wallet-browser-smoke.cjs'), env=browser_env)
        finally:
            self.stop_browser_server()

    def cleanup(self):
        self.tls = True
        self.fixture = False
        errors = []
        try:
            self.stop_browser_server()
        except RuntimeError:
            errors.append('browser server')
        if (self.temp / 'local.yaml').exists():
            for project in self.projects:
                result = self.compose('down', '-v', '--remove-orphans', project=project, stdout=subprocess.DEVNULL, check=False)
                if result.returncode:
                    errors.append(project)
        # Temp ring is owned by the API UID. Remove only this check's directory.
        if (self.temp / 'secrets').exists():
            run('docker', 'run', '--rm', '--user', '0', '-v', f'{self.temp}:/check', 'node:24-bookworm-slim', 'sh', '-c', 'rm -rf /check/secrets', stdout=subprocess.DEVNULL)
        shutil.rmtree(self.temp)
        for image in self.images.values():
            run('docker', 'image', 'rm', image, stdout=subprocess.DEVNULL, check=False)
        if errors:
            raise RuntimeError('Cleanup failed for isolated projects: ' + ', '.join(errors))

    def main(self):
        try:
            if self.args.wallet_browser_image:
                run('docker', 'image', 'inspect', self.args.wallet_browser_image, stdout=subprocess.DEVNULL)
            self.wallet_signature()  # Fail before building if external test dependencies are missing.
            self.prepare()
            for kind, file in (('api', 'backend/Dockerfile'), ('web', 'frontend/Dockerfile')):
                run('docker', 'build', '--platform', 'linux/amd64', '-f', str(ROOT / file), '-t', self.images[kind], str(ROOT), stdout=subprocess.DEVNULL)
            result = run('docker', 'run', '--rm', '--entrypoint', 'nginx', self.images['web'], '-V', capture_output=True, text=True)
            self.expect('--with-http_realip_module' in result.stderr, 'unprivileged nginx realip module', 'image')
            for proxy in ('0.0.0.0', '172.29.100.0/24', '1.2.3.4; include /evil;', '1.2.3.4\n5.6.7.8'):
                invalid = subprocess.run(['docker', 'run', '--rm', '-e', 'VESSEL_TRUSTED_PROXY=' + proxy, '--entrypoint', '/docker-entrypoint.d/19-trusted-proxy.sh', self.images['web']], capture_output=True)
                self.expect(invalid.returncode != 0, 'invalid nginx trusted peer rejected: ' + repr(proxy), 'image')
            self.wallet_bootstrap()
            self.compose('up', '-d', '--wait', stdout=subprocess.DEVNULL)
            self.ready()
            self.wallet_sign_in()
            self.seed()
            self.worker()
            self.monitor()
            self.notifications()
            self.matrix()
            self.transport_fixture()
            # The default Compose mode: web on plain HTTP behind the operator's own HTTPS proxy. The browser's public
            # HTTPS origin and session cookie still apply; only the transport to web changes.
            self.tls = False
            self.compose('up', '-d', '--wait', stdout=subprocess.DEVNULL)
            self.ready()
            self.matrix()
            self.transport_fixture()
            self.tls = True
            self.compose('up', '-d', '--wait', stdout=subprocess.DEVNULL)
            self.ready()
            if self.args.browser_module:
                self.browser_smokes()
            self.restore()
            status, _, _ = self.request('/api/auth/sign-out', 'POST', {'expectedSessionId': self.session['session']['id']})
            self.expect(status == 204 and self.request('/api/auth/session')[0] == 401,
                        'sign-out revokes the HTTPS session', 'wallet')
            print('Matrix counts: ' + json.dumps(self.counts, sort_keys=True), flush=True)
        finally:
            self.cleanup()


def parse_args(args=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--browser-module', help='Absolute existing Playwright module path, no dependency install.')
    parser.add_argument('--storage', choices=('s3', 'local'), default='s3', help='Storage/restore path to check (default: private RustFS).')
    parser.add_argument('--wallet-browsers', default='chromium,firefox', help='Wallet browser engines, comma separated (default: chromium,firefox).')
    parser.add_argument('--wallet-browser-image', help='Pre-pulled matching Playwright image for isolated remote Firefox; requires --browser-module.')
    parsed = parser.parse_args(args)
    if not parsed.wallet_browsers or any(browser not in ('chromium', 'firefox') for browser in parsed.wallet_browsers.split(',')):
        parser.error('--wallet-browsers must be chromium, firefox or chromium,firefox')
    if parsed.wallet_browser_image and not parsed.browser_module:
        parser.error('--wallet-browser-image requires --browser-module')
    return parsed


if __name__ == '__main__':
    Check(parse_args()).main()
