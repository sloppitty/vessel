import { describe, expect, it, vi } from 'vitest'
import { accountFixture, emptyOverview, portfolioFixture, savedPlayFixture, sizingFixture } from '@/test/workspace-fixture'
import { activityPage, operationFixture } from '@/test/activity-fixture'
import { emptyReview, reviewFixture } from '@/test/review-fixture'
import { overviewSummaryFixture } from '@/test/overview-summary-fixture'
import { createWorkspaceApi, type WorkspaceApi } from './workspace'
import { isActivityFills, isActivityOperation } from './activity'
import { isReviewDocument, type ReviewDocument } from './review'
import { isSizingDocument } from './sizing'
import type { PaperPlayState } from './paper'
import { isPlayHistory, type PlayHistory, type StatusChange } from './plays'
import { credentialsFixture } from '@/test/identity-fixture'

const paperAccount = { ...accountFixture, tradingMode: 'paper', syncStatus: 'paper', venueId: 'hyperliquid', startingCapitalUsd: '4000', balanceUsd: '4000', availableBalanceUsd: '0', availableBalanceSource: 'paper' }
const paperPortfolio = { ...portfolioFixture, tradingMode: 'paper' }
const paperPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, tradingMode: 'paper', venueId: 'hyperliquid', instrumentSource: 'venue' } }
const paperExecution: PaperPlayState['executions'][number] = { id: '12345678-1111-2222-3333-444444444444', entryId: 'entry-1', levelId: null,
  role: 'entry', price: '100.00000000000000000000000001', quantity: '1', pnlUsd: '0', occurredAtUtc: '2026-10-20T12:01:00Z',
  candleOpenAtUtc: '2026-10-20T12:00:00Z', candleCloseAtUtc: '2026-10-20T12:01:00Z' }
const paperOperation = { ...operationFixture, id: savedPlayFixture.summary.id, tradingMode: 'paper', executionSource: 'paper', fillCount: 0,
  paperExecutionCount: 2, entryOrderCount: 0, exitOrderCount: 0, pnlBasis: 'paper-simulation', playAssociation: 'paper', playId: savedPlayFixture.summary.id,
  fundingStatus: 'not-applicable', stablecoinFeesNominalUsd: '0', netResultUsd: '0' }
function response(body: unknown) {
  const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response(JSON.stringify(body))))
  vi.stubGlobal('fetch', fetch)
  return fetch
}

