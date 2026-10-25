import { describe, expect, it, vi } from 'vitest'
import { overviewSummaryFixture } from '@/test/overview-summary-fixture'
import { createWorkspaceApi } from './workspace'
import { isOverviewSummary, overviewSummaryPath, type OverviewQuery } from './overview-summary'
import { credentialsFixture } from '@/test/identity-fixture'

const query: OverviewQuery = { groupBy: 'portfolio', period: '30', scope: 'all' }
describe('Overview summary API', () => {
  it('validates exact strings, authenticates the scoped query and passes cancellation', async () => {
    const document = overviewSummaryFixture()
    document.totals.balance.amountUsd = '79228162514264337593543950335.0000000000000000000000000001'
    document.rows[0]!.shareOfBalance = { numerator: document.totals.balance.amountUsd, denominator: document.totals.balance.amountUsd }
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(document)))
    vi.stubGlobal('fetch', fetch)
    const controller = new AbortController()
    expect(await createWorkspaceApi(credentialsFixture).overviewSummary(query, controller.signal)).toEqual(document)
    expect(fetch.mock.calls[0]![0]).toBe('/api/overview/summary?groupBy=portfolio&period=30&scope=all')
    expect(fetch.mock.calls[0]![1].headers['X-Vessel-User']).toBe(credentialsFixture.userId)
    controller.abort(); expect(fetch.mock.calls[0]![1].signal.aborted).toBe(true)
  })
  it.each(['7', '30', '90', 'all'] as const)('accepts the %s window', period => {
    const q = { ...query, period }; expect(isOverviewSummary(overviewSummaryFixture(q), q)).toBe(true)
  })
  it.each([
    { groupBy: 'venue' }, { period: '1' }, { scope: '' }, { scope: 'account:bad' }, { scope: 'portfolio:00000000-0000-0000-0000-000000000000' },
    { scope: 'all&scope=account:bad' }, { scope: null },
  ])('rejects an invalid query %j before fetching', fields => {
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch)
    expect(() => createWorkspaceApi(credentialsFixture).overviewSummary({ ...query, ...fields } as OverviewQuery)).toThrow('request is invalid')
    expect(fetch).not.toHaveBeenCalled()
  })
  it.each(['account', 'portfolio'] as const)('rejects a mismatched %s scope row', kind => {
    const q = { groupBy: kind, period: '30', scope: `${kind}:bbbbbbbb-cccc-dddd-eeee-ffffffffffff` } as OverviewQuery
    expect(isOverviewSummary(overviewSummaryFixture(q), q)).toBe(false)
  })
  it('encodes resource scope without inserting another query parameter', () => {
    expect(overviewSummaryPath({ ...query, scope: 'account:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' })).toContain('scope=account%3Aaaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee')
  })
  it.each([
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.balance.amountUsd = 1 as unknown as string },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.balance.amountUsd = '1e4' },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.balance.amountUsd = null },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.balance.coverage.totalAccountCount = 2 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.balance.coverage.contributingAccountCount = 2 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.rows[0]!.icon = '<svg>' },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.rows[0]!.id = 'portfolio:bad' },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.rows[0]!.shareOfBalance!.denominator = '1' },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.rows[0]!.shareOfBalance = null },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.rows.push(d.rows[0]!); d.totalGroupCount = 2 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totalGroupCount = 2 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.fromUtc = d.asOfUtc },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.scope = 'unassigned' },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.available.coverage.reasons = [] },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.balance.coverage.contributingOperationCount = 1 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.available.coverage.contributingAccountCount = -1 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.totals.available.coverage.totalAccountCount = 1.5 },
    (d: ReturnType<typeof overviewSummaryFixture>) => { d.rows = Array.from({ length: 201 }, () => d.rows[0]!); d.totalGroupCount = 201 },
  ])('rejects malformed response %#', async mutate => {
    const document = overviewSummaryFixture(); mutate(document)
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(document))))
    await expect(createWorkspaceApi(credentialsFixture).overviewSummary(query)).rejects.toMatchObject({ kind: 'invalid-response' })
  })
})
