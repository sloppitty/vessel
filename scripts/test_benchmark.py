"""Tests for the pure parts of scripts/benchmark.py. Docker is mocked: nothing here starts, fills or removes anything."""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

SCRIPTS = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location('benchmark', SCRIPTS / 'benchmark.py')
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)

ANCHOR = datetime(2026, 10, 23, 12, tzinfo=timezone.utc)


def small(seed=7, years=2, scale=0.05):
    return benchmark.generate(seed, years, scale, ANCHOR)


class DatasetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gen = small()

    def test_is_deterministic_from_its_parameters(self):
        self.assertEqual(self.gen.digest(), small().digest())
        self.assertNotEqual(self.gen.digest(), small(seed=8).digest())
        self.assertNotEqual(self.gen.digest(), benchmark.generate(7, 2, 0.05, ANCHOR + timedelta(hours=1)).digest())

    def test_scale_and_years_grow_the_history(self):
        fills = self.gen.table_counts()['imported_fills']
        self.assertGreater(small(scale=0.1).table_counts()['imported_fills'], fills * 1.5)
        times = [datetime.fromisoformat(r['OccurredAtUtc']) for r in self.gen.records('imported_fills')]
        self.assertLess(min(times), ANCHOR - timedelta(days=365 * 1.7))
        self.assertGreater(max(times), ANCHOR - timedelta(days=30))
        self.assertLess(max(times), ANCHOR)

    def test_one_owner_with_portfolios_venues_and_a_disabled_account(self):
        accounts = self.gen.records('accounts')
        self.assertEqual({self.gen.owner}, {r['OwnerId'] for t in benchmark.TABLES for r in self.gen.records(t)
                                            if 'OwnerId' in r})
        real = [a for a in accounts if a['TradingMode'] == 'real' and a['VenueId'] != 'manual']
        self.assertEqual({'hyperliquid', 'lighter', 'risex'}, {a['VenueId'] for a in real})
        self.assertIn('f', {a['IsEnabled'] for a in real})
        self.assertIn(None, {a['PortfolioId'] for a in real})
        self.assertEqual({'real', 'paper'}, {a['TradingMode'] for a in accounts})
        self.assertGreaterEqual(len(self.gen.records('portfolios')), 4)

    def test_plays_cover_every_status_with_their_history(self):
        plays = self.gen.records('plays')
        self.assertEqual({'Draft', 'Planned', 'Paused', 'Open', 'Closed', 'Cancelled', 'Unresolved'},
                         {p['Status'] for p in plays})
        revisions = Counter(r['PlayId'] for r in self.gen.records('play_plan_revisions'))
        reviews = Counter(r['PlayId'] for r in self.gen.records('play_review_revisions'))
        changes = Counter(r['PlayId'] for r in self.gen.records('play_status_changes'))
        for play in plays:
            self.assertEqual(int(play['PlanRevision']), revisions[play['Id']])
            self.assertEqual(int(play['ReviewRevision']), reviews[play['Id']])
            self.assertGreater(int(play['Version']), changes[play['Id']])
            if play['Status'] != 'Cancelled':  # a Draft can be cancelled without planning
                self.assertEqual(play['Status'] == 'Draft', play['PlannedAtUtc'] is None)
            self.assertEqual(play['Status'] in ('Closed', 'Cancelled', 'Unresolved'), play['EndedAtUtc'] is not None)
        self.assertIn('Completed', {p['ReviewCompletion'] for p in plays})
        self.assertTrue(self.gen.records('play_notes'))
        self.assertTrue(self.gen.records('play_order_links'))

    def test_linked_orders_are_unique_and_closed_plays_settle(self):
        links = [r for r in self.gen.records('play_order_links') if r['State'] == 'Linked']
        self.assertEqual(len(links), len({r['ImportedOrderId'] for r in links}))
        orders = {o['Id']: o for o in self.gen.records('imported_orders')}
        fills = defaultdict(Decimal)
        for f in self.gen.records('imported_fills'):
            fills[(f['AccountId'], f['ContractId'], f['ExternalOrderId'])] += Decimal(f['Quantity'])
        closed = {p['Id'] for p in self.gen.records('plays') if p['Status'] == 'Closed'}
        balance = defaultdict(Decimal)
        for link in links:
            if link['PlayId'] in closed:
                o = orders[link['ImportedOrderId']]
                quantity = fills[(o['AccountId'], o['ContractId'], o['ExternalOrderId'])]
                balance[link['PlayId']] += quantity if link['Role'] == 'Entry' else -quantity
        self.assertTrue(closed)
        self.assertEqual(set(), {play for play, rest in balance.items() if rest != 0})

    def test_fills_walk_a_consistent_signed_position_matching_the_snapshot(self):
        """Partial fills, flips and round trips: every effect fits the walk, except across a forced exit."""
        forced = {(r['AccountId'], r['ContractId']) for r in self.gen.records('imported_forced_events')}
        positions = {(p['AccountId'], p['ContractId']): Decimal(p['SignedQuantity'])
                     for p in self.gen.records('account_positions')}
        walks, effects, per_order = defaultdict(Decimal), Counter(), Counter()
        for f in sorted(self.gen.records('imported_fills'), key=lambda r: r['OccurredAtUtc']):
            key = (f['AccountId'], f['ContractId'])
            effects[f['PositionEffect']] += 1
            per_order[(key, f['ExternalOrderId'])] += 1
            if key in forced:
                continue
            before = walks[key]
            after = before + (Decimal(f['Quantity']) if f['Side'] == 'buy' else -Decimal(f['Quantity']))
            effect = ('open' if before == 0 or (before > 0) == (after > before) and after * before > 0
                      else 'flip' if before * after < 0 else 'close')
            self.assertEqual(effect, f['PositionEffect'], f)
            walks[key] = after
        for key, quantity in walks.items():
            if key not in forced:
                self.assertEqual(positions.get(key, Decimal(0)), quantity, key)
        self.assertGreater(effects['flip'], 0)
        self.assertGreater(max(per_order.values()), 1)

    def test_orders_are_placed_before_their_fills_and_finish_after_them(self):
        fills = defaultdict(list)
        for f in self.gen.records('imported_fills'):
            fills[(f['AccountId'], f['ContractId'], f['ExternalOrderId'])].append(datetime.fromisoformat(f['OccurredAtUtc']))
        checked = 0
        for o in self.gen.records('imported_orders'):
            placed, status = datetime.fromisoformat(o['PlacedAtUtc']), datetime.fromisoformat(o['StatusAtUtc'])
            self.assertLessEqual(placed, status, o)
            for at in fills.get((o['AccountId'], o['ContractId'], o['ExternalOrderId']), []):
                self.assertLessEqual(placed, at, o)
                self.assertLessEqual(at, status, o)
                checked += 1
        self.assertEqual(sum(map(len, fills.values())), checked)

    def test_cache_charge_estimate_follows_the_rows_in_scope(self):
        enabled = [a.id for a in self.gen.accounts if a.enabled]
        everything = self.gen.cache_charge(enabled)
        self.assertGreater(everything, self.gen.cache_charge(enabled[:1]) > 4096)
        self.assertGreater(small(scale=0.1).cache_charge([a.id for a in small(scale=0.1).accounts if a.enabled]), everything)
        self.assertTrue(benchmark.cache_status(everything).startswith('hit'))

    def test_phase_two_facts_are_present(self):
        counts = self.gen.table_counts()
        for table in ('funding_payments', 'account_leverage_observations', 'account_risk_observations',
                      'execution_omissions', 'imported_forced_events', 'account_fill_coverage', 'account_history_gaps',
                      'account_history_backfills', 'fill_import_conflicts', 'play_evidence', 'play_rules',
                      'paper_ledger_entries'):
            self.assertGreater(counts[table], 0, table)
        gaps = self.gen.records('account_history_gaps')
        self.assertEqual({True, False}, {g['ClosedAtUtc'] is None for g in gaps})
        self.assertEqual({'forced', 'fee', 'missing-pnl'}, {o['Kind'] for o in self.gen.records('execution_omissions')})

    def test_search_terms_exist(self):
        rare = [line for lines in self.gen.rows.lines.values() for line in lines if benchmark.RARE_WORD in line]
        self.assertEqual(1, len(rare))
        theses = [p['Plan'] for p in self.gen.records('plays') if benchmark.COMMON_WORD in p['Plan']]
        self.assertTrue(theses)

    def test_copy_rows_have_one_field_per_column(self):
        for table, columns in benchmark.TABLES.items():
            for line in self.gen.rows.lines.get(table, []):
                self.assertEqual(len(columns), len(line.split('\t')), table)
        script = ''.join(benchmark.copy_script(self.gen))
        self.assertTrue(script.startswith('BEGIN;\n') and script.endswith('COMMIT;\n'))
        self.assertLess(script.index('COPY accounts '), script.index('COPY imported_fills '))
        self.assertLess(script.index('COPY imported_orders '), script.index('COPY play_order_links '))


