#!/usr/bin/env python3
"""Deterministic, scriptable Hyperliquid info-API fake for the full-stack end-to-end test.

Synthetic data only. It is a test fixture, never a venue fallback: it serves the read-only `/info`
requests Vessel's Hyperliquid adapter sends (TLS, so the production https/wss host rules stay intact),
a minimal market WebSocket, and a plain-HTTP control API on a separate port that the harness uses to
place, fill and cancel orders, list markets, gate or fail reads between steps.

Standard library only, so it runs unmodified in the pinned python image and in unit tests.
"""
import argparse
import base64
import hashlib
import json
import math
import os
import socket
import ssl
import struct
import threading
import time
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

USDC_TOKEN_ID = '0x6d1e7cde53ba9467b783cb7c530ce054'
INTERVALS_MS = {'1m': 60_000, '3m': 180_000, '5m': 300_000, '15m': 900_000, '30m': 1_800_000, '1h': 3_600_000,
                '2h': 7_200_000, '4h': 14_400_000, '8h': 28_800_000, '12h': 43_200_000, '1d': 86_400_000,
                '3d': 259_200_000, '1w': 604_800_000, '1M': 2_592_000_000}
WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'


def text(value):
    """Exact plain decimal text (no exponent), as Hyperliquid sends decimals."""
    value = Decimal(value)
    result = format(value, 'f')
    if '.' in result:
        result = result.rstrip('0').rstrip('.')
    return '0' if result in ('', '-0') else result


def now_ms():
    return int(time.time() * 1000)


