import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { isJournalSearchPage, isPlayOutcome, isPlaySummary, journalSearchPath, type JournalSearchPage, type PlayOutcome, type PlaySummary } from './plays'
import { savedPlayFixture } from '@/test/workspace-fixture'

// Phase 2, item 13: the journal search request and response, and the list rows' outcome (F-07).
function response(value: unknown) { vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(value), { status: 200 }))) }
afterEach(() => vi.unstubAllGlobals())
const play = (n: number, fields: Partial<PlaySummary> = {}): PlaySummary =>
  ({ ...savedPlayFixture.summary, id: `${String(n).padStart(8, '0')}-3333-4444-5555-666666666666`, ...fields })
const scored: PlayOutcome = { state: 'scored', exitAtUtc: '2026-10-08T14:30:00Z', resultUsd: '-2.5', estimatedR: '-0.5', reason: null }
const hit = (n: number) => ({ play: play(n), field: 'thesis' as string, excerpt: '…a weekly range…', matchStart: 3, matchLength: 6 })
const page = (fields: Omit<Partial<JournalSearchPage>, 'items'> & { items?: unknown[] } = {}) =>
  ({ query: 'weekly', items: [hit(1)], total: 1, offset: 0, nextOffset: null, notIndexed: 0, ...fields })

describe('List outcomes', () => {
  it('accepts figures only for a scored Closed Play and states only for their statuses', () => {
    expect(isPlayOutcome(scored, 'closed')).toBe(true)
    expect(isPlayOutcome({ ...scored, estimatedR: null, reason: 'No stop.' }, 'closed')).toBe(true)
    expect(isPlayOutcome({ state: 'executions', exitAtUtc: '2026-10-08T14:30:00Z', resultUsd: null, estimatedR: null, reason: 'Incomplete.' }, 'closed')).toBe(true)
    expect(isPlayOutcome({ state: 'not-indexed', exitAtUtc: null, resultUsd: null, estimatedR: null, reason: 'Not indexed yet.' }, 'open')).toBe(true)
    // A result outside scored, a scored Play without one, R without a result, an exit on a Cancelled Play, a state for another status.
    expect(isPlayOutcome({ ...scored, state: 'fees' }, 'closed')).toBe(false)
    expect(isPlayOutcome({ ...scored, resultUsd: null, estimatedR: null }, 'closed')).toBe(false)
    expect(isPlayOutcome({ state: 'executions', exitAtUtc: null, resultUsd: null, estimatedR: '1', reason: null }, 'closed')).toBe(false)
    expect(isPlayOutcome({ state: 'cancelled', exitAtUtc: '2026-10-08T14:30:00Z', resultUsd: null, estimatedR: null, reason: null }, 'cancelled')).toBe(false)
    expect(isPlayOutcome(scored, 'open')).toBe(false)
    expect(isPlayOutcome({ ...scored, resultUsd: '1e3' }, 'closed')).toBe(false)
    expect(isPlaySummary(play(1, { status: 'closed', endedAtUtc: '2026-10-09T00:00:00Z', outcome: scored }))).toBe(true)
    expect(isPlaySummary(play(1, { outcome: scored }))).toBe(false)
    expect(isPlaySummary(play(1))).toBe(true)
  })
})

describe('Journal search API', () => {
  it('builds a bounded query and refuses one the server would', () => {
    expect(journalSearchPath({ q: '  50%_off ' })).toBe('/api/plays/search?q=50%25_off')
    expect(journalSearchPath({ q: 'range', offset: 50 })).toBe('/api/plays/search?q=range&offset=50')
    for (const query of [{ q: 'x' }, { q: ' ' }, { q: 'x'.repeat(101) }, { q: 'range', offset: -1 }, { q: 'range', offset: 1.5 }])
      expect(() => journalSearchPath(query)).toThrow()
  })

  it('accepts only pages whose counts, offsets and excerpts agree', () => {
    expect(isJournalSearchPage(page(), { q: 'weekly' })).toBe(true)
    expect(isJournalSearchPage(page({ items: Array.from({ length: 50 }, (_, n) => hit(n)), total: 51, nextOffset: 50 }), { q: 'weekly' })).toBe(true)
    expect(isJournalSearchPage(page({ items: [hit(60)], total: 51, offset: 50 }), { q: 'weekly', offset: 50 })).toBe(true)
    expect(isJournalSearchPage(page({ total: 2 }), { q: 'weekly' })).toBe(false)
    expect(isJournalSearchPage(page({ nextOffset: 1 }), { q: 'weekly' })).toBe(false)
    expect(isJournalSearchPage(page({ offset: 50 }), { q: 'weekly' })).toBe(false)
    expect(isJournalSearchPage(page({ items: [hit(1), hit(1)], total: 2 }), { q: 'weekly' })).toBe(false)
    expect(isJournalSearchPage(page({ items: [{ ...hit(1), matchStart: 14 }] }), { q: 'weekly' })).toBe(false)
    expect(isJournalSearchPage(page({ items: [{ ...hit(1), field: 'drawings' }] }), { q: 'weekly' })).toBe(false)
    expect(isJournalSearchPage(page({ notIndexed: -1 }), { q: 'weekly' })).toBe(false)
  })

  it('searches in the client’s mode and refuses another mode’s Play', async () => {
    response(page({ items: [{ ...hit(1), play: play(1, { tradingMode: 'paper' }) }] }))
    const result = await createWorkspaceApi('test-only', undefined, 'paper').searchJournal({ q: 'weekly' })
    expect(fetch).toHaveBeenCalledWith('/api/plays/search?q=weekly&tradingMode=paper', expect.anything())
    expect(result.items[0]!.field).toBe('thesis')
    response(page({ items: [{ ...hit(1), play: play(1, { tradingMode: 'real' }) }] }))
    await expect(createWorkspaceApi('test-only', undefined, 'paper').searchJournal({ q: 'weekly' })).rejects.toThrow()
    await expect(createWorkspaceApi('test-only').searchJournal({ q: 'w' })).rejects.toThrow()
  })
})
