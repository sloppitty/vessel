// Regression tests for the PR #47 review: stream startup/backoff, account detail order, write cancellation, 429 text and panel timers.
import { act, render as rtlRender, renderHook, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { MonitorStreamEvent } from '@/api/monitor'
import type { PlayExecution } from '@/api/plays'
import type { WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { createWorkspaceApi } from '@/api/workspace'
import { render } from '@/test/render'
import { eventCursor, monitorFixture } from '@/test/monitor-fixture'
import { accountFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { useMonitorEvents } from '@/features/workspace/useMonitorEvents'
import { AccountDetail } from '@/features/workspace/AccountDetail'
import { usePlayExecution } from '@/features/plays/usePlayExecution'
import { MonitorPanel } from './MonitorPanel'
import { credentialsFixture } from '@/test/identity-fixture'

vi.mock('@/features/workspace/ActivityOperations', () => ({ ActivityOperations: () => null }))
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })
const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })

function streamClient(fail?: () => unknown) {
  const connections: { after: string; emit: (frame: MonitorStreamEvent) => void }[] = []
  const api = {
    monitorEvents: vi.fn().mockResolvedValue({ events: [], nextCursor: eventCursor('7'), resetRequired: true }),
    monitor: vi.fn().mockResolvedValue({ workerOnline: true, heartbeatAtUtc: null, accounts: [] }),
    monitorStream: vi.fn((after: string, signal: AbortSignal, emit: (frame: MonitorStreamEvent) => void) => {
      connections.push({ after, emit })
      if (fail) return Promise.reject(fail())
      return new Promise<void>(resolve => signal.addEventListener('abort', () => resolve(), { once: true }))
    }),
  } as unknown as WorkspaceApi
  return { api, connections }
}

describe('finding 3: monitor stream startup and reconnect', () => {
  it('does not replay the whole retained history nor reload the shell on the first ready frame', async () => {
    vi.useFakeTimers()
    const { api, connections } = streamClient()
    const reload = vi.fn()
    renderHook(() => useMonitorEvents(api, true, reload, 0))
    await advance(0)
    act(() => connections[0]!.emit({ type: 'status', status: { state: 'ready', nextCursor: eventCursor('7') } }))
    await advance(300)
    expect(connections[0]!.after).toBe(eventCursor('7'))
    expect(reload).not.toHaveBeenCalled()
  })
  it('backs off when the server refuses a fifth stream with 429', async () => {
    vi.useFakeTimers()
    const { api } = streamClient(() => new ApiError('http', 'The request could not be completed.', 429, new Date(Date.now() + 5_000).toISOString()))
    renderHook(() => useMonitorEvents(api, true, vi.fn(), 0))
    await advance(0)
    await advance(120_000)
    // Fixed 5 s retry yields ~25 attempts in two minutes.
    expect((api.monitorStream as ReturnType<typeof vi.fn>).mock.calls.length).toBeLessThanOrEqual(8)
  })
})

describe('finding 4: account detail order', () => {
  it('renders the back button and heading before the monitor panel', () => {
    const api = { snapshot: vi.fn(() => new Promise(() => {})) } as unknown as WorkspaceApi
    render(<AccountDetail account={{ ...accountFixture, venueId: 'hyperliquid', syncStatus: 'synced', sourceId: '7' }} api={api}
      refreshing={false} onSync={vi.fn()} onBack={vi.fn()} onManage={vi.fn()} monitor={{ ...monitorFixture, accountId: accountFixture.id, state: 'stale' }} />)
    const back = screen.getByRole('button', { name: /All accounts/ })
    // The page title names the account; the refreshed summary card carries the venue, freshness and badges.
    const summary = screen.getByRole('region', { name: 'Account summary' })
    const source = screen.getByText('Source')
    const panel = screen.getByRole('region', { name: 'Background monitor' })
    const tiles = screen.getByText('Available balance')
    const order = [back, summary, source, panel, tiles]
    for (let index = 1; index < order.length; index++) {
      expect(order[index - 1]!.compareDocumentPosition(order[index]!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    }
    // The exceptional monitor badge stays inside the account summary.
    expect(summary).toContainElement(screen.getByText('Monitor stale', { selector: '.monitor-badge' }))
  })
})

describe('finding 5: Play status in the execution cancellation key', () => {
  it('does not abort an in-flight link write when the Play status changes', async () => {
    const execution = { playId: savedPlayFixture.summary.id, status: 'planned', links: [] } as unknown as PlayExecution
    let linkSignal: AbortSignal | undefined
    const api = {
      playExecution: vi.fn().mockResolvedValue(execution),
      linkOrder: vi.fn((_id: string, _request: unknown, signal: AbortSignal) => { linkSignal = signal; return new Promise(() => {}) }),
    } as unknown as WorkspaceApi
    const view = renderHook(({ status }) => usePlayExecution(api, execution.playId, status, 0), { initialProps: { status: 'planned' } })
    await act(async () => {})
    act(() => { void view.result.current.link({ orderId: 'order-1', role: 'entry' }) })
    view.rerender({ status: 'open' })
    expect(linkSignal?.aborted).toBe(false)
  })
})

describe('finding 7: 429 on Check', () => {
  it('shows a specific admission message instead of the generic fallback', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: 'This account check is running or cooling down.', status: 429 }),
      { status: 429, headers: { 'Retry-After': '30', 'Content-Type': 'application/problem+json' } })))
    const failure = await createWorkspaceApi(credentialsFixture).runAccountMonitor(accountFixture.id).catch(error => error)
    expect(failure.message).not.toBe('The request could not be completed.')
  })
})

describe('finding 8: panel timer', () => {
  it('does not keep a 1 s interval running when there is no cooldown', () => {
    vi.useFakeTimers()
    rtlRender(<MonitorPanel monitor={monitorFixture} busy={false} error={null} onCheck={vi.fn()} />)
    expect(vi.getTimerCount()).toBe(0)
  })
})
