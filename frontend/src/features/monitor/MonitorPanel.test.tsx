import { act, fireEvent, render, renderHook, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import type { AccountMonitor } from '@/api/monitor'
import { ApiError } from '@/api/errors'
import { discrepancyFixture, missingStopFixture, monitorFixture, shortStopFixture } from '@/test/monitor-fixture'
import { MonitorBadge, MonitorDiscrepancies, MonitorPanel, MonitorWatches } from './MonitorPanel'
import { useMonitorRun } from './useMonitorRun'

afterEach(() => vi.useRealTimers())
const client = (overrides: Partial<WorkspaceApi> = {}) => ({
  runAccountMonitor: vi.fn().mockResolvedValue({ requestId: 'request', monitor: { ...monitorFixture, manualState: 'pending' } }),
  accountMonitor: vi.fn().mockResolvedValue({ ...monitorFixture, manualState: 'running' }),
  ...overrides,
}) as unknown as WorkspaceApi
function Controls({ api, monitor = monitorFixture }: { api: WorkspaceApi; monitor?: AccountMonitor }) {
  const run = useMonitorRun(api, monitor)
  return <MonitorPanel monitor={run.monitor!} busy={run.busy} error={run.error} retryAtUtc={run.retryAtUtc} statusUnavailable={run.statusUnavailable} onCheck={() => { void run.check() }} />
}

describe('monitor controls', () => {
  it('does not request a run on load, and disables pending/running checks until a completion cooldown expires', async () => {
    vi.useFakeTimers()
    vi.setSystemTime('2026-10-14T10:00:00Z')
    const api = client()
    const view = render(<Controls api={api} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(120_000) })
    expect(api.runAccountMonitor).not.toHaveBeenCalled()
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check' })))
    expect(api.runAccountMonitor).toHaveBeenCalledOnce()
    expect(api.accountMonitor).toHaveBeenCalledOnce()
    expect(screen.getByRole('button', { name: 'Checking…' })).toBeDisabled()
    view.rerender(<Controls api={api} monitor={{ ...monitorFixture, manualState: 'pending' }} />)
    expect(screen.getByRole('button', { name: 'Check pending…' })).toBeDisabled()
    view.rerender(<Controls api={api} monitor={{ ...monitorFixture, nextManualAllowedAtUtc: '2026-10-14T10:03:00Z' }} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('button', { name: 'Check in 59s' })).toBeDisabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(59_000) })
    expect(screen.getByRole('button', { name: 'Check' })).toBeEnabled()
  })
  it('reads monitor status after a failed run response and obeys its configured cooldown, not a guessed 60 seconds', async () => {
    vi.useFakeTimers()
    vi.setSystemTime('2026-10-14T10:00:00Z')
    const api = client({
      runAccountMonitor: vi.fn().mockRejectedValue(new ApiError('http', 'Attempt failed.', 502)),
      accountMonitor: vi.fn().mockResolvedValue({ ...monitorFixture, state: 'degraded', error: 'Venue read failed.',
        nextManualAllowedAtUtc: '2026-10-14T10:01:30Z' }),
    })
    render(<Controls api={api} />)
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check' })))
    expect(api.accountMonitor).toHaveBeenCalledWith(monitorFixture.accountId, expect.any(AbortSignal))
    expect(screen.getByRole('button', { name: 'Check in 90s' })).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Attempt failed.')
    expect(screen.getByText('Venue read failed.')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(90_000) })
    expect(screen.getByRole('button', { name: 'Check' })).toBeEnabled()
  })
  it('uses Retry-After if a cooling request and its status read both fail', async () => {
    vi.useFakeTimers()
    vi.setSystemTime('2026-10-14T10:00:00Z')
    const api = client({
      runAccountMonitor: vi.fn().mockRejectedValue(new ApiError('http', 'Cooling down.', 429, '2026-10-14T10:00:40Z')),
      accountMonitor: vi.fn().mockRejectedValue(new Error('Unavailable')),
    })
    render(<Controls api={api} />)
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check' })))
    expect(screen.getByRole('button', { name: 'Check in 40s' })).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Monitor status could not be refreshed.')
  })
  it('aborts manual requests and suppresses status reads and updates after replacement or unmount', async () => {
    let finish!: (result: { requestId: string; monitor: AccountMonitor }) => void
    const first = client({ runAccountMonitor: vi.fn().mockImplementation(() => new Promise(resolve => { finish = resolve })) })
    const changed = vi.fn()
    const view = renderHook(({ api }) => useMonitorRun(api, monitorFixture, changed), { initialProps: { api: first } })
    let pending!: Promise<void>
    act(() => { pending = view.result.current.check() })
    const second = client()
    view.rerender({ api: second })
    expect(vi.mocked(first.runAccountMonitor).mock.calls[0]![1]!.aborted).toBe(true)
    await act(async () => { finish({ requestId: 'late', monitor: monitorFixture }); await pending })
    expect(first.accountMonitor).not.toHaveBeenCalled()
    expect(changed).not.toHaveBeenCalled()
    expect(view.result.current.busy).toBe(false)
    view.unmount()
  })
  it.each([
    ['resolve', 'before return'], ['reject', 'before return'],
    ['resolve', 'during retry'], ['reject', 'during retry'],
    ['resolve', 'after retry'], ['reject', 'after retry'],
  ] as const)('forgets an aborted Check on return to the same account: %s %s', async (outcome, timing) => {
    let settle!: () => void
    let rejectRetry!: (cause: Error) => void
    const oldRequest = new Promise<{ requestId: string; monitor: AccountMonitor }>((resolve, reject) => {
      settle = () => outcome === 'resolve'
        ? resolve({ requestId: 'late', monitor: { ...monitorFixture, manualState: 'running' } })
        : reject(new ApiError('http', 'Old request failed.', 502))
    })
    const currentMonitor = { ...monitorFixture, state: 'degraded' as const, error: 'Current run failed.' }
    const api = client({
      runAccountMonitor: vi.fn().mockReturnValueOnce(oldRequest)
        .mockImplementationOnce(() => new Promise((_, reject) => { rejectRetry = reject })),
      accountMonitor: vi.fn().mockResolvedValue(currentMonitor),
    })
    const changed = vi.fn()
    const view = renderHook(({ monitor }: { monitor: AccountMonitor | null }) => useMonitorRun(api, monitor, changed),
      { initialProps: { monitor: monitorFixture as AccountMonitor | null } })
    let oldWork!: Promise<void>, retry!: Promise<void>
    act(() => { oldWork = view.result.current.check() })
    expect(view.result.current.busy).toBe(true)
    view.rerender({ monitor: null })
    expect(vi.mocked(api.runAccountMonitor).mock.calls[0]![1]!.aborted).toBe(true)
    if (timing === 'before return') await act(async () => { settle(); await oldWork })
    view.rerender({ monitor: monitorFixture })
    expect(view.result.current.busy).toBe(false)
    expect(view.result.current.monitor).toBe(monitorFixture)
    expect(view.result.current.error).toBeNull()
    act(() => { retry = view.result.current.check() })
    expect(api.runAccountMonitor).toHaveBeenCalledTimes(2)
    if (timing === 'during retry') await act(async () => { settle(); await oldWork })
    expect(view.result.current.busy).toBe(true)
    expect(view.result.current.monitor).toBe(monitorFixture)
    expect(view.result.current.error).toBeNull()
    expect(api.accountMonitor).not.toHaveBeenCalled()
    expect(changed).not.toHaveBeenCalled()
    await act(async () => { rejectRetry(new ApiError('http', 'Current request failed.', 502)); await retry })
    if (timing === 'after retry') await act(async () => { settle(); await oldWork })
    expect(view.result.current.busy).toBe(false)
    expect(view.result.current.monitor).toBe(currentMonitor)
    expect(view.result.current.error).toBe('Current request failed.')
    expect(api.accountMonitor).toHaveBeenCalledOnce()
    expect(changed).toHaveBeenCalledOnce()
  })
  it.each(['resolve', 'reject'] as const)('drops the old accepted monitor when its status read is aborted and later %ss', async outcome => {
    let settle!: () => void
    const api = client({
      accountMonitor: vi.fn().mockImplementation(() => new Promise<AccountMonitor>((resolve, reject) => {
        settle = () => outcome === 'resolve'
          ? resolve({ ...monitorFixture, manualState: 'running' })
          : reject(new Error('Old status failed.'))
      })),
    })
    const changed = vi.fn()
    const view = renderHook(({ monitor }: { monitor: AccountMonitor | null }) => useMonitorRun(api, monitor, changed),
      { initialProps: { monitor: monitorFixture as AccountMonitor | null } })
    let pending!: Promise<void>
    await act(async () => { pending = view.result.current.check() })
    expect(view.result.current.monitor?.manualState).toBe('pending')
    view.rerender({ monitor: null })
    expect(vi.mocked(api.accountMonitor).mock.calls[0]![1]!.aborted).toBe(true)
    view.rerender({ monitor: monitorFixture })
    expect(view.result.current.busy).toBe(false)
    expect(view.result.current.monitor).toBe(monitorFixture)
    await act(async () => { settle(); await pending })
    expect(view.result.current.monitor).toBe(monitorFixture)
    expect(view.result.current.error).toBeNull()
    expect(view.result.current.statusUnavailable).toBe(false)
    expect(changed).not.toHaveBeenCalled()
  })
  it('shows exceptional account badges only, worker offline and credential management', () => {
    const manage = vi.fn()
    const view = render(<MonitorBadge monitor={monitorFixture} />)
    expect(screen.queryByText('Monitored')).not.toBeInTheDocument()
    view.rerender(<MonitorPanel monitor={{ ...monitorFixture, state: 'credential-error', workerOnline: false }} busy={false} error={null} onCheck={vi.fn()} onManageCredential={manage} />)
    expect(screen.getByText(/Worker offline/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Manage account credential' }))
    expect(manage).toHaveBeenCalledOnce()
  })
  it('shows exact discrepancy evidence and dismissal status, sends only an optional plain-text note', async () => {
    const dismiss = vi.fn().mockResolvedValue(undefined)
    const view = render(<MonitorDiscrepancies discrepancies={[discrepancyFixture]} busy={false} onDismiss={dismiss} />)
    expect(screen.getByText(/0.100000000000000001/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'About dismissing warnings' })).toHaveAccessibleDescription(
      'Dismissal acknowledges a warning. It does not change executions or stop checks, including resting exits after closure or cancellation.')
    expect(screen.getByText(/does not change executions or stop checks/)).toHaveClass('sr-only')
    fireEvent.change(screen.getByRole('textbox'), { target: { value: '  Investigating.  ' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Dismiss' })))
    expect(dismiss).toHaveBeenCalledWith(discrepancyFixture.id, 'Investigating.')
    view.rerender(<MonitorDiscrepancies discrepancies={[{ ...discrepancyFixture, state: 'dismissed', note: '<b>plain text</b>' }]} busy={false} onDismiss={dismiss} />)
    expect(screen.queryByRole('button', { name: 'Dismiss' })).not.toBeInTheDocument()
    expect(screen.getByText('<b>plain text</b>').tagName).toBe('P')
  })
  it('shows an unverifiable outcome as neither resolved nor checked, and still lets the owner dismiss it', async () => {
    const dismiss = vi.fn().mockResolvedValue(undefined)
    const unverifiable = { ...discrepancyFixture, state: 'candidate' as const, outcome: 'unverifiable' as const, unverifiableAtUtc: '2026-10-21T10:00:00Z' }
    const view = render(<MonitorDiscrepancies discrepancies={[unverifiable]} busy={false} onDismiss={dismiss} />)
    expect(screen.getByText('warning · unverifiable')).toHaveAttribute('data-outcome', 'unverifiable')
    expect(screen.getByText(/not resolved and is no longer checked/)).toHaveClass('monitor-unverifiable')
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Dismiss' })))
    expect(dismiss).toHaveBeenCalledWith(unverifiable.id, undefined)
    // A later fresh decision clears the outcome; a resolved record is not dismissable again.
    view.rerender(<MonitorDiscrepancies discrepancies={[{ ...discrepancyFixture, state: 'resolved' }]} busy={false} onDismiss={dismiss} />)
    expect(screen.getByText('warning · resolved')).not.toHaveAttribute('data-outcome')
    expect(screen.queryByText(/no longer checked/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Dismiss' })).not.toBeInTheDocument()
  })
  it('lists watched orders of an ended Play with a persistent unverifiable state', () => {
    const base = { playId: discrepancyFixture.playId!, startedAtUtc: '2026-10-14T10:00:00Z' }
    const view = render(<MonitorWatches watches={[
      { ...base, linkId: 'l-1', orderId: 'exit-1', role: 'stop', state: 'watching', endedAtUtc: null },
      { ...base, linkId: 'l-2', orderId: 'exit-2', role: 'target', state: 'resolved', endedAtUtc: '2026-10-15T10:00:00Z' },
      // A resting entry of a Cancelled Play is watched like an exit.
      { ...base, linkId: 'l-3', orderId: 'entry-3', role: 'entry', state: 'unverifiable', endedAtUtc: '2026-10-21T10:00:00Z' },
    ]} />)
    expect(screen.getByRole('region', { name: 'Watched orders' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'About watched orders' })).toHaveAccessibleDescription(
      'After a Play ends, its linked orders are checked until the venue reports them filled, cancelled or no longer open.')
    expect(screen.getByText('Watching')).toHaveAttribute('data-watch', 'watching')
    expect(screen.getByText('No longer resting')).toHaveAttribute('data-watch', 'resolved')
    expect(screen.getByText('Unverifiable')).toHaveAttribute('data-watch', 'unverifiable')
    expect(screen.getAllByText(/may still rest at the venue/)).toHaveLength(1)
    // Orders read "Order <venue ID> · <role>", or the plan level's name where the Play knows it.
    expect(screen.getByText('Order entry-3 · entry')).toBeInTheDocument()
    view.rerender(<MonitorWatches watches={[{ ...base, linkId: 'l-1', orderId: 'exit-1', role: 'stop', state: 'watching', endedAtUtc: null }]}
      levelName={watch => watch.linkId === 'l-1' ? 'Entry 1 stop' : null} />)
    expect(screen.getByText('Order exit-1 · Entry 1 stop')).toBeInTheDocument()
    view.rerender(<MonitorWatches watches={[]} />)
    expect(screen.queryByRole('region', { name: 'Watched orders' })).not.toBeInTheDocument()
  })
  it('omits empty timing boilerplate but retains the available schedule and warning state', () => {
    const view = render(<MonitorPanel monitor={{ ...monitorFixture, lastSuccessAtUtc: null, nextRunAtUtc: null }}
      busy={false} error={null} onCheck={vi.fn()} />)
    expect(screen.queryByText(/not recorded/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Check' })).toBeEnabled()
    view.rerender(<MonitorPanel monitor={{ ...monitorFixture, state: 'degraded', lastSuccessAtUtc: null }}
      busy={false} error={null} onCheck={vi.fn()} />)
    expect(screen.getByText(/^Next /)).toBeVisible()
    expect(screen.queryByText(/Last success/)).not.toBeInTheDocument()
    expect(screen.getByText('Incomplete venue coverage.')).toBeVisible()
  })
  it('keeps a failed dismissal visible without claiming success', async () => {
    render(<MonitorDiscrepancies discrepancies={[discrepancyFixture]} busy={false} onDismiss={vi.fn().mockRejectedValue(new Error('failed'))} />)
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Dismiss' })))
    expect(screen.getByRole('alert')).toHaveTextContent('Dismissal was not confirmed.')
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeEnabled()
  })
  it('labels scheduled runs and owner refreshes apart from an owner Check', () => {
    const view = render(<MonitorPanel monitor={{ ...monitorFixture, manualState: 'running', runTrigger: 'scheduled' }} busy={false} error={null} onCheck={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Scheduled check running' })).toBeDisabled()
    view.rerender(<MonitorPanel monitor={{ ...monitorFixture, manualState: 'pending', runTrigger: 'refresh' }} busy={false} error={null} onCheck={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Account refresh running' })).toBeDisabled()
    view.rerender(<MonitorPanel monitor={{ ...monitorFixture, manualState: 'pending', runTrigger: 'owner' }} busy={false} error={null} onCheck={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Check pending…' })).toBeDisabled()
  })
  it('without a credential handler, explains where to update the credential instead of linking elsewhere', () => {
    render(<MonitorPanel monitor={{ ...monitorFixture, state: 'credential-error' }} busy={false} error={null} onCheck={vi.fn()} />)
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
    expect(screen.getByText(/Manage account dialog/)).toBeInTheDocument()
  })
  it('polls stored status while a run is pending, then reloads once it is idle even without a stream event', async () => {
    vi.useFakeTimers()
    const idle = { ...monitorFixture, nextManualAllowedAtUtc: null }
    const api = client({ accountMonitor: vi.fn()
      .mockResolvedValueOnce({ ...monitorFixture, manualState: 'running', runTrigger: 'owner' })
      .mockResolvedValueOnce(idle) })
    const changed = vi.fn()
    const pending: AccountMonitor = { ...monitorFixture, manualState: 'pending', runTrigger: 'owner' }
    const view = renderHook(() => useMonitorRun(api, pending, changed))
    await act(async () => { await vi.advanceTimersByTimeAsync(2_999) })
    expect(api.accountMonitor).not.toHaveBeenCalled()
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(view.result.current.monitor?.manualState).toBe('running')
    expect(changed).not.toHaveBeenCalled()
    await act(async () => { await vi.advanceTimersByTimeAsync(3_000) })
    expect(view.result.current.monitor).toBe(idle)
    expect(changed).toHaveBeenCalledOnce()
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000) })
    expect(api.accountMonitor).toHaveBeenCalledTimes(2)
    view.unmount()
  })
  it('stops polling on unmount and after its bound', async () => {
    vi.useFakeTimers()
    const api = client({ accountMonitor: vi.fn().mockResolvedValue({ ...monitorFixture, manualState: 'running', runTrigger: 'scheduled' }) })
    const running: AccountMonitor = { ...monitorFixture, manualState: 'running', runTrigger: 'scheduled' }
    const view = renderHook(() => useMonitorRun(api, running))
    await act(async () => { await vi.advanceTimersByTimeAsync(10 * 60_000) })
    expect(api.accountMonitor).toHaveBeenCalledTimes(40)
    view.unmount()
    expect(vi.getTimerCount()).toBe(0)
  })
  it('clears a 429 message when its cooldown ends and other failures once a newer attempt is shown', async () => {
    vi.useFakeTimers()
    vi.setSystemTime('2026-10-14T10:00:00Z')
    const api = client({
      runAccountMonitor: vi.fn().mockRejectedValueOnce(new ApiError('http', 'A check is already running.', 429, '2026-10-14T10:00:20Z'))
        .mockRejectedValueOnce(new ApiError('http', 'Attempt failed.', 502)),
      accountMonitor: vi.fn().mockResolvedValue(monitorFixture),
    })
    const view = render(<Controls api={api} />)
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check' })))
    expect(screen.getByRole('alert')).toHaveTextContent('A check is already running.')
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check' })))
    expect(screen.getByRole('alert')).toHaveTextContent('Attempt failed.')
    view.rerender(<Controls api={api} monitor={{ ...monitorFixture }} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Attempt failed.')
    view.rerender(<Controls api={api} monitor={{ ...monitorFixture, lastAttemptAtUtc: '2026-10-14T10:05:00Z' }} />)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('lifts the unknown-status block on the next successful stored read without a new monitor prop, keeping the cooldown', async () => {
    vi.useFakeTimers()
    vi.setSystemTime('2026-10-14T10:00:00Z')
    const api = client({
      runAccountMonitor: vi.fn().mockRejectedValue(new ApiError('http', 'Cooling down.', 429, '2026-10-14T10:00:10Z')),
      accountMonitor: vi.fn().mockRejectedValueOnce(new Error('Unavailable'))
        .mockResolvedValue({ ...monitorFixture, nextManualAllowedAtUtc: '2026-10-14T10:00:20Z' }),
    })
    render(<Controls api={api} />)
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check' })))
    expect(screen.getByText(/Check is blocked until stored monitor status/)).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Monitor status could not be refreshed.')
    await act(async () => { await vi.advanceTimersByTimeAsync(3_000) })
    expect(api.accountMonitor).toHaveBeenCalledTimes(2)
    expect(screen.queryByText(/Check is blocked until stored monitor status/)).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Cooling down.')
    expect(screen.getByRole('alert')).not.toHaveTextContent('could not be refreshed')
    expect(screen.getByRole('button', { name: /^Check in \d+s$/ })).toBeDisabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(17_000) })
    expect(screen.getByRole('button', { name: 'Check' })).toBeEnabled()
    expect(api.accountMonitor).toHaveBeenCalledTimes(2)
  })
})

/** The value beside a fact-table label, or null when the record has no such row. */
const fact = (item: HTMLElement, label: string) => {
  const term = within(item).queryByText(label, { selector: 'dt' })
  return term ? term.nextElementSibling?.textContent ?? null : null
}

describe('incident visibility (Phase 0, 0.4a)', () => {
  it('heads the panel with its open incidents and never says Monitored beside an open critical record', () => {
    const view = render(<MonitorPanel monitor={{ ...monitorFixture, discrepancies: [missingStopFixture] }} busy={false} error={null} onCheck={vi.fn()} />)
    const panel = () => screen.getByRole('region', { name: 'Background monitor' })
    expect(within(panel()).queryByText('Monitored')).not.toBeInTheDocument()
    expect(within(panel()).getByText('Needs attention · 1 critical')).toBeInTheDocument()
    // The Execution tab passes the Play's own records instead of the account's.
    view.rerender(<MonitorPanel monitor={monitorFixture} discrepancies={[missingStopFixture, shortStopFixture]} busy={false} error={null} onCheck={vi.fn()} />)
    expect(within(panel()).getByText('Needs attention · 1 critical · 1 warning')).toBeInTheDocument()
    // Resolved and dismissed records leave the run state as the heading.
    view.rerender(<MonitorPanel monitor={monitorFixture} discrepancies={[{ ...missingStopFixture, state: 'resolved' }, { ...shortStopFixture, state: 'dismissed' }]}
      busy={false} error={null} onCheck={vi.fn()} />)
    expect(within(panel()).getByText('Monitored')).toBeInTheDocument()
    // A stale run keeps its state beside the incidents; an unverifiable record counts until it is dismissed.
    const unverifiable = { ...shortStopFixture, state: 'candidate' as const, outcome: 'unverifiable' as const, unverifiableAtUtc: '2026-10-21T10:00:00Z' }
    view.rerender(<MonitorPanel monitor={{ ...monitorFixture, state: 'stale' }} discrepancies={[unverifiable]} busy={false} error={null} onCheck={vi.fn()} />)
    expect(within(panel()).getByText('Needs attention · 1 warning')).toBeInTheDocument()
    expect(within(panel()).getByText('Monitor stale')).toBeInTheDocument()
  })
  it('replaces raw kinds and JSON with a human fact table, the orders and one next action', () => {
    render(<MonitorDiscrepancies discrepancies={[shortStopFixture]} busy={false} onDismiss={vi.fn()} />)
    const item = screen.getByRole('listitem')
    expect(within(item).getByText('The stop covers less than the position')).toBeInTheDocument()
    expect(within(item).queryByText(/protection\.short/)).not.toBeInTheDocument()
    expect(fact(item, 'Open quantity')).toBe('1.25')
    expect(fact(item, 'Stop quantity')).toBe('0.5')
    expect(fact(item, 'Uncovered')).toBe('0.75')
    expect(fact(item, 'Last observed')).toBe(new Date('2026-10-14T10:05:00Z').toLocaleString())
    expect(within(item).getByText('Order 900000029 · stop · resting')).toBeInTheDocument()
    expect(within(item).getAllByText(/^Next: /)).toHaveLength(1)
    // The recorded JSON stays available, closed, behind a disclosure.
    const disclosure = within(item).getByText('Recorded details').closest('details')!
    expect(disclosure).not.toHaveAttribute('open')
    expect(disclosure.querySelector('pre')).toHaveTextContent('"stopCoverage": "orders"')
    expect(screen.getByRole('textbox', { name: 'Dismissal note: The stop covers less than the position · BTC' })).toBeInTheDocument()
  })
  it('names whole-position or unknown coverage instead of computing an uncovered quantity, and keeps unknown as Unknown', () => {
    const whole = { ...shortStopFixture, details: { ...shortStopFixture.details, stopCoverage: 'whole-position' } }
    const unknown = { ...missingStopFixture, id: 'unknown', details: { ...missingStopFixture.details, quantity: null, stopCoverage: 'unknown' } }
    render(<MonitorDiscrepancies discrepancies={[whole, unknown]} busy={false} onDismiss={vi.fn()} />)
    const [first, second] = screen.getAllByRole('listitem')
    expect(fact(first!, 'Stop coverage')).toBe('Whole-position stop')
    expect(fact(first!, 'Uncovered')).toBeNull()
    expect(fact(first!, 'Stop quantity')).toBeNull()
    expect(fact(second!, 'Open quantity')).toBe('Unknown')
    expect(fact(second!, 'Stop coverage')).toBe('Unknown')
    expect(fact(second!, 'Uncovered')).toBeNull()
    expect(within(second!).getByText('No stop protects the open position')).toBeInTheDocument()
    expect(within(second!).getByText('Order 900000028 · target · resting')).toBeInTheDocument()
  })
  it('closes an unverifiable record once dismissed, and offers no next action or dismissal for history', () => {
    const dismissed = { ...shortStopFixture, state: 'dismissed' as const, outcome: 'unverifiable' as const,
      unverifiableAtUtc: '2026-10-21T10:00:00Z', dismissedAtUtc: '2026-10-21T11:00:00Z', note: 'Checked at the venue.' }
    const view = render(<MonitorDiscrepancies discrepancies={[dismissed]} busy={false} onDismiss={vi.fn()} />)
    expect(screen.getByText('warning · dismissed')).not.toHaveAttribute('data-outcome')
    expect(screen.queryByText(/no longer checked/)).not.toBeInTheDocument()
    expect(screen.queryByText(/^Next: /)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Dismiss' })).not.toBeInTheDocument()
    // Changed evidence that became unverifiable after the dismissal is outstanding again.
    view.rerender(<MonitorDiscrepancies discrepancies={[{ ...dismissed, unverifiableAtUtc: '2026-10-22T10:00:00Z' }]} busy={false} onDismiss={vi.fn()} />)
    expect(screen.getByText('warning · unverifiable')).toHaveAttribute('data-outcome', 'unverifiable')
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeEnabled()
  })
  it('names position, unlinked-fill and drift facts, and Play levels where the Play is known', () => {
    const position = { ...discrepancyFixture, id: 'position', kind: 'position.excess', playId: null, lastObservedAtUtc: null,
      details: { playIds: [], expectedQuantity: '0.1', venueQuantity: '0.100000000000000001' } }
    const fills = { ...discrepancyFixture, id: 'fills', kind: 'fill.unlinked', playId: null, details: { count: 2, totalQuantity: '0.3', fills: [] } }
    const drift = { ...discrepancyFixture, id: 'drift', kind: 'level.drift', severity: 'info' as const,
      details: { levels: [{ orderId: '900000030', actualPrice: '61000', plannedPrice: '60000', remainingQuantity: '0.5' }] } }
    render(<MonitorDiscrepancies discrepancies={[position, fills, drift, shortStopFixture]} busy={false} onDismiss={vi.fn()}
      levelName={key => key === 'stop|entry-1|stop-1' ? 'Entry 1 stop' : null} />)
    const [first, second, third, fourth] = screen.getAllByRole('listitem')
    expect(fact(first!, 'Expected by open Plays')).toBe('0.1')
    expect(fact(first!, 'Venue position')).toBe('0.100000000000000001')
    expect(fact(first!, 'Last observed')).toBe('Unknown')
    expect(fact(second!, 'Unlinked fills')).toBe('2')
    expect(fact(second!, 'Total quantity')).toBe('0.3')
    expect(within(third!).getByText('A linked order no longer matches its plan level')).toBeInTheDocument()
    expect(within(third!).getByText('Order 900000030 · at 61000, plan 60000')).toBeInTheDocument()
    expect(within(fourth!).getByText('Order 900000029 · Entry 1 stop · resting')).toBeInTheDocument()
  })
})
