#!/usr/bin/env python3
"""Phase 2, item 14d: a synthetic multi-year Release benchmark of Vessel's heaviest reads.

Starts its own throwaway PostgreSQL 18 and RabbitMQ containers, applies the migrations explicitly (check-config, identity,
journal, ensure-owner, provision-messaging, as deploy/migrate does), seeds one owner's deterministic synthetic journal with
COPY, rebuilds the journal search projection with its offline command and starts the built API DLL in Release
(Production, Token mode, a generated token kept in memory, no worker modules). It then times Activity, Review, the Plays
list, Overview, open risk and journal search: warm-up rounds, then measured rounds interleaved across endpoints. A read
through the Activity facts cache is made after a committed no-op revision write and repeated; the report infers whether
the repeat can be a hit from the cache's capacity. Deep pages come from the counts, and scenarios the dataset cannot
populate are skipped and named. Prints a Markdown report, then stops the API and removes every container carrying the
run's label and its temporary directory, after errors, Ctrl+C and SIGTERM too.

Never reads .env, never uses a default connection string, never touches the owner's database and never runs in CI.

    python3 scripts/benchmark.py --disposable [--seed 1] [--years 3] [--scale 1] [--warmup 5] [--rounds 20] [--out FILE]
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from decimal import ROUND_DOWN, ROUND_HALF_EVEN, Decimal
import gzip
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dev import verification_environment  # noqa: E402  (the shared filter of inherited server settings)

ROOT = Path(__file__).resolve().parent.parent
BACKEND = ROOT / 'backend'
API_PROJECT = BACKEND / 'src' / 'Vessel.Api'
API_BIN = API_PROJECT / 'bin' / 'Release' / 'net10.0'
# The images the deployment Compose example pins.
POSTGRES_IMAGE = 'postgres:18-alpine'
RABBIT_IMAGE = 'rabbitmq:4.3.6-management'
LABEL = 'vessel-bench.run'
DATABASE = 'vessel_bench'
UTC = timezone.utc
D = Decimal
ZERO = D(0)

# ---------------------------------------------------------------------------------------------------------------------
# Synthetic market and journal shape
# ---------------------------------------------------------------------------------------------------------------------

# asset: (base price, daily log volatility, size decimals). Prices follow the Hyperliquid rule (5 significant figures,
# at most 6 - szDecimals decimals) on every venue, which keeps them valid tick multiples everywhere.
ASSETS = {
    'BTC': (D('62000'), 0.028, 5),
    'ETH': (D('3100'), 0.034, 4),
    'SOL': (D('145'), 0.045, 2),
    'HYPE': (D('28'), 0.055, 2),
    'DOGE': (D('0.16'), 0.05, 0),
    '1000PEPE': (D('0.011'), 0.06, 0),
}
# venue -> canonical contract -> native market ID (None when the venue uses the canonical key itself).
MARKETS = {
    'hyperliquid': {'BTC': None, 'ETH': None, 'SOL': None, 'HYPE': None, 'DOGE': None, '1000PEPE': 'kPEPE'},
    'lighter': {'BTC': '1', 'ETH': '0', 'SOL': '2', 'HYPE': '24', '1000PEPE': '3'},
    'risex': {'BTC': '1', 'ETH': '2', 'SOL': '3'},
}
VENUES = {
    'hyperliquid': dict(scope='primary-perpetual-dex', maker=D('0.00015'), taker=D('0.00045'), fee_basis='reported',
                        pnl='gross', fill_leverage=False, flips=True,
                        types=('Limit', 'Market', 'Stop Market', 'Take Profit Market'),
                        statuses={'filled': 'filled', 'canceled': 'canceled', 'open': 'open'}),
    'lighter': dict(scope='lighter-perps-account', maker=ZERO, taker=ZERO, fee_basis='standard-account-free',
                    pnl='gross', fill_leverage=False, flips=True,
                    types=('limit', 'market', 'stop-loss', 'take-profit'),
                    statuses={'filled': 'filled', 'canceled': 'canceled', 'open': 'open'}),
    'risex': dict(scope='risex-perps-account', maker=D('0.0002'), taker=D('0.0005'), fee_basis='reported',
                  pnl='net-of-fee', fill_leverage=True, flips=False,
                  types=('limit', 'market', 'stop_market', 'take_profit_market'),
                  statuses={'filled': 'FILLED', 'canceled': 'CANCELLED', 'open': 'OPEN'}),
}
PORTFOLIOS = [('core', 'Core', 'briefcase'), ('swing', 'Swing', 'chart-line'), ('lab', 'Experiments', 'rocket')]
# key, venue, name, portfolio key (None: unassigned), enabled, activity weight, equity, contracts, keeps history
ACCOUNTS = [
    ('hl-main', 'hyperliquid', 'Hyperliquid main', 'core', True, 1.6, 40000, ['BTC', 'ETH', 'SOL', 'HYPE'], True),
    ('hl-swing', 'hyperliquid', 'Hyperliquid swing', 'swing', True, 0.9, 15000, ['BTC', 'ETH', 'DOGE', '1000PEPE'], False),
    ('hl-scalp', 'hyperliquid', 'Hyperliquid scalps', 'lab', True, 1.4, 8000, ['BTC', 'ETH', 'SOL'], True),
    ('lighter-main', 'lighter', 'Lighter main', 'core', True, 1.1, 25000, ['BTC', 'ETH', 'SOL', 'HYPE'], True),
    ('lighter-sub', 'lighter', 'Lighter sub-account', 'swing', True, 0.7, 6000, ['ETH', 'SOL', '1000PEPE'], False),
    ('risex-main', 'risex', 'RISEx main', 'core', True, 1.0, 12000, ['BTC', 'ETH', 'SOL'], False),
    ('risex-alt', 'risex', 'RISEx alt', None, True, 0.6, 4000, ['BTC', 'ETH'], False),
    ('hl-old', 'hyperliquid', 'Old Hyperliquid wallet', 'lab', False, 0.8, 10000, ['BTC', 'ETH', 'SOL'], False),
]
# Round trips per account and year at scale 1, before the account's weight.
ROUND_TRIPS_PER_YEAR = 750
# Ended Plays per year at scale 1; current ones (drafts, planned, paused, open) scale with --scale only.
ENDED_PLAYS_PER_YEAR = {'Closed': 190, 'Cancelled': 45, 'Unresolved': 6}
CURRENT_PLAYS = {'Draft': 20, 'Planned': 14, 'Paused': 4, 'Open': 8}
USDC_TOKEN = '0x6d1e7cde53ba9467b783cb7c530ce054'

SETUPS = ['breakout retest', 'range reclaim', 'trend pullback', 'failed breakdown', 'funding squeeze', 'liquidity sweep',
          'higher-low continuation', 'news fade', 'mean reversion', 'weekly open reclaim', 'deviation below range',
          'momentum continuation']
FRAMES = ['15m', '1h', '4h', 'daily']
REASONS = ['open interest rising into resistance', 'funding reset after the flush', 'volume confirmed the reclaim',
           'the prior high held as support', 'majors leading, alts lagging', 'the breakout retest held on the hourly',
           'liquidations cleared the late longs', 'spot bid absorbing the sell side', 'a clean higher low on the 4h']
INVALIDATIONS = ['a close back inside the range', 'losing the reclaimed level on the 1h', 'funding flipping hard positive',
                 'the higher low breaking', 'a daily close below the retest']
STRATEGIES = ['Scale out at the first target, trail the rest.', 'One target, stop to break-even after the first push.',
              'Planned size only; no adds.', 'Fade the extension into the prior high.']
LESSONS = ['Waited for the retest instead of chasing; that paid.', 'Moved the stop too early and got wicked out.',
           'Size was right; the exit was early.', 'Should have skipped it: the setup was not in the plan.',
           'The funding read was right but the timing was not.', 'Followed the plan; the market simply did not follow.']
NEXT_ACTIONS = ['Only take the retest after a full candle close.', 'Keep the stop where the plan put it.',
                'Write the invalidation before the entry, not after.', 'Halve size after two losses in a row.',
                'Check funding before every breakout entry.']
MISTAKE_TAGS = ['late-entry', 'moved-stop', 'oversized', 'fomo', 'early-exit', 'no-plan']
LOG_TEXTS = ['Filled on the retest, stop resting below the level.', 'First target hit; stop to break-even.',
             'Funding turning against the position, watching closely.', 'Price chopping at the entry, no action.',
             'Added nothing; the plan says no adds.', 'Volume drying up into the target.',
             'Résumé: the thesis still holds after the flush.', 'Spread widened during the news; stayed patient.']
EVIDENCE_NOTES = ['Hourly chart at the entry with the reclaimed level marked.', 'Funding and open interest before entry.',
                  'The retest candle and the volume spike.', 'Exit screenshot after the target filled.',
                  'Daily structure: higher lows since the range low.', '']
RULES = [('the first target fills', 'move the stop to break-even and trail the rest'),
         ('two losses in a row', 'halve the next position size'),
         ('funding is above 0.05 % per hour', 'skip breakout longs until it resets')]
CANCEL_REASONS = ['Invalidated', 'Missed', 'ChangedMind', 'Expired', 'Mistake', 'Other']
CANCEL_NOTES = ['Price never came back to the entry.', 'The setup broke before the trigger.',
                'Changed my mind after the funding print.', '']
# A word that occurs exactly once (in one evidence note), for the rare search.
RARE_WORD = 'zephyrine'
COMMON_WORD = 'retest'


def fmt(value):
    """An exact decimal as a plain string, never in exponent form."""
    text = format(value, 'f')
    if '.' in text:
        text = text.rstrip('0').rstrip('.')
    return text or '0'


def iso(at):
    return at.astimezone(UTC).isoformat(timespec='microseconds')


def tick_price(value, size_decimals):
    """The Hyperliquid price rule: integers always; otherwise 5 significant figures and at most 6 - szDecimals decimals."""
    value = D(repr(value)) if isinstance(value, float) else value
    if value >= 100000:
        return value.quantize(D(1))
    decimals = max(0, min(6 - size_decimals, 4 - math.floor(math.log10(value))))
    return value.quantize(D(1).scaleb(-decimals), ROUND_HALF_EVEN)


def lots(value, size_decimals):
    """A quantity in whole lots of the size step, rounded down, never below one lot."""
    step = D(1).scaleb(-size_decimals)
    value = value if isinstance(value, Decimal) else D(repr(value))
    return max(step, value.quantize(step, ROUND_DOWN))


def money(value):
    return value.quantize(D('0.000001'), ROUND_HALF_EVEN)


class Rows:
    """COPY text rows per table, in insertion order. Values: None, bool, int, Decimal, str, datetime, dict/list (JSON)."""

    def __init__(self):
        self.lines = defaultdict(list)

    def add(self, table, *values):
        if len(values) != len(TABLES[table]):
            raise ValueError(f'{table}: {len(values)} values for {len(TABLES[table])} columns')
        self.lines[table].append('\t'.join(copy_value(v) for v in values))


def copy_value(value):
    """One field in PostgreSQL's COPY text format."""
    if value is None:
        return r'\N'
    if isinstance(value, bool):
        return 't' if value else 'f'
    if isinstance(value, Decimal):
        text = fmt(value)
    elif isinstance(value, datetime):
        text = iso(value)
    elif isinstance(value, (dict, list)):
        text = json.dumps(value, separators=(',', ':'), ensure_ascii=False)
    else:
        text = str(value)
    return text.replace('\\', '\\\\').replace('\t', '\\t').replace('\n', '\\n').replace('\r', '\\r')


def parse_copy(line, table):
    """The inverse of copy_value for tests: a dict of column -> text (None for NULL)."""
    def unescape(field):
        if field == r'\N':
            return None
        return re.sub(r'\\(.)', lambda m: {'t': '\t', 'n': '\n', 'r': '\r'}.get(m.group(1), m.group(1)), field)
    return dict(zip(TABLES[table], (unescape(f) for f in line.split('\t'))))


