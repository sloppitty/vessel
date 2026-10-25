import { describe, expect, it, vi } from 'vitest'
import { candleCache } from '@/features/market/candleCache'
import { onApiEvent, type ApiEvent } from './events'
import { createWorkspaceApi } from './workspace'
import { accountFixture, candleSeriesFixture, emptyOverview, marketContextFixture, portfolioFixture, sizingFixture } from '@/test/workspace-fixture'
import { credentialsFixture } from '@/test/identity-fixture'

function response(body: unknown, status = 200) {
  const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status }))
  vi.stubGlobal('fetch', fetch)
  return fetch
}
describe('Core workspace API', () => {
  it('passes session cancellation through sync to the HTTP request', async () => {
    const fetch = response(accountFixture)
    const controller = new AbortController()
    await createWorkspaceApi(credentialsFixture).sync(accountFixture.id, controller.signal)
    const signal = fetch.mock.calls[0]![1].signal as AbortSignal
    expect(signal.aborted).toBe(false)
    controller.abort()
    expect(signal.aborted).toBe(true)
  })
  it.each([undefined, null, '0', '-0.0000000000000000000000000001', '131.89123456789012345678901234'])('keeps exact nullable snapshot available margin %s', async value => {
    const snapshot = { observedAtUtc: '2026-10-12T10:00:00Z', valueScope: 'fixture', accountValueUsd: '1000',
      withdrawableUsd: null, availableMarginUsd: value, marginUsedUsd: '20', positions: [] }
    response(snapshot)
    expect((await createWorkspaceApi(credentialsFixture).snapshot(accountFixture.id))?.availableMarginUsd).toBe(value)
  })
  it.each([0, '', '1e3', ' ', true, {}, ['0']])('rejects malformed snapshot available margin %j', async value => {
    response({ observedAtUtc: '2026-10-12T10:00:00Z', valueScope: 'fixture', accountValueUsd: '1000',
      withdrawableUsd: null, availableMarginUsd: value, marginUsedUsd: '20', positions: [] })
    await expect(createWorkspaceApi(credentialsFixture).snapshot(accountFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('keeps the account available amount and selected source, and portfolio coverage', async () => {
    const account = { ...accountFixture, availableBalanceUsd: '-0.0000000000000000000000000001', availableBalanceSource: 'margin' }
    response(account)
    expect(await createWorkspaceApi(credentialsFixture).account(accountFixture.id)).toEqual(account)
    const portfolio = { ...portfolioFixture, availableBalanceUsd: '0', availableBalanceAccountCount: 1,
      enabledAccountCount: 2, availableBalanceCoverage: 'partial' }
    response([portfolio])
    expect(await createWorkspaceApi(credentialsFixture).portfolios()).toEqual([portfolio])
  })
  it.each([{ availableBalanceUsd: 0 }, { availableBalanceUsd: '' }, { availableBalanceSource: 'equity' }, { availableBalanceSource: ['margin'] }])('rejects malformed account availability %j', async fields => {
    response({ ...accountFixture, ...fields })
    await expect(createWorkspaceApi(credentialsFixture).account(accountFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it.each([{ availableBalanceUsd: 0 }, { availableBalanceUsd: '' }, { availableBalanceAccountCount: -1 },
    { enabledAccountCount: 0.5 }, { availableBalanceCoverage: 'full' }, { availableBalanceCoverage: ['partial'] }])('rejects malformed portfolio availability %j', async fields => {
    response([{ ...portfolioFixture, ...fields }])
    await expect(createWorkspaceApi(credentialsFixture).portfolios()).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('keeps exact combined balance strings and coverage and never caches overview responses', async () => {
    const fixture = { ...emptyOverview, totals: { ...emptyOverview.totals,
      totalBalanceUsd: '79228162514264337593543950335.0000000000000000000000000001',
      availableBalanceUsd: '-2.0000000000000000000000000001', balanceAccountCount: 2,
      availableBalanceAccountCount: 1, enabledAccountCount: 3, balanceCoverage: 'partial', availableBalanceCoverage: 'partial' } }
    const fetch = response(fixture)
    const api = createWorkspaceApi(credentialsFixture)
    expect(await api.overview()).toEqual(fixture)
    fetch.mockResolvedValue(new Response(JSON.stringify(emptyOverview)))
    expect(await api.overview()).toEqual(emptyOverview)
    expect(fetch).toHaveBeenCalledTimes(2)
    expect(fetch).toHaveBeenLastCalledWith('/api/overview', expect.objectContaining({ cache: 'no-store' }))
  })
  it.each([
    { totalBalanceUsd: 10 }, { availableBalanceUsd: 5 }, { balanceAccountCount: -1 },
    { availableBalanceAccountCount: 0.5 }, { enabledAccountCount: -1 }, { balanceCoverage: 'full' },
    { availableBalanceCoverage: 'unknown' },
  ])('rejects malformed overview balance fields %#', async bad => {
    response({ ...emptyOverview, totals: { ...emptyOverview.totals, ...bad } })
    await expect(createWorkspaceApi(credentialsFixture).overview()).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('binds the session in a header, not storage, URL or owner body fields', async () => {
    const fetch = response(emptyOverview)
    await expect(createWorkspaceApi(credentialsFixture).overview()).resolves.toEqual(emptyOverview)
    expect(fetch).toHaveBeenCalledWith('/api/overview', expect.objectContaining({
      headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json' },
      credentials: 'same-origin', referrerPolicy: 'same-origin', cache: 'no-store', redirect: 'error',
    }))
    expect(localStorage.getItem('token')).toBeNull()
  })
  it('keeps exact decimal strings in manual account create and response', async () => {
    const fetch = response(accountFixture, 201)
    const payload = { portfolioId: portfolioFixture.id, name: 'Main account', venueId: 'manual' as const, manualAccountValueUsd: '1250.123456' }
    await expect(createWorkspaceApi(credentialsFixture).createAccount(payload)).resolves.toEqual(accountFixture)
    expect(fetch).toHaveBeenCalledWith('/api/accounts', expect.objectContaining({ method: 'POST', body: JSON.stringify(payload) }))
  })
  it('creates a portfolio through the real endpoint contract', async () => {
    const fetch = response(portfolioFixture, 201)
    await createWorkspaceApi(credentialsFixture).createPortfolio('Swing trading')
    expect(fetch).toHaveBeenCalledWith('/api/portfolios', expect.objectContaining({ method: 'POST', body: '{"name":"Swing trading"}' }))
    const withIcon = response({ ...portfolioFixture, icon: 'rocket' }, 201)
    await expect(createWorkspaceApi(credentialsFixture).createPortfolio('Swing trading', 'rocket')).resolves.toMatchObject({ icon: 'rocket' })
    expect(withIcon).toHaveBeenCalledWith('/api/portfolios', expect.objectContaining({ method: 'POST', body: '{"name":"Swing trading","icon":"rocket"}' }))
  })
  it('updates a portfolio name and icon in one request', async () => {
    const fetch = response({ ...portfolioFixture, icon: 'gem' })
    await expect(createWorkspaceApi(credentialsFixture).updatePortfolio(portfolioFixture.id, { name: 'Swing', icon: 'gem' })).resolves.toMatchObject({ icon: 'gem' })
    expect(fetch).toHaveBeenCalledWith(`/api/portfolios/${portfolioFixture.id}`, expect.objectContaining({ method: 'PATCH', body: '{"name":"Swing","icon":"gem"}' }))
  })
  it('rejects a portfolio whose icon is not a short string', async () => {
    response([{ ...portfolioFixture, icon: 'rocket' }])
    await expect(createWorkspaceApi(credentialsFixture).portfolios()).resolves.toMatchObject([{ icon: 'rocket' }])
    response([{ ...portfolioFixture, icon: 7 }])
    await expect(createWorkspaceApi(credentialsFixture).portfolios()).rejects.toThrow('incompatible workspace data')
    response([{ ...portfolioFixture, icon: 'x'.repeat(41) }])
    await expect(createWorkspaceApi(credentialsFixture).portfolios()).rejects.toThrow('incompatible workspace data')
  })
  it('accepts an unavailable snapshot as null, not a zero balance', async () => {
    response(null)
    await expect(createWorkspaceApi(credentialsFixture).snapshot(accountFixture.id)).resolves.toBeNull()
  })
  it.each([
    [401, undefined, 'unauthorized'],
    [403, 'user-suspended', 'unauthorized'],
    [403, undefined, 'http'],
  ] as const)('reports a refused session (%s %s) without returning sensitive response data', async (status, code, kind) => {
    response({ detail: 'secret-token in server output', ...code ? { code } : {} }, status)
    const cause = await createWorkspaceApi(credentialsFixture).accounts().catch((error: unknown) => error)
    expect(cause).toMatchObject({ kind, status })
    expect((cause as Error).message).not.toContain('secret-token')
  })
  it('validates a GUID before composing an account fetch path', () => {
    const fetch = response(null)
    expect(() => createWorkspaceApi(credentialsFixture).snapshot('../../private')).toThrow('identifier')
    expect(fetch).not.toHaveBeenCalled()
  })
  it('shows a safe provider failure message without raw provider bodies', async () => {
    response({ detail: 'raw wallet and secret provider detail' }, 502)
    await expect(createWorkspaceApi(credentialsFixture).sync(accountFixture.id)).rejects.toThrow('Previous account data')
  })
  it('keeps the venue refresh message specific to account refresh', async () => {
    response({ detail: 'proxy detail' }, 502)
    await expect(createWorkspaceApi(credentialsFixture).overview()).rejects.toThrow('The request could not be completed.')
  })
  it.each([500, 503])('shows only a server-generated trace reference for %s', async status => {
    const trace = '0123456789abcdef0123456789abcdef'
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'internal stack detail', traceId: trace }),
      { status, headers: { 'X-Correlation-ID': trace } })))
    const error = await createWorkspaceApi(credentialsFixture).overview().catch((cause: unknown) => cause)
    expect(error).toMatchObject({ kind: 'http', status })
    expect(String((error as Error).message)).toContain(`Reference: ${trace}`)
    expect(String((error as Error).message)).not.toContain('internal')
  })
  it('ignores a malformed correlation header', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 500, headers: { 'X-Correlation-ID': 'call support <script>' } })))
    await expect(createWorkspaceApi(credentialsFixture).overview()).rejects.toThrow(/^The request could not be completed\.$/)
  })
  it('reads one account, including its settings revision', async () => {
    const fetch = response({ ...accountFixture, settingsRevision: 4 })
    await expect(createWorkspaceApi(credentialsFixture).account(accountFixture.id)).resolves.toMatchObject({ settingsRevision: 4 })
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}`, expect.anything())
  })
  it('accepts imported fills that a later Play links to', async () => {
    const linked = { id: accountFixture.id, accountId: accountFixture.id, contractId: 'BTC', side: 'buy', direction: 'Open Long', price: '1',
      quantity: '1', fee: '0', feeToken: 'USDC', closedPnlUsd: '0', occurredAtUtc: '2026-10-01T10:00:00Z', orderId: '1', sourceFillId: '1',
      transactionHash: '0x1', positionEffect: 'open', feeBasis: 'reported', pnlBasis: 'gross', playId: portfolioFixture.id }
    response([linked])
    await expect(createWorkspaceApi(credentialsFixture).fills(accountFixture.id)).resolves.toEqual([linked])
  })
  it('accepts bounded safe validation detail for input correction', async () => {
    response({ detail: 'The public address is invalid.' }, 400)
    await expect(createWorkspaceApi(credentialsFixture).createAccount({ portfolioId: portfolioFixture.id, name: 'Wallet', venueId: 'hyperliquid', address: 'bad' })).rejects.toThrow('public address')
  })
  it.each([
    { ...accountFixture, accountValueUsd: 1250.123456 },
    { ...accountFixture, accountValueUsd: 'NaN' },
    { ...accountFixture, positionCount: -1 },
    { ...emptyOverview, totals: { ...emptyOverview.totals, totalAccountValueUsd: 0 } },
  ])('rejects incompatible response numeric semantics %#', async body => {
    response('totals' in body ? body : [body])
    const api = createWorkspaceApi(credentialsFixture)
    const run = 'totals' in body ? api.overview() : api.accounts()
    await expect(run).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('does not expose network transport errors', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('private detail in failed transport')))
    await expect(createWorkspaceApi(credentialsFixture).overview()).rejects.toThrow('did not complete')
  })
  it('sends a complete account settings replacement including explicit unlink and enabled state', async () => {
    const fetch = response({ ...accountFixture, portfolioId: null, isEnabled: false })
    const settings = { name: 'Renamed wallet', portfolioId: null, isEnabled: false, expectedRevision: 1 }
    await createWorkspaceApi(credentialsFixture).updateAccount(accountFixture.id, settings)
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}`, expect.objectContaining({
      method: 'PUT', body: JSON.stringify(settings),
    }))
  })
  it('creates an account without a stored default portfolio', async () => {
    const fetch = response({ ...accountFixture, portfolioId: null }, 201)
    await createWorkspaceApi(credentialsFixture).createAccount({ portfolioId: null, name: 'Unassigned', venueId: 'manual' })
    expect(fetch).toHaveBeenCalledWith('/api/accounts', expect.objectContaining({
      body: '{"portfolioId":null,"name":"Unassigned","venueId":"manual"}',
    }))
  })
  it('handles delete no-content responses without trying to parse JSON', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    await expect(createWorkspaceApi(credentialsFixture).deleteAccount(accountFixture.id)).resolves.toBeUndefined()
    await expect(createWorkspaceApi(credentialsFixture).deletePortfolio(portfolioFixture.id)).resolves.toBeUndefined()
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}`, expect.objectContaining({ method: 'DELETE' }))
    expect(fetch).toHaveBeenCalledWith(`/api/portfolios/${portfolioFixture.id}`, expect.objectContaining({ method: 'DELETE' }))
  })
  it('shows the safe linked-play conflict instead of treating deletion as successful', async () => {
    response({ detail: 'This account has linked plays. Disable it instead.' }, 409)
    await expect(createWorkspaceApi(credentialsFixture).deleteAccount(accountFixture.id)).rejects.toMatchObject({
      kind: 'http', status: 409, message: 'This account has linked plays. Disable it instead.',
    })
  })
  it('retains enable/disable state in account response validation', async () => {
    response([{ ...accountFixture, isEnabled: false }])
    await expect(createWorkspaceApi(credentialsFixture).accounts()).resolves.toEqual([{ ...accountFixture, isEnabled: false }])
  })
  it('rejects non-boolean enabled flags', async () => {
    response([{ ...accountFixture, isEnabled: 'false' }])
    await expect(createWorkspaceApi(credentialsFixture).accounts()).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('accepts stablecoin wallet observations as decimal strings without merging perp equity', async () => {
    const body = {
      observedAtUtc: '2026-10-01T12:00:00Z', valueScope: 'primary-perpetual-dex',
      accountValueUsd: '0', withdrawableUsd: '0', marginUsedUsd: '0', positions: [],
      stablecoinWallet: {
        observedAtUtc: '2026-10-01T12:00:02Z', accountMode: 'unifiedAccount', scope: 'hypercore-spot-stablecoins',
        totalNominalUsd: '12.12345678901234567890123456', availableNominalUsd: '10.12345678901234567890123456',
        balances: [{ symbol: 'USDC', tokenIndex: 0, tokenId: 'known-token',
          total: '12.12345678901234567890123456', held: '2', available: '10.12345678901234567890123456' }],
        notice: 'Wallet only, not free margin.',
      },
    }
    response(body)
    await expect(createWorkspaceApi(credentialsFixture).snapshot(accountFixture.id)).resolves.toEqual(body)
  })
  it('rejects stablecoin balance numbers that would lose exact string semantics', async () => {
    response({
      observedAtUtc: '2026-10-01T12:00:00Z', valueScope: 'primary-perpetual-dex',
      accountValueUsd: '0', withdrawableUsd: '0', marginUsedUsd: '0', positions: [],
      stablecoinWallet: {
        observedAtUtc: '2026-10-01T12:00:02Z', accountMode: 'unifiedAccount', scope: 'hypercore-spot-stablecoins',
        totalNominalUsd: '12', availableNominalUsd: '10',
        balances: [{ symbol: 'USDC', tokenIndex: 0, tokenId: 'known-token', total: 12, held: '2', available: '10' }],
        notice: 'Wallet only.',
      },
    })
    await expect(createWorkspaceApi(credentialsFixture).snapshot(accountFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
})

describe('Instrument catalogue requests', () => {
  const catalogue = {
    venueId: 'hyperliquid', marketScope: 'perpetuals', scope: 'venue-perpetuals',
    instruments: [{ contractId: '1000PEPE', quantityDecimals: 0, maxLeverage: 10, quoteAsset: 'USDC' }], notice: 'Primary perpetual DEX only.',
  }
  it('accepts a maintenance margin fraction stated by the venue', async () => {
    const stated = { ...catalogue, instruments: [{ ...catalogue.instruments[0]!, maintenanceMarginFraction: 0.05 }] }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(stated))))
    await expect(createWorkspaceApi(credentialsFixture).instruments(accountFixture.id, new AbortController().signal)).resolves.toEqual(stated)
  })
  it('uses the authenticated metadata route with cancellation and preserves exact contract IDs', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(catalogue))))
    const controller = new AbortController()
    await expect(createWorkspaceApi(credentialsFixture).instruments(accountFixture.id, controller.signal)).resolves.toEqual(catalogue)
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}/instruments`, expect.objectContaining({
      credentials: 'same-origin', referrerPolicy: 'same-origin', redirect: 'error', cache: 'no-store',
      headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json' }, signal: expect.any(AbortSignal),
    }))
  })
  it.each([
    { ...catalogue, marketScope: 'spot' },
    { ...catalogue, scope: 'primary-perpetual-dex' },
    { ...catalogue, instruments: [{ contractId: 'BTC', quantityDecimals: 2, maxLeverage: 10, quoteAsset: 'USDC', maintenanceMarginFraction: 1 }] },
    { ...catalogue, instruments: [{ contractId: 'BTC', quantityDecimals: -1, maxLeverage: 10, quoteAsset: 'USDC' }] },
    { ...catalogue, instruments: [{ contractId: 'BTC', quantityDecimals: 2, maxLeverage: 1.5, quoteAsset: 'USDC' }] },
    { ...catalogue, instruments: [catalogue.instruments[0], catalogue.instruments[0]] },
    { ...catalogue, instruments: [{ contractId: ' BTC ', quantityDecimals: 2, maxLeverage: 10, quoteAsset: 'USDC' }] },
    { ...catalogue, scope: 'manual' },
  ])('rejects malformed or unsupported catalogue data', async body => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body))))
    await expect(createWorkspaceApi(credentialsFixture).instruments(accountFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('uses a catalogue-specific safe 502 error instead of account refresh wording', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('private provider details', { status: 502 })))
    await expect(createWorkspaceApi(credentialsFixture).instruments(accountFixture.id)).rejects.toThrow('venue instrument catalogue')
  })
})