class FakeHyperliquid:
    """Venue state per lowercase address. Every method is thread-safe; times are real milliseconds."""

    def __init__(self, clock=now_ms):
        self.clock = clock
        self.lock = threading.Condition()
        self.reset({})

    # ---- control ---------------------------------------------------------------------------------

    def reset(self, spec):
        with self.lock:
            self.universe = list(spec.get('universe') or [
                {'name': 'BTC', 'szDecimals': 5, 'maxLeverage': 40, 'px': '100'},
                {'name': 'ETH', 'szDecimals': 4, 'maxLeverage': 25, 'px': '2000'},
                {'name': 'SOL', 'szDecimals': 2, 'maxLeverage': 20, 'px': '150'},
                {'name': 'AVAX', 'szDecimals': 2, 'maxLeverage': 10, 'px': '20'},
            ])
            self.accounts = {}
            self.next_oid = 900_000_001
            self.next_tid = 700_000_001
            self.requests = {}
            self.peers = {}
            self.gate = None
            self.failures = {}
            for address, account in (spec.get('accounts') or {}).items():
                self.account(address).update({k: v for k, v in account.items() if k in ('accountValue', 'usdc')})
            return {'ok': True}

    def account(self, address):
        address = address.lower()
        if address not in self.accounts:
            self.accounts[address] = {'accountValue': '10000', 'usdc': '250', 'positions': {}, 'orders': {},
                                      'history': [], 'fills': []}
        return self.accounts[address]

    def list_market(self, market):
        with self.lock:
            if all(m['name'] != market['name'] for m in self.universe):
                self.universe.append(market)
            return {'universe': [m['name'] for m in self.universe]}

    def place(self, address, coin, side, px, sz, kind='limit', trigger=None, reduce_only=False, tpsl=False, at=None):
        """Places a resting order. kind: limit | stop (Stop Market) | tp (Take Profit Market)."""
        if side not in ('B', 'A') or kind not in ('limit', 'stop', 'tp'):
            raise ValueError('side must be B or A; kind limit, stop or tp')
        with self.lock:
            oid = self.next_oid
            self.next_oid += 1
            is_trigger = kind != 'limit'
            order = {'coin': coin, 'side': side, 'limitPx': text(px), 'sz': text(sz), 'oid': oid,
                     'timestamp': at or self.clock(), 'triggerCondition': 'N/A', 'isTrigger': is_trigger,
                     'triggerPx': text(trigger if trigger is not None else px) if is_trigger else '0.0',
                     'children': [], 'isPositionTpsl': bool(tpsl), 'reduceOnly': bool(reduce_only or is_trigger),
                     'orderType': {'limit': 'Limit', 'stop': 'Stop Market', 'tp': 'Take Profit Market'}[kind],
                     'origSz': text(sz), 'tif': 'Gtc' if kind == 'limit' else None, 'cloid': None}
            if is_trigger:
                order['triggerCondition'] = ('Price below ' if (kind == 'stop') == (side == 'A') else 'Price above ') + order['triggerPx']
            self.account(address)['orders'][oid] = order
            self.lock.notify_all()
            return {'oid': oid}

    def _status(self, account, order, status):
        account['history'].append({'order': dict(order), 'status': status, 'statusTimestamp': self.clock()})

    def cancel(self, address, oid, record=True):
        """Cancels a resting order. record=False removes it from the book without any status row."""
        with self.lock:
            account = self.account(address)
            order = account['orders'].pop(int(oid))
            if record:
                self._status(account, order, 'canceled')
            return {'oid': order['oid']}

    def fill(self, address, oid, sz=None, px=None):
        """Fills a resting order (fully by default), updating the position, fills and order status."""
        with self.lock:
            account = self.account(address)
            order = account['orders'][int(oid)]
            remaining = Decimal(order['sz'])
            size = Decimal(sz) if sz is not None else remaining
            if size <= 0 or size > remaining:
                raise ValueError('fill size must be positive and at most the remaining size')
            price = Decimal(px) if px is not None else Decimal(order['triggerPx'] if order['isTrigger'] else order['limitPx'])
            fill = self._execute(account, order['coin'], order['side'], price, size, oid=order['oid'])
            left = remaining - size
            if left == 0:
                del account['orders'][order['oid']]
                done = dict(order, sz='0')
                self._status(account, done, 'filled')
            else:
                order['sz'] = text(left)
            return fill

    def raw_fill(self, address, coin, side, px, sz):
        """A fill without a resting order, e.g. on a market the catalogue does not list."""
        with self.lock:
            oid = self.next_oid
            self.next_oid += 1
            return self._execute(self.account(address), coin, side, Decimal(px), Decimal(sz), oid=oid)

    def _execute(self, account, coin, side, price, size, oid, at=None):
        position = account['positions'].get(coin, {'szi': Decimal(0), 'entryPx': price})
        start = position['szi']
        signed = size if side == 'B' else -size
        end = start + signed
        closed = Decimal(0)
        if start == 0 or (start > 0) == (signed > 0):
            direction = 'Open Long' if signed > 0 else 'Open Short'
            entry = (abs(start) * position['entryPx'] + size * price) / abs(end)
        elif end == 0 or (end > 0) == (start > 0):
            direction = 'Close Long' if start > 0 else 'Close Short'
            closed = (price - position['entryPx']) * size * (1 if start > 0 else -1)
            entry = position['entryPx']
        else:
            direction = 'Long > Short' if start > 0 else 'Short > Long'
            closed = (price - position['entryPx']) * abs(start) * (1 if start > 0 else -1)
            entry = price
        account['positions'][coin] = {'szi': end, 'entryPx': entry if end != 0 else price}
        tid = self.next_tid
        self.next_tid += 1
        fee = (price * size * Decimal('0.00025')).quantize(Decimal('0.000001'))
        fill = {'coin': coin, 'px': text(price), 'sz': text(size), 'side': side, 'time': at or self.clock(),
                'startPosition': text(start), 'dir': direction, 'closedPnl': text(closed),
                'hash': '0x' + hashlib.sha256(f'{coin}:{tid}'.encode()).hexdigest(), 'oid': oid, 'crossed': True,
                'fee': text(fee), 'tid': tid, 'feeToken': 'USDC'}
        account['fills'].insert(0, fill)
        self.lock.notify_all()
        return fill

    def seed_fill(self, address, coin, side, px, sz, ago_ms):
        """Pre-existing history: an executed order and its fill some time ago."""
        with self.lock:
            account = self.account(address)
            at = self.clock() - int(ago_ms)
            oid = self.next_oid
            self.next_oid += 1
            fill = self._execute(account, coin, side, Decimal(px), Decimal(sz), oid=oid, at=at)
            order = {'coin': coin, 'side': side, 'limitPx': text(px), 'sz': '0', 'oid': oid, 'timestamp': at,
                     'triggerCondition': 'N/A', 'isTrigger': False, 'triggerPx': '0.0', 'children': [],
                     'isPositionTpsl': False, 'reduceOnly': False, 'orderType': 'Limit', 'origSz': text(sz),
                     'tif': 'Gtc', 'cloid': None}
            account['history'].append({'order': order, 'status': 'filled', 'statusTimestamp': at})
            return fill

    def arm_gate(self, kind):
        """The next `/info` request of this type blocks until released (or released by timeout)."""
        with self.lock:
            self.gate = {'type': kind, 'entered': False, 'peer': None, 'released': False}
            return dict(self.gate)

    def release_gate(self):
        with self.lock:
            if self.gate:
                self.gate['released'] = True
            self.lock.notify_all()
            return dict(self.gate or {})

    def gate_state(self):
        with self.lock:
            return dict(self.gate or {})

    def fail(self, kind, count, status=500):
        with self.lock:
            self.failures[kind] = {'count': int(count), 'status': int(status)}
            return {'ok': True}

    def summary(self):
        with self.lock:
            return {'requests': dict(self.requests), 'peers': {k: sorted(v) for k, v in self.peers.items()},
                    'accounts': {a: {'positions': {c: text(p['szi']) for c, p in v['positions'].items()},
                                     'open': sorted(v['orders']), 'fills': len(v['fills'])}
                                 for a, v in self.accounts.items()}}

    # ---- venue reads -----------------------------------------------------------------------------

    def before(self, kind, peer):
        """Counts the request; applies a scripted failure or blocks on an armed gate. Returns an HTTP status or None."""
        with self.lock:
            self.requests[kind] = self.requests.get(kind, 0) + 1
            self.peers.setdefault(kind, set()).add(peer)
            failure = self.failures.get(kind)
            if failure and failure['count'] > 0:
                failure['count'] -= 1
                return failure['status']
            gate = self.gate
            if gate and gate['type'] == kind and not gate['entered']:
                gate['entered'], gate['peer'] = True, peer
                self.lock.notify_all()
                self.lock.wait_for(lambda: gate['released'], timeout=900)
        return None

    def info(self, body):
        kind = body.get('type')
        with self.lock:
            if kind == 'meta':
                return {'universe': [{k: m[k] for k in ('name', 'szDecimals', 'maxLeverage')} for m in self.universe],
                        'marginTables': []}
            if kind == 'metaAndAssetCtxs':
                return [{'universe': [{k: m[k] for k in ('name', 'szDecimals', 'maxLeverage')} for m in self.universe]},
                        [self._context(m) for m in self.universe]]
            if kind == 'spotMeta':
                return {'tokens': [{'name': 'USDC', 'szDecimals': 8, 'weiDecimals': 8, 'index': 0, 'tokenId': USDC_TOKEN_ID,
                                    'isCanonical': True, 'evmContract': None, 'fullName': None}], 'universe': []}
            # Builder (HIP-3) catalogue as the real API answers with no builder DEX: allPerpMetas lists the primary DEX
            # only, perpDexs has its null placeholder, no annotations.
            if kind == 'allPerpMetas':
                return [{'universe': [{k: m[k] for k in ('name', 'szDecimals', 'maxLeverage')} for m in self.universe],
                         'marginTables': [], 'collateralToken': 0}]
            if kind == 'perpDexs':
                return [None]
            if kind == 'perpConciseAnnotations':
                return []
            if kind == 'candleSnapshot':
                return self._candles(body['req'])
            account = self.account(body['user'])
            if kind == 'clearinghouseState':
                return self._clearinghouse(account)
            if kind == 'userFills':
                return [dict(f) for f in account['fills'][:2000]]
            if kind == 'userAbstraction':
                return 'default'
            if kind == 'spotClearinghouseState':
                return {'balances': [{'coin': 'USDC', 'token': 0, 'hold': '0', 'total': account['usdc'], 'entryNtl': '0'}]}
            if kind == 'historicalOrders':
                return [{'order': dict(h['order']), 'status': h['status'], 'statusTimestamp': h['statusTimestamp']}
                        for h in account['history'][-2000:]]
            if kind == 'frontendOpenOrders':
                return [dict(o) for o in sorted(account['orders'].values(), key=lambda o: -o['timestamp'])]
        raise KeyError(kind)

    def _price(self, coin):
        return Decimal(next((m.get('px', '100') for m in self.universe if m['name'] == coin), '100'))

    def _context(self, market):
        px = text(self._price(market['name']))
        return {'funding': '0.0000125', 'openInterest': '1234.5', 'prevDayPx': px, 'dayNtlVlm': '1000000',
                'premium': '0', 'oraclePx': px, 'markPx': px, 'midPx': px, 'impactPxs': [px, px], 'dayBaseVlm': '100'}

    def _clearinghouse(self, account):
        positions, margin = [], Decimal(0)
        for coin, p in sorted(account['positions'].items()):
            if p['szi'] == 0:
                continue
            used = (abs(p['szi']) * p['entryPx'] / 3).quantize(Decimal('0.000001'))
            margin += used
            positions.append({'type': 'oneWay', 'position': {
                'coin': coin, 'szi': text(p['szi']), 'leverage': {'type': 'cross', 'value': 3}, 'entryPx': text(p['entryPx']),
                'positionValue': text(abs(p['szi']) * p['entryPx']), 'unrealizedPnl': '0', 'returnOnEquity': '0',
                'liquidationPx': None, 'marginUsed': text(used), 'maxLeverage': 40,
                'cumFunding': {'allTime': '0', 'sinceOpen': '0', 'sinceChange': '0'}}})
        summary = {'accountValue': account['accountValue'], 'totalNtlPos': '0', 'totalRawUsd': account['accountValue'],
                   'totalMarginUsed': text(margin)}
        return {'marginSummary': summary, 'crossMarginSummary': summary, 'crossMaintenanceMarginUsed': '0',
                'withdrawable': text(Decimal(account['accountValue']) - margin), 'assetPositions': positions,
                'time': self.clock()}

    def _candles(self, req):
        step = INTERVALS_MS[req['interval']]
        end = min(int(req['endTime']), self.clock())
        start = max(int(req['startTime']), end - step * 500)
        base = self._price(req['coin'])
        candles = []
        t = start - start % step
        while t <= end and len(candles) < 5000:
            i = t // step
            # Deterministic per candle open time: the same request always returns the same prices.
            o = base * (1 + Decimal(str(round(math.sin(i / 7) / 50, 6))))
            c = base * (1 + Decimal(str(round(math.sin((i + 1) / 7) / 50, 6))))
            q = Decimal('0.0001')
            o, c = o.quantize(q), c.quantize(q)
            candles.append({'t': t, 'T': t + step - 1, 's': req['coin'], 'i': req['interval'], 'o': text(o), 'c': text(c),
                            'h': text(max(o, c) * Decimal('1.002')), 'l': text(min(o, c) * Decimal('0.998')),
                            'v': '12.5', 'n': 40})
            t += step
        return candles


