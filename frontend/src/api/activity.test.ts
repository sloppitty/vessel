import { afterEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from './workspace'
import { activityPath, type ActivityQuery } from './activity'
import { activityFillFixture, activityFillsFixture, activityFixture, activityPage, operationFillsFixture, operationFixture, unknownPeakOperationFixture, unknownPeakSummaryFixture } from '@/test/activity-fixture'
import { accountFixture } from '@/test/workspace-fixture'

function response(value: unknown, status = 200) { vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(value), { status }))) }
afterEach(() => vi.unstubAllGlobals())
const first: ActivityQuery = { page: 1, pageSize: 25 }
const other = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
const at = (n: number) => ({ ...operationFixture, id: `${String(n).padStart(8, '0')}-1234-1234-1234-123456789abc:long`, lastOccurredAtUtc: `2026-10-${String(20 - n).padStart(2, '0')}T10:00:00Z` })

describe('activity API', () => {
  it('preserves exact strings, authenticates and requests the account and page', async () => {
    response(activityFixture)
    const page = await createWorkspaceApi('secret-test-token').activity({ accountId: accountFixture.id, page: 1, pageSize: 25 })
    expect(page).toEqual(activityFixture)
    expect(page.operations[0]!.averageEntryPrice).toBe('100.123456789123456789')
    expect(fetch).toHaveBeenCalledWith(`/api/activity?accountId=${accountFixture.id}&page=1&pageSize=25`, expect.objectContaining({ headers: expect.objectContaining({ Authorization: 'Bearer secret-test-token' }) }))
  })
  it('sends only the filters that narrow the list', () => {
    expect(activityPath(first)).toBe('/api/activity?page=1&pageSize=25')
    expect(activityPath({ scope: 'all', period: 'all', direction: '', status: '', contract: '', ...first })).toBe('/api/activity?page=1&pageSize=25')
    expect(activityPath({ scope: `portfolio:${accountFixture.id}`, account: accountFixture.id, portfolio: 'unassigned', contract: '1000PEPE',
      direction: 'short', status: 'partial', period: 'custom', from: '2026-10-01', to: '2026-10-12', page: 3, pageSize: 100 }))
      .toBe(`/api/activity?scope=portfolio%3A${accountFixture.id}&account=${accountFixture.id}&portfolio=unassigned&contract=1000PEPE&direction=short&status=partial&period=custom&from=2026-10-01&to=2026-10-12&page=3&pageSize=100`)
    // Dates belong to the custom period only.
    expect(activityPath({ period: '30', from: '2026-10-01', ...first })).toBe('/api/activity?period=30&page=1&pageSize=25')
    expect(activityPath({ scope: 'unassigned', page: 2, pageSize: 5 })).toBe('/api/activity?scope=unassigned&page=2&pageSize=5')
  })
  it.each([
    { accountId: '../../private' }, { accountId: '' }, { account: 'x' }, { portfolio: 'nobody' }, { contract: 'X'.repeat(129) },
    { direction: 'up' }, { status: 'ended' }, { period: '365' }, { period: 'custom', from: '2026-13-01' }, { period: 'custom', to: '12/10/2026' },
    { scope: 'portfolio:nope' }, { page: 0 }, { page: 1.5 }, { pageSize: 0 }, { pageSize: 101 },
  ])('rejects invalid requests %j without making them', fields => {
    response(activityFixture)
    expect(() => createWorkspaceApi('token').activity({ ...first, ...fields } as ActivityQuery)).toThrow('The activity request is invalid.')
    expect(fetch).not.toHaveBeenCalled()
  })
  it.each(Object.keys(operationFixture))('rejects a missing operation field %s', async key => {
    const document = structuredClone(activityFixture)
    delete (document.operations[0] as unknown as Record<string, unknown>)[key]
    response(document)
    await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it.each([
    { quantity: 2 }, { direction: 'buy' }, { status: 'planned' }, { averageEntryPrice: '1e3' }, { entryOrderCount: 1.5 },
    { netResultUsd: 0.1 }, { grossClosedPnlUsd: 0.1 }, { grossClosedPnlUsd: '1e3' },
    { pnlBasis: 'guess' }, { playAssociation: 'linked', playId: null }, { otherFees: [{ token: 'ETH', amount: 1 }] },
    { coverage: [null] }, { openedAtUtc: 'yesterday' }, { closedAtUtc: '2026-10-12T11:00:00Z' }, { fillCount: 0 }, { entryOrderCount: 2 },
    { direction: null },
  ])('rejects malformed or contradictory fields %j', async fields => {
    response({ ...activityFixture, operations: [{ ...operationFixture, ...fields }] })
    await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it.each([null, '-0.0000000000000000000000000001', '100000000000000000000000000000.123456789'])(
    'preserves unknown or exact before-fee P&L %s', async grossClosedPnlUsd => {
      response({ ...activityFixture, operations: [{ ...operationFixture, grossClosedPnlUsd }] })
      expect((await createWorkspaceApi('token').activity(first)).operations[0]!.grossClosedPnlUsd).toBe(grossClosedPnlUsd)
    })
  it('accepts incomplete coverage and unknown values without inventing an entry', async () => {
    const incomplete = { ...operationFixture, status: 'incomplete', direction: null, openedAtUtc: null, quantity: null, totalEnteredQuantity: null,
      totalExitedQuantity: null, openQuantity: null, averageEntryPrice: null, averageExitPrice: null, coverage: ['Opening history missing.'] }
    response({ ...activityFixture, operations: [incomplete] })
    await expect(createWorkspaceApi('token').activity(first)).resolves.toMatchObject({ operations: [{ direction: null, quantity: null }] })
  })
  it('accepts snapshot-anchored missing entries with known direction and exits', async () => {
    const incomplete = { ...operationFixture, status: 'incomplete', openedAtUtc: null, quantity: null, totalEnteredQuantity: null,
      totalExitedQuantity: '2', openQuantity: '0', averageEntryPrice: null, averageExitPrice: '120',
      closedAtUtc: operationFixture.lastOccurredAtUtc, coverage: ['Opening entries are missing.'] }
    response({ ...activityFixture, operations: [incomplete] })
    await expect(createWorkspaceApi('token').activity(first)).resolves.toMatchObject({ operations: [{ direction: 'long', averageEntryPrice: null, averageExitPrice: '120' }] })
    for (const invalid of [{ coverage: [] }, { averageEntryPrice: '100' }, { direction: null }, { openQuantity: null }]) {
      response({ ...activityFixture, operations: [{ ...incomplete, ...invalid }] })
      await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
  })
  it('accepts an operation whose peak is unknown because same-timestamp orders disagree, with everything else known', async () => {
    response(activityPage([unknownPeakSummaryFixture]))
    await expect(createWorkspaceApi('token').activity(first)).resolves.toMatchObject({ operations: [{ status: 'closed', direction: 'long',
      quantity: null, totalEnteredQuantity: '3', totalExitedQuantity: '3', openQuantity: '0', averageEntryPrice: '100', netResultUsd: '27' }] })
    // The same unknown peak while the position is still open.
    const open = { ...unknownPeakSummaryFixture, status: 'open' as const, closedAtUtc: null, totalExitedQuantity: '1', openQuantity: '2' }
    response(activityPage([open]))
    await expect(createWorkspaceApi('token').activity({ ...first, status: 'partial' })).resolves.toMatchObject({ operations: [{ quantity: null }] })
    // Only the peak may be unknown, and only with a coverage notice that says so.
    for (const invalid of [{ coverage: [] }, { totalEnteredQuantity: null }, { averageEntryPrice: null }, { openQuantity: null },
      { openedAtUtc: null }, { direction: null }, { closedAtUtc: null }]) {
      response(activityPage([{ ...unknownPeakSummaryFixture, ...invalid }]))
      await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
  })
  it('accepts complete scope-wide coverage however long it is', async () => {
    const coverage = Array.from({ length: 10_001 }, (_, n) => ({ accountId: accountFixture.id, contractId: `C${n}`, notice: 'Snapshot predates retained fills.' }))
    response({ ...activityFixture, coverage })
    await expect(createWorkspaceApi('token').activity(first)).resolves.toMatchObject({ coverage: { length: 10_001 } })
  })
  it('rejects missing page fields, foreign coverage identities and duplicated operations', async () => {
    for (const key of Object.keys(activityFixture)) {
      const document = structuredClone(activityFixture); delete (document as unknown as Record<string, unknown>)[key]; response(document)
      await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
    response({ ...activityFixture, coverage: [{ accountId: 'invalid', contractId: null, notice: 'Missing' }] })
    await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
    response(activityPage([operationFixture, operationFixture]))
    await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('accepts a linked Play title and refuses one on an unlinked row or beyond the title limit', async () => {
    const playId = '33345678-1234-1234-1234-123456789abc'
    const linked = { ...operationFixture, playId, playAssociation: 'linked' as const }
    for (const operation of [{ ...linked, playTitle: 'BTC breakout' }, { ...linked, playTitle: '' }, { ...linked, playTitle: null }, linked]) {
      response(activityPage([operation]))
      await expect(createWorkspaceApi('token').activity(first)).resolves.toMatchObject({ operations: { length: 1 } })
    }
    for (const operation of [{ ...operationFixture, playTitle: 'Not linked' }, { ...linked, playTitle: 'x'.repeat(201) }, { ...linked, playTitle: 7 }]) {
      response(activityPage([operation as typeof operationFixture]))
      await expect(createWorkspaceApi('token').activity(first)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
  })
  it('accepts a later page with exact counts and checks paging arithmetic', async () => {
    const rows = [at(1), at(2)]
    response(activityPage(rows, { page: 3, pageSize: 2, totalCount: 6, scopeCount: 9 }))
    await expect(createWorkspaceApi('token').activity({ page: 3, pageSize: 2 })).resolves.toMatchObject({ page: 3, totalCount: 6, scopeCount: 9 })
    for (const overrides of [
      { page: 4, pageSize: 2, totalCount: 6 }, // past the last page
      { page: 3, pageSize: 2, totalCount: 5 }, // the last page holds one row
      { page: 1, pageSize: 2, totalCount: 6, scopeCount: 5 }, // more matches than in scope
      { page: 1, pageSize: 25, totalCount: 6 }, // another page size than requested
      { version: '' }, { asOfUtc: 'today' }, { facets: { accountIds: [], contracts: ['BTC'] } }, { facets: { accountIds: [accountFixture.id], contracts: [] } },
    ]) {
      response(activityPage(rows, overrides))
      await expect(createWorkspaceApi('token').activity({ page: overrides.page ?? 1, pageSize: 2 })).rejects.toMatchObject({ kind: 'invalid-response' })
    }
    response(activityPage([at(2), at(1)], { pageSize: 2 }))
    await expect(createWorkspaceApi('token').activity({ page: 1, pageSize: 2 })).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('rejects rows outside the requested filters, account or scope', async () => {
    const short = { ...operationFixture, direction: 'short' as const }
    for (const [query, operations] of [
      [{ ...first, direction: 'long' }, [short]], [{ ...first, contract: 'ETH' }, [operationFixture]], [{ ...first, status: 'closed' }, [operationFixture]],
      [{ ...first, account: other }, [operationFixture]], [{ ...first, accountId: other }, [operationFixture]], [{ ...first, scope: `account:${other}` }, [operationFixture]],
    ] as [ActivityQuery, typeof operationFixture[]][]) {
      response(activityPage(operations))
      await expect(createWorkspaceApi('token').activity(query)).rejects.toMatchObject({ kind: 'invalid-response' })
    }
    response(activityPage([], { totalCount: 0, scopeCount: 0, coverage: [{ accountId: other, contractId: null, notice: 'Missing.' }] }))
    await expect(createWorkspaceApi('token').activity({ ...first, accountId: accountFixture.id })).rejects.toMatchObject({ kind: 'invalid-response' })
    response(activityPage([{ ...operationFixture, totalExitedQuantity: '1', openQuantity: '1' }]))
    await expect(createWorkspaceApi('token').activity({ ...first, status: 'partial' })).resolves.toMatchObject({ totalCount: 1 })
    response(activityFixture)
    await expect(createWorkspaceApi('token').activity({ ...first, accountId: accountFixture.id.toUpperCase() })).resolves.toEqual(activityFixture)
  })
})

describe('activity fills API', () => {
  it('reads one operation and validates its rows', async () => {
    response(activityFillsFixture)
    await expect(createWorkspaceApi('token').activityFills(accountFixture.id, operationFixture.id)).resolves.toEqual(activityFillsFixture)
    expect(fetch).toHaveBeenCalledWith(`/api/activity/fills?accountId=${accountFixture.id}&operationId=${encodeURIComponent(operationFixture.id)}`, expect.anything())
  })
  it.each([
    { operationId: `${other}:long` }, { omittedFillCount: -1 }, { fills: [] },
    { fills: [{ ...operationFillsFixture[0]!, role: 'flip' }] },
    { fills: [{ ...operationFillsFixture[0]!, fill: { ...activityFillFixture, accountId: other } }] },
    { fills: [operationFillsFixture[0]!, { ...operationFillsFixture[0]!, fill: { ...activityFillFixture, id: other, contractId: 'ETH' } }] },
    { fills: [{ ...operationFillsFixture[0]!, fill: { ...activityFillFixture, occurredAtUtc: '2026-10-13T00:00:00Z' } }, operationFillsFixture[0]!] },
    { fills: Array.from({ length: 1001 }, () => operationFillsFixture[0]!) },
  ])('rejects foreign, unordered or unbounded rows %#', async fields => {
    response({ ...activityFillsFixture, ...fields })
    await expect(createWorkspaceApi('token').activityFills(accountFixture.id, operationFixture.id)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
  it('accepts an entry and an exit sharing a timestamp, as an unknown-peak operation has', async () => {
    const { id, fills } = unknownPeakOperationFixture
    response({ ...activityFillsFixture, operationId: id, fills, omittedFillCount: 0 })
    await expect(createWorkspaceApi('token').activityFills(accountFixture.id, id)).resolves.toMatchObject({ fills: { length: 4 } })
  })
  it('reports a changed operation as a 404 and rejects invalid identities without a request', async () => {
    response({}, 404)
    await expect(createWorkspaceApi('token').activityFills(accountFixture.id, operationFixture.id)).rejects.toMatchObject({ status: 404, message: expect.stringContaining('changed') })
    response(activityFillsFixture)
    expect(() => createWorkspaceApi('token').activityFills('../x', operationFixture.id)).toThrow('invalid')
    expect(() => createWorkspaceApi('token').activityFills(accountFixture.id, 'BTC')).toThrow('invalid')
    expect(fetch).not.toHaveBeenCalled()
  })
})