class FormattingTests(unittest.TestCase):
    def test_copy_text_escapes_and_round_trips(self):
        self.assertEqual(r'\N', benchmark.copy_value(None))
        self.assertEqual('t', benchmark.copy_value(True))
        self.assertEqual('0.00000001', benchmark.copy_value(Decimal('1E-8')))
        self.assertEqual('62000', benchmark.copy_value(Decimal('6.2E+4')))
        self.assertEqual(r'a\tb\nc\\d', benchmark.copy_value('a\tb\nc\\d'))
        self.assertEqual('{"a":[1,"é"]}', benchmark.copy_value({'a': [1, 'é']}))
        values = [None, 'a\tb\\c', 'line\nbreak', 'monday']
        line = '\t'.join(benchmark.copy_value(v) for v in values)
        self.assertEqual(dict(zip(benchmark.TABLES['reporting_settings'], values)),
                         benchmark.parse_copy(line, 'reporting_settings'))

    def test_prices_follow_the_tick_rule_and_quantities_whole_lots(self):
        self.assertEqual(Decimal('62001'), benchmark.tick_price(62001.4, 5))
        self.assertEqual(Decimal('3100.1'), benchmark.tick_price(3100.12, 4))
        self.assertEqual(Decimal('0.011235'), benchmark.tick_price(0.0112349, 0))
        self.assertEqual(Decimal('123457'), benchmark.tick_price(123456.7, 5))
        self.assertEqual(Decimal('0.00001'), benchmark.lots(0.000001, 5))
        self.assertEqual(Decimal('1.23'), benchmark.lots(Decimal('1.239'), 2))