# ---- HTTP surfaces ---------------------------------------------------------------------------------


def handler(venue, control):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *args):
            pass

        def send_json(self, status, value):
            body = json.dumps(value).encode()
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def body(self):
            # .NET's JsonContent streams without a length: accept chunked bodies as well.
            if 'chunked' in self.headers.get('Transfer-Encoding', '').lower():
                data = b''
                while True:
                    size = int(self.rfile.readline().split(b';')[0].strip() or b'0', 16)
                    if size == 0:
                        while self.rfile.readline() not in (b'\r\n', b'\n', b''):
                            pass
                        break
                    data += self.rfile.read(size)
                    self.rfile.readline()
                return json.loads(data or b'{}')
            length = int(self.headers.get('Content-Length') or 0)
            return json.loads(self.rfile.read(length) or b'{}')

        def do_GET(self):
            if control:
                if self.path == '/control/summary':
                    return self.send_json(200, venue.summary())
                if self.path == '/control/gate':
                    return self.send_json(200, venue.gate_state())
                if self.path == '/health':
                    return self.send_json(200, {'ok': True})
            elif self.path == '/ws' and self.headers.get('Upgrade', '').lower() == 'websocket':
                return self.websocket()
            self.send_json(404, {'error': 'not found'})

        def do_POST(self):
            try:
                body = self.body()
            except ValueError:
                return self.send_json(400, {'error': 'invalid json'})
            if not control:
                if self.path != '/info':
                    return self.send_json(404, {'error': 'not found'})
                kind = str(body.get('type'))
                status = venue.before(kind, self.client_address[0])
                if status:
                    return self.send_json(status, {'error': 'scripted failure'})
                try:
                    return self.send_json(200, venue.info(body))
                except (KeyError, TypeError, ValueError):
                    return self.send_json(422, {'error': 'unsupported request'})
            routes = {
                '/control/reset': lambda b: venue.reset(b),
                '/control/market': lambda b: venue.list_market(b),
                '/control/order': lambda b: venue.place(b['address'], b['coin'], b['side'], b['px'], b['sz'], b.get('kind', 'limit'),
                                                        b.get('trigger'), b.get('reduceOnly', False), b.get('tpsl', False)),
                '/control/fill': lambda b: venue.fill(b['address'], b['oid'], b.get('sz'), b.get('px')),
                '/control/raw-fill': lambda b: venue.raw_fill(b['address'], b['coin'], b['side'], b['px'], b['sz']),
                '/control/seed-fill': lambda b: venue.seed_fill(b['address'], b['coin'], b['side'], b['px'], b['sz'], b['agoMs']),
                '/control/cancel': lambda b: venue.cancel(b['address'], b['oid'], b.get('record', True)),
                '/control/gate': lambda b: venue.arm_gate(b['type']),
                '/control/release': lambda b: venue.release_gate(),
                '/control/fail': lambda b: venue.fail(b['type'], b.get('count', 1), b.get('status', 500)),
            }
            route = routes.get(self.path)
            if route is None:
                return self.send_json(404, {'error': 'not found'})
            try:
                return self.send_json(200, route(body))
            except (KeyError, ValueError, TypeError) as error:
                return self.send_json(400, {'error': type(error).__name__ + ': ' + str(error)})

        # A minimal RFC 6455 server: subscribe acknowledgements and pongs, enough for the chart relay.
        def websocket(self):
            accept = base64.b64encode(hashlib.sha1((self.headers['Sec-WebSocket-Key'] + WS_GUID).encode()).digest()).decode()
            self.send_response(101)
            self.send_header('Upgrade', 'websocket')
            self.send_header('Connection', 'Upgrade')
            self.send_header('Sec-WebSocket-Accept', accept)
            self.end_headers()
            self.close_connection = True
            try:
                while True:
                    opcode, payload = read_frame(self.rfile)
                    if opcode == 8:
                        write_frame(self.wfile, 8, b'')
                        return
                    if opcode == 9:
                        write_frame(self.wfile, 10, payload)
                        continue
                    if opcode != 1:
                        continue
                    message = json.loads(payload)
                    if message.get('method') == 'ping':
                        reply = {'channel': 'pong'}
                    else:
                        reply = {'channel': 'subscriptionResponse',
                                 'data': {'method': message.get('method'), 'subscription': message.get('subscription')}}
                    write_frame(self.wfile, 1, json.dumps(reply).encode())
            except (OSError, ValueError, ConnectionError):
                return

    return Handler


