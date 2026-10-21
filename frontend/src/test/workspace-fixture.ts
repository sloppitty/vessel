import { vi } from 'vitest'
import { emptyReview } from './review-fixture'
import type { SizingDocument } from '@/api/sizing'
import type { OpenRisk } from '@/api/risk'
import { pagedPlayViews, playInView, playViews, unpagedPlayLimit, type PlayCounts, type PlayList, type PlayListQuery, type PlaySummary, type SavedPlay } from '@/api/plays'
import type { BrokerAccount, CandleSeries, InstrumentCatalog, MarketContext, Overview, Portfolio, WorkspaceApi } from '@/api/workspace'

export const portfolioFixture: Portfolio = {
  id: '11111111-2222-3333-4444-555555555555', name: 'Swing trading', accountCount: 1,
  totalValueUsd: '1250.123456', valueCoverage: 'complete',
  balanceUsd: '1250.123456', balanceCoverage: 'complete',
}
export const accountFixture: BrokerAccount = {
  id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', portfolioId: portfolioFixture.id,
  name: 'Main account', venueId: 'manual', address: null, accountValueUsd: '1250.123456',
  lastSyncedAtUtc: null, syncStatus: 'manual', lastSyncError: null, positionCount: 0, historyNotice: null, isEnabled: true, settingsRevision: 1,
}
export const emptyOverview: Overview = {
  portfolios: [], accounts: [],
  totals: { portfolioCount: 0, accountCount: 0, totalAccountValueUsd: null, valuedAccountCount: 0, openPositionCount: 0, importedFillCount: 0,
    totalBalanceUsd: null, balanceAccountCount: 0, balanceCoverage: 'unavailable',
    availableBalanceUsd: null, availableBalanceAccountCount: 0, availableBalanceCoverage: 'unavailable', enabledAccountCount: 0 },
  recentActivity: [], scopeNote: 'Values are only known account records. Provider history can be incomplete.',
}
export const overviewFixture: Overview = {
  ...emptyOverview, portfolios: [portfolioFixture], accounts: [accountFixture],
  totals: { ...emptyOverview.totals, portfolioCount: 1, accountCount: 1, totalAccountValueUsd: '1250.123456', valuedAccountCount: 1,
    totalBalanceUsd: '1250.123456', balanceAccountCount: 1, balanceCoverage: 'complete', enabledAccountCount: 1 },
}

export const savedPlayFixture: SavedPlay = {
  summary: {
    id: '22222222-3333-4444-5555-666666666666', title: 'Saved idea', status: 'draft',
    accountId: accountFixture.id, venueId: 'manual', instrument: 'BTC', instrumentSource: 'manual', direction: 'long',
    planRevision: 0, version: 1, cancelReason: null, hasReview: false,
    createdAtUtc: '2026-10-05T12:00:00Z', updatedAtUtc: '2026-10-05T12:00:00Z', plannedAtUtc: null, endedAtUtc: null,
  },
  plan: {
    direction: 'long', sizingMode: 'margin', size: '', leverage: '1', budgetOverride: null,
    entries: [{ id: 'entry-1', name: 'Entry 1', color: '#b9c9e4', share: '100', price: '', stops: [], targets: [] }],
    notes: { thesis: '', invalidation: '', strategy: '', evidence: '' },
  },
  drawings: {}, review: '',
}

export const instrumentCatalogFixture: InstrumentCatalog = {
  venueId: 'hyperliquid', marketScope: 'perpetuals', scope: 'venue-perpetuals',
  instruments: [
    { contractId: 'BTC', quantityDecimals: 5, maxLeverage: 40, quoteAsset: 'USDC' },
    { contractId: '1000PEPE', quantityDecimals: 0, maxLeverage: 10, quoteAsset: 'USDC' },
  ],
  notice: 'Primary perpetual DEX metadata only. No orders, balances or execution refresh.',
}

