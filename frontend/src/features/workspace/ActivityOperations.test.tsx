import { act, fireEvent, render, screen, within, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { ActivityFills, ActivityOperation, ActivityPage, ActivityQuery } from '@/api/activity'
import { ApiError } from '@/api/errors'
import type { WorkspaceApi } from '@/api/workspace'
import { activityFillFixture, activityFillsFixture, activityFixture, activityPage, operationFixture } from '@/test/activity-fixture'
import { accountFixture, portfolioFixture } from '@/test/workspace-fixture'
import { accountPicker, chooseInPicker } from '@/test/account-picker'
import { ActivityOperations } from './ActivityOperations'
import { AccountDetail } from './AccountDetail'
import { render as renderWithVenues } from '@/test/render'
import { OperationsTable } from './OperationsTable'
import { ActivityTable } from './ActivityTable'
import { sideDirection, resultSign, operationPriceReturn, operationDates, operationStatus } from './activity-format'
import { emptyFilters, filterQuery, initialActivityState, type ActivityListState } from './activity-filters'

const client = (activity = vi.fn().mockResolvedValue(activityFixture), activityFills = vi.fn().mockResolvedValue(activityFillsFixture)) =>
  ({ activity, activityFills }) as unknown as WorkspaceApi
const closed: ActivityOperation = { ...operationFixture, id: '22345678-1234-1234-1234-123456789abc:short', direction: 'short', status: 'closed',
  closedAtUtc: '2026-10-12T12:00:00Z', lastOccurredAtUtc: '2026-10-12T12:00:00Z', openQuantity: '0', totalExitedQuantity: '2',
  averageExitPrice: '99', netResultUsd: '2', exitOrderCount: 1 }
const otherAccount = { ...accountFixture, id: 'bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee', name: 'Other account', portfolioId: null }
/** Distinct operations, newest first. */
const numbered = (count: number, from = 0) => Array.from({ length: count }, (_, n) => ({ ...operationFixture,
  id: `${String(from + n).padStart(8, '0')}-1234-1234-1234-123456789abc:long`, contractId: `CONTRACT-${from + n}`,
  lastOccurredAtUtc: new Date(Date.parse('2026-10-12T10:00:00Z') - (from + n) * 60_000).toISOString() }))
const lastQuery = (activity: ReturnType<typeof vi.fn>) => activity.mock.calls.at(-1)![0] as ActivityQuery
/** A list whose page and filters live outside it, as the shell keeps them for Activity. */
function Shelled({ api, initial = initialActivityState, onState, reloadGeneration = 0 }: { api: WorkspaceApi; initial?: ActivityListState; onState?: (state: ActivityListState) => void; reloadGeneration?: number }) {
  const [state, setState] = useState(initial)
  return <ActivityOperations api={api} accounts={[accountFixture, otherAccount]} portfolios={[portfolioFixture]} filters state={state} reloadGeneration={reloadGeneration}
    onStateChange={next => { setState(next); onState?.(next) }} />
}
const deferred = <T,>() => {
  let resolve!: (value: T) => void, reject!: (cause: unknown) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

describe('operation table', () => {
  it('toggles from any summary cell, retains keyboard control and leaves expanded actions independent', async () => {
    const preview = vi.fn()
    render(<OperationsTable api={client()} operations={[closed]} accounts={[accountFixture]} onPreview={preview} />)
    await userEvent.click(screen.getByText(accountFixture.name))
    const button = screen.getByRole('button', { name: 'Collapse BTC operation' })
    expect(button).toHaveAttribute('aria-expanded', 'true')
    await userEvent.click(screen.getByRole('button', { name: /Inspect BTC retrospective/ }))
    expect(preview).toHaveBeenCalledExactlyOnceWith(closed)
    expect(button).toHaveAttribute('aria-expanded', 'true')
    await userEvent.click(screen.getByText('$2.00'))
    expect(button).toHaveAttribute('aria-expanded', 'false')
    button.focus()
    await userEvent.keyboard('{Enter}')
    expect(button).toHaveAttribute('aria-expanded', 'true')
    await userEvent.keyboard(' ')
    expect(button).toHaveAttribute('aria-expanded', 'false')
  })
  it('keeps only the size and nonzero remaining quantity in the summary', () => {
    const api = client()
    const view = render(<OperationsTable api={api} operations={[closed]} accounts={[accountFixture]} />)
    expect(screen.queryByText(/Remaining|Entered|Exited|Peak quantity|Reported executions|Stablecoin units/)).not.toBeInTheDocument()
    view.rerender(<OperationsTable api={api} operations={[{ ...operationFixture, openQuantity: '0.0000000000000000000000000001',
      totalExitedQuantity: '1.9999999999999999999999999999' }]} accounts={[accountFixture]} />)
    expect(screen.getByText(/Remaining/)).toBeInTheDocument()
    expect(screen.getByText('Partially closed')).toHaveClass('partial')
  })
  it('uses before-fee P&L and the authoritative net result without another fee deduction', () => {
    render(<OperationsTable api={client()} operations={[{ ...closed, pnlBasis: 'net-of-fee', grossClosedPnlUsd: '-109.31',
      reportedClosedPnlUsd: '-110', netResultUsd: '-110', stablecoinFeesNominalUsd: '0.69',
      direction: 'long', averageEntryPrice: '1796.44', averageExitPrice: '1740.45' }]} accounts={[accountFixture]} />)
    expect(screen.getByText('-$109.31')).toBeInTheDocument()
    expect(screen.getByText('-$110.00')).toBeInTheDocument()
    expect(screen.getByText('$0.69')).toBeInTheDocument()
    expect(screen.getByText('-3.12%')).toHaveClass('negative')
    expect(screen.getByText('$1,740.45').closest('td')).toHaveClass('negative')
    expect(screen.queryByText('net-of-fee')).not.toBeInTheDocument()
  })
  it('renders a round trip as one row, colours Long and loads its constituent fills on expand', async () => {
    const activityFills = vi.fn().mockResolvedValue(activityFillsFixture)
    render(<OperationsTable api={client(undefined, activityFills)} operations={[operationFixture]} accounts={[accountFixture]} />)
    expect(screen.getByText('Long')).toHaveClass('long')
    expect(screen.getByText('-$0.01')).toHaveClass('negative')
    expect(screen.getByText('Unassigned')).toBeInTheDocument()
    expect(screen.getByTitle('100.123456789123456789')).toHaveTextContent('$100.12')
    expect(activityFills).not.toHaveBeenCalled()
    const button = screen.getByRole('button', { name: 'Expand BTC operation' })
    expect(button).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(button)
    expect(screen.getByRole('button', { name: 'Collapse BTC operation' })).toHaveAttribute('aria-expanded', 'true')
    expect(await screen.findByRole('columnheader', { name: 'Side' })).toBeInTheDocument()
    expect(activityFills).toHaveBeenCalledExactlyOnceWith(accountFixture.id, operationFixture.id, expect.any(AbortSignal))
    expect(screen.queryByRole('columnheader', { name: /Orders/ })).not.toBeInTheDocument()
    expect(screen.queryByText(/constituent fills/)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Collapse BTC operation' }))
    expect(screen.queryByRole('columnheader', { name: 'Side' })).not.toBeInTheDocument()
  })
  it('shows Short, positive results, elapsed duration, coverage and a display limit when expanded', async () => {
    const fills: ActivityFills = { ...activityFillsFixture, operationId: closed.id, omittedFillCount: 10 }
    render(<OperationsTable api={client(undefined, vi.fn().mockResolvedValue(fills))} operations={[{ ...closed, fillCount: 11, coverage: ['Snapshot disagrees.'] }]} accounts={[accountFixture]} />)
    expect(screen.getByText('Short')).toHaveClass('short')
    expect(screen.getByText('$2.00')).toHaveClass('positive')
    expect(screen.getByText('2h')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    expect(screen.getByText('Snapshot disagrees.')).toBeInTheDocument()
    expect(await screen.findByText('Showing the first 1 executions; operation totals include all 11.')).toBeInTheDocument()
  })
  it('shows direction and known exits when snapshot anchoring finds missing entries', async () => {
    const missing: ActivityOperation = { ...closed, status: 'incomplete', openedAtUtc: null, quantity: null,
      totalEnteredQuantity: null, averageEntryPrice: null, coverage: ['Opening entries are missing.'] }
    render(<OperationsTable api={client()} operations={[missing]} accounts={[accountFixture]} />)
    expect(screen.getByText('Short')).toHaveClass('short')
    expect(screen.getByText(/Opened unknown/)).toBeInTheDocument()
    expect(screen.queryByText(/Exited 2/)).not.toBeInTheDocument()
    expect(screen.getByTitle('99')).toHaveTextContent('$99.00')
    expect(screen.getByText('Incomplete')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    expect(screen.getByText(/Exited 2/)).toBeInTheDocument()
    expect(screen.getByText('Opening entries are missing.')).toBeInTheDocument()
  })
  it('hides disabled account operations even if an earlier response includes them', () => {
    render(<OperationsTable api={client()} operations={[operationFixture]} accounts={[{ ...accountFixture, isEnabled: false }]} />)
    expect(screen.getByText('No operations in this view')).toBeInTheDocument()
    expect(screen.queryByText('BTC')).not.toBeInTheDocument()
  })
  it('also hides stale fill-level facts for disabled accounts', () => {
    render(<ActivityTable fills={[activityFillFixture]} accounts={[{ ...accountFixture, isEnabled: false }]} />)
    expect(screen.queryByText('BTC')).not.toBeInTheDocument()
    expect(screen.getByText('No imported executions yet')).toBeInTheDocument()
  })
  it('shows original flip facts separately from their operation allocation', async () => {
    const fill = { ...activityFillFixture, positionEffect: 'flip' as const, side: 'sell' as const, quantity: '5', fee: '0.5', closedPnlUsd: '10' }
    const fills: ActivityFills = { ...activityFillsFixture, fills: [{ fill, role: 'exit', quantity: '2', fee: '0.5', closedPnlUsd: '10' }] }
    render(<OperationsTable api={client(undefined, vi.fn().mockResolvedValue(fills))} operations={[operationFixture]} accounts={[accountFixture]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    expect(await screen.findByText('Original flip facts')).toBeInTheDocument()
    expect(screen.getByText(/Flip rows show this operation's allocated quantity/)).toBeInTheDocument()
    expect(screen.getAllByText('Short')).toHaveLength(2)
    for (const badge of screen.getAllByText('Short')) expect(badge).toHaveClass('short')
  })
  it('labels each execution by its own side, without repeating instrument, account or venue wording', () => {
    render(<ActivityTable fills={[{ ...activityFillFixture, id: 'open', side: 'buy', positionEffect: 'open' },
      { ...activityFillFixture, id: 'close', side: 'sell', positionEffect: 'close', direction: 'Close Long' }]} accounts={[accountFixture]} />)
    expect(screen.getByText('Long')).toHaveClass('long')
    expect(screen.getByText('Short')).toHaveClass('short')
    expect(screen.queryByText('Close Long')).not.toBeInTheDocument()
    expect(screen.queryByText('BTC')).not.toBeInTheDocument()
    expect(screen.queryByText(accountFixture.name)).not.toBeInTheDocument()
    expect(sideDirection({ ...activityFillFixture, side: 'buy', positionEffect: 'close' })).toBe('long')
    expect(sideDirection({ ...activityFillFixture, side: 'sell', positionEffect: 'unknown' })).toBe('short')
  })
  it('never claims there are no executions when the response omits all constituent rows', async () => {
    render(<OperationsTable api={client(undefined, vi.fn().mockResolvedValue({ ...activityFillsFixture, fills: [], omittedFillCount: 1 }))} operations={[operationFixture]} accounts={[accountFixture]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    expect(await screen.findByText('Execution rows were omitted by the response limit.')).toBeInTheDocument()
    expect(screen.queryByText('No imported executions yet')).not.toBeInTheDocument()
  })
  it('shows a delayed skeleton while executions load and offers Retry after a failure', async () => {
    const first = deferred<ActivityFills>()
    const activityFills = vi.fn().mockReturnValueOnce(first.promise).mockResolvedValue(activityFillsFixture)
    render(<OperationsTable api={client(undefined, activityFills)} operations={[operationFixture]} accounts={[accountFixture]} />)
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    expect(screen.queryByText('Loading executions…')).not.toBeInTheDocument()
    expect(await screen.findByText('Loading executions…')).toBeInTheDocument()
    await act(async () => first.reject(new ApiError('unavailable', 'The API is unavailable.')))
    expect(await screen.findByRole('alert')).toHaveTextContent('The API is unavailable.')
    await userEvent.click(screen.getByRole('button', { name: 'Retry executions' }))
    expect(await screen.findByRole('columnheader', { name: 'Side' })).toBeInTheDocument()
    expect(activityFills).toHaveBeenCalledTimes(2)
  })
  it('says an operation changed when its executions are gone and reloads Activity', async () => {
    const reload = vi.fn()
    const gone = new ApiError('http', 'This operation changed. Reload Activity to see its current executions.', 404)
    render(<OperationsTable api={client(undefined, vi.fn().mockRejectedValue(gone))} operations={[operationFixture]} accounts={[accountFixture]} onReload={reload} />)
    await userEvent.click(screen.getByRole('button', { name: 'Expand BTC operation' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('This operation changed since the page loaded.')
    expect(screen.queryByRole('button', { name: 'Retry executions' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reload activity' }))
    expect(reload).toHaveBeenCalledOnce()
  })
  it('classifies tiny and huge result signs without binary floating point', () => {
    expect(resultSign('-0.0000000000000000000000000001')).toBe('negative')
    expect(resultSign('999999999999999999999999999999.1')).toBe('positive')
    expect(resultSign('-0.0000')).toBe('neutral')
    expect(resultSign(null)).toBe('neutral')
  })
})

describe('operation display values', () => {
  it.each([
    ['long', '100', '110', '+10.00%'], ['short', '100', '110', '-10.00%'],
    ['short', '100', '90', '+10.00%'], ['long', '100', '90', '-10.00%'],
    ['long', '100', '100', '0.00%'], ['long', '100', '99.999999', '0.00%'],
    ['long', '0.000000000000000000000000001', '0.0000000000000000000000000011', '+10.00%'],
    ['long', '100000000000000000000000000000', '110000000000000000000000000000', '+10.00%'],
    ['long', '100', '100.005', '+0.01%'],
  ] as const)('formats %s %s → %s without floating point loss', (direction, averageEntryPrice, averageExitPrice, expected) => {
    expect(operationPriceReturn({ ...closed, direction, averageEntryPrice, averageExitPrice })).toBe(expected)
  })
  it('does not invent a return for missing, incomplete or zero-price entries', () => {
    for (const override of [{ averageEntryPrice: null }, { averageExitPrice: null }, { averageEntryPrice: '0' },
      { direction: null }, { status: 'incomplete' as const }]) {
      expect(operationPriceReturn({ ...closed, ...override })).toBeNull()
    }
  })
  it('distinguishes incomplete from partial', () => {
    const partial = { ...operationFixture, totalExitedQuantity: '1', openQuantity: '1' }
    expect(operationStatus(partial)).toEqual({ label: 'Partially closed', tone: 'partial' })
    expect(operationStatus({ ...partial, status: 'incomplete' }).label).toBe('Incomplete')
  })
  it('shortens same-day UTC times while retaining dates and years across boundaries', () => {
    expect(operationDates(closed, 'utc')).toBe('12 Oct, 10:00 → 12:00 UTC')
    expect(operationDates({ ...closed, closedAtUtc: '2026-10-13T12:00:00Z' }, 'utc')).toBe('12 Oct, 10:00 → 13 Oct, 12:00 UTC')
    expect(operationDates({ ...closed, closedAtUtc: '2027-01-01T12:00:00Z' }, 'utc')).toBe('12 Oct 2026, 10:00 → 1 Jan 2027, 12:00 UTC')
  })
  it('turns filters into request parameters, with dates only for a custom period', () => {
    expect(filterQuery(emptyFilters)).toEqual({})
    expect(filterQuery({ ...emptyFilters, account: otherAccount.id, portfolio: 'unassigned', contract: 'ETH', direction: 'short', status: 'partial', period: 'custom', from: '2026-10-01', to: '2026-10-02' }))
      .toEqual({ account: otherAccount.id, portfolio: 'unassigned', contract: 'ETH', direction: 'short', status: 'partial', period: 'custom', from: '2026-10-01', to: '2026-10-02' })
    expect(filterQuery({ ...emptyFilters, period: '7', from: '2026-10-01' })).toEqual({ period: '7' })
    expect(filterQuery({ ...emptyFilters, direction: 'sideways', status: 'unknown', period: 'forever' })).toEqual({})
  })
})

describe('activity loading, filters and pages', () => {
  it.each(['portfolio', 'disabled account', 'deleted account', 'instrument'])('validates a disappeared %s selection against the displayed options', async kind => {
    const eth = { ...closed, id: '32345678-1234-1234-1234-123456789abc:short', contractId: 'ETH', accountId: otherAccount.id }
    let facets = { accountIds: [accountFixture.id, otherAccount.id], contracts: ['BTC', 'ETH'] }
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => Promise.resolve(activityPage(
      [operationFixture, eth].filter(op => (!query.contract || op.contractId === query.contract) && (!query.account || op.accountId === query.account)), { facets, scopeCount: 2 })))
    const api = client(activity)
    const view = render(<ActivityOperations api={api} accounts={[accountFixture, otherAccount]} portfolios={[portfolioFixture]} filters />)
    await screen.findByRole('button', { name: 'Expand ETH operation' })
    const label = kind === 'portfolio' ? 'Portfolio' : kind === 'instrument' ? 'Instrument' : 'Account'
    if (label === 'Account') await chooseInPicker('Account', otherAccount.name)
    else await userEvent.selectOptions(screen.getByRole('combobox', { name: label }), kind === 'portfolio' ? portfolioFixture.id : 'ETH')
    await waitFor(() => expect(lastQuery(activity)).toMatchObject(kind === 'portfolio' ? { portfolio: portfolioFixture.id } : kind === 'instrument' ? { contract: 'ETH' } : { account: otherAccount.id }))
    facets = { accountIds: [accountFixture.id], contracts: ['BTC'] }
    view.rerender(<ActivityOperations api={api}
      accounts={kind === 'disabled account' ? [accountFixture, { ...otherAccount, isEnabled: false }] : kind === 'deleted account' ? [accountFixture] :
        kind === 'portfolio' ? [{ ...accountFixture, portfolioId: null }, otherAccount] : [accountFixture, otherAccount]}
      portfolios={kind === 'portfolio' ? [] : [portfolioFixture]} filters reloadGeneration={1} />)
    if (label === 'Account') await waitFor(() => expect(accountPicker('Account')).toHaveAttribute('data-value', ''))
    else await waitFor(() => expect(screen.getByRole('combobox', { name: label })).toHaveValue(''))
    await waitFor(() => expect(lastQuery(activity)).toEqual({ page: 1, pageSize: 25 }))
    expect(await within(await screen.findByRole('table')).findByText('BTC')).toBeInTheDocument()
  })
  it('sends every filter to the server, returns to page 1 and resets', async () => {
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => Promise.resolve(activityPage(numbered(25), { totalCount: 60, scopeCount: 80, page: query.page, facets: { accountIds: [accountFixture.id], contracts: ['ETH', ...numbered(25).map(op => op.contractId)] } })))
    const states: ActivityListState[] = []
    render(<Shelled api={client(activity)} initial={{ ...initialActivityState, page: 2 }} onState={state => states.push(state)} />)
    await screen.findByRole('button', { name: 'Expand CONTRACT-0 operation' })
    expect(lastQuery(activity)).toEqual({ page: 2, pageSize: 25 })
    // The pager counts unfiltered rows; the match note appears only while a filter narrows the scope.
    expect(screen.queryByText(/operations match the filters/)).not.toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Direction' }), 'long')
    expect(await screen.findByText('60 of 80 operations match the filters.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
    // Dates kept behind "All retained activity" are not sent, so they do not count as a filter.
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Period' }), 'custom')
    fireEvent.change(screen.getByLabelText('From date (UTC)'), { target: { value: '2026-10-01' } })
    expect(await screen.findByText('60 of 80 operations match the filters.')).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Period' }), 'all')
    await waitFor(() => expect(screen.queryByText(/operations match the filters/)).not.toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
    const cases: [string, string, Partial<ActivityQuery>][] = [['Account', otherAccount.id, { account: otherAccount.id }], ['Portfolio', 'unassigned', { portfolio: 'unassigned' }],
      ['Instrument', 'ETH', { contract: 'ETH' }], ['Direction', 'short', { direction: 'short' }], ['Status', 'partial', { status: 'partial' }], ['Period', '7', { period: '7' }]]
    for (const [label, value, sent] of cases) {
      if (label === 'Account') await chooseInPicker('Account', otherAccount.name)
      else await userEvent.selectOptions(screen.getByRole('combobox', { name: label }), value)
      await waitFor(() => expect(lastQuery(activity)).toEqual({ ...sent, page: 1, pageSize: 25 }))
      await userEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
      await waitFor(() => expect(lastQuery(activity)).toEqual({ page: 1, pageSize: 25 }))
    }
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Period' }), 'custom')
    await userEvent.type(screen.getByLabelText('From date (UTC)'), '2026-10-01')
    await waitFor(() => expect(lastQuery(activity)).toEqual({ period: 'custom', from: '2026-10-01', page: 1, pageSize: 25 }))
    expect(screen.getByLabelText('Through date (UTC)')).toHaveAttribute('min', '2026-10-01')
    expect(states.every(state => state.page === 1)).toBe(true)
  })
  it('keeps the previous rows dimmed and the pager disabled while another page loads, then scrolls to the table', async () => {
    const pages = new Map<number, ReturnType<typeof deferred<ActivityPage>>>()
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => {
      if (query.page === 1) return Promise.resolve(activityPage(numbered(25), { totalCount: 60, version: 'v1' }))
      const next = deferred<ActivityPage>(); pages.set(query.page, next); return next.promise
    })
    const scroll = vi.fn()
    Element.prototype.scrollIntoView = scroll
    try {
      render(<Shelled api={client(activity)} />)
      const nav = await screen.findByRole('navigation', { name: 'Activity pages' })
      expect(within(nav).getByText('1–25 of 60')).toBeInTheDocument()
      expect(within(nav).getByRole('button', { name: '1' })).toHaveAttribute('aria-current', 'page')
      await userEvent.click(within(nav).getByRole('button', { name: '2' }))
      expect(scroll).toHaveBeenCalled()
      expect(within(screen.getByRole('table')).getByText('CONTRACT-0')).toBeInTheDocument()
      expect(within(screen.getByRole('table')).getByText('CONTRACT-0').closest('[aria-busy="true"]')).toHaveClass('list-refetching')
      expect(within(nav).getByRole('button', { name: 'Next' })).toBeDisabled()
      expect(screen.queryByText('Loading operations…')).not.toBeInTheDocument()
      await act(async () => pages.get(2)!.resolve(activityPage(numbered(25, 25), { page: 2, totalCount: 60, version: 'v1' })))
      expect(await within(screen.getByRole('table')).findByText('CONTRACT-25')).toBeInTheDocument()
      expect(within(screen.getByRole('navigation', { name: 'Activity pages' })).getByText('26–50 of 60')).toBeInTheDocument()
      expect(screen.queryByText('Activity changed')).not.toBeInTheDocument()
    } finally { delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView }
  })
  it('changes the page size from page 1 and keeps it with the filters', async () => {
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => Promise.resolve(activityPage(numbered(Math.min(query.pageSize, 60 - (query.page - 1) * query.pageSize)), { page: query.page, pageSize: query.pageSize, totalCount: 60 })))
    const states: ActivityListState[] = []
    render(<Shelled api={client(activity)} initial={{ ...initialActivityState, page: 3 }} onState={state => states.push(state)} />)
    await screen.findByText('51–60 of 60')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Rows per page' }), '50')
    await waitFor(() => expect(lastQuery(activity)).toEqual({ page: 1, pageSize: 50 }))
    expect(await screen.findByText('1–50 of 60')).toBeInTheDocument()
    expect(states.at(-1)).toEqual({ ...initialActivityState, pageSize: 50 })
  })
  it('says Activity changed when a later page reports another version, and Reload returns to page 1', async () => {
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => Promise.resolve(
      activityPage(numbered(25, (query.page - 1) * 25), { page: query.page, totalCount: 60, version: query.page === 1 ? 'v1' : 'v2' })))
    render(<Shelled api={client(activity)} />)
    const nav = await screen.findByRole('navigation', { name: 'Activity pages' })
    await userEvent.click(within(nav).getByRole('button', { name: 'Next' }))
    expect(await screen.findByText('Activity changed')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reload activity' }))
    await waitFor(() => expect(lastQuery(activity)).toEqual({ page: 1, pageSize: 25 }))
    await waitFor(() => expect(screen.queryByText('Activity changed')).not.toBeInTheDocument())
    expect(await within(screen.getByRole('table')).findByText('CONTRACT-0')).toBeInTheDocument()
  })
  it('re-reads expanded executions on every accepted reload, even when the row facts are unchanged', async () => {
    const mixed = { ...operationFixture, playAssociation: 'mixed' as const }
    const activity = vi.fn().mockResolvedValue(activityPage([mixed]))
    const linked = 'dddddddd-1234-1234-1234-123456789abc'
    const activityFills = vi.fn().mockResolvedValueOnce(activityFillsFixture)
      .mockResolvedValue({ ...activityFillsFixture, fills: [{ ...activityFillsFixture.fills[0]!, fill: { ...activityFillFixture, playId: linked } }] })
    const api = client(activity, activityFills)
    const view = render(<ActivityOperations api={api} accounts={[accountFixture]} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Expand BTC operation' }))
    await waitFor(() => expect(activityFills).toHaveBeenCalledTimes(1))
    // A shell reload: the operation stays mixed with the same count, time and version, but an execution moved Play.
    view.rerender(<ActivityOperations api={api} accounts={[accountFixture]} reloadGeneration={1} />)
    await waitFor(() => expect(activity).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(activityFills).toHaveBeenCalledTimes(2))
    // The Activity reload button and Retry after a failure re-read them as well.
    activity.mockRejectedValueOnce(new Error('outage'))
    view.rerender(<ActivityOperations api={api} accounts={[accountFixture]} reloadGeneration={2} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Retry operations' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Expand BTC operation' }))
    await waitFor(() => expect(activityFills).toHaveBeenCalledTimes(3))
    expect(activityFills).toHaveBeenCalledTimes(3)
  })
  it('keeps the page the server clamped to, without reading it again, so later growth does not jump back', async () => {
    let total = 30
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => {
      const last = Math.max(1, Math.ceil(total / query.pageSize)), page = Math.min(query.page, last)
      return Promise.resolve(activityPage(numbered(Math.min(query.pageSize, total - (page - 1) * query.pageSize), (page - 1) * query.pageSize),
        { page, pageSize: query.pageSize, totalCount: total }))
    })
    const states: ActivityListState[] = []
    const api = client(activity)
    const view = render(<Shelled api={api} initial={{ ...initialActivityState, page: 5 }} onState={state => states.push(state)} />)
    expect(await screen.findByText('26–30 of 30')).toBeInTheDocument()
    await waitFor(() => expect(states.at(-1)?.page).toBe(2))
    expect(activity.mock.calls.map(([query]) => (query as ActivityQuery).page)).toEqual([5])
    // History grows past the old requested page; a reload stays on the page that was shown.
    total = 200
    view.rerender(<Shelled api={api} initial={{ ...initialActivityState, page: 5 }} onState={state => states.push(state)} reloadGeneration={1} />)
    await waitFor(() => expect(lastQuery(activity)).toEqual({ page: 2, pageSize: 25 }))
    expect(await screen.findByText('26–50 of 200')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Next' }))
    await waitFor(() => expect(lastQuery(activity)).toEqual({ page: 3, pageSize: 25 }))
    expect(await screen.findByText('51–75 of 200')).toBeInTheDocument()
  })
  it('ignores a clamped answer to a request that is no longer current', async () => {
    const late = deferred<ActivityPage>()
    const activity = vi.fn().mockImplementation((query: ActivityQuery) => query.page === 5 ? late.promise
      : Promise.resolve(activityPage(numbered(25), { totalCount: 60 })))
    const states: ActivityListState[] = []
    render(<Shelled api={client(activity)} initial={{ ...initialActivityState, page: 5 }} onState={state => states.push(state)} />)
    await waitFor(() => expect(activity).toHaveBeenCalledTimes(1))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Direction' }), 'long')
    await act(async () => late.resolve(activityPage(numbered(5, 25), { page: 2, totalCount: 30 })))
    await waitFor(() => expect(lastQuery(activity)).toEqual({ direction: 'long', page: 1, pageSize: 25 }))
    expect(await screen.findByText('1–25 of 60')).toBeInTheDocument()
    expect(states.every(state => state.page !== 2)).toBe(true)
    expect(activity).toHaveBeenCalledTimes(2)
  })
  it('asks Overview for five scoped rows without a pager', async () => {
    const operations = numbered(5)
    const activity = vi.fn().mockResolvedValue(activityPage(operations, { pageSize: 5, totalCount: 6 }))
    render(<ActivityOperations api={client(activity)} accounts={[accountFixture]} scope={`portfolio:${portfolioFixture.id}`} compact />)
    expect(await screen.findByText('CONTRACT-4')).toBeInTheDocument()
    expect(lastQuery(activity)).toEqual({ scope: `portfolio:${portfolioFixture.id}`, page: 1, pageSize: 5 })
    expect(screen.getByText('Showing 5 of 6 operations.')).toBeInTheDocument()
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument()
    expect(screen.queryByText('Scope and coverage')).not.toBeInTheDocument()
  })
  it('shows a delayed skeleton on first load, then failure and retry', async () => {
    const first = deferred<ActivityPage>()
    const activity = vi.fn().mockReturnValueOnce(first.promise).mockResolvedValue(activityFixture)
    render(<ActivityOperations api={client(activity)} accounts={[accountFixture]} />)
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    const skeleton = await screen.findByRole('status')
    expect(skeleton).toHaveTextContent('Loading operations…')
    expect(within(skeleton).getAllByRole('columnheader', { hidden: true }).length).toBeGreaterThan(0)
    await act(async () => first.reject(new Error('outage')))
    expect(await screen.findByRole('alert')).toHaveTextContent('Operations could not be loaded')
    await userEvent.click(screen.getByRole('button', { name: 'Retry operations' }))
    expect(await screen.findByText('BTC')).toBeInTheDocument()
    expect(activity).toHaveBeenCalledTimes(2)
  })
  it('keeps the filters on screen when the server rejects a combination', async () => {
    const activity = vi.fn().mockResolvedValueOnce(activityFixture).mockRejectedValueOnce(new ApiError('http', 'The date range is invalid.', 400)).mockResolvedValue(activityFixture)
    render(<Shelled api={client(activity)} />)
    await screen.findAllByRole('button', { name: /^Expand .+ operation$/ })
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Direction' }), 'short')
    expect(await screen.findByRole('alert')).toHaveTextContent('The date range is invalid.')
    await userEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
    expect((await screen.findAllByRole('button', { name: /^Expand .+ operation$/ })).length).toBeGreaterThan(0)
    expect(screen.queryByText(/operations match the filters/)).not.toBeInTheDocument()
  })
  it('discards late results from the previous account and aborts its request', async () => {
    const first = deferred<ActivityPage>()
    const activity = vi.fn().mockReturnValueOnce(first.promise).mockResolvedValue(activityPage([]))
    const api = client(activity)
    const second = 'cccccccc-bbbb-cccc-dddd-eeeeeeeeeeee'
    const view = render(<ActivityOperations api={api} accounts={[accountFixture]} accountId={accountFixture.id} />)
    view.rerender(<ActivityOperations api={api} accounts={[accountFixture]} accountId={second} />)
    await screen.findByText('No operations in this view')
    expect((activity.mock.calls[0]![1] as AbortSignal).aborted).toBe(true)
    expect(lastQuery(activity)).toEqual({ accountId: second, page: 1, pageSize: 25 })
    first.resolve(activityFixture)
    await waitFor(() => expect(screen.queryByText('BTC')).not.toBeInTheDocument())
  })
  it('shows account coverage and the server scope note explicitly', async () => {
    const page = activityPage([operationFixture], { scopeNote: 'Fixture scope.', coverage: [{ accountId: accountFixture.id, contractId: 'BTC', notice: 'Snapshot mismatch.' }] })
    render(<ActivityOperations api={client(vi.fn().mockResolvedValue(page))} accounts={[accountFixture]} />)
    await userEvent.click(await screen.findByText('Scope and coverage'))
    expect(screen.getByText('Fixture scope.')).toBeInTheDocument()
    expect(screen.getByText(/Snapshot mismatch/)).toBeInTheDocument()
    expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(2)
  })
  it('keeps the account history notice once in scope and coverage, not as a repeated panel subtitle', async () => {
    const notice = 'Incomplete fixture history; missing funding is not zero.'
    const page = activityPage([operationFixture], { coverage: [{ accountId: accountFixture.id, contractId: null, notice }] })
    const api = { ...client(vi.fn().mockResolvedValue(page)), snapshot: vi.fn().mockResolvedValue(null) }
    renderWithVenues(<AccountDetail account={{ ...accountFixture, venueId: 'hyperliquid', syncStatus: 'synced', historyNotice: notice }}
      api={api} refreshing={false} onSync={vi.fn()} onBack={vi.fn()} onManage={vi.fn()} />)
    const notices = await screen.findAllByText(/Incomplete fixture history/)
    expect(notices).toHaveLength(1)
    expect(notices[0]).not.toBeVisible()
    await userEvent.click(screen.getByText('Scope and coverage', { selector: 'summary' }))
    expect(notices[0]).toBeVisible()
  })
})