# Insertion order respects every foreign key. Columns are quoted EF names.
TABLES = {
    'portfolios': ['Id', 'OwnerId', 'TradingMode', 'Name', 'Icon'],
    'accounts': ['Id', 'OwnerId', 'TradingMode', 'StartingCapitalUsd', 'VenueId', 'Name', 'PortfolioId', 'Address',
                 'SourceId', 'ManualAccountValueUsd', 'LastSyncedAtUtc', 'SyncStatus', 'LastSyncError', 'HistoryNotice',
                 'IsEnabled', 'KeepExecutionHistory', 'SettingsRevision'],
    'risk_settings': ['OwnerId', 'DailyLossLimitR', 'OpenRiskLimitPercent', 'UpdatedAtUtc', 'WeeklyLossLimitR'],
    'reporting_settings': ['OwnerId', 'TimeZone', 'UpdatedAtUtc', 'WeekStart'],
    'sizing_settings': ['OwnerId', 'RiskPercent', 'UpdatedAtUtc'],
    'paper_settings': ['OwnerId', 'CheckIntervalMinutes', 'Enabled'],
    'paper_ledger_entries': ['Id', 'AccountId', 'AmountUsd', 'ExecutionId', 'Kind', 'OccurredAtUtc', 'OwnerId', 'PlayId',
                             'TradingMode'],
    'imported_orders': ['Id', 'OwnerId', 'AccountId', 'ContractId', 'VenueContractId', 'ExternalOrderId', 'Side',
                        'OrderType', 'LimitPrice', 'TriggerPrice', 'ReduceOnly', 'IsPositionTpsl', 'OriginalSize',
                        'RemainingSize', 'PlacedAtUtc', 'Status', 'VenueStatus', 'StatusAtUtc', 'ObservedAtUtc',
                        'ExecutionsOmitted'],
    'imported_fills': ['Id', 'OwnerId', 'AccountId', 'ContractId', 'VenueContractId', 'SourceFillId', 'Side', 'Direction',
                       'PositionEffect', 'Price', 'Quantity', 'Fee', 'FeeToken', 'FeeBasis', 'ClosedPnlUsd', 'PnlBasis',
                       'OccurredAtUtc', 'ExternalOrderId', 'TransactionHash', 'Leverage', 'Backfilled'],
    'funding_payments': ['Id', 'OwnerId', 'AccountId', 'SourceId', 'ContractId', 'VenueContractId', 'Amount', 'Asset',
                         'OccurredAtUtc', 'OccurredAtUnixNanoseconds'],
    'account_funding_states': ['OwnerId', 'AccountId', 'CoveredFromUtc', 'CoveredToUtc', 'LastAttemptFromUtc',
                               'LastAttemptToUtc', 'LastReadComplete', 'PnlBasis', 'Notice'],
    'account_leverage_observations': ['Id', 'OwnerId', 'AccountId', 'ContractId', 'VenueContractId', 'ObservedAtUtc',
                                      'SignedQuantity', 'EntryPrice', 'Leverage'],
    'account_risk_observations': ['Id', 'OwnerId', 'AccountId', 'ContractId', 'VenueContractId', 'ObservedAtUtc',
                                  'SignedQuantity', 'EntryPrice', 'LiquidationPrice', 'MarginMode'],
    'account_snapshots': ['OwnerId', 'AccountId', 'ObservedAtUtc', 'ValueScope', 'AccountValueUsd', 'WithdrawableUsd',
                          'AvailableMarginUsd', 'MarginUsedUsd', 'StablecoinsObservedAtUtc', 'AccountMode',
                          'StablecoinScope'],
    'account_positions': ['OwnerId', 'AccountId', 'ContractId', 'VenueContractId', 'SignedQuantity', 'EntryPrice',
                          'UnrealizedPnlUsd', 'MarginUsedUsd', 'Leverage', 'LiquidationPrice', 'MarginMode'],
    'account_stablecoins': ['OwnerId', 'AccountId', 'TokenIndex', 'TokenId', 'Symbol', 'Total', 'Held', 'Available'],
    'execution_omissions': ['Id', 'OwnerId', 'AccountId', 'Kind', 'Reason', 'SourceId', 'ContractId', 'VenueContractId',
                            'OrderId', 'OccurredAtUtc', 'UncertainFromUtc', 'UncertainToUtc', 'FirstSeenAtUtc',
                            'LastSeenAtUtc'],
    'imported_forced_events': ['Id', 'OwnerId', 'AccountId', 'SourceId', 'Kind', 'ContractId', 'VenueContractId', 'Side',
                               'Quantity', 'Price', 'OccurredAtUtc', 'OrderId', 'Fee', 'RealizedPnl', 'Exclusion',
                               'FirstSeenAtUtc', 'LastSeenAtUtc'],
    'fill_import_conflicts': ['Id', 'OwnerId', 'AccountId', 'FillId', 'ContractId', 'SourceFillId', 'Fields',
                              'FirstSeenAtUtc', 'LastSeenAtUtc', 'Reports'],
    'account_fill_coverage': ['Id', 'OwnerId', 'AccountId', 'Source', 'FirstObservedAtUtc', 'LastObservedAtUtc',
                              'ImportCount', 'RequestedFromUtc', 'RequestedToUtc', 'Truncated', 'Complete',
                              'CoveredFromUtc', 'CoveredToUtc', 'GapFromUtc', 'GapToUtc'],
    'account_history_gaps': ['Id', 'OwnerId', 'AccountId', 'FromUtc', 'ToUtc', 'OpenedAtUtc', 'OpenedByCoverageId',
                             'ClosedAtUtc', 'ClosedByCoverageId', 'BeyondVenueReach', 'OpenedByBackfillId'],
    'account_history_backfills': ['Id', 'OwnerId', 'AccountId', 'GapId', 'State', 'FromUtc', 'ToUtc', 'StartKnown',
                                  'CursorUtc', 'Witness', 'Pages', 'RawRows', 'AddedFills', 'OmittedExecutions',
                                  'FirstRawAtUtc', 'StartServed', 'ContinuityLost', 'Exhausted', 'NextPageAtUtc',
                                  'Failures', 'Error', 'CreatedAtUtc', 'UpdatedAtUtc', 'FinishedAtUtc'],
    'play_rules': ['Id', 'OwnerId', 'TradingMode', 'Version', 'If', 'Then', 'SourcePlayId', 'CreatedAtUtc',
                   'ArchivedAtUtc'],
    'plays': ['Id', 'OwnerId', 'AccountId', 'TradingMode', 'VenueId', 'ContractId', 'InstrumentSource', 'Status', 'Title',
              'Plan', 'Direction', 'Drawings', 'Review', 'Origin', 'ReviewAdherence', 'ReviewMistakeTag', 'ReviewLesson',
              'ReviewNextAction', 'ReviewRule', 'RuleVersion', 'RuleIf', 'RuleThen', 'ReviewRevision', 'ReviewedAtUtc',
              'ReviewCompletion', 'ReviewCompletedAtUtc', 'PlanRevision', 'Version', 'CancelReason', 'CreatedAtUtc',
              'UpdatedAtUtc', 'PlannedAtUtc', 'EndedAtUtc'],
    'play_plan_revisions': ['Id', 'OwnerId', 'PlayId', 'Number', 'Status', 'Plan', 'Reason', 'CreatedAtUtc', 'Preflight',
                            'Rule'],
    'play_status_changes': ['Id', 'OwnerId', 'PlayId', 'From', 'To', 'Reason', 'Note', 'Source', 'UnresolvedReason',
                            'Evidence', 'PlayVersion', 'OccurredAtUtc'],
    'play_order_links': ['Id', 'OwnerId', 'PlayId', 'AccountId', 'ImportedOrderId', 'Role', 'EntryId', 'LevelId', 'State',
                         'Source', 'CreatedAtUtc', 'UpdatedAtUtc'],
    'play_review_revisions': ['Id', 'OwnerId', 'PlayId', 'Number', 'OccurredAtUtc', 'Review', 'StructuredReview',
                              'Action'],
    'play_notes': ['Id', 'OwnerId', 'PlayId', 'RequestId', 'Text', 'State', 'CreatedAtUtc'],
    'play_evidence': ['Id', 'OwnerId', 'PlayId', 'ObjectKey', 'ContentType', 'SizeBytes', 'Sha256', 'Source', 'Note',
                      'Markup', 'CreatedAtUtc', 'UpdatedAtUtc'],
}
# Every table whose rows are counted in the report: the seeded ones plus the projections the rebuild writes.
COUNTED = list(TABLES) + ['play_search', 'play_search_texts', 'activity_facts_revisions']




class Account:
    def __init__(self, gen, spec):
        (self.key, self.venue, self.name, portfolio, self.enabled, self.weight, equity, self.contracts,
         self.keep_history) = spec
        self.id = gen.uuid()
        self.portfolio_id = gen.portfolio_ids[portfolio] if portfolio else None
        if self.venue == 'lighter':
            # A Lighter account index; it has no stored owner address.
            self.address, self.source_id = None, str(gen.rng.randrange(10_000, 900_000))
        else:
            self.address = '0x' + gen.hex(160)
            self.source_id = self.address
        self.equity = D(equity)
        self.realized = ZERO
        self.orders = self.fills = 0
        self.leverage = {c: gen.rng.choice([3, 5, 10, 20] if c in ('BTC', 'ETH') else [3, 5, 10]) for c in self.contracts}
        self.begin, self.end = gen.window(self)
        self.gap_plan = []


class RoundTrip:
    """One signed-position round trip on an account's native partition: entry fills, then exits (or still open)."""

    def __init__(self, account, contract, start, direction='long'):
        self.account, self.contract, self.start, self.direction = account, contract, start, direction
        self.end = None
        self.flip_in = self.flip_out = self.open_at_end = self.forced = self.omitted = False
        self.qty = self.average = self.remaining = ZERO
        self.opened = start
        self.entry_orders, self.exit_orders, self.fills = [], [], []
        self.levels = {}

    @property
    def sign(self):
        return 1 if self.direction == 'long' else -1


