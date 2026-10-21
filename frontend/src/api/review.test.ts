import { afterEach, describe, expect, it, vi } from 'vitest'
import { defaultReviewQuery, isReviewDocument, isReviewExclusions, reviewExclusionsPath, reviewPath } from './review'
import { createWorkspaceApi } from './workspace'
import { emptyReview, reviewFixture } from '@/test/review-fixture'

afterEach(() => vi.unstubAllGlobals())
describe('Review API', () => {
  it('accepts both empty and populated reports, preserving exact decimal strings', async () => {
    expect(isReviewDocument(emptyReview)).toBe(true)
    expect(isReviewDocument(reviewFixture)).toBe(true)
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(reviewFixture)))
    vi.stubGlobal('fetch', fetch)
    const controller = new AbortController()
    const report = await createWorkspaceApi('token').review(defaultReviewQuery, controller.signal)
    expect(report.metrics.net).toBe('150.123456789')
    expect(fetch).toHaveBeenCalledWith('/api/review?period=month&portfolio=all', expect.objectContaining({
      signal: expect.any(AbortSignal), cache: 'no-store', credentials: 'omit',
      headers: { Authorization: 'Bearer token', Accept: 'application/json' },
    }))
    controller.abort()
    expect(fetch.mock.calls[0]![1].signal.aborted).toBe(true)
  })
  it.each([
    { metrics: { ...reviewFixture.metrics, net: 150 } },
    { metrics: { ...reviewFixture.metrics, batting: '1.1' } },
    { metrics: { ...reviewFixture.metrics, riskCount: 4 } },
    { metrics: { ...reviewFixture.metrics, wins: 9 } },
    { series: [{ ...reviewFixture.series[0], cumulative: 'NaN' }] },
    { periods: [reviewFixture.periods[0]] },
    { bucketDays: 0 }, { nextOffset: 0 }, { currency: 'USDC' },
    { plays: [{ ...reviewFixture.plays[0], id: '../../play' }] },
  ])('rejects malformed data %#', change => {
    expect(isReviewDocument({ ...reviewFixture, ...change })).toBe(false)
  })
  const positionReport = {
    ...reviewFixture,
    metrics: { ...reviewFixture.metrics, positions: 1 },
    positions: { mode: 'auto', included: true, minPlays: 5, plays: 2, positions: 1, omitted: 0, playWeight: '2' },
    plays: [...reviewFixture.plays, { ...reviewFixture.plays[0], id: null, operationId: 'abc:long', title: 'BTC Long', origin: 'position',
      executionSource: 'imported', rMultiple: null, reviewed: false, local: null }],
  }
  it('accepts imported positions only without a Play ID, R or Play-local result', () => {
    expect(isReviewDocument(positionReport)).toBe(true)
    const position = positionReport.plays.at(-1)!
    for (const change of [{ id: reviewFixture.plays[0]!.id }, { operationId: null }, { rMultiple: '1' }, { local: reviewFixture.plays[0]!.local ?? { netUsd: '1', basis: 'executions', reason: null, signDiffers: false } }])
      expect(isReviewDocument({ ...positionReport, plays: [{ ...position, ...change }] })).toBe(false)
    expect(isReviewDocument({ ...positionReport, plays: [{ ...reviewFixture.plays[0], id: null }] })).toBe(false)
    expect(isReviewDocument({ ...positionReport, metrics: { ...positionReport.metrics, positions: 99 } })).toBe(false)
    expect(isReviewDocument({ ...positionReport, positions: { ...positionReport.positions, positions: null } })).toBe(false)
    expect(isReviewDocument({ ...positionReport, positions: { ...positionReport.positions, mode: 'sometimes' } })).toBe(false)
  })
  it('sends the positions mode and threshold', () => {
    const query = new URL(reviewPath({ ...defaultReviewQuery, positions: 'auto', minPlays: 5 }), 'http://localhost').searchParams
    expect([query.get('positions'), query.get('minPlays')]).toEqual(['auto', '5'])
  })
  it('keeps paper Review typed and rejects imported-position rows or aggregates even if relabelled paper', async () => {
    const paper = {
      ...reviewFixture, tradingMode: 'paper' as const,
      plays: [{ ...reviewFixture.plays[0]!, executionSource: 'paper' as const,
        local: { netUsd: '200.123456789', basis: 'paper-simulation' as const, reason: null, signDiffers: false } }],
      positions: { mode: 'exclude' as const, included: false, minPlays: 5, plays: 3, positions: null, omitted: null, playWeight: '2' },
    }
    expect(isReviewDocument(paper)).toBe(true)
    const mixed = [
      { ...paper, plays: [{ ...positionReport.plays.at(-1), executionSource: 'paper' }] },
      { ...paper, positions: positionReport.positions },
      { ...paper, metrics: { ...paper.metrics, positions: 1 } },
      { ...paper, periods: paper.periods.map(p => ({ ...p, positions: 1 })) },
      { ...paper, groups: { ...paper.groups, accounts: paper.groups.accounts.map(g => ({ ...g, metrics: { ...g.metrics, positions: 1 } })) } },
    ]
    for (const report of mixed) expect.soft(isReviewDocument(report)).toBe(false)
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(paper)))
    vi.stubGlobal('fetch', fetch)
    const api = createWorkspaceApi('token').forTradingMode!('paper')
    expect(await api.review({ ...defaultReviewQuery, positions: 'exclude' })).toEqual(paper)
    expect(fetch).toHaveBeenLastCalledWith('/api/review?period=month&portfolio=all&positions=exclude&tradingMode=paper', expect.anything())
    fetch.mockResolvedValue(new Response(JSON.stringify(mixed[0])))
    await expect(api.review(defaultReviewQuery)).rejects.toThrow()
    fetch.mockResolvedValue(new Response(JSON.stringify(paper)))
    await expect(createWorkspaceApi('token').review(defaultReviewQuery)).rejects.toThrow(/different trading mode/)
  })
  it('encodes asset identifiers without changing scope', () => {
    const path = reviewPath({ ...defaultReviewQuery, instrument: 'A&B/#', offset: 50 })
    const query = new URL(path, 'http://localhost').searchParams
    expect(query.get('instrument')).toBe('A&B/#')
    expect(query.get('portfolio')).toBe('all')
    expect(query.get('offset')).toBe('50')
  })
  it('does not use mocked performance when the endpoint fails', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('server details', { status: 500 })))
    await expect(createWorkspaceApi('token').review(defaultReviewQuery)).rejects.toThrow()
  })
  it('keeps executed-notional returns for retrospective Plays and rejects invented R', () => {
    const play = { ...reviewFixture.plays[0]!, origin: 'retrospective', executionSource: 'imported', rMultiple: null }
    expect(isReviewDocument({ ...reviewFixture, plays: [play] })).toBe(true)
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, returnPercent: null }] })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, rMultiple: '2' }] })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, executionSource: 'guessed' }] })).toBe(false)
  })
  it('accepts the Play-local result and basis counts, and reports from servers before them', () => {
    const play = reviewFixture.plays[0]!
    const local = { netUsd: '-2', basis: 'executions', reason: null, signDiffers: true }
    const metrics = { ...reviewFixture.metrics, signDiffers: 1, localUnknown: 1, feeOnlyLosses: 1, unknownGrossLosses: 0 }
    expect(isReviewDocument({ ...reviewFixture, metrics, plays: [{ ...play, executionSource: 'imported', local }] })).toBe(true)
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, executionSource: 'manual', local: { ...local, basis: 'manual-ledger', signDiffers: false } }] })).toBe(true)
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, local: { netUsd: null, basis: 'executions', reason: 'Its linked fills do not settle the play.', signDiffers: false } }] })).toBe(true)
    // Basis and source must agree; counts stay within their population.
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, executionSource: 'manual', local }] })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, plays: [{ ...play, local: { ...local, netUsd: 'NaN' } }] })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, metrics: { ...metrics, signDiffers: 4 } })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, metrics: { ...metrics, feeOnlyLosses: 2 } })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, metrics: { ...metrics, localUnknown: -1 } })).toBe(false)
    expect(isReviewDocument({ ...reviewFixture, metrics: { ...metrics, unknownGrossLosses: '0' } })).toBe(false)
  })
  it('validates bounded exclusion pages and preserves exact record reasons', async () => {
    const play = reviewFixture.plays[0]!
    const page = { items: [{ playId: play.id, title: play.title, accountId: play.accountId,
      accountName: play.accountName, instrument: play.instrument, reasons: ['Order 84321 is missing fill 12345678-1234-1234-1234-123456789abc.'] }],
      total: 1, offset: 0, nextOffset: null, notice: 'Same Review period and scope.' }
    expect(isReviewExclusions(page)).toBe(true)
    expect(isReviewExclusions({ ...page, nextOffset: 0 })).toBe(false)
    expect(isReviewExclusions({ ...page, total: 2 })).toBe(false)
    expect(isReviewExclusions({ ...page, items: [page.items[0], page.items[0]], total: 2 })).toBe(false)
    expect(isReviewExclusions({ ...page, items: [{ ...page.items[0], reasons: Array.from({ length: 200 }, (_, i) => `Fill ${i} has unsupported fees.`) }] })).toBe(true)
    expect(isReviewExclusions({ ...page, items: Array.from({ length: 51 }, () => page.items[0]), total: 51 })).toBe(false)
    expect(isReviewExclusions({ ...page, items: [{ ...page.items[0], reasons: [] }] })).toBe(false)
    expect(isReviewExclusions({ ...page, items: [{ ...page.items[0], playId: '../foreign' }] })).toBe(false)
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(page)))
    vi.stubGlobal('fetch', fetch)
    const query = { ...defaultReviewQuery, instrument: 'A&B/#', offset: 0 }
    expect(await createWorkspaceApi('token').reviewExclusions!(query)).toEqual(page)
    expect(fetch).toHaveBeenCalledWith(reviewExclusionsPath(query), expect.objectContaining({ cache: 'no-store', credentials: 'omit' }))
    expect(new URL(reviewExclusionsPath(query), 'http://localhost').searchParams.get('instrument')).toBe('A&B/#')
  })
})
