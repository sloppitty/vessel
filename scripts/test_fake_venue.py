import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import tempfile
import unittest
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent / 'e2e'))
import fake_venue  # noqa: E402

ADDRESS = '0x' + 'ab' * 20


class Clock:
    def __init__(self):
        self.value = 1_800_000_000_000

    def __call__(self):
        self.value += 1
        return self.value


class FakeVenueModelTests(unittest.TestCase):
    def setUp(self):
        self.venue = fake_venue.FakeHyperliquid(clock=Clock())

    def info(self, kind, **extra):
        return self.venue.info({'type': kind, 'user': ADDRESS, **extra})

    def test_decimal_text_is_plain_and_exact(self):
        self.assertEqual(fake_venue.text('1E+2'), '100')
        self.assertEqual(fake_venue.text('0.10000'), '0.1')
        self.assertEqual(fake_venue.text('-0.0'), '0')
        self.assertEqual(fake_venue.text('0.000001'), '0.000001')

    def test_orders_fill_into_positions_fills_and_history(self):
        entry = self.venue.place(ADDRESS, 'BTC', 'B', '100', '1')['oid']
        stop = self.venue.place(ADDRESS, 'BTC', 'A', '95', '1', kind='stop', trigger='95')['oid']
        book = self.info('frontendOpenOrders')
        self.assertEqual({o['oid'] for o in book}, {entry, stop})
        stop_row = next(o for o in book if o['oid'] == stop)
        self.assertEqual((stop_row['orderType'], stop_row['isTrigger'], stop_row['triggerPx'], stop_row['reduceOnly']),
                         ('Stop Market', True, '95', True))
        partial = self.venue.fill(ADDRESS, entry, sz='0.4')
        self.assertEqual((partial['dir'], partial['startPosition'], partial['sz']), ('Open Long', '0', '0.4'))
        self.assertEqual(next(o for o in self.info('frontendOpenOrders') if o['oid'] == entry)['sz'], '0.6')
        self.venue.fill(ADDRESS, entry)
        self.assertNotIn(entry, {o['oid'] for o in self.info('frontendOpenOrders')})
        self.assertEqual(self.info('historicalOrders')[-1]['status'], 'filled')
        position = self.info('clearinghouseState')['assetPositions'][0]['position']
        self.assertEqual((position['coin'], position['szi'], position['entryPx']), ('BTC', '1', '100'))
        close = self.venue.fill(ADDRESS, stop)
        self.assertEqual((close['dir'], close['closedPnl'], close['side']), ('Close Long', '-5', 'A'))
        self.assertEqual(self.info('clearinghouseState')['assetPositions'], [])
        self.assertEqual([f['oid'] for f in self.info('userFills')], [stop, entry, entry])

    def test_cancel_records_status_unless_removed_silently(self):
        a = self.venue.place(ADDRESS, 'ETH', 'B', '2000', '1')['oid']
        b = self.venue.place(ADDRESS, 'ETH', 'B', '1990', '1')['oid']
        self.venue.cancel(ADDRESS, a)
        self.venue.cancel(ADDRESS, b, record=False)
        self.assertEqual(self.info('frontendOpenOrders'), [])
        self.assertEqual([(h['order']['oid'], h['status']) for h in self.info('historicalOrders')], [(a, 'canceled')])

    def test_flip_and_short_close_pnl(self):
        self.venue.raw_fill(ADDRESS, 'SOL', 'A', '150', '2')
        flip = self.venue.raw_fill(ADDRESS, 'SOL', 'B', '140', '3')
        self.assertEqual((flip['dir'], flip['closedPnl']), ('Short > Long', '20'))
        self.assertEqual(self.info('clearinghouseState')['assetPositions'][0]['position']['szi'], '1')

    def test_markets_candles_and_contexts_are_deterministic(self):
        self.assertNotIn('NEW', [m['name'] for m in self.info('meta')['universe']])
        self.venue.list_market({'name': 'NEW', 'szDecimals': 0, 'maxLeverage': 3, 'px': '1'})
        self.assertIn('NEW', [m['name'] for m in self.info('meta')['universe']])
        meta, contexts = self.info('metaAndAssetCtxs')
        self.assertEqual(len(meta['universe']), len(contexts))
        req = {'coin': 'BTC', 'interval': '1h', 'startTime': 0, 'endTime': 1_800_000_000_000}
        first = self.venue.info({'type': 'candleSnapshot', 'req': req})
        self.assertEqual(first, self.venue.info({'type': 'candleSnapshot', 'req': req}))
        self.assertTrue(all(float(c['h']) >= max(float(c['o']), float(c['c'])) and c['T'] == c['t'] + 3_599_999 for c in first))
        self.assertEqual(self.info('spotMeta')['tokens'][0]['tokenId'], fake_venue.USDC_TOKEN_ID)

    def test_builder_catalogue_reads_answer_with_no_builder_dex(self):
        # The Hyperliquid reader marks positions incomplete when any of these fails.
        metas = self.venue.info({'type': 'allPerpMetas'})
        self.assertEqual([m['collateralToken'] for m in metas], [0])
        self.assertNotIn(':', ''.join(m['name'] for m in metas[0]['universe']))
        self.assertEqual(self.venue.info({'type': 'perpDexs'}), [None])
        self.assertEqual(self.venue.info({'type': 'perpConciseAnnotations'}), [])

    def test_scripted_failures_and_gate(self):
        self.venue.fail('userFills', 1, 503)
        self.assertEqual(self.venue.before('userFills', '10.0.0.1'), 503)
        self.assertIsNone(self.venue.before('userFills', '10.0.0.1'))
        self.venue.arm_gate('frontendOpenOrders')
        import threading
        done = threading.Event()
        threading.Thread(target=lambda: (self.venue.before('frontendOpenOrders', '10.0.0.2'), done.set()), daemon=True).start()
        for _ in range(100):
            if self.venue.gate_state()['entered']:
                break
            done.wait(0.01)
        self.assertEqual(self.venue.gate_state()['peer'], '10.0.0.2')
        self.assertFalse(done.is_set())
        self.venue.release_gate()
        self.assertTrue(done.wait(2))
        self.assertEqual(self.venue.summary()['requests'], {'userFills': 2, 'frontendOpenOrders': 1})