describe('Candle requests', () => {
  it('uses the authenticated candles route with encoded query and keeps decimal strings', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(candleSeriesFixture))))
    await expect(createWorkspaceApi(credentialsFixture).candles(accountFixture.id, { instrument: 'BTC', interval: '1h', endTime: 1_790_000_000_000 }))
      .resolves.toEqual(candleSeriesFixture)
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}/candles?instrument=BTC&interval=1h&endTime=1790000000000`,
      expect.objectContaining({ credentials: 'same-origin', referrerPolicy: 'same-origin', headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'application/json' } }))
  })
  it.each([
    { instrument: 'xyz:BTC', interval: '1h' as const },
    { instrument: 'BTC', interval: '2m' as unknown as '1h' },
    { instrument: 'BTC', interval: '1h' as const, endTime: -1 },
  ])('rejects invalid queries before sending them', async query => {
    vi.stubGlobal('fetch', vi.fn())
    await expect(createWorkspaceApi(credentialsFixture).candles(accountFixture.id, query)).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
  const [first, second] = candleSeriesFixture.candles
  it.each([
    { ...candleSeriesFixture, instrument: 'ETH' },
    { ...candleSeriesFixture, interval: '4h' },
    { ...candleSeriesFixture, candles: [{ ...first, open: 100.5 }] },
    { ...candleSeriesFixture, candles: [{ ...first, low: '-1' }] },
    { ...candleSeriesFixture, candles: [second, first] },
    { ...candleSeriesFixture, candles: [first, first] },
    { ...candleSeriesFixture, historyExhausted: 'no' },
  ])('rejects malformed or mismatched candle data', async body => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body))))
    await expect(createWorkspaceApi(credentialsFixture).candles(accountFixture.id, { instrument: 'BTC', interval: '1h' })).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('uses a candle-specific safe 502 error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('private provider details', { status: 502 })))
    await expect(createWorkspaceApi(credentialsFixture).candles(accountFixture.id, { instrument: 'BTC', interval: '1h' })).rejects.toThrow('Venue candles are unavailable')
  })
})

describe('Market context requests', () => {
  it('reads exact statistics for one instrument', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(marketContextFixture))))
    await expect(createWorkspaceApi(credentialsFixture).marketContext(accountFixture.id, 'BTC')).resolves.toEqual(marketContextFixture)
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}/market-context?instrument=BTC`, expect.anything())
  })
  it.each([
    { ...marketContextFixture, instrument: 'ETH' },
    { ...marketContextFixture, markPrice: 103.5 },
    { ...marketContextFixture, openInterest: '-1' },
    { ...marketContextFixture, fundingRate: 'high' },
    { ...marketContextFixture, observedAt: 'yesterday' },
  ])('rejects malformed statistics', async body => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body))))
    await expect(createWorkspaceApi(credentialsFixture).marketContext(accountFixture.id, 'BTC')).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('accepts negative funding and premium and null mid price', async () => {
    const body = { ...marketContextFixture, fundingRate: '-0.00002', premium: '-0.0003', midPrice: null }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body))))
    await expect(createWorkspaceApi(credentialsFixture).marketContext(accountFixture.id, 'BTC')).resolves.toEqual(body)
  })
})