class Generator:
    """A deterministic synthetic journal for one owner, from --seed, --years, --scale and the anchor time."""

    def __init__(self, seed, years, scale, anchor):
        if years <= 0 or scale <= 0:
            raise ValueError('years and scale must be positive')
        self.rng = random.Random(seed)
        self.years, self.scale = years, scale
        self.anchor = anchor.astimezone(UTC).replace(microsecond=0)
        self.start = self.anchor - timedelta(days=round(365.25 * years))
        self.rows = Rows()
        self.counts, self.statuses = Counter(), Counter()
        self.round_trips, self.orders = [], []
        self.first_closed = None
        self.owner = self.uuid()
        days = (self.anchor - self.start).days + 2
        self.prices = {a: self.walk(base, vol, days) for a, (base, vol, _) in ASSETS.items()}

    # -- helpers ------------------------------------------------------------------------------------------------------
    def uuid(self):
        return str(uuid.UUID(int=self.rng.getrandbits(128), version=4))

    def hex(self, bits):
        return format(self.rng.getrandbits(bits), f'0{bits // 4}x')

    def walk(self, base, vol, days):
        """A mean-reverting daily log-price walk around the base price."""
        log_base = math.log(float(base))
        x, values = log_base, []
        for _ in range(days + 1):
            values.append(x)
            x += self.rng.gauss(0, vol) + 0.02 * (log_base - x)
        return values

    def price(self, asset, at):
        series = self.prices[asset]
        t = (at - self.start).total_seconds() / 86400
        i = max(0, min(int(t), len(series) - 2))
        f = min(1.0, max(0.0, t - i))
        return math.exp(series[i] * (1 - f) + series[i + 1] * f)

    def between(self, start, end):
        return start + timedelta(seconds=self.rng.uniform(0, max(0.0, (end - start).total_seconds())))

    @staticmethod
    def ms(at):
        return at.replace(microsecond=at.microsecond // 1000 * 1000)

    def window(self, account):
        """When the account traded: enabled accounts until shortly before the anchor, the disabled one early on."""
        span = self.anchor - self.start
        if not account.enabled:
            return self.start + span * 0.02, self.start + span * 0.55
        return self.start + span * self.rng.uniform(0, 0.08), self.anchor - timedelta(minutes=30)

    # -- generation ---------------------------------------------------------------------------------------------------
    def generate(self):
        self.portfolios()
        self.accounts = [Account(self, spec) for spec in ACCOUNTS]
        self.account_rows()
        self.settings()
        for account in self.accounts:
            self.plan_gaps(account)
            self.trade(account)
        self.forced_and_omitted()
        self.plays()
        self.emit_venue_facts()
        for account in self.accounts:
            self.emit_account_state(account)
            self.coverage(account)
        self.paper()
        return self

    def portfolios(self):
        self.portfolio_ids = {}
        for key, name, icon in PORTFOLIOS:
            self.portfolio_ids[key] = self.uuid()
            self.rows.add('portfolios', self.portfolio_ids[key], self.owner, 'real', name, icon)
        self.paper_portfolio = self.uuid()
        self.rows.add('portfolios', self.paper_portfolio, self.owner, 'paper', 'Paper practice', 'target')

    def account_rows(self):
        notice = "Retained fills are bounded by the venue's recent window; earlier history may be missing."
        for a in self.accounts:
            synced = self.anchor - timedelta(minutes=4) if a.enabled else a.end + timedelta(hours=2)
            self.rows.add('accounts', a.id, self.owner, 'real', None, a.venue, a.name, a.portfolio_id, a.address,
                          a.source_id, None, synced, 'synced', None, notice, a.enabled, a.keep_history,
                          1 + self.rng.randrange(0, 4))
        self.manual_account = self.uuid()
        self.rows.add('accounts', self.manual_account, self.owner, 'real', None, 'manual', 'Cold storage (manual)',
                      self.portfolio_ids['core'], None, None, D('2500'), None, 'manual', None, None, True, False, 1)
        self.paper_account = SimpleNamespace(id=self.uuid(), venue='hyperliquid')
        self.rows.add('accounts', self.paper_account.id, self.owner, 'paper', D('4000'), 'hyperliquid',
                      'Paper Hyperliquid', self.paper_portfolio, None, None, None, None, 'paper', None, None, True, False, 1)

    def settings(self):
        at = self.anchor - timedelta(days=12)
        self.rows.add('risk_settings', self.owner, D('3'), D('6'), at, D('8'))
        self.rows.add('reporting_settings', self.owner, 'Europe/Madrid', at, 'monday')
        self.rows.add('sizing_settings', self.owner, D('1.25'), at)
        self.rows.add('paper_settings', self.owner, 60, True)
        self.rows.add('paper_ledger_entries', self.uuid(), self.paper_account.id, D('4000'), None, 'seed',
                      self.anchor - timedelta(days=20), self.owner, None, 'paper')

    def trade(self, account):
        """Sequential round trips per contract over the account's window; a few flip straight into the next one."""
        years = (account.end - account.begin).total_seconds() / (365.25 * 86400)
        total = max(4, round(ROUND_TRIPS_PER_YEAR * account.weight * self.scale * years))
        weights = [0.4, 0.3, 0.2, 0.1, 0.05][:len(account.contracts)]
        open_contracts = set()
        if account.enabled:
            open_contracts = set(self.rng.sample(account.contracts, 1 + (self.rng.random() < 0.5)))
        for contract, weight in zip(account.contracts, weights):
            n = max(2, round(total * weight / sum(weights)))
            starts = sorted(self.between(account.begin, account.end) for _ in range(n))
            if contract in open_contracts and account.end - starts[-1] > timedelta(days=12):
                starts.append(self.anchor - timedelta(hours=self.rng.uniform(2, 240)))
            trips, previous = [], None
            for i, start in enumerate(starts):
                limit = starts[i + 1] if i + 1 < len(starts) else account.end
                if previous is not None and previous.flip_out:
                    start = previous.end
                if limit - start < timedelta(minutes=4):
                    continue
                last = i == len(starts) - 1
                flip_in = previous is not None and previous.flip_out
                direction = (('short' if previous.direction == 'long' else 'long') if flip_in
                             else 'long' if self.rng.random() < 0.57 else 'short')
                rt = RoundTrip(account, contract, self.ms(start), direction)
                rt.flip_in = flip_in
                rt.open_at_end = last and contract in open_contracts
                hold = timedelta(minutes=min(self.rng.lognormvariate(math.log(100), 1.5), 60 * 24 * 21))
                rt.end = None if rt.open_at_end else self.ms(start + max(timedelta(minutes=2), min(hold, (limit - start) * 0.85)))
                rt.flip_out = (not last and not rt.open_at_end and VENUES[account.venue]['flips']
                               and self.rng.random() < 0.04)
                trips.append(rt)
                previous = rt
            for rt, following in zip(trips, trips[1:] + [None]):
                self.build(rt, following if following is not None and following.flip_in else None)
            self.round_trips.extend(trips)

    def size(self, account, entry, distance, sz):
        notional = float(account.equity) * self.rng.uniform(0.004, 0.015) / distance
        return lots(notional / float(entry), sz)

    def levels(self, rt, entry, sz, vol):
        distance = self.rng.uniform(0.006, 0.035) * vol / 0.035
        reward = self.rng.uniform(1.5, 3.2)
        sign = rt.sign
        target = tick_price(float(entry) * (1 + sign * reward * distance), sz)
        rt.levels = dict(entry=entry, distance=distance, leverage=rt.account.leverage[rt.contract],
                         stop=tick_price(float(entry) * (1 - sign * distance), sz), target=target,
                         first_target=tick_price((float(entry) + float(target)) / 2, sz))

    def build(self, rt, following):
        """Prices, orders and fills of one round trip; a flip's opening part becomes the next round trip's entry."""
        a, venue, (_, vol, sz) = rt.account, VENUES[rt.account.venue], ASSETS[rt.contract]
        sign, types = rt.sign, venue['types']
        if self.rng.random() < 0.1:
            a.leverage[rt.contract] = self.rng.choice([2, 3, 5, 10, 20])
        entry = tick_price(self.price(rt.contract, rt.start), sz)
        self.levels(rt, entry, sz, vol)
        entry_side, exit_side = ('buy', 'sell') if sign > 0 else ('sell', 'buy')
        at = rt.start
        if rt.flip_in:
            # The previous round trip's flip fill opened this one.
            rt.entry_orders.append(rt.flip_order)
            rt.average = rt.flip_price
            rt.opened = rt.start
        else:
            rt.qty = self.size(a, entry, rt.levels['distance'], sz)
            limit = self.rng.random() < 0.7
            canceled = self.rng.random() < 0.05
            order = self.order(rt, entry_side, types[0] if limit else types[1], entry, None, False,
                               lots(rt.qty * D('1.5'), sz) if canceled else rt.qty,
                               at - timedelta(seconds=self.rng.uniform(1, 120)), 'entry')
            rt.entry_orders.append(order)
            position = ZERO
            for part in self.split(rt.qty, sz, self.rng.choices([1, 2, 3], [6, 3, 1])[0]):
                price = entry if limit else tick_price(float(entry) * (1 + sign * self.rng.uniform(0, 0.0008)), sz)
                at = self.ms(at + timedelta(seconds=self.rng.uniform(0.3, 40)))
                rt.average = (rt.average * position + price * part) / (position + part)
                position += part
                self.fill(rt, order, entry_side, 'open', price, part, ZERO, at, maker=limit)
            rt.opened = rt.fills[0]['at']
            self.finish(order, 'canceled' if canceled else 'filled', at + timedelta(seconds=self.rng.uniform(5, 600)))
        rt.entry_done = at
        if rt.open_at_end:
            self.open_exits(rt, exit_side, sz)
            return
        end = max(rt.end, at + timedelta(seconds=15 + self.rng.uniform(0, 30)))
        legs = self.exit_legs(rt, end, sz)
        if following is not None:
            # One market order closes the rest and opens the opposite position of the next round trip.
            following.qty = self.size(a, entry, rt.levels['distance'], sz)
            legs[-1] = ('flip', types[1], tick_price(self.price(rt.contract, end), sz), legs[-1][3], None)
        # Execution times first: every order is placed before its earliest fill and finishes at or after its last.
        times = [end] if len(legs) == 1 else [self.ms(at + (end - at) * 0.55), end]
        schedule = []
        for leg, when in zip(legs, times):
            parts = [leg[3]] if leg[0] == 'flip' else self.split(leg[3], sz, self.rng.choices([1, 2], [7, 3])[0])
            fill_at, fill_times = when - timedelta(seconds=2 * len(parts)), []
            for _ in parts:
                fill_at = self.ms(fill_at + timedelta(seconds=self.rng.uniform(0.2, 1.8)))
                fill_times.append(fill_at)
            schedule.append((leg, parts, fill_times))
        # Resting stops and targets go in shortly after the entry, before the first exit executes.
        placed = self.between(at + timedelta(seconds=0.5),
                              min(at + timedelta(seconds=60), schedule[0][2][0] - timedelta(seconds=0.5)))
        protective = []
        if not any(leg[0] == 'stop' for leg in legs):
            protective.append(self.order(rt, exit_side, types[2], rt.levels['stop'], rt.levels['stop'], True, rt.qty,
                                         placed, 'stop'))
        if not any(leg[0] == 'target' for leg in legs) and self.rng.random() < 0.7:
            protective.append(self.order(rt, exit_side, types[3], rt.levels['target'], rt.levels['target'], True, rt.qty,
                                         placed, 'target'))
        for (role, kind, price, qty, trigger), parts, fill_times in schedule:
            opening = following.qty if role == 'flip' else ZERO
            order = self.order(rt, exit_side, kind, price, trigger, role in ('stop', 'target'), qty + opening,
                               placed if role in ('stop', 'target')
                               else fill_times[0] - timedelta(seconds=self.rng.uniform(0.1, 3)), role)
            rt.exit_orders.append(order)
            for n, (part, fill_at) in enumerate(zip(parts, fill_times)):
                fill_price = price if role != 'stop' else tick_price(float(price) * (1 - sign * self.rng.uniform(0, 0.001)), sz)
                pnl = money((fill_price - rt.average) * part * sign)
                self.fill(rt, order, exit_side, 'flip' if role == 'flip' else 'close', fill_price,
                          part + (opening if n == len(parts) - 1 else ZERO), pnl, fill_at, maker=role == 'target')
            rt.end = fill_times[-1]
            self.finish(order, 'filled', rt.end)
            if role == 'flip':
                following.flip_order, following.flip_price, following.start = order, price, rt.end
        for order in protective:
            self.finish(order, 'canceled', rt.end + timedelta(seconds=self.rng.uniform(0.5, 5)))
            rt.exit_orders.append(order)

    def exit_legs(self, rt, end, sz):
        """(role, order type, price, quantity, trigger) of each exit leg, from the round trip's outcome."""
        types, lv = VENUES[rt.account.venue]['types'], rt.levels
        rt.outcome = self.rng.choices(['win', 'loss', 'scratch', 'manual'], [44, 38, 10, 8])[0]
        trigger = self.rng.random() < 0.5
        kind = types[3] if trigger else types[0]
        if rt.outcome == 'win':
            if self.rng.random() < 0.35 and rt.qty >= D(1).scaleb(-sz) * 2:
                half = lots(rt.qty / 2, sz)
                return [('target', kind, lv['first_target'], half, lv['first_target'] if trigger else None),
                        ('target', kind, lv['target'], rt.qty - half, lv['target'] if trigger else None)]
            return [('target', kind, lv['target'], rt.qty, lv['target'] if trigger else None)]
        if rt.outcome == 'loss':
            return [('stop', types[2], lv['stop'], rt.qty, lv['stop'])]
        close = float(lv['entry']) if rt.outcome == 'scratch' else self.price(rt.contract, end)
        return [('exit', types[1], tick_price(close * (1 + rt.sign * self.rng.uniform(-0.001, 0.001)), sz), rt.qty, None)]

    def open_exits(self, rt, exit_side, sz):
        """A position still open at the anchor: maybe a first target already filled; resting stop and target orders."""
        types, lv = VENUES[rt.account.venue]['types'], rt.levels
        placed = rt.entry_done + timedelta(seconds=self.rng.uniform(1, 60))
        rt.remaining = rt.qty
        if self.rng.random() < 0.3 and rt.qty >= D(1).scaleb(-sz) * 2:
            half = lots(rt.qty / 2, sz)
            order = self.order(rt, exit_side, types[0], lv['first_target'], None, True, half, placed, 'target')
            at = self.ms(self.between(placed + timedelta(minutes=1), self.anchor - timedelta(minutes=40)))
            self.fill(rt, order, exit_side, 'close', lv['first_target'], half,
                      money((lv['first_target'] - rt.average) * half * rt.sign), at, maker=True)
            self.finish(order, 'filled', at)
            rt.exit_orders.append(order)
            rt.remaining -= half
        # Without a resting stop and without a Play, an open position is what the no-stop check warns about.
        if self.rng.random() < 0.6:
            rt.exit_orders.append(self.order(rt, exit_side, types[2], lv['stop'], lv['stop'], True, rt.remaining, placed,
                                             'stop'))
        rt.exit_orders.append(self.order(rt, exit_side, types[0], lv['target'], None, True, rt.remaining, placed, 'target'))
        for order in rt.exit_orders:
            if order.status is None:
                self.finish(order, 'open', placed)

    def split(self, qty, sz, parts):
        """A quantity in 1 to `parts` positive whole-lot pieces (partial fills)."""
        step = D(1).scaleb(-sz)
        units = int(qty / step)
        parts = max(1, min(parts, units))
        cuts = sorted(self.rng.sample(range(1, units), parts - 1)) if parts > 1 else []
        bounds = [0] + cuts + [units]
        return [step * (b - a) for a, b in zip(bounds, bounds[1:])]

    def order(self, rt, side, kind, price, trigger, reduce_only, size, placed, role):
        a = rt.account
        a.orders += 1
        base = {'hyperliquid': 31_000_000_000, 'lighter': 281_474_976_710_656, 'risex': 7_000_000}[a.venue]
        order = SimpleNamespace(id=self.uuid(), account=a, contract=rt.contract, native=MARKETS[a.venue][rt.contract],
                                external=str(base + a.orders * 7 + self.rng.randrange(7)), side=side, kind=kind,
                                price=price, trigger=trigger, reduce_only=reduce_only, size=size, placed=self.ms(placed),
                                role=role, filled=ZERO, omitted=False, status=None, status_at=None)
        self.orders.append(order)
        return order

    def finish(self, order, status, at):
        order.status, order.status_at = status, min(self.ms(at), self.anchor - timedelta(minutes=1))

    def fill(self, rt, order, side, effect, price, qty, pnl, at, maker):
        a, venue = rt.account, VENUES[rt.account.venue]
        a.fills += 1
        fee = money(price * qty * (venue['maker'] if maker else venue['taker']))
        if venue['pnl'] == 'net-of-fee':
            # RISEx reports realized P&L with the fee taken off; an opening trade reports exactly -fee.
            pnl = -fee if effect == 'open' else money(pnl - fee)
        words = {'open': 'Open Long' if side == 'buy' else 'Open Short',
                 'close': 'Close Long' if side == 'sell' else 'Close Short',
                 'flip': 'Long > Short' if side == 'sell' else 'Short > Long'}
        source = {'hyperliquid': 900_000_000_000_000, 'lighter': 40_000_000, 'risex': 1_000_000_000}[a.venue]
        rt.fills.append(dict(id=self.uuid(), order=order, side=side, effect=effect, direction=words[effect], price=price,
                             qty=qty, fee=fee, token='USDC', pnl=pnl, at=at, hash='0x' + self.hex(256),
                             leverage=rt.levels['leverage'] if venue['fill_leverage'] else None,
                             source=str(source + a.fills * 3 + self.rng.randrange(3))))
        order.filled += qty
        a.realized += pnl - (fee if venue['pnl'] == 'gross' else ZERO)

    def forced_and_omitted(self):
        """Lighter liquidations and fee-bearing trades, and RISEx trades without P&L: kept as markers, never as fills."""
        candidates = [rt for rt in self.round_trips
                      if not (rt.flip_in or rt.flip_out or rt.open_at_end) and rt.account.enabled]
        per = max(1, round(self.years * self.scale))
        lighter = [rt for rt in candidates if rt.account.venue == 'lighter']
        for rt in self.rng.sample(lighter, min(len(lighter), 2 * per)):
            rt.forced = True
        lighter = [rt for rt in lighter if not rt.forced]
        for rt in self.rng.sample(lighter, min(len(lighter), 3 * per)):
            rt.omitted, rt.omission_kind = True, 'fee'
        risex = [rt for rt in candidates if rt.account.venue == 'risex']
        for rt in self.rng.sample(risex, min(len(risex), per)):
            rt.omitted, rt.omission_kind = True, 'missing-pnl'

    # -- Plays --------------------------------------------------------------------------------------------------------
    def plays(self):
        self.rules = self.play_rules()
        eligible = [rt for rt in self.round_trips
                    if not (rt.flip_in or rt.flip_out or rt.open_at_end or rt.forced or rt.omitted)
                    and rt.end < self.anchor - timedelta(hours=2) and rt.start > self.start + timedelta(days=2)]
        wanted = {status: max(1, round(n * self.years * self.scale)) for status, n in ENDED_PLAYS_PER_YEAR.items()}
        picked = sorted(self.rng.sample(eligible, min(len(eligible), wanted['Closed'] + wanted['Unresolved'])),
                        key=lambda r: r.start)
        unresolved = {int((k + 0.5) * len(picked) / wanted['Unresolved']) for k in range(wanted['Unresolved'])}
        for i, rt in enumerate(picked):
            self.ended_play(rt, 'Unresolved' if i in unresolved else 'Closed')
        open_trips = [rt for rt in self.round_trips if rt.open_at_end and not rt.flip_in]
        for rt in self.rng.sample(open_trips, min(len(open_trips), max(1, round(CURRENT_PLAYS['Open'] * self.scale)))):
            self.open_play(rt)
        enabled = [a for a in self.accounts if a.enabled]
        for status in ('Planned', 'Paused', 'Draft'):
            for _ in range(max(1, round(CURRENT_PLAYS[status] * self.scale))):
                self.waiting_play(self.rng.choice(enabled), status)
        for _ in range(wanted['Cancelled']):
            self.cancelled_play(self.rng.choice(self.accounts))
        if not self.counts['rare']:
            # Guarantee the rare search term even when no evidence note was drawn.
            self.rows.add('play_notes', self.uuid(), self.owner, self.first_closed, self.uuid(),
                          f'Pattern nicknamed {RARE_WORD}.', None, self.anchor - timedelta(days=1))
            self.counts['rare'] = 1

    def play_rules(self):
        """Three if-then rule versions: two archived, the last one active."""
        span = self.anchor - self.start
        bounds = [(0.35, 0.65), (0.65, 0.88), (0.88, None)]
        rules = []
        for version, ((rule_if, rule_then), (begin, end)) in enumerate(zip(RULES, bounds), 1):
            rule = dict(version=version, rule_if=rule_if, rule_then=rule_then, created=self.start + span * begin,
                        archived=None if end is None else self.start + span * end)
            self.rows.add('play_rules', self.uuid(), self.owner, 'real', version, rule_if, rule_then, None,
                          rule['created'], rule['archived'])
            rules.append(rule)
        return rules

    def active_rule(self, at):
        return next((r for r in self.rules if r['created'] <= at and (r['archived'] is None or at < r['archived'])), None)

    def notes_text(self, asset, setup):
        return {
            'thesis': f'{asset} {setup} on the {self.rng.choice(FRAMES)} chart: {self.rng.choice(REASONS)}.'
                      + (f' Entry only on the {COMMON_WORD} of the level.' if self.rng.random() < 0.35 else ''),
            'invalidation': f'Invalid on {self.rng.choice(INVALIDATIONS)}.',
            'strategy': self.rng.choice(STRATEGIES),
            'evidence': 'Chart capture attached.' if self.rng.random() < 0.4 else '',
        }

    def plan(self, rt, asset, setup, blank=False, two_targets=False):
        """The plan document as the API stores it (web casing); number fields keep their exact text."""
        notes = self.notes_text(asset, setup)
        if blank:
            # A Draft may leave numbers blank.
            return {'direction': rt.direction, 'sizingMode': 'margin', 'size': '', 'leverage': '5', 'budgetOverride': None,
                    'entries': [{'id': 'e1', 'name': 'Entry 1', 'color': '#4f8cff', 'share': '100', 'price': '',
                                 'stops': [], 'targets': []}], 'notes': notes}
        lv = rt.levels
        leverage = lv['leverage']
        if self.rng.random() < 0.2:
            # A percentage stop is the return on margin at the plan's leverage.
            stops = [{'id': 's1', 'unit': 'percent', 'value': fmt(D(repr(round(lv['distance'] * leverage * 100, 2)))),
                      'share': '100'}]
        else:
            stops = [{'id': 's1', 'unit': 'price', 'value': fmt(lv['stop']), 'share': '100'}]
        if two_targets:
            targets = [{'id': 't1', 'unit': 'price', 'value': fmt(lv['first_target']), 'share': '50'},
                       {'id': 't2', 'unit': 'price', 'value': fmt(lv['target']), 'share': '50'}]
        else:
            targets = [{'id': 't1', 'unit': 'price', 'value': fmt(lv['target']), 'share': '100'}]
        margin = (rt.qty * lv['entry'] / leverage).quantize(D('0.01'))
        return {'direction': rt.direction, 'sizingMode': 'margin', 'size': fmt(margin), 'leverage': str(leverage),
                'budgetOverride': None,
                'entries': [{'id': 'e1', 'name': 'Entry 1', 'color': '#4f8cff', 'share': '100', 'price': fmt(lv['entry']),
                             'stops': stops, 'targets': targets}],
                'notes': notes}

    def title(self, asset, direction, setup, at):
        if self.rng.random() < 0.3:
            return f'{asset} {direction.capitalize()} · {at.day} {at.strftime("%b %Y")}'
        return f'{asset} {setup}'

    def new_play(self, account, contract, status, created, plan, title, mode='real'):
        play = dict(id=self.uuid(), account=account, contract=contract, status=status, created=created, plan=plan,
                    title=title, mode=mode, version=1, updated=created, planned=None, ended=None, revisions=[],
                    changes=[], review={}, review_revisions=[], cancel=None, rule=None, plan_revision=0)
        # Draft edits change the plan in place, without revisions.
        for _ in range(self.rng.choices([0, 1, 2], [5, 3, 2])[0]):
            self.touch(play, created + timedelta(minutes=self.rng.uniform(1, 15)))
        self.statuses[status if mode == 'real' else 'Paper'] += 1
        return play

    @staticmethod
    def touch(play, at):
        play['version'] += 1
        play['updated'] = max(play['updated'], at)
        return play['version']

    def change(self, play, from_status, to_status, at, source='Owner', reason=None, note=None, unresolved=None,
               evidence=None):
        version = self.touch(play, at)
        play['changes'].append((from_status, to_status, reason, note, source, unresolved, evidence, version, at))

    def mark_planned(self, play, at):
        """Plan it: revision 1 with the pre-flight checklist (recent plans) and the active if-then rule, if any."""
        play['planned'] = at
        self.change(play, 'Draft', 'Planned', at)
        rule, snapshot, preflight = self.active_rule(at), None, None
        if rule and self.rng.random() < 0.85:
            answer = 'acknowledged' if self.rng.random() < 0.9 else 'exception'
            play['rule'] = rule
            snapshot = {'version': rule['version'], 'if': rule['rule_if'], 'then': rule['rule_then'], 'sourcePlayId': None,
                        'answer': answer,
                        'reason': 'A different setup; the rule does not apply.' if answer == 'exception' else None}
        if at > self.start + (self.anchor - self.start) * 0.5:
            preflight = [{'code': code, 'outcome': self.rng.choices(['passed', 'skipped', 'unknown'], [80, 12, 8])[0]}
                         for code in ('priced-stops', 'allocations', 'thesis', 'risk', 'liquidation')]
        play['revisions'].append((1, 'Planned', play['plan'], 'Planned', at, preflight, snapshot))
        play['plan_revision'] = 1

    def revise(self, play, at, status):
        """A plan revision after planning, with its reason: the stop moves to break-even."""
        plan = json.loads(json.dumps(play['plan']))
        entry = plan['entries'][0]
        entry['stops'] = [{'id': 's1', 'unit': 'price', 'value': entry['price'], 'share': '100'}]
        play['plan'] = plan
        play['plan_revision'] += 1
        self.touch(play, at)
        play['revisions'].append((play['plan_revision'], status, plan, 'Moved the stop to break-even after the first push.',
                                  at, None, None))

    def link(self, play, order, role, entry_id, level_id, at, state='Linked'):
        source = 'Automatic' if state == 'Suggested' or self.rng.random() < 0.8 else 'Owner'
        self.rows.add('play_order_links', self.uuid(), self.owner, play['id'], play['account'].id, order.id, role,
                      entry_id, level_id, state, source, at, at)

    def link_round_trip(self, play, rt, after, exits=True):
        """Entry, stop and target links by level; an unplanned market exit is an Exit link."""
        for order in rt.entry_orders:
            self.link(play, order, 'Entry', 'e1', None, max(after, order.placed) + timedelta(seconds=40))
        if not exits:
            return
        targets, planned = 0, len(play['plan']['entries'][0]['targets'])
        for order in rt.exit_orders:
            at = max(after, order.placed) + timedelta(seconds=40)
            if order.role == 'target':
                targets += 1
                if targets <= planned:
                    self.link(play, order, 'Target', 'e1', f't{targets}', at)
            elif order.role == 'stop':
                self.link(play, order, 'Stop', 'e1', 's1', at)
            else:
                self.link(play, order, 'Exit', None, None, at)

    def ended_play(self, rt, status):
        asset, setup = rt.contract, self.rng.choice(SETUPS)
        first, last = rt.fills[0]['at'], rt.fills[-1]['at']
        created = max(self.start + timedelta(hours=1), first - timedelta(minutes=self.rng.uniform(20, 36 * 60)))
        two = sum(1 for o in rt.exit_orders if o.role == 'target' and o.filled > 0) == 2
        play = self.new_play(rt.account, asset, status, created, self.plan(rt, asset, setup, two_targets=two),
                             self.title(asset, rt.direction, setup, created))
        # A few plans are written after the first execution (the post-hoc badge).
        post_hoc = last - first > timedelta(hours=3) and self.rng.random() < 0.05
        planned = (first + timedelta(minutes=self.rng.uniform(5, 90)) if post_hoc
                   else self.between(created + timedelta(seconds=30), first - timedelta(seconds=20)))
        self.mark_planned(play, planned)
        opened = max(first, planned) + timedelta(seconds=self.rng.uniform(10, 480))
        self.change(play, 'Planned', 'Open', opened, source='Venue', note='Entry 1 filled.')
        if self.rng.random() < 0.25 and last - opened > timedelta(minutes=10):
            self.revise(play, self.between(opened, last - timedelta(minutes=5)), 'Open')
        self.link_round_trip(play, rt, planned, exits=status == 'Closed')
        self.log_entries(play, opened, last)
        if status == 'Closed':
            closed = min(self.anchor - timedelta(minutes=5), last + timedelta(seconds=self.rng.uniform(30, 720)))
            self.change(play, 'Open', 'Closed', closed, source='Venue', note='All linked entries have exited.')
            play['ended'] = closed
            self.first_closed = self.first_closed or play['id']
            self.integrity_quirk(rt)
        else:
            declared = min(self.anchor - timedelta(minutes=5), last + timedelta(hours=self.rng.uniform(1, 96)))
            entered = sum((f['qty'] for f in rt.fills if f['effect'] == 'open'), ZERO)
            evidence = {'entered': fmt(entered), 'exited': '0', 'open': fmt(entered), 'orderCount': len(rt.entry_orders),
                        'orders': [{'orderId': o.external, 'role': 'entry', 'entryId': 'e1', 'levelId': None,
                                    'status': o.status, 'filled': fmt(o.filled)} for o in rt.entry_orders],
                        'problems': []}
            self.change(play, 'Open', 'Unresolved', declared,
                        note='The exit was shared with another play, so it cannot settle this one.',
                        unresolved=self.rng.choices(['SharedClose', 'ForcedExitNotRetained', 'Other'], [5, 2, 3])[0],
                        evidence=evidence)
            play['ended'] = declared
        self.review(play)
        self.evidence(play, planned, play['ended'])
        self.emit_play(play)

    def integrity_quirk(self, rt):
        """A few closed Plays Review must exclude: a filled order missing one execution, or a fee in another token."""
        roll = self.rng.random()
        if roll < 0.03 and rt.entry_orders[0].status == 'filled':
            rt.entry_orders[0].size += D(1).scaleb(-ASSETS[rt.contract][2])
        elif roll < 0.05 and rt.account.venue == 'hyperliquid':
            rt.fills[-1]['token'] = 'HYPE'

    def open_play(self, rt):
        asset, setup = rt.contract, self.rng.choice(SETUPS)
        first = rt.fills[0]['at']
        created = first - timedelta(minutes=self.rng.uniform(20, 24 * 60))
        two = any(o.role == 'target' and o.filled > 0 for o in rt.exit_orders)
        play = self.new_play(rt.account, asset, 'Open', created, self.plan(rt, asset, setup, two_targets=two),
                             self.title(asset, rt.direction, setup, created))
        planned = self.between(created + timedelta(seconds=30), first - timedelta(seconds=20))
        self.mark_planned(play, planned)
        opened = first + timedelta(seconds=self.rng.uniform(10, 480))
        self.change(play, 'Planned', 'Open', opened, source='Venue', note='Entry 1 filled.')
        self.link_round_trip(play, rt, planned)
        now = self.anchor - timedelta(minutes=10)
        self.log_entries(play, opened, now)
        self.evidence(play, planned, now)
        self.emit_play(play)

    def idea(self, account, at, entry_shift):
        """A hypothetical round trip for a Play without executions: levels and size from the price at `at`."""
        asset = self.rng.choice(account.contracts)
        rt = RoundTrip(account, asset, at, 'long' if self.rng.random() < 0.58 else 'short')
        _, vol, sz = ASSETS[asset]
        entry = tick_price(self.price(asset, at) * (1 - rt.sign * entry_shift), sz)
        self.levels(rt, entry, sz, vol)
        rt.qty = self.size(account, entry, rt.levels['distance'], sz)
        return rt

    def waiting_play(self, account, status):
        """A current Draft, Planned or Paused Play near the anchor, with a resting entry order when planned."""
        created = self.anchor - timedelta(hours=self.rng.uniform(1, 24 * (45 if status == 'Draft' else 14)))
        rt = self.idea(account, self.anchor, self.rng.uniform(0.005, 0.03))
        setup = self.rng.choice(SETUPS)
        blank = status == 'Draft' and self.rng.random() < 0.4
        instrument = None if status == 'Draft' and self.rng.random() < 0.2 else rt.contract
        title = '' if blank and self.rng.random() < 0.5 else self.title(rt.contract, rt.direction, setup, created)
        play = self.new_play(account, instrument, status, created, self.plan(rt, rt.contract, setup, blank=blank), title)
        if status != 'Draft':
            planned = self.between(created + timedelta(minutes=1),
                                   min(self.anchor - timedelta(minutes=30), created + timedelta(days=1)))
            self.mark_planned(play, planned)
            if status == 'Paused':
                self.change(play, 'Planned', 'Paused', self.between(planned, self.anchor - timedelta(minutes=10)))
            elif self.rng.random() < 0.85:
                order = self.order(rt, 'buy' if rt.sign > 0 else 'sell', VENUES[account.venue]['types'][0],
                                   rt.levels['entry'], None, False, rt.qty,
                                   min(self.anchor - timedelta(minutes=5), planned + timedelta(minutes=self.rng.uniform(1, 20))),
                                   'entry')
                self.finish(order, 'open', order.placed)
                # An ambiguous candidate waits for the owner as a suggestion.
                self.link(play, order, 'Entry', 'e1', None, order.placed + timedelta(seconds=40),
                          state='Linked' if self.rng.random() < 0.8 else 'Suggested')
        self.emit_play(play)

    def cancelled_play(self, account):
        created = self.between(max(account.begin, self.start + timedelta(days=1)), account.end - timedelta(days=4))
        rt = self.idea(account, created, self.rng.uniform(0.005, 0.03))
        setup = self.rng.choice(SETUPS)
        play = self.new_play(account, rt.contract, 'Cancelled', created, self.plan(rt, rt.contract, setup),
                             self.title(rt.contract, rt.direction, setup, created))
        at, previous = created, 'Draft'
        if self.rng.random() < 0.6:
            at = self.between(created + timedelta(minutes=1), created + timedelta(hours=6))
            self.mark_planned(play, at)
            previous = 'Planned'
            if self.rng.random() < 0.4:
                order = self.order(rt, 'buy' if rt.sign > 0 else 'sell', VENUES[account.venue]['types'][0],
                                   rt.levels['entry'], None, False, rt.qty, at + timedelta(minutes=self.rng.uniform(1, 30)),
                                   'entry')
                self.link(play, order, 'Entry', 'e1', None, order.placed + timedelta(seconds=40))
                self.finish(order, 'canceled', order.placed + timedelta(hours=self.rng.uniform(1, 48)))
                at = order.status_at
        cancelled = min(self.anchor - timedelta(minutes=10), at + timedelta(minutes=self.rng.uniform(5, 600)))
        reason = self.rng.choice(CANCEL_REASONS)
        self.change(play, previous, 'Cancelled', cancelled, reason=reason, note=self.rng.choice(CANCEL_NOTES) or None)
        play['cancel'], play['ended'] = reason, cancelled
        self.review(play)
        self.emit_play(play)

    def log_entries(self, play, start, end):
        """Append-only in-trade log entries, with an optional state."""
        if end <= start:
            return
        for _ in range(self.rng.choices([0, 1, 2, 3, 4], [3, 3, 2, 1, 1])[0]):
            self.rows.add('play_notes', self.uuid(), self.owner, play['id'], self.uuid(), self.rng.choice(LOG_TEXTS),
                          self.rng.choice([None, 'calm', 'rushed', 'fomo', 'tired', 'after-loss']), self.between(start, end))

    def review(self, play):
        """Completed with structured answers, text only (still needs review), no review needed, or nothing yet."""
        at = play['ended'] + timedelta(hours=self.rng.uniform(1, 120))
        if at >= self.anchor:
            return
        cancelled = play['status'] == 'Cancelled'
        roll = self.rng.random()
        if cancelled and roll < 0.45:
            play['review_revisions'].append((at, '', None, 'not-needed'))
            play['review'] = dict(text='', completion='NotNeeded', completed=at)
        elif roll < (0.6 if cancelled else 0.65):
            answers = {'Adherence': self.rng.choices(['followed', 'deviated', 'not-applicable'], [6, 3, 1])[0],
                       'MistakeTag': self.rng.choice(MISTAKE_TAGS) if self.rng.random() < 0.4 else None,
                       'Lesson': self.rng.choice(LESSONS), 'NextAction': self.rng.choice(NEXT_ACTIONS),
                       'Rule': (self.rng.choices(['followed', 'deviated', 'not-applicable'], [7, 2, 1])[0]
                                if play['rule'] else None)}
            text = f'{self.rng.choice(LESSONS)} {self.rng.choice(NEXT_ACTIONS)}'
            if self.rng.random() < 0.15:
                play['review_revisions'].append((at - timedelta(minutes=30), text, None, 'edited'))
            play['review_revisions'].append((at, text, answers, 'completed'))
            play['review'] = dict(text=text, answers=answers, completion='Completed', completed=at, reviewed=at)
        elif roll < 0.8:
            text = self.rng.choice(LESSONS)
            play['review_revisions'].append((at, text, None, 'edited'))
            play['review'] = dict(text=text, reviewed=at)
        for when, *_ in play['review_revisions']:
            self.touch(play, when)

    def evidence(self, play, start, end):
        """Chart captures and uploads with notes; the bytes are never needed by the reads under test."""
        if self.rng.random() >= 0.25 or end <= start:
            return
        for _ in range(1 if self.rng.random() < 0.75 else 2):
            note = self.rng.choice(EVIDENCE_NOTES)
            if note and not self.counts['rare']:
                note, self.counts['rare'] = f'{note} Pattern nicknamed {RARE_WORD}.', 1
            evidence_id, at = self.uuid(), self.between(start, end)
            markup = ({'version': 1, 'shapes': [{'type': 'arrow', 'from': [0.2, 0.7], 'to': [0.5, 0.4]}]}
                      if self.rng.random() < 0.3 else None)
            self.rows.add('play_evidence', evidence_id, self.owner, play['id'],
                          f'{self.owner}/{play["id"]}/{evidence_id}.png', 'image/png', self.rng.randrange(40_000, 400_000),
                          self.hex(256), 'Capture' if self.rng.random() < 0.6 else 'Upload', note, markup, at, at)

    def emit_play(self, play):
        a, review, rule = play['account'], play['review'], play['rule']
        answers = review.get('answers') or {}
        drawings = {}
        if play['contract'] and self.rng.random() < 0.3:
            drawings[f'{a.venue}:{play["contract"]}'] = [
                {'id': 'd1', 'type': 'horizontal', 'price': play['plan']['entries'][0]['price'] or '1'},
                {'id': 'd2', 'type': 'trendline', 'points': [[1, 2], [3, 4]]}]
        self.rows.add(
            'plays', play['id'], self.owner, a.id, play['mode'], a.venue, play['contract'], 'Venue', play['status'],
            play['title'], play['plan'], play['plan']['direction'], drawings, review.get('text', ''), 'Planned',
            answers.get('Adherence'), answers.get('MistakeTag'), answers.get('Lesson', ''), answers.get('NextAction', ''),
            answers.get('Rule'), rule['version'] if rule else None, rule['rule_if'] if rule else None,
            rule['rule_then'] if rule else None, len(play['review_revisions']), review.get('reviewed'),
            review.get('completion'), review.get('completed'), play['plan_revision'], play['version'], play['cancel'],
            play['created'], play['updated'], play['planned'], play['ended'])
        for number, status, plan, reason, at, preflight, snapshot in play['revisions']:
            self.rows.add('play_plan_revisions', self.uuid(), self.owner, play['id'], number, status, plan, reason, at,
                          preflight, snapshot)
        for from_status, to_status, reason, note, source, unresolved, evidence, version, at in play['changes']:
            self.rows.add('play_status_changes', self.uuid(), self.owner, play['id'], from_status, to_status, reason, note,
                          source, unresolved, evidence, version, at)
        # The structured review as the domain serializes ReviewAnswers (default options, so PascalCase).
        empty = {'Adherence': None, 'MistakeTag': None, 'Lesson': '', 'NextAction': '', 'Rule': None}
        for number, (at, text, answers_then, action) in enumerate(play['review_revisions'], 1):
            self.rows.add('play_review_revisions', self.uuid(), self.owner, play['id'], number, at, text,
                          answers_then or empty, action)

    # -- venue facts --------------------------------------------------------------------------------------------------
    def emit_venue_facts(self):
        backfilled = {a.id: [(f, t) for f, t, closed, _ in a.gap_plan if closed] for a in self.accounts}
        for rt in self.round_trips:
            if rt.forced:
                self.emit_forced(rt)
            elif rt.omitted:
                self.emit_omitted(rt)
        for order in self.orders:
            a = order.account
            remaining = ZERO if order.status == 'filled' else max(ZERO, order.size - order.filled)
            observed = min(self.anchor - timedelta(minutes=2), order.status_at + timedelta(minutes=self.rng.uniform(1, 90)))
            self.rows.add('imported_orders', order.id, self.owner, a.id, order.contract, order.native, order.external,
                          order.side, order.kind, order.price, order.trigger, order.reduce_only, False, order.size,
                          remaining, order.placed, order.status, VENUES[a.venue]['statuses'][order.status],
                          order.status_at, max(observed, order.status_at), order.omitted)
        for rt in self.round_trips:
            if rt.omitted:
                continue
            a = rt.account
            for fill in rt.fills:
                self.rows.add('imported_fills', fill['id'], self.owner, a.id, rt.contract, MARKETS[a.venue][rt.contract],
                              fill['source'], fill['side'], fill['direction'], fill['effect'], fill['price'], fill['qty'],
                              fill['fee'], fill['token'], VENUES[a.venue]['fee_basis'], fill['pnl'], VENUES[a.venue]['pnl'],
                              fill['at'], fill['order'].external, fill['hash'], fill['leverage'],
                              any(f <= fill['at'] <= t for f, t in backfilled[a.id]))
            self.funding(rt)
            self.observations(rt)

    def emit_forced(self, rt):
        """The round trip ended in a venue liquidation or deleverage: its exits are a forced event and a marker."""
        a = rt.account
        exits = [f for f in rt.fills if f['effect'] != 'open']
        rt.fills = [f for f in rt.fills if f['effect'] == 'open']
        for order in rt.exit_orders:
            if order.filled > 0:
                order.status, order.filled = 'canceled', ZERO
        at = exits[-1]['at'] if exits else rt.end
        source = str(70_000_000 + self.rng.randrange(10_000_000))
        kind = self.rng.choice(['liquidation', 'liquidation', 'deleverage'])
        seen = min(self.anchor - timedelta(minutes=3), at + timedelta(minutes=self.rng.uniform(2, 120)))
        self.rows.add('imported_forced_events', self.uuid(), self.owner, a.id, source, kind, rt.contract,
                      MARKETS[a.venue][rt.contract], 'sell' if rt.direction == 'long' else 'buy', rt.qty,
                      rt.levels['stop'], at, None, None, None,
                      'Venue-forced execution; kept beside the fills, never linked or scored.', seen, seen)
        self.rows.add('execution_omissions', self.uuid(), self.owner, a.id, 'forced', kind, source, rt.contract,
                      MARKETS[a.venue][rt.contract], None, at, None, None, seen, seen)

    def emit_omitted(self, rt):
        """Every execution of the round trip was left out (fee-bearing, or no reported P&L): markers only."""
        a = rt.account
        reason = 'fee-bearing or unverified fee' if rt.omission_kind == 'fee' else 'no reported realized P&L'
        for fill in rt.fills:
            fill['order'].omitted = True
            seen = min(self.anchor - timedelta(minutes=3), fill['at'] + timedelta(minutes=self.rng.uniform(2, 120)))
            self.rows.add('execution_omissions', self.uuid(), self.owner, a.id, rt.omission_kind, reason, fill['source'],
                          rt.contract, MARKETS[a.venue][rt.contract], fill['order'].external, fill['at'], None, None,
                          seen, seen)

    def funding(self, rt):
        """Hourly settled funding while the position is open; a few land just after it closed (unassigned)."""
        closed = rt.end if rt.end is not None else self.anchor - timedelta(minutes=5)
        hour = rt.opened.replace(minute=0, second=0, microsecond=0) + timedelta(hours=1)
        while hour < closed:
            rate = self.rng.gauss(0.0000125, 0.00002)
            amount = money(D(repr(-rt.sign * float(rt.qty) * self.price(rt.contract, hour) * rate)))
            if amount != 0:
                self.funding_row(rt, hour, amount)
            hour += timedelta(hours=1)
        if rt.end is not None and self.rng.random() < 0.01:
            self.funding_row(rt, hour, money(D('-0.000137') * rt.qty) or D('-0.000001'))

    def funding_row(self, rt, hour, amount):
        a = rt.account
        nanos, at = None, hour
        if a.venue == 'risex':
            # RISEx reports nanoseconds; the timestamp keeps microseconds and the native instant is kept beside it.
            extra = self.rng.randrange(1_000_000)
            nanos = int(hour.timestamp()) * 1_000_000_000 + extra
            at = hour + timedelta(microseconds=extra // 1000)
        self.counts[f'funding:{a.key}'] += 1
        self.rows.add('funding_payments', self.uuid(), self.owner, a.id,
                      f'{rt.contract}:{int(hour.timestamp())}:{self.counts[f"funding:{a.key}"]}', rt.contract,
                      MARKETS[a.venue][rt.contract], amount, 'USDC', at, nanos)

    def observations(self, rt):
        """Refresh observations of position leverage and liquidation price while the position was open."""
        a = rt.account
        closed = rt.end if rt.end is not None else self.anchor - timedelta(minutes=5)
        if closed - rt.opened < timedelta(minutes=20):
            return
        at, lev = self.between(rt.opened, closed), rt.levels['leverage']
        entry = rt.average.quantize(D('0.00000001'))
        if self.rng.random() < 0.65:
            self.rows.add('account_leverage_observations', self.uuid(), self.owner, a.id, rt.contract,
                          MARKETS[a.venue][rt.contract], at, rt.qty * rt.sign, entry, lev)
        if self.rng.random() < 0.55:
            liquidation = tick_price(float(rt.average) * (1 - rt.sign * 0.9 / lev), ASSETS[rt.contract][2])
            self.rows.add('account_risk_observations', self.uuid(), self.owner, a.id, rt.contract,
                          MARKETS[a.venue][rt.contract], at, rt.qty * rt.sign, entry, liquidation,
                          'cross' if self.rng.random() < 0.8 else 'isolated')

    # -- account state ------------------------------------------------------------------------------------------------
    def emit_account_state(self, a):
        """The latest snapshot (positions match the open round trips), funding coverage and a few import conflicts."""
        observed = self.anchor - timedelta(minutes=4) if a.enabled else a.end + timedelta(hours=2)
        margin = unrealized = ZERO
        positions = []
        for rt in (rt for rt in self.round_trips if rt.account is a and rt.open_at_end):
            sz, lev = ASSETS[rt.contract][2], rt.levels['leverage']
            mark = tick_price(self.price(rt.contract, observed), sz)
            pnl = money((mark - rt.average) * rt.remaining * rt.sign)
            used = money(rt.remaining * mark / lev)
            margin, unrealized = margin + used, unrealized + pnl
            positions.append((rt.contract, MARKETS[a.venue][rt.contract], rt.remaining * rt.sign,
                              rt.average.quantize(D('0.00000001')), pnl, used, lev,
                              tick_price(float(rt.average) * (1 - rt.sign * 0.9 / lev), sz)))
        value = max(D('50'), money(a.equity + a.realized + unrealized))
        withdrawable = max(ZERO, value - margin)
        wallet = a.key == 'hl-main'
        self.rows.add('account_snapshots', self.owner, a.id, observed, VENUES[a.venue]['scope'], value, withdrawable,
                      None if a.venue == 'hyperliquid' else withdrawable, margin, observed if wallet else None,
                      'default' if wallet else None, 'hypercore-spot-stablecoins' if wallet else None)
        for contract, native, quantity, entry, pnl, used, lev, liquidation in positions:
            self.rows.add('account_positions', self.owner, a.id, contract, native, quantity, entry, pnl, used, lev,
                          liquidation, 'cross')
        if wallet:
            self.rows.add('account_stablecoins', self.owner, a.id, 0, USDC_TOKEN, 'USDC', D('1250.5'), ZERO, D('1250.5'))
        if a.enabled:
            self.rows.add('account_funding_states', self.owner, a.id, observed - timedelta(days=30), observed,
                          observed - timedelta(days=30), observed, True, 'unknown',
                          'Funding covers the last 30 days; older payments may be missing.')
        fills = [f for rt in self.round_trips if rt.account is a and not rt.omitted for f in rt.fills]
        for fill in self.rng.sample(fills, min(2, len(fills))):
            seen = min(self.anchor - timedelta(minutes=3), fill['at'] + timedelta(days=self.rng.uniform(1, 20)))
            self.rows.add('fill_import_conflicts', self.uuid(), self.owner, a.id, fill['id'], fill['order'].contract,
                          fill['source'], self.rng.choice(['price', 'fee', 'closedPnl', 'fee,closedPnl']), seen, seen,
                          self.rng.randrange(1, 4))

    def plan_gaps(self, a):
        """History gaps in the account's window: most closed by a backfill, two still open, one beyond the venue's reach."""
        gaps = []
        for n in range(1 if a.weight < 1 else 2):
            at = self.between(a.begin + (a.end - a.begin) * (0.2 + 0.35 * n), a.begin + (a.end - a.begin) * (0.45 + 0.35 * n))
            closed = not (a.key in ('hl-scalp', 'lighter-main') and n == 0)
            gaps.append((self.ms(at - timedelta(hours=self.rng.uniform(6, 72))), self.ms(at), closed,
                         a.key == 'lighter-main' and n == 0))
        a.gap_plan = gaps

    def coverage(self, a):
        """The fill-import coverage ledger: continuous refresh (and check) records, a record per gap, and backfills."""
        cuts = [a.begin] + [to for _, to, _, _ in a.gap_plan] + [a.end]
        for i, (seg_start, seg_end) in enumerate(zip(cuts, cuts[1:])):
            gap = a.gap_plan[i - 1] if i > 0 else None
            opened_by = None
            for source in ['refresh', 'check'] if a.keep_history else ['refresh']:
                record = self.uuid()
                first = seg_start + timedelta(minutes=5)
                imports = max(1, int((seg_end - first).total_seconds() / (3600 * (8 if source == 'refresh' else 1))))
                truncated = gap is not None and source == 'refresh'
                opened_by = record if truncated else opened_by
                self.rows.add('account_fill_coverage', record, self.owner, a.id, source, first, seg_end, imports, None,
                              seg_end, truncated, False, gap[1] if gap else seg_start - timedelta(days=1), seg_end,
                              gap[0] if truncated else None, gap[1] if truncated else None)
            if gap is None:
                continue
            gap_from, gap_to, closed, beyond = gap
            gap_id, closed_at, closed_by = self.uuid(), None, None
            if closed:
                closed_at = min(a.end, gap_to + timedelta(hours=self.rng.uniform(2, 48)))
                closed_by = self.uuid()
                self.rows.add('account_fill_coverage', closed_by, self.owner, a.id, 'backfill', closed_at, closed_at, 1,
                              gap_from, closed_at, False, False, gap_from, closed_at, None, None)
            self.rows.add('account_history_gaps', gap_id, self.owner, a.id, gap_from, gap_to, gap_to + timedelta(minutes=5),
                          opened_by, closed_at, closed_by, beyond, None)
            if closed:
                started = closed_at - timedelta(minutes=self.rng.uniform(3, 30))
                self.rows.add('account_history_backfills', self.uuid(), self.owner, a.id, gap_id, 'completed', gap_from,
                              closed_at, True, closed_at, json.dumps([str(self.rng.randrange(10 ** 12))]),
                              self.rng.randrange(2, 9), self.rng.randrange(200, 2000), self.rng.randrange(0, 60), 0,
                              gap_from, True, False, True, closed_at + timedelta(minutes=1), 0, None, started, closed_at,
                              closed_at)

    def paper(self):
        """A few paper Drafts; the venue-fact reads under test are real-only."""
        for _ in range(max(2, round(5 * self.scale))):
            created = self.anchor - timedelta(days=self.rng.uniform(1, 30))
            rt = RoundTrip(self.paper_account, 'BTC', created)
            play = self.new_play(self.paper_account, 'BTC', 'Draft', created, self.plan(rt, 'BTC', 'paper idea', blank=True),
                                 'BTC paper idea', mode='paper')
            self.emit_play(play)

    # -- inspection ---------------------------------------------------------------------------------------------------
    def table_counts(self):
        return {table: len(self.rows.lines.get(table, [])) for table in TABLES}

    def digest(self):
        h = hashlib.sha256()
        for table in TABLES:
            for line in self.rows.lines.get(table, []):
                h.update(table.encode() + b'\0' + line.encode() + b'\n')
        return h.hexdigest()

    def records(self, table):
        return [parse_copy(line, table) for line in self.rows.lines.get(table, [])]

    def cache_charge(self, account_ids):
        """The estimated ActivitySummaryCache charge of the aggregate over these accounts, by the cache's own rule:
        768 bytes per retained row plus two per string character, 128 per Play-linked fill, and per operation its row,
        288 bytes per execution and 16 per funding payment. Operation rows are approximated (OPERATION_ROW)."""
        ids = set(account_ids)
        charge = 4096 + 2 * 37 * len(ids)
        for table, columns in CACHE_STRINGS.items():
            for record in self.records(table):
                if record['Id' if table == 'accounts' else 'AccountId'] in ids:
                    charge += 768 + 2 * sum(len(record[c] or '') for c in columns)
                    charge += {'imported_fills': 288, 'funding_payments': 16}.get(table, 0)
        linked = {r['ImportedOrderId'] for r in self.records('play_order_links') if r['State'] == 'Linked'}
        trips = [rt for rt in self.round_trips if rt.account.id in ids and not rt.omitted]
        charge += 128 * sum(1 for rt in trips for f in rt.fills if f['order'].id in linked)
        return charge + OPERATION_ROW * len(trips)


def generate(seed, years, scale, anchor):
    return Generator(seed, years, scale, anchor).generate()


def quoted(name):
    return '"' + name + '"'


def copy_script(gen):
    """The whole seed as one transaction of COPY blocks in foreign-key order."""
    yield 'BEGIN;\n'
    for table, columns in TABLES.items():
        lines = gen.rows.lines.get(table)
        if not lines:
            continue
        yield f'COPY {table} ({", ".join(map(quoted, columns))}) FROM STDIN;\n'
        for start in range(0, len(lines), 5000):
            yield '\n'.join(lines[start:start + 5000]) + '\n'
        yield '\\.\n'
    yield 'COMMIT;\n'


# ---------------------------------------------------------------------------------------------------------------------
# Measurement
# ---------------------------------------------------------------------------------------------------------------------

def percentile(values, p):
    """Linear interpolation between closest ranks (NumPy's default); p in [0, 100]."""
    if not values:
        raise ValueError('no samples')
    if not 0 <= p <= 100:
        raise ValueError('p must be within 0 and 100')
    ordered = sorted(values)
    rank = (len(ordered) - 1) * p / 100
    low = math.floor(rank)
    high = min(low + 1, len(ordered) - 1)
    return ordered[low] + (ordered[high] - ordered[low]) * (rank - low)


def summarize(values):
    return {'n': len(values), 'p50': percentile(values, 50), 'p95': percentile(values, 95), 'min': min(values),
            'max': max(values)} if values else None


def nonempty(key):
    return lambda body: isinstance(body.get(key), list) and len(body[key]) > 0, f'non-empty "{key}"'


def positive(key):
    return lambda body: isinstance(body.get(key), int) and body[key] > 0, f'positive "{key}"'


def page_of(key, page):
    """The requested page itself, with rows: a page past the end is clamped by the API and so reads as another number."""
    return (lambda body: body.get('page') == page and isinstance(body.get(key), list) and len(body[key]) > 0,
            f'page {page} with a non-empty "{key}"')


def search_page(offset):
    return (lambda body: body.get('offset') == offset and isinstance(body.get('items'), list) and len(body['items']) > 0
            and body.get('notIndexed') == 0, f'offset {offset} with non-empty "items" and nothing unindexed')


# Page sizes: the Plays list default (ListPaging), Activity's largest page and JournalSearch.PageSize.
PLAYS_PAGE, ACTIVITY_PAGE, SEARCH_PAGE = 25, 100, 50


def deep_page(total, size):
    """A populated page past the first (the middle one), or None when everything fits on one page."""
    pages = math.ceil(total / size) if total > 0 else 0
    return pages // 2 + 1 if pages >= 2 else None


def plan_endpoints(account, operation, operations, closed, matches):
    """The measured reads as (name, path, cache scope or None, check, expectation), and the skipped scenarios.
    Deep pages come from the counts the API reported; a scenario the dataset cannot populate is skipped, never faked.
    The cache scope names the Activity facts aggregate a read goes through: every enabled account, or one account."""
    planned, skipped = [], []

    def add(name, path, cache, check):
        planned.append((name, path, cache, *check))
    add('Activity, page 1', '/api/activity', 'all', nonempty('operations'))
    page = deep_page(operations, ACTIVITY_PAGE)
    if page:
        add(f'Activity, page {page} of {ACTIVITY_PAGE} rows', f'/api/activity?page={page}&pageSize={ACTIVITY_PAGE}', 'all',
            page_of('operations', page))
    else:
        skipped.append(f'Activity deep page: {operations} operations fit on one page of {ACTIVITY_PAGE}')
    add('Activity, closed longs in 90 days', '/api/activity?status=closed&direction=long&period=90', 'all',
        nonempty('operations'))
    add('Activity, executions of one operation', f'/api/activity/fills?accountId={account}&operationId={operation}',
        'account', nonempty('fills'))
    add('Review, all', '/api/review?period=all', None, nonempty('plays'))
    add('Review, year', '/api/review?period=year', None, nonempty('plays'))
    add('Review, all with imported positions', '/api/review?period=all&positions=include', 'all', nonempty('plays'))
    add('Plays, All tab', '/api/plays?view=all', None, nonempty('plays'))
    add('Plays, In progress tab', '/api/plays?view=progress', None, nonempty('plays'))
    page = deep_page(closed, PLAYS_PAGE)
    if page:
        add(f'Plays, Closed tab page {page}', f'/api/plays?view=closed&page={page}&pageSize={PLAYS_PAGE}', None,
            page_of('plays', page))
    else:
        skipped.append(f'Plays Closed tab deep page: {closed} Closed Plays fit on one page of {PLAYS_PAGE}')
    add('Plays, tab counts', '/api/plays/counts', None, positive('all'))
    add('Plays, title and instrument search', '/api/plays?view=all&q=BTC', None, nonempty('plays'))
    add('Overview', '/api/overview', None, nonempty('accounts'))
    add('Overview summary by portfolio, all', '/api/overview/summary?groupBy=portfolio&period=all', 'all', nonempty('rows'))
    add('Overview summary by account, 30 days', '/api/overview/summary?groupBy=account&period=30', 'all', nonempty('rows'))
    add('Open risk', '/api/risk/open', None, nonempty('plays'))
    add('Journal search, common word', f'/api/plays/search?q={COMMON_WORD}', None, search_page(0))
    page = deep_page(matches, SEARCH_PAGE)
    if page:
        offset = (page - 1) * SEARCH_PAGE
        add(f'Journal search, offset {offset}', f'/api/plays/search?q={COMMON_WORD}&offset={offset}', None,
            search_page(offset))
    else:
        skipped.append(f'Journal search deep page: {matches} matches fit on one page of {SEARCH_PAGE}')
    add('Journal search, rare word', f'/api/plays/search?q={RARE_WORD}', None,
        (lambda body: body.get('total') == 1 and len(body.get('items', [])) == 1, 'exactly one match'))
    return planned, skipped


# The string properties the cache charges, per table of the entities it retains (ActivitySummaryCache.Charge).
CACHE_STRINGS = {
    'accounts': ['TradingMode', 'VenueId', 'Name', 'Address', 'SourceId', 'SyncStatus', 'LastSyncError', 'HistoryNotice'],
    'imported_fills': ['ContractId', 'VenueContractId', 'SourceFillId', 'Side', 'Direction', 'PositionEffect', 'FeeToken',
                       'FeeBasis', 'PnlBasis', 'ExternalOrderId', 'TransactionHash'],
    'account_snapshots': ['ValueScope', 'AccountMode', 'StablecoinScope'],
    'account_positions': ['ContractId', 'VenueContractId', 'MarginMode'],
    'account_stablecoins': ['TokenId', 'Symbol'],
    'funding_payments': ['SourceId', 'ContractId', 'VenueContractId', 'Asset'],
    'account_funding_states': ['PnlBasis', 'Notice'],
    'account_leverage_observations': ['ContractId', 'VenueContractId'],
    'execution_omissions': ['ContractId', 'VenueContractId'],
    'imported_forced_events': ['SourceId', 'Kind', 'ContractId', 'VenueContractId', 'Side', 'OrderId', 'Exclusion'],
}
# An ActivityOperationDto row: 768 bytes plus about 250 characters of identity, quantity, price and P&L strings.
OPERATION_ROW = 1300
# The cache's default capacity (Vessel:Activity:CacheCapacityBytes); the run never changes it.
CACHE_CAPACITY = 256 * 1024 * 1024


def cache_status(charge, capacity=CACHE_CAPACITY):
    """Whether a repeated read can be a hit: the cache refuses an entry whose charge exceeds its capacity. The charge is
    an estimate, so values near the capacity are reported as unconfirmed rather than as hits or misses."""
    mib, limit = charge / 2 ** 20, capacity / 2 ** 20
    if charge <= 0.8 * capacity:
        return f'hit (est. {mib:.0f} MiB)'
    if charge > 1.25 * capacity:
        return f'not cached (est. {mib:.0f} > {limit:.0f} MiB)'
    return f'unconfirmed (est. {mib:.0f} MiB, near {limit:.0f})'


class Client:
    """One keep-alive HTTP/1.1 connection to the API, timed from request to the last body byte."""

    def __init__(self, port, token):
        self.port, self.headers = port, {'Authorization': 'Bearer ' + token, 'Accept': 'application/json',
                                         'Accept-Encoding': 'gzip'}
        self.connection = None

    def get(self, path):
        for attempt in (1, 2):
            if self.connection is None:
                self.connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=300)
            try:
                started = time.perf_counter()
                self.connection.request('GET', path, headers=self.headers)
                response = self.connection.getresponse()
                wire = response.read()
                elapsed = (time.perf_counter() - started) * 1000
                break
            except (http.client.HTTPException, ConnectionError):
                self.connection.close()
                self.connection = None
                if attempt == 2:
                    raise
        body = gzip.decompress(wire) if response.getheader('Content-Encoding') == 'gzip' else wire
        return response.status, elapsed, len(wire), len(body), body

    def close(self):
        if self.connection:
            self.connection.close()


class Endpoint:
    def __init__(self, name, path, cache, check, expectation):
        self.name, self.path, self.cache, self.check, self.expectation = name, path, cache, check, expectation
        self.cached = cache is not None
        self.steady, self.miss = [], []
        self.wire = self.size = 0
        self.failures = []

    def record(self, status, elapsed, wire, size, body, bucket, measured):
        if status != 200:
            self.failures.append(f'HTTP {status}')
            return
        try:
            parsed = json.loads(body)
            ok = isinstance(parsed, dict) and self.check(parsed)
        except ValueError:
            ok = False
        if not ok:
            self.failures.append(f'response is not {self.expectation}')
            return
        self.wire, self.size = wire, size
        if measured:
            bucket.append(elapsed)


def measure(client, endpoints, warmup, rounds, write, progress=print):
    """Interleaved rounds: every round visits every endpoint, in an order rotated by one per round. A read through the
    Activity facts cache is made after a committed no-op revision write (always a miss) and repeated at once; the repeat
    is a hit only when the aggregate fits the cache (see cache_status)."""
    for number in range(warmup + rounds):
        measured = number >= warmup
        shift = number % len(endpoints)
        for endpoint in endpoints[shift:] + endpoints[:shift]:
            if endpoint.cached:
                write()
                endpoint.record(*client.get(endpoint.path), endpoint.miss, measured)
            endpoint.record(*client.get(endpoint.path), endpoint.steady, measured)
        progress(f'  round {number + 1}/{warmup + rounds} ({"measured" if measured else "warm-up"})')


# ---------------------------------------------------------------------------------------------------------------------
# Safety, processes and containers
# ---------------------------------------------------------------------------------------------------------------------

class Refused(Exception):
    pass


def check_arguments(args, environ=None):
    """The run only proceeds when explicitly told its target is its own disposable containers."""
    environ = os.environ if environ is None else environ
    if not args.disposable:
        raise Refused('Refusing to run: pass --disposable to confirm that this run may create, fill and then remove '
                      'its own PostgreSQL and RabbitMQ containers. It never uses another database.')
    if any(environ.get(name) for name in ('CI', 'GITHUB_ACTIONS')):
        raise Refused('Refusing to run under CI: the benchmark is a manual measurement on a chosen host.')
    if not 0 < args.years <= 10 or not 0 < args.scale <= 20:
        raise Refused('Use --years in (0, 10] and --scale in (0, 20].')
    if args.rounds < 1 or args.warmup < 0:
        raise Refused('Use at least one measured round and a non-negative number of warm-up rounds.')


def child_environment(settings):
    """The caller's environment without inherited server settings, connection strings or tokens, plus ours only."""
    environment = verification_environment()
    environment.update(settings)
    return environment


class Run:
    """Everything one run creates, so cleanup removes exactly that and nothing else."""

    def __init__(self, args, log=print):
        self.args, self.log = args, log
        self.id = secrets.token_hex(4)
        self.containers = {'postgres': f'vessel-bench-{self.id}-pg', 'rabbitmq': f'vessel-bench-{self.id}-mq'}
        self.started = []
        self.api = None
        self.temp = None
        self.pg_password, self.mq_password = secrets.token_hex(24), secrets.token_hex(24)
        self.token = secrets.token_urlsafe(48)
        self.secrets = {self.pg_password, self.mq_password, self.token}

    def redact(self, text):
        text = str(text)
        for secret in self.secrets:
            text = text.replace(secret, '[redacted]')
        return text

    def run(self, args, **kwargs):
        """A command whose output is captured and redacted; its argv never holds a secret."""
        kwargs.setdefault('capture_output', True)
        kwargs.setdefault('text', True)
        result = subprocess.run(args, **kwargs)
        if result.returncode:
            detail = self.redact((result.stdout or '') + (result.stderr or ''))[-4000:]
            raise RuntimeError(f'{" ".join(args[:4])} exited with {result.returncode}:\n{detail}')
        return result

    # -- containers ---------------------------------------------------------------------------------------------------
    def start_containers(self):
        env = dict(os.environ, POSTGRES_PASSWORD=self.pg_password, RABBITMQ_DEFAULT_PASS=self.mq_password)
        label = f'{LABEL}={self.id}'
        for key, options, image in (
                ('postgres', ['-e', 'POSTGRES_USER=vessel', '-e', f'POSTGRES_DB={DATABASE}', '-e', 'POSTGRES_PASSWORD',
                              '-p', '127.0.0.1::5432'], POSTGRES_IMAGE),
                ('rabbitmq', ['--hostname', 'vessel-bench-rabbitmq', '-e', 'RABBITMQ_DEFAULT_USER=bench',
                              '-e', 'RABBITMQ_DEFAULT_PASS', '-p', '127.0.0.1::5672'], RABBIT_IMAGE)):
            # Tracked before launch: a failed or interrupted `docker run` can still leave a created container.
            self.started.append(self.containers[key])
            self.run(['docker', 'run', '-d', '--rm', '--name', self.containers[key], '--label', label, *options, image],
                     env=env)
        self.pg_port = self.port(self.containers['postgres'], 5432)
        self.mq_port = self.port(self.containers['rabbitmq'], 5672)
        self.wait('PostgreSQL', ['docker', 'exec', self.containers['postgres'], 'pg_isready', '-h', '127.0.0.1',
                                 '-U', 'vessel', '-d', DATABASE])
        self.wait('RabbitMQ', ['docker', 'exec', self.containers['rabbitmq'], 'rabbitmq-diagnostics', '-q',
                               'check_port_connectivity'])

    def port(self, container, port):
        output = self.run(['docker', 'port', container, f'{port}/tcp']).stdout
        match = re.search(r'127\.0\.0\.1:(\d+)', output)
        if not match:
            raise RuntimeError(f'{container} published no loopback port.')
        return int(match.group(1))

    def wait(self, name, probe, timeout=180):
        deadline = time.monotonic() + timeout
        while subprocess.run(probe, capture_output=True).returncode:
            if time.monotonic() > deadline:
                raise RuntimeError(f'{name} did not become ready within {timeout} s.')
            time.sleep(1)

    def psql(self, sql=None, stdin=None, query=False):
        args = ['docker', 'exec', '-i', self.containers['postgres'], 'psql', '-X', '-q', '-v', 'ON_ERROR_STOP=1',
                '-U', 'vessel', '-d', DATABASE]
        if query:
            args += ['-At', '-F', '\t']
        if sql is not None:
            args += ['-c', sql]
        return self.run(args, input=stdin).stdout

    def seed(self, generator):
        process = subprocess.Popen(['docker', 'exec', '-i', self.containers['postgres'], 'psql', '-X', '-q', '-v',
                                    'ON_ERROR_STOP=1', '-U', 'vessel', '-d', DATABASE],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            for chunk in copy_script(generator):
                process.stdin.write(chunk)
            process.stdin.close()
        except BrokenPipeError:
            pass
        output = process.stdout.read()
        if process.wait():
            raise RuntimeError('Seeding failed:\n' + self.redact(output)[-4000:])

    # -- the API and its offline commands -----------------------------------------------------------------------------
    def settings(self):
        connection = (f'Host=127.0.0.1;Port={self.pg_port};Database={DATABASE};Username=vessel;'
                      f'Password={self.pg_password}')
        return {
            'ASPNETCORE_ENVIRONMENT': 'Production',
            'DOTNET_ENVIRONMENT': 'Production',
            'ConnectionStrings__Vessel': connection,
            'Vessel__Auth__Mode': 'Token',
            'Vessel__Auth__Token': self.token,
            'Vessel__Auth__OwnerId': self.owner,
            'Vessel__Messaging__RabbitMqUri': f'amqp://bench:{self.mq_password}@127.0.0.1:{self.mq_port}/',
            'Vessel__Evidence__Storage__Provider': 'Local',
            'Vessel__Evidence__Storage__Local__RootPath': str(self.temp / 'evidence'),
            'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
            'DOTNET_NOLOGO': '1',
            'Logging__LogLevel__Default': 'Warning',
        }

    def build(self):
        self.run(['dotnet', 'build', str(API_PROJECT), '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false',
                  '-nologo', '-v:q'], cwd=ROOT, env=child_environment({}))

    def migrate(self):
        """deploy/migrate from the build output: check-config, identity, journal, ensure-owner, provision-messaging."""
        env = child_environment(self.settings())
        dll = str(API_BIN / 'Vessel.Api.dll')
        self.run(['dotnet', 'tool', 'restore'], cwd=BACKEND, env=env)
        self.run(['dotnet', dll, 'check-config'], cwd=API_BIN, env=env)
        for project in ('src/Vessel.Identity', 'src/Vessel.Persistence'):
            self.run(['dotnet', 'ef', 'database', 'update', '--project', project, '--configuration', 'Release',
                      '--no-build'], cwd=BACKEND, env=env)
        self.run(['dotnet', dll, 'identity', 'ensure-owner'], cwd=API_BIN, env=env)
        self.run(['dotnet', dll, 'provision-messaging'], cwd=API_BIN, env=env)

    def rebuild_search(self):
        output = self.run(['dotnet', str(API_BIN / 'Vessel.Api.dll'), 'play-search-rebuild', '--apply'], cwd=API_BIN,
                          env=child_environment(self.settings())).stdout
        match = re.search(r'Indexed (\d+) of (\d+) Plays', output)
        if not match or match.group(1) != match.group(2):
            raise RuntimeError('The search projection rebuild did not index every Play: ' + self.redact(output))
        return int(match.group(1))

    def start_api(self):
        port = free_port()
        log = open(self.temp / 'api.log', 'w')
        self.api = subprocess.Popen(['dotnet', str(API_BIN / 'Vessel.Api.dll'), '--urls', f'http://127.0.0.1:{port}'],
                                    cwd=API_BIN, env=child_environment(self.settings()), stdout=log,
                                    stderr=subprocess.STDOUT, start_new_session=True)
        log.close()
        deadline = time.monotonic() + 180
        while True:
            if self.api.poll() is not None:
                raise RuntimeError('The API exited during startup:\n' + self.api_log())
            try:
                connection = http.client.HTTPConnection('127.0.0.1', port, timeout=5)
                connection.request('GET', '/health/ready')
                if connection.getresponse().status == 200:
                    break
            except OSError:
                pass
            finally:
                connection.close()
            if time.monotonic() > deadline:
                raise RuntimeError('The API was not ready within 180 s:\n' + self.api_log())
            time.sleep(0.5)
        return port

    def api_log(self):
        try:
            return self.redact((self.temp / 'api.log').read_text(errors='replace'))[-4000:]
        except OSError:
            return ''

    def api_peak_rss(self):
        try:
            status = Path(f'/proc/{self.api.pid}/status').read_text()
            return int(re.search(r'VmHWM:\s+(\d+) kB', status).group(1)) // 1024
        except (OSError, AttributeError):
            return None

    def stop_api(self):
        if self.api is None or self.api.poll() is not None:
            return
        os.killpg(self.api.pid, signal.SIGTERM)
        try:
            self.api.wait(timeout=30)
        except subprocess.TimeoutExpired:
            os.killpg(self.api.pid, signal.SIGKILL)
            self.api.wait()

    def labelled(self):
        """The containers that carry this run's label, whether or not their start was seen to succeed."""
        found = subprocess.run(['docker', 'ps', '-a', '--filter', f'label={LABEL}={self.id}', '--format', '{{.Names}}'],
                               capture_output=True, text=True)
        return found.stdout.split() if found.returncode == 0 else []

    def cleanup(self):
        """Stops the API, then removes the containers this run tracked or finds by its label, each only after checking
        it carries this run's label, and its temporary directory. It cannot run after SIGKILL or a host crash; the
        report then names the label to remove by hand."""
        problems, foreign, stuck = [], set(), set()
        try:
            self.stop_api()
        except OSError as error:
            problems.append(f'API stop: {error}')
        for attempt in range(3):
            for name in dict.fromkeys([*reversed(self.started), *self.labelled()]):
                inspected = subprocess.run(['docker', 'inspect', '-f', f'{{{{index .Config.Labels "{LABEL}"}}}}', name],
                                           capture_output=True, text=True)
                if inspected.returncode:
                    continue  # never created, or already gone (--rm)
                if inspected.stdout.strip() != self.id:
                    foreign.add(name)
                    continue
                if subprocess.run(['docker', 'rm', '-f', '-v', name], capture_output=True).returncode:
                    stuck.add(name)
            if not self.labelled():
                break
            time.sleep(1)  # a container the daemon was still creating when the client was interrupted
        else:
            problems.append(f'containers labelled {LABEL}={self.id} remain; remove them with docker rm -f -v')
        problems += [f'could not remove {name}' for name in sorted(stuck)]
        problems += [f'{name} does not carry this run\'s label; left alone' for name in sorted(foreign)]
        if self.temp is not None:
            shutil.rmtree(self.temp, ignore_errors=True)
        return problems


def free_port():
    import socket
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def available_mb():
    for line in Path('/proc/meminfo').read_text().splitlines():
        if line.startswith('MemAvailable:'):
            return int(line.split()[1]) // 1024
    return None


def wait_for_memory(minimum_mb, phase, log=print, timeout=1800, pause=60):
    """Waits, up to `timeout` seconds, until MemAvailable reaches `minimum_mb` (0 disables the check)."""
    if not minimum_mb:
        return
    deadline = time.monotonic() + timeout
    while (available := available_mb()) is not None and available < minimum_mb:
        if time.monotonic() > deadline:
            raise RuntimeError(f'Only {available} MB available before {phase}; needed {minimum_mb} MB.')
        log(f'  waiting for memory before {phase}: {available} MB available, {minimum_mb} MB needed')
        time.sleep(pause)


# ---------------------------------------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------------------------------------

def host_notes():
    cpu = next((line.split(':', 1)[1].strip() for line in Path('/proc/cpuinfo').read_text().splitlines()
                if line.startswith('model name')), platform.processor() or 'unknown CPU')
    memory = {}
    for line in Path('/proc/meminfo').read_text().splitlines():
        key, value = line.split(':', 1)
        memory[key] = int(value.split()[0]) // 1024
    load = ', '.join(f'{x:.2f}' for x in os.getloadavg())
    return {'cpu': cpu, 'cpus': os.cpu_count(), 'memory_mb': memory.get('MemTotal'),
            'available_mb': memory.get('MemAvailable'), 'load': load, 'kernel': platform.release()}


def git_revision():
    sha = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    dirty = bool(subprocess.run(['git', 'status', '--porcelain'], cwd=ROOT, capture_output=True, text=True).stdout.strip())
    return sha or 'unknown', dirty


def ms(value):
    return '–' if value is None else f'{value:.1f}'


def kib(value):
    return f'{value / 1024:.1f}'


def report(meta, counts, endpoints):
    """The Markdown block for the Phase 2 state document."""
    lines = [
        f'### Phase 2 benchmark (item 14d), {meta["date"]}',
        '',
        f'Commit `{meta["sha"][:12]}`{" (uncommitted changes)" if meta["dirty"] else ""}. Dataset: seed {meta["seed"]}, '
        f'{meta["years"]} years, scale {meta["scale"]}, anchored {meta["anchor"]}; {meta["warmup"]} warm-up and '
        f'{meta["rounds"]} measured rounds, interleaved across endpoints (order rotated each round).',
        '',
        f'Host: {meta["host"]["cpu"]}, {meta["host"]["cpus"]} CPUs, {meta["host"]["memory_mb"]} MB RAM '
        f'({meta["host"]["available_mb"]} MB available at start), load {meta["host"]["load"]}, Linux '
        f'{meta["host"]["kernel"]}. {meta["postgres"]} and {RABBIT_IMAGE} in throwaway containers; the API DLL built '
        f'in Release (.NET {meta["dotnet"]}), Production/Token mode, no worker modules, on the same host. '
        f'API peak RSS {meta["api_rss_mb"]} MB; PostgreSQL container {meta["postgres_memory"]}.',
        '',
        f'Phases: generate {meta["phases"]["generate"]:.1f} s, migrate {meta["phases"]["migrate"]:.1f} s, '
        f'seed {meta["phases"]["seed"]:.1f} s, search rebuild {meta["phases"]["rebuild"]:.1f} s, '
        f'measure {meta["phases"]["measure"]:.1f} s.',
        '',
        '| Table | Rows |',
        '| --- | ---: |',
    ]
    lines += [f'| `{table}` | {count:,} |' for table, count in counts.items()]
    lines += [
        '',
        f'Activity aggregates the {meta["activity"]["fills"]:,} retained fills of enabled accounts into '
        f'{meta["activity"]["operations"]:,} operations, {meta["activity"]["closed"]:,} of them closed.',
        '',
        'Times are milliseconds from request to the last body byte on one keep-alive connection, gzip accepted. '
        'Reads through the Activity facts cache are made once right after a committed no-op account update, which '
        'advances the facts revision so the aggregate is computed again ("After write"), then repeated at once ("p50", '
        '"p95"). The API exposes no cache diagnostics, so the Cache column infers whether that repeat is a hit: the '
        'cache refuses an aggregate whose charge exceeds its 256 MiB default capacity, and the charge is estimated '
        'from the generated rows by the cache\'s own rule. Above or near the capacity the repeat is recomputed or '
        'unconfirmed, never called a hit. Other reads are not cached; their p50 and p95 are of every measured read.',
        '',
        '| Endpoint | Request | p50 | p95 | After write p50 | After write p95 | Cache | Wire KiB | JSON KiB |',
        '| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: |',
    ]
    for e in endpoints:
        steady, miss = summarize(e.steady), summarize(e.miss)
        path = re.sub(r'accountId=[^&]+&operationId=[^&]+', 'accountId=…&operationId=…', e.path)
        cache = meta['cache'][e.cache]['status'] if e.cache else '–'
        lines.append(f'| {e.name} | `{path}` | {ms(steady and steady["p50"])} | {ms(steady and steady["p95"])} | '
                     f'{ms(miss and miss["p50"])} | {ms(miss and miss["p95"])} | {cache} | {kib(e.wire)} | {kib(e.size)} |')
    if meta['skipped']:
        lines += ['', 'Skipped, because this dataset cannot populate them: ' + '; '.join(meta['skipped']) + '.']
    return '\n'.join(lines)


# ---------------------------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------------------------

def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--disposable', action='store_true',
                        help='required: this run creates, fills and removes its own PostgreSQL and RabbitMQ containers')
    parser.add_argument('--seed', type=int, default=1, help='dataset seed (default 1)')
    parser.add_argument('--years', type=float, default=3, help='years of history (default 3)')
    parser.add_argument('--scale', type=float, default=1, help='volume multiplier; 1 is about 50,000 fills over 3 years')
    parser.add_argument('--anchor', help='dataset end as an ISO UTC time (default: now, to the hour)')
    parser.add_argument('--warmup', type=int, default=5, help='warm-up rounds (default 5)')
    parser.add_argument('--rounds', type=int, default=20, help='measured rounds (default 20)')
    parser.add_argument('--no-build', action='store_true', help='reuse the existing Release build of Vessel.Api')
    parser.add_argument('--min-available-mb', type=int, default=0,
                        help='wait until this much memory is available before building, migrating, seeding and '
                             'starting the API (default 0: no check)')
    parser.add_argument('--out', help='also write the timings (no journal data) as JSON to this file')
    return parser.parse_args(argv)


def anchor_time(text):
    if text:
        at = datetime.fromisoformat(text.replace('Z', '+00:00'))
        return (at if at.tzinfo else at.replace(tzinfo=UTC)).astimezone(UTC)
    return datetime.now(UTC).replace(minute=0, second=0, microsecond=0)


def main(argv=None):
    args = arguments(argv)
    try:
        check_arguments(args)
    except Refused as refusal:
        print(refusal, file=sys.stderr)
        return 2
    run = Run(args)

    def interrupted(*_):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, interrupted)
    failures, phases = [], {}
    host = host_notes()
    sha, dirty = git_revision()
    try:
        anchor = anchor_time(args.anchor)
        clock = time.monotonic()
        print(f'Generating the dataset (seed {args.seed}, {args.years} years, scale {args.scale})…', flush=True)
        generator = generate(args.seed, args.years, args.scale, anchor)
        run.owner = generator.owner
        phases['generate'] = time.monotonic() - clock
        run.temp = Path(tempfile.mkdtemp(prefix='vessel-bench-'))
        (run.temp / 'evidence').mkdir()
        if not args.no_build:
            wait_for_memory(args.min_available_mb, 'the build')
            print('Building Vessel.Api in Release…', flush=True)
            run.build()
        if not (API_BIN / 'Vessel.Api.dll').exists():
            raise RuntimeError('No Release build of Vessel.Api; run without --no-build.')
        print(f'Starting throwaway containers (run {run.id})…', flush=True)
        run.start_containers()
        wait_for_memory(args.min_available_mb, 'the migrations')
        clock = time.monotonic()
        print('Applying migrations explicitly…', flush=True)
        run.migrate()
        phases['migrate'] = time.monotonic() - clock
        wait_for_memory(args.min_available_mb, 'seeding')
        clock = time.monotonic()
        print(f'Seeding {sum(generator.table_counts().values()):,} rows with COPY…', flush=True)
        run.seed(generator)
        run.psql('VACUUM (ANALYZE)')
        phases['seed'] = time.monotonic() - clock
        clock = time.monotonic()
        print('Rebuilding the journal search projection…', flush=True)
        run.rebuild_search()
        phases['rebuild'] = time.monotonic() - clock
        counts = {}
        for line in run.psql(' UNION ALL '.join(f"SELECT '{t}', count(*) FROM {t}" for t in COUNTED), query=True).splitlines():
            table, count = line.split('\t')
            counts[table] = int(count)
        postgres = run.psql('SHOW server_version', query=True).strip()
        wait_for_memory(args.min_available_mb, 'the API')
        print('Starting the API (Release, Production, Token mode)…', flush=True)
        port = run.start_api()
        client = Client(port, run.token)
        account = next(a for a in generator.accounts if a.enabled)
        revision_sql = f"SELECT revision FROM activity_facts_revisions WHERE owner_id = '{generator.owner}'"
        noop = f'UPDATE accounts SET "Name" = "Name" WHERE "OwnerId" = \'{generator.owner}\' AND "Id" = \'{account.id}\''
        before = run.psql(revision_sql, query=True).strip()
        run.psql(noop)
        if run.psql(revision_sql, query=True).strip() == before:
            raise RuntimeError('The no-op account update did not advance the Activity facts revision.')
        status, _, _, _, body = client.get('/api/activity?status=closed&pageSize=100')
        page = json.loads(body) if status == 200 else {}
        operation = next((o for o in page.get('operations', []) if o['fillCount'] >= 3), None)
        if operation is None:
            raise RuntimeError('Activity returned no closed operation with several executions to expand.')
        activity = {'fills': page['retainedFillCount'], 'operations': page['scopeCount'], 'closed': page['totalCount']}
        # Deep pages come from the API's own counts, checked against the generated ones where they are exact.
        status, _, _, _, body = client.get('/api/plays/counts')
        closed = json.loads(body)['closed'] if status == 200 else None
        if closed != generator.statuses['Closed']:
            raise RuntimeError(f'The Plays counts show {closed} Closed Plays; the dataset has {generator.statuses["Closed"]}.')
        status, _, _, _, body = client.get(f'/api/plays/search?q={COMMON_WORD}')
        matches = json.loads(body)['total'] if status == 200 else 0
        planned, skipped = plan_endpoints(operation['accountId'], operation['id'], activity['operations'], closed, matches)
        endpoints = [Endpoint(*spec) for spec in planned]
        enabled = [a.id for a in generator.accounts if a.enabled]
        charges = {'all': generator.cache_charge(enabled), 'account': generator.cache_charge([operation['accountId']])}
        clock = time.monotonic()
        print(f'Measuring {len(endpoints)} endpoints: {args.warmup} warm-up and {args.rounds} measured rounds…', flush=True)
        measure(client, endpoints, args.warmup, args.rounds, lambda: run.psql(noop), lambda text: print(text, flush=True))
        phases['measure'] = time.monotonic() - clock
        client.close()
        failures = [f'{e.name}: {", ".join(sorted(set(e.failures)))}' for e in endpoints if e.failures]
        stats = subprocess.run(['docker', 'stats', '--no-stream', '--format', '{{.MemUsage}}', run.containers['postgres']],
                               capture_output=True, text=True).stdout.strip().split(' / ')[0] or 'unknown'
        meta = {'date': datetime.now(UTC).strftime('%Y-%m-%d'), 'sha': sha, 'dirty': dirty, 'seed': args.seed,
                'years': args.years, 'scale': args.scale, 'anchor': anchor.strftime('%Y-%m-%dT%H:%MZ'),
                'warmup': args.warmup, 'rounds': args.rounds, 'host': host, 'postgres': f'PostgreSQL {postgres}',
                'dotnet': subprocess.run(['dotnet', '--version'], capture_output=True, text=True).stdout.strip(),
                'api_rss_mb': run.api_peak_rss(), 'postgres_memory': stats, 'phases': phases, 'activity': activity,
                'skipped': skipped, 'cache': {scope: {'estimatedBytes': charge, 'status': cache_status(charge)}
                                              for scope, charge in charges.items()}}
        print()
        print(report(meta, counts, endpoints))
        if args.out:
            Path(args.out).write_text(json.dumps({
                'meta': meta, 'counts': counts,
                'endpoints': [{'name': e.name, 'path': e.path, 'cacheScope': e.cache, 'wireBytes': e.wire, 'jsonBytes': e.size,
                               'steady': summarize(e.steady), 'miss': summarize(e.miss), 'steadyMs': e.steady,
                               'missMs': e.miss, 'failures': e.failures} for e in endpoints]}, indent=2) + '\n')
    except KeyboardInterrupt:
        failures.append('interrupted')
    except Exception as error:  # noqa: BLE001  (reported, then everything is cleaned up)
        failures.append(run.redact(error))
    finally:
        problems = run.cleanup()
        print('Cleanup: ' + ('; '.join(problems) if problems else
                             f'stopped the API and removed {", ".join(run.started) or "no containers"} and the temporary '
                             'directory.'), flush=True)
        failures += problems
    if failures:
        print('\nFailed:\n- ' + '\n- '.join(failures), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