class EndpointTests(unittest.TestCase):
    def test_deep_pages_come_from_the_counts_or_are_skipped(self):
        self.assertIsNone(benchmark.deep_page(0, 25))
        self.assertIsNone(benchmark.deep_page(25, 25))
        self.assertEqual(2, benchmark.deep_page(26, 25))
        self.assertEqual(12, benchmark.deep_page(570, 25))
        planned, skipped = benchmark.plan_endpoints('a', 'o', operations=267, closed=10, matches=7)
        paths = [spec[1] for spec in planned]
        self.assertIn('/api/activity?page=2&pageSize=100', paths)
        self.assertFalse(any('view=closed' in p or 'offset=' in p for p in paths))
        self.assertEqual(2, len(skipped))
        self.assertTrue(any('10 Closed Plays' in s for s in skipped) and any('7 matches' in s for s in skipped))
        planned, skipped = benchmark.plan_endpoints('a', 'o', operations=12000, closed=570, matches=120)
        paths = [spec[1] for spec in planned]
        self.assertEqual([], skipped)
        self.assertIn('/api/plays?view=closed&page=12&pageSize=25', paths)
        self.assertIn(f'/api/plays/search?q={benchmark.COMMON_WORD}&offset=50', paths)
        self.assertIn('/api/activity?page=61&pageSize=100', paths)

    def test_deep_page_checks_reject_empty_and_clamped_pages(self):
        check, _ = benchmark.page_of('plays', 3)
        self.assertTrue(check({'page': 3, 'plays': [{}]}))
        self.assertFalse(check({'page': 3, 'plays': []}))
        self.assertFalse(check({'page': 1, 'plays': [{}]}))  # the API clamps a page past the end
        self.assertFalse(check({'plays': [{}]}))
        check, _ = benchmark.search_page(50)
        self.assertTrue(check({'offset': 50, 'items': [{}], 'notIndexed': 0}))
        self.assertFalse(check({'offset': 50, 'items': [], 'notIndexed': 0}))
        self.assertFalse(check({'offset': 0, 'items': [{}], 'notIndexed': 0}))
        self.assertFalse(check({'offset': 50, 'items': [{}], 'notIndexed': 2}))

    def test_a_repeat_is_called_a_hit_only_when_the_aggregate_fits_the_cache(self):
        capacity = benchmark.CACHE_CAPACITY
        self.assertTrue(benchmark.cache_status(capacity // 2).startswith('hit'))
        self.assertTrue(benchmark.cache_status(capacity).startswith('unconfirmed'))
        self.assertTrue(benchmark.cache_status(capacity * 2).startswith('not cached'))

    def test_failed_reads_are_recorded_and_never_timed(self):
        endpoint = benchmark.Endpoint('x', '/x', 'all', *benchmark.page_of('rows', 2))
        endpoint.record(200, 5.0, 10, 10, b'{"page": 1, "rows": [1]}', endpoint.steady, True)
        endpoint.record(500, 5.0, 10, 10, b'{}', endpoint.steady, True)
        endpoint.record(200, 5.0, 10, 10, b'not json', endpoint.steady, True)
        endpoint.record(200, 7.0, 10, 10, b'{"page": 2, "rows": [1]}', endpoint.steady, True)
        self.assertEqual([7.0], endpoint.steady)
        self.assertEqual(3, len(endpoint.failures))


class PercentileTests(unittest.TestCase):
    def test_linear_interpolation_between_ranks(self):
        values = [10, 1, 9, 2, 8, 3, 7, 4, 6, 5]
        self.assertAlmostEqual(5.5, benchmark.percentile(values, 50))
        self.assertAlmostEqual(9.55, benchmark.percentile(values, 95))
        self.assertEqual(1, benchmark.percentile(values, 0))
        self.assertEqual(10, benchmark.percentile(values, 100))
        self.assertEqual(4.2, benchmark.percentile([4.2], 95))
        self.assertEqual({'n': 3, 'p50': 2, 'p95': 2.9, 'min': 1, 'max': 3},
                         {k: round(v, 6) for k, v in benchmark.summarize([3, 1, 2]).items()})
        self.assertIsNone(benchmark.summarize([]))
        with self.assertRaises(ValueError):
            benchmark.percentile([], 50)
        with self.assertRaises(ValueError):
            benchmark.percentile([1], 101)


def arguments(**overrides):
    values = dict(disposable=True, years=3, scale=1, rounds=20, warmup=5, out=None)
    values.update(overrides)
    return argparse.Namespace(**values)


class SafetyTests(unittest.TestCase):
    def test_refuses_without_an_explicit_disposable_target_or_under_ci(self):
        with self.assertRaisesRegex(benchmark.Refused, '--disposable'):
            benchmark.check_arguments(arguments(disposable=False), environ={})
        with self.assertRaisesRegex(benchmark.Refused, 'CI'):
            benchmark.check_arguments(arguments(), environ={'CI': 'true'})
        with self.assertRaises(benchmark.Refused):
            benchmark.check_arguments(arguments(years=0), environ={})
        benchmark.check_arguments(arguments(), environ={})

    def test_main_refuses_before_starting_anything(self):
        with mock.patch.object(benchmark.subprocess, 'run') as run, mock.patch.object(benchmark.subprocess, 'Popen') as popen:
            with mock.patch('sys.stderr'):
                self.assertEqual(2, benchmark.main(['--years', '1']))
        run.assert_not_called()
        popen.assert_not_called()

    def test_children_never_inherit_server_settings_or_connection_strings(self):
        inherited = {'PATH': '/bin', 'HOME': '/home/x', 'ConnectionStrings__Vessel': 'Host=owner-db',
                     'CUSTOMCONNSTR_Vessel': 'Host=owner-db', 'Vessel__Auth__Token': 'owner-token',
                     'Vessel_TEST_POSTGRES': 'Host=test', 'VESSEL_S3_SECRET_ACCESS_KEY': 'secret',
                     'ASPNETCORE_ENVIRONMENT': 'Development', 'POSTGRES_PASSWORD': 'owner-password'}
        with mock.patch.dict(os.environ, inherited, clear=True):
            child = benchmark.child_environment({'ConnectionStrings__Vessel': 'Host=bench'})
        self.assertEqual({'PATH': '/bin', 'HOME': '/home/x', 'ConnectionStrings__Vessel': 'Host=bench'}, child)

    def test_secrets_never_reach_a_command_line_and_are_redacted(self):
        run = benchmark.Run(arguments(), log=lambda *_: None)
        calls = []

        def fake(args, **kwargs):
            calls.append((args, kwargs.get('env') or {}))
            out = '127.0.0.1:40000\n' if args[:2] == ['docker', 'port'] else ''
            return subprocess.CompletedProcess(args, 0, out, '')
        with mock.patch.object(benchmark.subprocess, 'run', fake):
            run.start_containers()
        for args, env in calls:
            for secret in run.secrets:
                self.assertNotIn(secret, ' '.join(args))
        environments = [env for args, env in calls if args[:2] == ['docker', 'run']]
        self.assertEqual(run.pg_password, environments[0]['POSTGRES_PASSWORD'])
        self.assertTrue(all(f'{benchmark.LABEL}={run.id}' in args for args, _ in calls if args[:2] == ['docker', 'run']))
        self.assertEqual('token [redacted]', run.redact('token ' + run.token))

    def test_cleanup_removes_only_this_runs_labelled_containers(self):
        run = benchmark.Run(arguments(), log=lambda *_: None)
        run.started = [run.containers['postgres'], 'someone-elses']
        calls = []

        def fake(args, **kwargs):
            calls.append(args)
            if args[:2] == ['docker', 'inspect']:
                return subprocess.CompletedProcess(args, 0, run.id if args[-1] == run.containers['postgres'] else 'other', '')
            return subprocess.CompletedProcess(args, 0, '', '')
        with mock.patch.object(benchmark.subprocess, 'run', fake):
            problems = run.cleanup()
        removed = [args[-1] for args in calls if args[:2] == ['docker', 'rm']]
        self.assertEqual([run.containers['postgres']], removed)
        self.assertEqual(1, len(problems))
        self.assertIn('someone-elses', problems[0])

    def test_an_interrupted_start_is_still_removed(self):
        docker = FakeDocker(create_then=KeyboardInterrupt)
        run = benchmark.Run(arguments(), log=lambda *_: None)
        with mock.patch.object(benchmark.subprocess, 'run', docker), mock.patch.object(benchmark.time, 'sleep'):
            with self.assertRaises(KeyboardInterrupt):
                run.start_containers()
            self.assertEqual({run.containers['postgres']}, set(docker.containers))
            self.assertEqual([], run.cleanup())
        self.assertEqual({}, docker.containers)
        self.assertEqual([run.containers['postgres']], docker.removed)

    def test_a_failed_start_removes_what_the_daemon_created_and_nothing_else(self):
        for creates in (True, False):
            docker = FakeDocker(fail_with=125, creates=creates, others={'unrelated': 'other-run'})
            run = benchmark.Run(arguments(), log=lambda *_: None)
            with mock.patch.object(benchmark.subprocess, 'run', docker), mock.patch.object(benchmark.time, 'sleep'):
                with self.assertRaises(RuntimeError):
                    run.start_containers()
                self.assertEqual([], run.cleanup())
            self.assertEqual({'unrelated': 'other-run'}, docker.containers)
            self.assertEqual([run.containers['postgres']] if creates else [], docker.removed)


class FakeDocker:
    """A daemon of named, labelled containers: `docker run` creates one and then fails or is interrupted as told."""

    def __init__(self, create_then=None, fail_with=None, creates=True, others=None):
        self.containers, self.removed = dict(others or {}), []
        self.create_then, self.fail_with, self.creates = create_then, fail_with, creates

    def __call__(self, args, **kwargs):
        args = [str(a) for a in args]
        done = lambda code=0, out='': subprocess.CompletedProcess(args, code, out, '')
        if args[:2] == ['docker', 'run']:
            name, label = args[args.index('--name') + 1], args[args.index('--label') + 1].split('=', 1)[1]
            if self.creates:
                self.containers[name] = label
            if self.create_then:
                raise self.create_then()
            return done(self.fail_with or 0)
        if args[:2] == ['docker', 'ps']:
            wanted = args[args.index('--filter') + 1].split('=', 2)[2]
            return done(0, '\n'.join(n for n, label in self.containers.items() if label == wanted))
        if args[:2] == ['docker', 'inspect']:
            return done(0, self.containers[args[-1]]) if args[-1] in self.containers else done(1)
        if args[:2] == ['docker', 'rm']:
            self.removed.append(args[-1])
            self.containers.pop(args[-1], None)
            return done()
        return done()


if __name__ == '__main__':
    unittest.main()