describe('Market stream', () => {
  const encoder = new TextEncoder()
  const query = { instrument: 'BTC', interval: '1h' as const }
  const [first] = candleSeriesFixture.candles
  function stream(chunks: string[], { close = true, headers = { 'Content-Type': 'text/event-stream; charset=utf-8' }, status = 200 } = {}) {
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
        if (close) controller.close()
      },
    })
    const fetch = vi.fn().mockResolvedValue(new Response(body, { status, headers }))
    vi.stubGlobal('fetch', fetch)
    return fetch
  }
  const frame = (event: string, data: unknown) => `event: ${event}\ndata: ${typeof data === 'string' ? data : JSON.stringify(data)}\n\n`

  it('streams with the session header, nothing secret in the URL, and validates every event', async () => {
    const status = { state: 'live', observedAt: '2026-10-02T08:00:00Z' }
    const text = [
      ': keepalive\n\n',
      frame('candle', first), frame('candle', { ...first, open: 100.5 }), frame('candle', '{broken'),
      frame('context', { ...marketContextFixture, instrument: 'ETH' }), frame('context', marketContextFixture),
      frame('status', status), frame('status', { state: 'up', observedAt: status.observedAt }), frame('status', { state: 'stale', observedAt: 'soon' }),
      frame('trade', { price: '1' }),
    ].join('')
    const fetch = stream([text.slice(0, 37), text.slice(37, 300), text.slice(300)])
    const events: unknown[] = []
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, e => events.push(e))).resolves.toBeUndefined()
    expect(fetch).toHaveBeenCalledWith(`/api/accounts/${accountFixture.id}/market-stream?instrument=BTC&interval=1h`, expect.objectContaining({
      headers: { 'X-Vessel-User': credentialsFixture.userId, Accept: 'text/event-stream' }, credentials: 'same-origin', referrerPolicy: 'same-origin', redirect: 'error', cache: 'no-store',
    }))
    expect(events).toEqual([{ type: 'candle', candle: first }, { type: 'context', context: marketContextFixture }, { type: 'status', status }])
  })

  it('maps errors before the stream starts, including too many streams', async () => {
    stream([], { status: 429, headers: { 'Content-Type': 'application/problem+json' } })
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn()))
      .rejects.toMatchObject({ kind: 'http', status: 429, message: 'Too many live chart streams. Close another chart and try again.' })
    stream([], { status: 502 })
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn())).rejects.toThrow('Venue market data is unavailable')
    stream([], { status: 401 })
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn())).rejects.toMatchObject({ kind: 'unauthorized' })
    stream([frame('candle', first)], { headers: { 'Content-Type': 'application/json' } })
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn())).rejects.toMatchObject({ kind: 'invalid-response' })
  })

  it('rejects invalid queries before connecting', async () => {
    vi.stubGlobal('fetch', vi.fn())
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, { instrument: 'xyz:BTC', interval: '1h' }, new AbortController().signal, vi.fn()))
      .rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })

  it('treats an abort as a silent end, before and after the response', async () => {
    stream([frame('candle', first)], { close: false })
    const controller = new AbortController()
    const onEvent = vi.fn(() => controller.abort())
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, controller.signal, onEvent)).resolves.toBeUndefined()
    expect(onEvent).toHaveBeenCalledOnce()
    const aborted = new AbortController()
    aborted.abort()
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new DOMException('Aborted', 'AbortError')))
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, aborted.signal, vi.fn())).resolves.toBeUndefined()
  })

  it('rejects network failures and oversized messages', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('network')))
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn())).rejects.toMatchObject({ kind: 'unavailable' })
    const body = new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(encoder.encode(frame('candle', first))); controller.error(new TypeError('reset')) } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'text/event-stream' } })))
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn())).rejects.toMatchObject({ kind: 'unavailable' })
    stream([`data: ${'x'.repeat(1024 * 1024)}`], { close: false })
    await expect(createWorkspaceApi(credentialsFixture).marketStream(accountFixture.id, query, new AbortController().signal, vi.fn())).rejects.toMatchObject({ kind: 'invalid-response' })
  })
})