/** Synthetic test candles only; never shown as market data. */
export const candleSeriesFixture: CandleSeries = {
  venueId: 'hyperliquid', instrument: 'BTC', interval: '1h', priceSource: 'trades',
  candles: [
    { openTime: 1_790_000_000_000, closeTime: 1_790_003_599_999, open: '100.5', high: '102', low: '99.25', close: '101', volume: '12.5', trades: 40 },
    { openTime: 1_790_003_600_000, closeTime: 1_790_007_199_999, open: '101', high: '103.75', low: '100', close: '103', volume: '9', trades: 31 },
  ],
  requestedFrom: 1_788_200_000_000, requestedTo: 1_790_007_200_000, retrievedAt: '2026-10-01T12:00:00Z',
  historyExhausted: false, notice: 'Hyperliquid exposes only the latest 5,000 candles per interval. Prices are trade candles, not fills.',
}

/** Synthetic test statistics only. */
export const marketContextFixture: MarketContext = {
  venueId: 'hyperliquid', instrument: 'BTC', markPrice: '103.5', oraclePrice: '103.4', midPrice: '103.45',
  previousDayPrice: '100', dayNotionalVolume: '1234567.891', openInterest: '42.5', fundingRate: '0.0000125', premium: '-0.0001',
  observedAt: '2026-10-01T12:00:00Z', notice: 'Venue market context for the primary perpetual DEX.',
}

/** A market stream that stays open without events until the caller aborts it. */
export const idleMarketStream: WorkspaceApi['marketStream'] = (_id, _query, signal) =>
  new Promise(resolve => signal.aborted ? resolve() : signal.addEventListener('abort', () => resolve(), { once: true }))

/** The server's list selection over these plays: view, search, scope, order and page, for fake APIs. */
export function playListPage(plays: PlaySummary[], query: PlayListQuery): PlayList {
  const q = query.q?.trim().toLowerCase() ?? ''
  const account = query.scope?.startsWith('account:') ? query.scope.slice(8) : null
  const ended = !['drafts', 'progress', 'all'].includes(query.view)
  const time = (value: string | null) => value === null ? 0 : Date.parse(value)
  const matching = plays.filter(play => playInView(play, query.view) && (account === null || play.accountId === account) &&
    (!q || play.title.toLowerCase().includes(q) || (play.instrument ?? '').toLowerCase().includes(q)))
    .sort((a, b) => (ended ? time(b.endedAtUtc) - time(a.endedAtUtc) : 0) || time(b.updatedAtUtc) - time(a.updatedAtUtc) || a.id.localeCompare(b.id))
  if (!pagedPlayViews.includes(query.view)) {
    const limit = Math.min(query.pageSize ?? unpagedPlayLimit, unpagedPlayLimit)
    return { plays: matching.slice(0, limit), view: query.view, page: 1, pageSize: limit, totalCount: matching.length }
  }
  const pageSize = query.pageSize ?? 25
  const page = Math.min(query.page ?? 1, Math.max(1, Math.ceil(matching.length / pageSize)))
  return { plays: matching.slice((page - 1) * pageSize, page * pageSize), view: query.view, page, pageSize, totalCount: matching.length }
}
/** A fake `plays` endpoint over these plays. */
export const playsFrom = (plays: PlaySummary[]): WorkspaceApi['plays'] => query => Promise.resolve(playListPage(plays, query))
/** A fake `playCounts` endpoint over these plays. */
export const playCountsFrom = (plays: PlaySummary[]): WorkspaceApi['playCounts'] => query => Promise.resolve(
  Object.fromEntries(playViews.map(view => [view, playListPage(plays, { ...query, view }).totalCount])) as PlayCounts)

