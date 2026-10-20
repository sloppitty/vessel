import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { playCountsPath, playListPath, type PlayCounts, type PlayListQuery, type PlaySummary } from './plays'
import { accountFixture, savedPlayFixture } from '@/test/workspace-fixture'

function response(value: unknown) { vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(value), { status: 200 }))) }
afterEach(() => vi.unstubAllGlobals())
const base = savedPlayFixture.summary
const play = (n: number, fields: Partial<PlaySummary> = {}): PlaySummary =>
  ({ ...base, id: `${String(n).padStart(8, '0')}-3333-4444-5555-666666666666`, ...fields })
const closed = (n: number, fields: Partial<PlaySummary> = {}) => play(n, { status: 'closed', endedAtUtc: '2026-10-06T12:00:00Z', ...fields })
const counts: PlayCounts = { drafts: 2, progress: 3, review: 4, reviewed: 1, closed: 3, cancelled: 2, all: 10 }
const other = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'

describe('Plays list API', () => {
  it('builds view, search, scope and page parameters', () => {
    expect(playListPath({ view: 'progress' })).toBe('/api/plays?view=progress')
    expect(playListPath({ view: 'closed', q: '  BTC breakout ', scope: 'all', page: 2, pageSize: 50 })).toBe('/api/plays?q=BTC+breakout&scope=all&view=closed&page=2&pageSize=50')
    expect(playListPath({ view: 'drafts', q: '   ', scope: `account:${accountFixture.id}`, pageSize: 5 })).toBe(`/api/plays?scope=account%3A${accountFixture.id}&view=drafts&pageSize=5`)
    expect(playCountsPath({})).toBe('/api/plays/counts')
    expect(playCountsPath({ scope: 'all' })).toBe('/api/plays/counts?scope=all')
    expect(playCountsPath({ q: '50%_off', scope: 'unassigned' })).toBe('/api/plays/counts?q=50%25_off&scope=unassigned')
  })
  it.each([
    { view: 'ended' }, { view: 'all', page: 0 }, { view: 'all', pageSize: 101 }, { view: 'all', q: 'x'.repeat(101) }, { view: 'all', scope: 'portfolio:x' },
  ])('rejects an invalid request %j without sending it', query => {
    response({})
    expect(() => createWorkspaceApi('token').plays(query as PlayListQuery)).toThrow('The plays request is invalid.')
    expect(fetch).not.toHaveBeenCalled()
  })
  it('accepts a page with exact counts and rows of the requested view', async () => {
    const plays = [closed(1), closed(2)]
    response({ plays, view: 'review', page: 2, pageSize: 2, totalCount: 4 })
    await expect(createWorkspaceApi('token').plays({ view: 'review', page: 2, pageSize: 2 })).resolves.toMatchObject({ page: 2, totalCount: 4, plays })
    expect(fetch).toHaveBeenCalledWith('/api/plays?view=review&page=2&pageSize=2', expect.anything())
  })
  it.each([
    ['another view', { view: 'reviewed' }],
    ['a reviewed play under Needs review', { plays: [closed(1, { hasReview: true }), closed(2)] }],
    ['an open play under Needs review', { plays: [play(1, { status: 'open' }), closed(2)] }],
    ['a short page', { plays: [closed(1)] }],
    ['a page past the end', { page: 3 }],
    ['another page size', { pageSize: 25 }],
    ['duplicated rows', { plays: [closed(1), closed(1)] }],
    ['a malformed row', { plays: [closed(1), { ...closed(2), status: 'ended' }] }],
  ])('rejects %s', async (_name, fields) => {
    response({ plays: [closed(1), closed(2)], view: 'review', page: 2, pageSize: 2, totalCount: 4, ...fields })
    await expect(createWorkspaceApi('token').plays({ view: 'review', page: 2, pageSize: 2 })).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('accepts unpaged views cut at 200 with an exact total, or the few rows Overview asks for', async () => {
    const plays = Array.from({ length: 200 }, (_, n) => play(n + 1, { status: 'planned' }))
    response({ plays, view: 'progress', page: 1, pageSize: 200, totalCount: 250 })
    await expect(createWorkspaceApi('token').plays({ view: 'progress' })).resolves.toMatchObject({ totalCount: 250 })
    response({ plays: plays.slice(0, 199), view: 'progress', page: 1, pageSize: 200, totalCount: 250 })
    await expect(createWorkspaceApi('token').plays({ view: 'progress' })).rejects.toMatchObject({ kind: 'invalid-response' })
    response({ plays: plays.slice(0, 5), view: 'progress', page: 1, pageSize: 5, totalCount: 250 })
    await expect(createWorkspaceApi('token').plays({ view: 'progress', pageSize: 5 })).resolves.toMatchObject({ totalCount: 250 })
    response({ plays: [play(1)], view: 'drafts', page: 1, pageSize: 200, totalCount: 1 })
    await expect(createWorkspaceApi('token').plays({ view: 'drafts', scope: `account:${other}` })).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('reads consistent counts', async () => {
    response(counts)
    await expect(createWorkspaceApi('token').playCounts({ q: 'btc' })).resolves.toEqual(counts)
    expect(fetch).toHaveBeenCalledWith('/api/plays/counts?q=btc', expect.anything())
    for (const broken of [{ ...counts, all: 11 }, { ...counts, review: 5 }, { ...counts, drafts: -1 }, { ...counts, reviewed: undefined }]) {
      response(broken)
      await expect(createWorkspaceApi('token').playCounts({})).rejects.toMatchObject({ kind: 'invalid-response' })
    }
  })
})

describe('level rebind API (0.5b)', () => {
  const link = '00000000-0000-4000-8000-0000000000d1'
  const request = { expectedVersion: 4, orderId: 's-order', observedPrice: '101', reason: 'Trailed at the venue' }

  it('posts the expected version, observed order and price with the reason, and returns the saved Play', async () => {
    const saved = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, version: 5 } }
    response(saved)
    await expect(createWorkspaceApi('token').rebindLevel(base.id, link, request)).resolves.toEqual(saved)
    expect(fetch).toHaveBeenCalledWith(`/api/plays/${base.id}/execution/links/${link}/rebind`,
      expect.objectContaining({ method: 'POST', body: JSON.stringify(request) }))
  })

  it('refuses another Play in the answer and an invalid link ID before sending', async () => {
    response({ ...savedPlayFixture, summary: { ...savedPlayFixture.summary, id: other } })
    await expect(createWorkspaceApi('token').rebindLevel(base.id, link, request)).rejects.toMatchObject({ kind: 'invalid-response' })
    vi.mocked(fetch).mockClear()
    await expect(createWorkspaceApi('token').rebindLevel(base.id, '../links', request)).rejects.toMatchObject({ kind: 'invalid-response' })
    expect(fetch).not.toHaveBeenCalled()
  })
})