describe('Sizing API', () => {
  it('reads the sizing document with exact decimal strings and nulls', async () => {
    const fetch = response(sizingFixture)
    await expect(createWorkspaceApi(credentialsFixture).sizing()).resolves.toEqual(sizingFixture)
    expect(fetch).toHaveBeenCalledWith('/api/sizing', expect.objectContaining({ headers: expect.objectContaining({ 'X-Vessel-User': credentialsFixture.userId }) }))
  })
  it('saves the risk setting as a decimal string and returns the new document', async () => {
    const updated = { ...sizingFixture, settings: { riskPercent: '1.5' }, limits: { ...sizingFixture.limits, effectiveRiskPercent: '1.5' } }
    const fetch = response(updated)
    await expect(createWorkspaceApi(credentialsFixture).updateSizingSettings({ riskPercent: '1.5' })).resolves.toEqual(updated)
    expect(fetch).toHaveBeenCalledWith('/api/sizing/settings', expect.objectContaining({ method: 'PUT', body: '{"riskPercent":"1.5"}' }))
  })
  it('rejects numbers where decimal strings are expected', async () => {
    response({ ...sizingFixture, limits: { ...sizingFixture.limits, maxStopPercent: 10 } })
    await expect(createWorkspaceApi(credentialsFixture).sizing()).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('shows the server reason for an out-of-range risk', async () => {
    response({ detail: 'Risk per trade must be between 0.1% and 5%.' }, 400)
    await expect(createWorkspaceApi(credentialsFixture).updateSizingSettings({ riskPercent: '9' })).rejects.toThrow('between 0.1% and 5%')
  })
})


describe('candle cache API boundaries', () => {
  it('encodes refresh=true only when explicitly requested', async () => {
    const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response(JSON.stringify(candleSeriesFixture))))
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi(credentialsFixture)
    await api.candles(accountFixture.id, { instrument: 'BTC', interval: '1h', refresh: true })
    await api.candles(accountFixture.id, { instrument: 'BTC', interval: '1h', refresh: false })
    await api.candles(accountFixture.id, { instrument: 'BTC', interval: '1h', endTime: 1_790_000_000_000 })
    expect(fetch.mock.calls.map(call => call[0])).toEqual([
      `/api/accounts/${accountFixture.id}/candles?instrument=BTC&interval=1h&refresh=true`,
      `/api/accounts/${accountFixture.id}/candles?instrument=BTC&interval=1h`,
      `/api/accounts/${accountFixture.id}/candles?instrument=BTC&interval=1h&endTime=1790000000000`,
    ])
  })

  it.each(['disable', 'delete'])('invalidates candle entries after a successful account %s', async operation => {
    const api = createWorkspaceApi(credentialsFixture)
    const cache = candleCache(api), entry = cache.entry(accountFixture.id, 'BTC', '1h')
    response(candleSeriesFixture)
    await cache.load(entry, 'latest')
    expect(entry.snapshot.candles).toHaveLength(2)
    if (operation === 'disable') {
      response({ ...accountFixture, isEnabled: false })
      await api.updateAccount(accountFixture.id, { name: accountFixture.name, portfolioId: null, isEnabled: false, expectedRevision: 0 })
    } else {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))
      await api.deleteAccount(accountFixture.id)
    }
    expect(cache.entries.size).toBe(0)
    expect(entry.snapshot.candles).toEqual([])
  })

  it('clears cached candles when an unrelated authenticated API read is unauthorized', async () => {
    const api = createWorkspaceApi(credentialsFixture)
    const cache = candleCache(api), entry = cache.entry(accountFixture.id, 'BTC', '1h')
    response(candleSeriesFixture)
    await cache.load(entry, 'latest')
    response({}, 401)
    await expect(api.overview()).rejects.toThrow()
    expect(cache.entries.size).toBe(0)
  })
})