describe('financial report mode boundaries', () => {
  const reports: Array<{ name: string; body: unknown; read: (api: WorkspaceApi) => Promise<unknown> }> = [
    { name: 'Review', body: reviewFixture, read: api => api.review({ period: 'month', portfolio: 'all' }) },
    { name: 'Sizing', body: sizingFixture, read: api => api.sizing() },
    { name: 'Activity', body: activityPage([operationFixture]), read: api => api.activity({ page: 1, pageSize: 25 }) },
    { name: 'Overview summary', body: overviewSummaryFixture(),
      read: api => api.overviewSummary({ groupBy: 'portfolio', period: '30', scope: 'all' }) },
  ]

  it.each(reports)('rejects an omitted $name discriminator on a Paper client', async ({ body, read }) => {
    response(body)
    await expect(read(createWorkspaceApi(credentialsFixture, undefined, 'paper'))).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it.each(reports)('preserves omitted-mode $name responses for default and explicit Real clients', async ({ body, read }) => {
    for (const mode of [undefined, 'real'] as const) {
      response(body)
      await expect(read(createWorkspaceApi(credentialsFixture, undefined, mode))).resolves.toEqual(body)
    }
  })

  it('rejects unlabelled actual imported positions and aggregates even when Paper requests exclusion', async () => {
    const actual: ReviewDocument = JSON.parse(JSON.stringify(reviewFixture))
    Object.assign(actual.plays[0]!, { id: null, origin: 'position', executionSource: 'imported',
      operationId: `${accountFixture.id}:long`, rMultiple: null, reviewed: false, local: null })
    actual.metrics.positions = 1
    actual.periods[0]!.positions = 1
    actual.groups.accounts[0]!.metrics.positions = 1
    actual.positions = { mode: 'include', included: true, minPlays: 5, plays: 2, positions: 1, omitted: 0, playWeight: '2' }
    expect(isReviewDocument(actual)).toBe(true)
    expect(isReviewDocument({ ...actual, tradingMode: 'paper' })).toBe(false)
    response(actual)
    await expect(createWorkspaceApi(credentialsFixture).review({ period: 'month', portfolio: 'all', positions: 'include' })).resolves.toEqual(actual)
    response(actual)
    await expect(createWorkspaceApi(credentialsFixture, undefined, 'paper')
      .review({ period: 'month', portfolio: 'all', positions: 'exclude' })).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('preserves the current unlabelled Overview root but checks any supplied root discriminator', async () => {
    const body = { ...emptyOverview, accounts: [paperAccount], portfolios: [paperPortfolio] }
    const paper = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    response(body)
    await expect(paper.overview()).resolves.toEqual(body)
    response({ ...body, tradingMode: 'paper' })
    await expect(paper.overview()).resolves.toMatchObject({ tradingMode: 'paper' })
    response({ ...emptyOverview, tradingMode: 'real' })
    await expect(paper.overview()).rejects.toMatchObject({ kind: 'invalid-response' })
    response({ ...emptyOverview, tradingMode: 'paper' })
    await expect(createWorkspaceApi(credentialsFixture).overview()).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('does not require a discriminator on mode-neutral counts, exclusions or settings', async () => {
    const paper = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    const counts = { drafts: 0, progress: 0, review: 0, reviewed: 0, closed: 0, cancelled: 0, all: 0 }
    response(counts)
    await expect(paper.playCounts({})).resolves.toEqual(counts)
    const exclusions = { items: [], total: 0, offset: 0, nextOffset: null, notice: 'No excluded paper outcomes.' }
    response(exclusions)
    await expect(paper.reviewExclusions({ period: 'all', portfolio: 'all' })).resolves.toEqual(exclusions)
    const settings = { enabled: false, checkIntervalMinutes: 60 }
    response(settings)
    await expect(paper.paperSettings()).resolves.toEqual(settings)
  })
})

describe('mode-bound workspace requests', () => {
  it('keeps old requests real by default, and explicitly binds independent clients', async () => {
    const fetch = response([])
    const root = createWorkspaceApi(credentialsFixture)
    await root.accounts()
    expect(fetch).toHaveBeenLastCalledWith('/api/accounts', expect.anything())
    const paper = root.forTradingMode!('paper'), real = root.forTradingMode!('real')
    await paper.accounts(); await real.accounts(); await paper.accounts()
    expect(fetch.mock.calls.map(call => call[0])).toEqual(['/api/accounts', '/api/accounts?tradingMode=paper', '/api/accounts?tradingMode=real', '/api/accounts?tradingMode=paper'])
  })
  it('preserves mode on every financial report, counts and ID route', async () => {
    const client = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    const reads: Array<[unknown, () => Promise<unknown>]> = [
      [{ ...emptyOverview, accounts: [paperAccount], portfolios: [paperPortfolio] }, () => client.overview()],
      [[paperAccount], () => client.accounts()], [[paperPortfolio], () => client.portfolios()],
      [paperAccount, () => client.account(accountFixture.id)], [paperPlay, () => client.play(savedPlayFixture.summary.id)],
      [{ ...sizingFixture, tradingMode: 'paper', record: { ...sizingFixture.record, source: 'paper' } }, () => client.sizing()],
      [{ ...emptyReview, tradingMode: 'paper' }, () => client.review({ period: 'month', portfolio: 'all' })],
      [{ ...overviewSummaryFixture(), tradingMode: 'paper' }, () => client.overviewSummary({ groupBy: 'portfolio', period: '30', scope: 'all' })],
      [{ ...activityPage([]), tradingMode: 'paper', paperExecutionCount: 0 }, () => client.activity({ page: 1, pageSize: 25 })],
      [{ drafts: 0, progress: 0, review: 0, reviewed: 0, closed: 0, cancelled: 0, all: 0 }, () => client.playCounts({})],
    ]
    for (const [body, read] of reads) {
      const fetch = response(body)
      await read()
      const url = new URL(fetch.mock.calls[0]![0], 'http://test')
      expect(url.searchParams.get('tradingMode')).toBe('paper')
    }
  })
  it('uses owner paper settings and exact paper-state wire paths without real monitor calls', async () => {
    const client = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    let fetch = response({ enabled: false, checkIntervalMinutes: 60 })
    await client.paperSettings()
    expect(fetch.mock.calls[0]![0]).toBe('/api/paper/settings')
    fetch = response({ enabled: true, checkIntervalMinutes: 1440 })
    await client.savePaperSettings({ enabled: true, checkIntervalMinutes: 1440 })
    expect(fetch.mock.calls[0]![0]).toBe('/api/paper/settings')
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ enabled: true, checkIntervalMinutes: 1440 })
    fetch.mockClear()
    await expect(client.savePaperSettings({ enabled: true, checkIntervalMinutes: 1.5 })).rejects.toBeDefined()
    expect(fetch).not.toHaveBeenCalled()
    const state: PaperPlayState = { playId: savedPlayFixture.summary.id, status: 'gap', checkedAtUtc: null, nextCheckAtUtc: null,
      enteredQuantity: '0', exitedQuantity: '0', openQuantity: '0', realizedPnlUsd: '0', issues: ['Candle history unavailable.'], executions: [],
      declinedEntries: [], suspensions: [], hasUnscoredHistory: true }
    fetch = response(state)
    expect(await client.paperState(state.playId)).toEqual(state)
    expect(fetch.mock.calls[0]![0]).toBe(`/api/plays/${state.playId}/paper-state?tradingMode=paper`)
    fetch = response({ ...state, playId: accountFixture.id })
    await expect(client.paperState(state.playId)).rejects.toMatchObject({ kind: 'invalid-response' })
    fetch.mockClear()
    await expect(createWorkspaceApi(credentialsFixture).paperState(state.playId)).rejects.toBeDefined()
    expect(fetch).not.toHaveBeenCalled()
  })
  it.each(['blocked', 'suspended'] as const)('keeps %s audit facts strictly Paper-only and rejects malformed new wire fields', async status => {
    const state: PaperPlayState = { playId: savedPlayFixture.summary.id, status, checkedAtUtc: '2026-10-20T12:00:00Z',
      nextCheckAtUtc: status === 'blocked' ? '2026-10-20T13:00:00Z' : null,
      enteredQuantity: '1', exitedQuantity: '0', openQuantity: '1', realizedPnlUsd: '-0.000000000001', issues: [], executions: [],
      declinedEntries: [{ entryId: 'local-entry-2', reason: 'Insufficient paper margin.', declinedAtUtc: '2026-10-20T12:00:00Z' }],
      suspensions: [{ reason: 'Paper settings disabled.', suspendedAtUtc: '2026-10-20T12:00:01Z', resumedAtUtc: null }], hasUnscoredHistory: true }
    const client = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    const fetch = response(state)
    expect(await client.paperState(state.playId)).toEqual(state)
    expect(fetch.mock.calls[0]![0]).toBe(`/api/plays/${state.playId}/paper-state?tradingMode=paper`)
    for (const body of [{ ...state, tradingMode: 'real' }, { ...state, declinedEntries: null }, { ...state, suspensions: [{}] },
      { ...state, hasUnscoredHistory: undefined }]) {
      response(body)
      await expect(client.paperState(state.playId)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
    const noReads = response(state)
    for (const mode of [undefined, 'real'] as const)
      await expect(createWorkspaceApi(credentialsFixture, undefined, mode).paperState(state.playId)).rejects.toMatchObject({ kind: 'unavailable' })
    expect(noReads).not.toHaveBeenCalled()
  })
  it('rejects mismatched financial entities instead of relabelling them', async () => {
    response([accountFixture])
    await expect(createWorkspaceApi(credentialsFixture, undefined, 'paper').accounts()).rejects.toMatchObject({ kind: 'invalid-response' })
    response([paperAccount])
    await expect(createWorkspaceApi(credentialsFixture, undefined, 'real').accounts()).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('does not mutate a client or capture a newly selected mode in a late request', async () => {
    let finish!: (response: Response) => void
    const fetch = vi.fn().mockImplementationOnce(() => new Promise<Response>(resolve => { finish = resolve }))
      .mockResolvedValueOnce(new Response(JSON.stringify([paperAccount])))
    vi.stubGlobal('fetch', fetch)
    const root = createWorkspaceApi(credentialsFixture)
    const old = root.forTradingMode!('real').accounts()
    expect(await root.forTradingMode!('paper').accounts()).toEqual([paperAccount])
    finish(new Response(JSON.stringify([accountFixture])))
    expect(await old).toEqual([accountFixture])
    expect(fetch.mock.calls[0]![0]).toBe('/api/accounts?tradingMode=real')
    expect(fetch.mock.calls[1]![0]).toBe('/api/accounts?tradingMode=paper')
  })
  it('blocks actual monitoring, imports, credentials, discovery and manual execution before HTTP', async () => {
    const fetch = response(null), client = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    const controller = new AbortController()
    for (const read of [() => client.monitor(), () => client.accountMonitor(accountFixture.id), () => client.runAccountMonitor(accountFixture.id),
      () => client.sync(accountFixture.id), () => client.fills(accountFixture.id), () => client.accountCredential(accountFixture.id),
      () => client.discoverAccounts('hyperliquid', '0x' + '1'.repeat(40)), () => client.manualExecutions(savedPlayFixture.summary.id),
      () => client.playExecution(savedPlayFixture.summary.id), () => client.marketStream(accountFixture.id, { instrument: 'BTC', interval: '1m' }, controller.signal, vi.fn()), () => client.monitorStream('0', controller.signal, vi.fn()),
      () => client.updateSizingSettings({ riskPercent: '2' })]) await expect(read()).rejects.toBeDefined()
    expect(fetch).not.toHaveBeenCalled()
  })
  it('creates exact immutable-capital accounts and mode-bound portfolios, without wallet fields', async () => {
    let fetch = response(paperAccount)
    const client = createWorkspaceApi(credentialsFixture, undefined, 'paper')
    await client.createAccount({ name: 'Hypothesis 2', portfolioId: portfolioFixture.id, venueId: 'hyperliquid', tradingMode: 'paper', startingCapitalUsd: '0.00000000000000000000000001' })
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ name: 'Hypothesis 2', portfolioId: portfolioFixture.id, venueId: 'hyperliquid', tradingMode: 'paper', startingCapitalUsd: '0.00000000000000000000000001' })
    fetch = response(paperPortfolio)
    await client.createPortfolio('Paper portfolio')
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toMatchObject({ tradingMode: 'paper' })
    fetch.mockClear()
    await expect(client.createAccount({ name: 'Wrong', portfolioId: null, venueId: 'hyperliquid', startingCapitalUsd: '4000', address: '0x' + '1'.repeat(40) })).rejects.toBeDefined()
    expect(fetch).not.toHaveBeenCalled()
  })
})

describe('isolated typed paper report codecs', () => {
  it('decodes the paper row without importing fills or assuming actual order counts', () => {
    expect(isActivityOperation(paperOperation)).toBe(true)
    expect(isActivityOperation({ ...paperOperation, tradingMode: 'real' })).toBe(false)
    expect(isActivityOperation({ ...paperOperation, fillCount: 1 })).toBe(false)
    expect(isActivityOperation({ ...paperOperation, paperExecutionCount: 0 })).toBe(false)
    expect(isActivityOperation({ ...paperOperation, stablecoinFeesNominalUsd: '1' })).toBe(false)
  })
  it('decodes explicit simulated execution detail, never fake imported fill DTOs', () => {
    const body = { tradingMode: 'paper', operationId: savedPlayFixture.summary.id, fills: [], omittedFillCount: 0, paperExecutions: [paperExecution],
      omittedPaperExecutionCount: 0, fundingPayments: [], omittedFundingPaymentCount: 0 }
    expect(isActivityFills(body, accountFixture.id, body.operationId)).toBe(true)
    expect(isActivityFills({ ...body, paperExecutions: [{ ...paperExecution, price: 100 }] }, accountFixture.id, body.operationId)).toBe(false)
    expect(isActivityFills({ ...body, tradingMode: 'real' }, accountFixture.id, body.operationId)).toBe(false)
  })
  it('accepts a separate paper sizing source and rejects unknown report modes', () => {
    expect(isSizingDocument({ ...sizingFixture, tradingMode: 'paper', record: { ...sizingFixture.record, source: 'paper' } })).toBe(true)
    expect(isSizingDocument({ ...sizingFixture, tradingMode: 'test' })).toBe(false)
    expect(isSizingDocument({ ...sizingFixture, tradingMode: 'paper' })).toBe(false)
    expect(isSizingDocument({ ...sizingFixture, record: { ...sizingFixture.record, source: 'paper' } })).toBe(false)
    expect(isReviewDocument({ ...emptyReview, tradingMode: 'paper' })).toBe(true)
    expect(isReviewDocument({ ...emptyReview, tradingMode: 'test' })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, tradingMode: 'paper' })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, plays: reviewFixture.plays.map(p => ({ ...p, executionSource: 'paper', local: undefined })) })).toBe(false)
  })
})

describe('explicit simulated lifecycle history', () => {
  const change = (source?: StatusChange['source']): StatusChange => ({
    from: 'planned', to: 'open', reason: null, note: null, occurredAtUtc: '2026-10-20T12:01:00Z',
    ...(source === undefined ? {} : { source }),
  })
  const history = (source?: StatusChange['source']): PlayHistory => ({ revisions: [], statusChanges: [change(source)] })

  it('preserves owner planning and explicit paper entry/exit lifecycle facts', async () => {
    const body: PlayHistory = { revisions: [], statusChanges: [
      { ...change('owner'), from: 'draft', to: 'planned' },
      change('paper'),
      { ...change('paper'), from: 'open', to: 'closed', occurredAtUtc: '2026-10-20T12:02:00Z' },
    ] }
    expect(isPlayHistory(body)).toBe(true)
    const fetch = response(body)
    expect(await createWorkspaceApi(credentialsFixture, undefined, 'paper').playHistory(savedPlayFixture.summary.id)).toEqual(body)
    expect(fetch.mock.calls[0]![0]).toBe(`/api/plays/${savedPlayFixture.summary.id}/history?tradingMode=paper`)
  })

  it.each([undefined, 'real'] as const)('rejects simulated history in mode %s', async mode => {
    response(history('paper'))
    await expect(createWorkspaceApi(credentialsFixture, undefined, mode).playHistory(savedPlayFixture.summary.id))
      .rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it.each(['venue', 'manual'] as const)('rejects %s facts in paper history rather than relabelling them', async source => {
    response(history(source))
    await expect(createWorkspaceApi(credentialsFixture, undefined, 'paper').playHistory(savedPlayFixture.summary.id))
      .rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it.each([undefined, 'owner', 'venue', 'manual'] as const)('preserves existing real history source %s', async source => {
    const body = history(source)
    response(body)
    await expect(createWorkspaceApi(credentialsFixture).playHistory(savedPlayFixture.summary.id)).resolves.toEqual(body)
  })

  it('still rejects an unknown history discriminator', () => {
    expect(isPlayHistory({ revisions: [], statusChanges: [{ ...change(), source: 'simulated' }] })).toBe(false)
  })
})