/** Saved-play API stubs: no saved plays, and every write rejects unless a test overrides it. */
export function playApiStubs(): Pick<WorkspaceApi, 'overviewSummary' | 'plays' | 'playCounts' | 'play' | 'createPlay' | 'updatePlay' | 'changePlayStatus' | 'playHistory' | 'nextActions' | 'completeReview' | 'reopenReview' | 'noReviewNeeded' | 'declareUnresolved' | 'returnToOpen' | 'playNotes' | 'addPlayNote' |
  'deletePlay' | 'playExecution' | 'monitor' | 'accountMonitor' | 'runAccountMonitor' | 'monitorEvents' | 'monitorStream' | 'dismissPlayDiscrepancy' | 'dismissAccountDiscrepancy' | 'linkOrder' | 'unlinkOrder' | 'rejectOrder' | 'rebindLevel' | 'evidence' | 'evidenceImage' | 'uploadEvidence' | 'updateEvidenceNote' | 'updateEvidenceMarkup' | 'deleteEvidence' | 'sizing' | 'updateSizingSettings' | 'openRisk' | 'openRiskPreview' | 'riskSettings' | 'updateRiskSettings' | 'review' | 'reviewExclusions' | 'reviewProcess' | 'mistakeTags' | 'retrospectivePreview' | 'createRetrospectivePlay' | 'manualExecutions' | 'addManualExecution' | 'correctManualExecution' | 'correctManualExecutions' |
  'notificationHead' | 'notifications' | 'claimNotifications' | 'readNotification' | 'readAllNotifications' | 'notificationPreferences' |
  'saveNotificationPreferences' | 'requestTestNotification' | 'paperSettings' | 'savePaperSettings' | 'paperState'> {
  const unexpected = () => Promise.reject(new Error('Unexpected play API call in this test.'))
  return {
    paperSettings: vi.fn().mockResolvedValue({ enabled: false, checkIntervalMinutes: 60 }), savePaperSettings: vi.fn(unexpected), paperState: vi.fn(unexpected),
    // Tests outside Overview keep this unrelated read pending unless they supply a summary fixture.
    overviewSummary: vi.fn().mockImplementation(() => new Promise(() => {})),
    plays: vi.fn(playsFrom([])), playCounts: vi.fn(playCountsFrom([])), play: vi.fn(unexpected), createPlay: vi.fn(unexpected), updatePlay: vi.fn(unexpected),
    changePlayStatus: vi.fn(unexpected), playHistory: vi.fn().mockResolvedValue({ revisions: [], statusChanges: [] }),
    nextActions: vi.fn().mockResolvedValue([]),
    completeReview: vi.fn(unexpected), reopenReview: vi.fn(unexpected), noReviewNeeded: vi.fn(unexpected),
    declareUnresolved: vi.fn(unexpected), returnToOpen: vi.fn(unexpected),
    playNotes: vi.fn().mockResolvedValue([]), addPlayNote: vi.fn(unexpected),
    deletePlay: vi.fn(unexpected), playExecution: vi.fn(unexpected),
    monitor: vi.fn().mockResolvedValue({ workerOnline: true, heartbeatAtUtc: null, accounts: [] }),
    accountMonitor: vi.fn(unexpected), runAccountMonitor: vi.fn(unexpected),
    monitorEvents: vi.fn().mockResolvedValue({ events: [], nextCursor: '0', resetRequired: true }),
    monitorStream: vi.fn((_after, signal) => new Promise<void>(resolve => { signal.addEventListener('abort', () => resolve(), { once: true }) })),
    dismissPlayDiscrepancy: vi.fn(unexpected), dismissAccountDiscrepancy: vi.fn(unexpected),
    linkOrder: vi.fn(unexpected), unlinkOrder: vi.fn(unexpected), rejectOrder: vi.fn(unexpected), rebindLevel: vi.fn(unexpected), evidence: vi.fn().mockResolvedValue([]), evidenceImage: vi.fn(unexpected),
    uploadEvidence: vi.fn(unexpected), updateEvidenceNote: vi.fn(unexpected), updateEvidenceMarkup: vi.fn(unexpected), deleteEvidence: vi.fn(unexpected),
    sizing: vi.fn().mockResolvedValue(sizingFixture), updateSizingSettings: vi.fn(unexpected),
    // Open risk: nothing planned or open, no limit; a preview is unknown until a test supplies one.
    openRisk: vi.fn().mockImplementation((scope: string) => Promise.resolve(emptyOpenRisk(scope))),
    openRiskPreview: vi.fn().mockImplementation((request: { playId: string | null; plan: { direction: 'long' | 'short' } }) => Promise.resolve({
      playId: request.playId, direction: request.plan.direction, riskUsd: null, reason: 'An entry has no stop.', rounded: false })),
    riskSettings: vi.fn().mockResolvedValue({ openRiskLimitPercent: null }), updateRiskSettings: vi.fn(unexpected),
    review: vi.fn().mockResolvedValue(emptyReview),
    reviewExclusions: vi.fn().mockResolvedValue({ items: [], total: 0, offset: 0, nextOffset: null, notice: 'Fixture exclusions.' }),
    reviewProcess: vi.fn().mockResolvedValue({ items: [], total: 0, offset: 0, nextOffset: null }),
    mistakeTags: vi.fn().mockResolvedValue([]),
    retrospectivePreview: vi.fn(unexpected), createRetrospectivePlay: vi.fn(unexpected),
    manualExecutions: vi.fn(unexpected), addManualExecution: vi.fn(unexpected), correctManualExecution: vi.fn(unexpected),
    correctManualExecutions: vi.fn(unexpected),
    // In-app notifications: an empty, enabled channel with its worker online.
    notificationHead: vi.fn().mockResolvedValue({ nextCursor: '0.0.0', unreadCount: 0, enabled: true, workerOnline: true, heartbeatAtUtc: null }),
    notifications: vi.fn().mockImplementation(query => Promise.resolve({ items: [], page: 1, pageSize: query.pageSize, total: 0, unreadTotal: 0, allTotal: 0 })),
    claimNotifications: vi.fn().mockResolvedValue({ popups: [], more: 0 }),
    readNotification: vi.fn(unexpected), readAllNotifications: vi.fn(unexpected),
    notificationPreferences: vi.fn().mockResolvedValue({ enabled: true, minimumSeverity: 'info', categories: ['executions', 'links', 'discrepancies', 'monitor', 'credentials'],
      revision: 0, workerOnline: true, heartbeatAtUtc: null }),
    saveNotificationPreferences: vi.fn(unexpected), requestTestNotification: vi.fn(unexpected),
  }
}