describe('Client events', () => {
  function listen(api: ReturnType<typeof createWorkspaceApi>) {
    const events: ApiEvent[] = []
    return { events, stop: onApiEvent(api, event => events.push(event)) }
  }
  it.each([
    [401, '{}'],
    [403, JSON.stringify({ code: 'user-suspended' })],
  ])('reports an ended session (%s) from JSON, Play and stream requests to that client only', async (status, body) => {
    const api = createWorkspaceApi(credentialsFixture), other = createWorkspaceApi(credentialsFixture)
    const { events } = listen(api), { events: otherEvents } = listen(other)
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(new Response(body, { status }))))
    await expect(api.overview()).rejects.toMatchObject({ kind: 'unauthorized' })
    await expect(api.plays({ view: 'progress' })).rejects.toMatchObject({ kind: 'unauthorized' })
    await expect(api.marketStream(accountFixture.id, { instrument: 'BTC', interval: '1h' }, new AbortController().signal, vi.fn()))
      .rejects.toMatchObject({ kind: 'unauthorized' })
    expect(events).toEqual([{ type: 'unauthorized' }, { type: 'unauthorized' }, { type: 'unauthorized' }])
    expect(otherEvents).toEqual([])
  })
  it('reports a removed account only after a disabling update or a delete succeeds', async () => {
    const api = createWorkspaceApi(credentialsFixture)
    const { events, stop } = listen(api)
    const settings = { name: accountFixture.name, portfolioId: null, isEnabled: true, expectedRevision: 1 }
    response({ ...accountFixture, isEnabled: true })
    await api.updateAccount(accountFixture.id, settings)
    expect(events).toEqual([])
    response({ ...accountFixture, isEnabled: false })
    await api.updateAccount(accountFixture.id, { ...settings, isEnabled: false })
    response({ detail: 'This account has linked plays.' }, 409)
    await expect(api.deleteAccount(accountFixture.id)).rejects.toMatchObject({ status: 409 })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))
    await api.deleteAccount(accountFixture.id)
    expect(events).toEqual([{ type: 'account-removed', accountId: accountFixture.id }, { type: 'account-removed', accountId: accountFixture.id }])
    stop()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))
    await api.deleteAccount(accountFixture.id)
    expect(events).toHaveLength(2)
  })
})