def read_frame(stream):
    head = stream.read(2)
    if len(head) < 2:
        raise ConnectionError('closed')
    opcode, length = head[0] & 0x0F, head[1] & 0x7F
    if length == 126:
        length = struct.unpack('>H', stream.read(2))[0]
    elif length == 127:
        length = struct.unpack('>Q', stream.read(8))[0]
    mask = stream.read(4) if head[1] & 0x80 else b'\0\0\0\0'
    data = stream.read(length)
    return opcode, bytes(b ^ mask[i % 4] for i, b in enumerate(data))


def write_frame(stream, opcode, payload):
    head = bytes([0x80 | opcode])
    if len(payload) < 126:
        head += bytes([len(payload)])
    elif len(payload) < 65536:
        head += bytes([126]) + struct.pack('>H', len(payload))
    else:
        head += bytes([127]) + struct.pack('>Q', len(payload))
    stream.write(head + payload)
    stream.flush()


def serve(venue_port, control_port, cert, key, host='0.0.0.0'):
    venue = FakeHyperliquid()
    tls = ThreadingHTTPServer((host, venue_port), handler(venue, control=False))
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(cert, key)
    # The handshake runs on the request thread, so one slow client cannot stall accept().
    tls.socket = context.wrap_socket(tls.socket, server_side=True, do_handshake_on_connect=False)
    plain = ThreadingHTTPServer((host, control_port), handler(venue, control=True))
    for server in (tls, plain):
        server.daemon_threads = True
        threading.Thread(target=server.serve_forever, daemon=True).start()
    return venue, tls, plain


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8443)
    parser.add_argument('--control-port', type=int, default=8080)
    parser.add_argument('--cert', default=os.environ.get('FAKE_VENUE_CERT', '/certs/venue.crt'))
    parser.add_argument('--key', default=os.environ.get('FAKE_VENUE_KEY', '/certs/venue.key'))
    args = parser.parse_args()
    serve(args.port, args.control_port, args.cert, args.key)
    print(f'fake venue: https :{args.port}, control http :{args.control_port}', flush=True)
    threading.Event().wait()


if __name__ == '__main__':
    socket.setdefaulttimeout(None)
    main()