/** Open risk with nothing planned or open in the scope and no limit set. */
export const emptyOpenRisk = (scope: string): OpenRisk => ({
  asOfUtc: '2026-10-21T10:00:00.000Z', scope, balance: { amountUsd: null, contributingAccountCount: 0, totalAccountCount: 0 },
  long: { knownUsd: '0', knownCount: 0, unknownCount: 0, rounded: false }, short: { knownUsd: '0', knownCount: 0, unknownCount: 0, rounded: false },
  notIncluded: [], protection: { notCovered: 0, unverified: 0 }, plays: [], limitPercent: null, notice: 'Fixture open risk.',
})

/** A sizing document with no closed plays, full exposure and the default limits. */
export const sizingFixture: SizingDocument = {
  settings: { riskPercent: '1.25' },
  exposure: { level: 'full', multiplier: '1', reason: 'No losing streak.', lossStreak: 0, winsSinceStepDown: 0 },
  record: {
    source: 'none', closedPlays: 0, decidedPlays: 0, window: 20, wins: 0, losses: 0, scratches: 0,
    battingAverage: null, averageGainPercent: null, averageLossPercent: null, winLossRatio: null, breakEvenRewardRisk: null,
    averageWinR: null, averageLossR: null, recent: [],
  },
  limits: { effectiveRiskPercent: '1.25', maxStopPercent: '10', minRewardRisk: '2', maxStopSource: 'default', minRewardRiskSource: 'default' },
  notice: 'Mechanical estimates from your settings and record, not investment advice.',
}