describe('Portfolio membership API', () => {
  it('sends per-account expectations on both membership endpoints', async () => {
    const expected = [{ accountId: accountFixture.id, settingsRevision: 1 }]
    const fetch = response(portfolioFixture)
    await createWorkspaceApi(credentialsFixture).createPortfolio('Together', 'folder', [accountFixture.id], expected)
    expect(JSON.parse(fetch.mock.calls[0]![1].body as string).expected).toEqual(expected)
    fetch.mockResolvedValueOnce(new Response(JSON.stringify([{ ...accountFixture, portfolioId: null, settingsRevision: 2 }]), { status: 200 }))
    await createWorkspaceApi(credentialsFixture).moveAccounts([accountFixture.id], null, expected)
    expect(JSON.parse(fetch.mock.calls[1]![1].body as string).expected).toEqual(expected)
  })
  it.each([
    [], [{ accountId: portfolioFixture.id, settingsRevision: 1 }],
    [{ accountId: accountFixture.id, settingsRevision: 1 }, { accountId: accountFixture.id, settingsRevision: 1 }],
    [{ accountId: accountFixture.id, settingsRevision: 0 }], [{ accountId: accountFixture.id, settingsRevision: -1 }],
    [{ accountId: accountFixture.id, settingsRevision: 1.5 }], [{ accountId: accountFixture.id, settingsRevision: Number.MAX_SAFE_INTEGER + 1 }],
    [{ accountId: accountFixture.id }], [null], null,
  ].map(expected => ({ expected })))('rejects invalid expectations before either request $expected', async ({ expected }) => {
    const fetch = response([])
    const invalid = expected as Parameters<ReturnType<typeof createWorkspaceApi>['moveAccounts']>[2]
    await expect(createWorkspaceApi(credentialsFixture).moveAccounts([accountFixture.id], null, invalid)).rejects.toMatchObject({ kind: 'invalid-response' })
    await expect(createWorkspaceApi(credentialsFixture).createPortfolio('Together', 'folder', [accountFixture.id], invalid)).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
  it('requires selected IDs when portfolio creation has expectations', async () => {
    const fetch = response(portfolioFixture)
    await expect(createWorkspaceApi(credentialsFixture).createPortfolio('Together', 'folder', undefined,
      [{ accountId: accountFixture.id, settingsRevision: 1 }])).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
  it('includes optional account IDs in portfolio creation', async () => {
    const fetch = response(portfolioFixture)
    await createWorkspaceApi(credentialsFixture).createPortfolio('Together', 'anchor', [accountFixture.id])
    expect(fetch.mock.calls[0]![0]).toBe('/api/portfolios')
    expect(JSON.parse(fetch.mock.calls[0]![1].body as string)).toEqual({ name: 'Together', icon: 'anchor', accountIds: [accountFixture.id] })
  })
  it.each([portfolioFixture.id, null])('posts an exact nullable destination %s and validates the returned accounts', async portfolioId => {
    const updated = { ...accountFixture, portfolioId, settingsRevision: 2 }
    const fetch = response([updated])
    expect(await createWorkspaceApi(credentialsFixture).moveAccounts([accountFixture.id], portfolioId)).toEqual([updated])
    expect(fetch.mock.calls[0]![0]).toBe('/api/accounts/portfolio')
    expect(fetch.mock.calls[0]![1].method).toBe('POST')
    expect(JSON.parse(fetch.mock.calls[0]![1].body as string)).toEqual({ accountIds: [accountFixture.id], portfolioId })
    expect(fetch.mock.calls[0]![1].cache).toBe('no-store')
  })
  it.each([
    [], [accountFixture.id, accountFixture.id], ['not-a-guid'], Array.from({ length: 201 }, (_, index) => `aaaaaaaa-bbbb-cccc-dddd-${String(index).padStart(12, '0')}`),
  ].map(ids => ({ ids })))('rejects invalid account lists before sending $ids', async ({ ids }) => {
    const fetch = response([])
    await expect(createWorkspaceApi(credentialsFixture).moveAccounts(ids, null)).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
  it('rejects an invalid target before sending', async () => {
    const fetch = response([])
    await expect(createWorkspaceApi(credentialsFixture).moveAccounts([accountFixture.id], 'invalid')).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
  it.each([
    null, {}, [], [accountFixture, accountFixture], [{ ...accountFixture, id: portfolioFixture.id }],
    [{ ...accountFixture, portfolioId: null }], [{ ...accountFixture, settingsRevision: undefined }],
    [{ ...accountFixture, settingsRevision: 0 }], [{ ...accountFixture, accountValueUsd: 100 }],
  ].map(value => ({ value })))('rejects incomplete, unexpected or malformed move responses $value', async ({ value }) => {
    response(value)
    await expect(createWorkspaceApi(credentialsFixture).moveAccounts([accountFixture.id], portfolioFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
})