@unittest.skipUnless(shutil.which('openssl'), 'openssl is required for the TLS server test')
class FakeVenueServerTests(unittest.TestCase):
    def test_tls_venue_and_plain_control_ports(self):
        with tempfile.TemporaryDirectory() as temp:
            cert, key = os.path.join(temp, 'c.pem'), os.path.join(temp, 'k.pem')
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '1',
                            '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost'], check=True, capture_output=True)
            venue, tls, plain = fake_venue.serve(0, 0, cert, key, host='127.0.0.1')
            try:
                control = f'http://127.0.0.1:{plain.server_address[1]}'
                request = urllib.request.Request(control + '/control/order', json.dumps(
                    {'address': ADDRESS, 'coin': 'BTC', 'side': 'B', 'px': '100', 'sz': '1'}).encode(), {'Content-Type': 'application/json'})
                oid = json.load(urllib.request.urlopen(request))['oid']
                context = ssl.create_default_context(cafile=cert)
                request = urllib.request.Request(f'https://localhost:{tls.server_address[1]}/info', json.dumps(
                    {'type': 'frontendOpenOrders', 'user': ADDRESS.upper().replace('0X', '0x'), 'dex': ''}).encode(),
                    {'Content-Type': 'application/json'})
                self.assertEqual([o['oid'] for o in json.load(urllib.request.urlopen(request, context=context))], [oid])
                # .NET sends JSON request bodies chunked, without a Content-Length.
                chunks = iter([json.dumps({'type': 'meta'}).encode()])
                request = urllib.request.Request(f'https://localhost:{tls.server_address[1]}/info', chunks,
                                                 {'Content-Type': 'application/json', 'Transfer-Encoding': 'chunked'})
                self.assertIn('BTC', [m['name'] for m in json.load(urllib.request.urlopen(request, context=context))['universe']])
                summary = json.load(urllib.request.urlopen(control + '/control/summary'))
                self.assertEqual(summary['requests'], {'frontendOpenOrders': 1, 'meta': 1})
            finally:
                for server in (tls, plain):
                    server.shutdown()
                    server.server_close()


if __name__ == '__main__':
    unittest.main()
