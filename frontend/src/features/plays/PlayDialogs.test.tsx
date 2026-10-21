import { act, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import type { PlayHistory } from '@/api/plays'
import { ApiError } from '@/api/errors'
import { HistoryDialog } from './PlayDialogs'
import { savedPlayFixture } from '@/test/workspace-fixture'

function deferred() {
  let resolve!: (value: PlayHistory) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<PlayHistory>((yes, no) => { resolve = yes; reject = no })
  return { resolve, reject, promise }
}
const history = (reason: string): PlayHistory => ({ statusChanges: [], revisions: [{ number: 1, status: 'planned',
  reason, createdAtUtc: '2026-10-14T12:00:00Z', plan: savedPlayFixture.plan }] })

describe('history request lifetime', () => {
  it('labels simulated lifecycle changes explicitly without presenting them as venue fills', async () => {
    const body: PlayHistory = { revisions: [], statusChanges: [
      { from: 'draft', to: 'planned', reason: null, note: null, occurredAtUtc: '2026-10-20T12:00:00Z', source: 'owner' },
      { from: 'planned', to: 'open', reason: null, note: null, occurredAtUtc: '2026-10-20T12:01:00Z', source: 'paper' },
    ] }
    const api = { playHistory: vi.fn().mockResolvedValue(body) } as unknown as WorkspaceApi
    render(<HistoryDialog api={api} playId={savedPlayFixture.summary.id} onClose={vi.fn()} />)
    expect(await screen.findByText('Planned → Open · from simulated paper executions')).toBeInTheDocument()
    expect(screen.getByText('Draft → Planned')).toBeInTheDocument()
    expect(screen.queryByText(/from linked venue fills/)).not.toBeInTheDocument()
  })
  it.each(['play', 'api'] as const)('clears old history on %s replacement and suppresses late history and errors', async replacement => {
    const first = deferred(), second = deferred()
    const read = vi.fn<WorkspaceApi['playHistory']>().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const api = { playHistory: read } as unknown as WorkspaceApi
    const otherApi = { playHistory: read } as unknown as WorkspaceApi
    const { rerender, unmount } = render(<HistoryDialog api={api} playId="one" onClose={vi.fn()} />)
    const firstSignal = read.mock.calls[0]![1]!
    rerender(<HistoryDialog api={replacement === 'api' ? otherApi : api} playId={replacement === 'play' ? 'two' : 'one'} onClose={vi.fn()} />)
    expect(firstSignal.aborted).toBe(true)
    await act(async () => { second.resolve(history('Current history')) })
    await act(async () => { first.resolve(history('Stale history')) })
    expect(screen.getByText('Plan revision 1 · Current history')).toBeInTheDocument()
    expect(screen.queryByText(/Stale history/)).not.toBeInTheDocument()
    const third = deferred()
    read.mockReturnValueOnce(third.promise)
    rerender(<HistoryDialog api={api} playId="three" onClose={vi.fn()} />)
    expect(screen.getByRole('status')).toHaveTextContent('Loading history')
    expect(screen.queryByText(/Current history/)).not.toBeInTheDocument()
    const thirdSignal = read.mock.calls[2]![1]!
    unmount()
    expect(thirdSignal.aborted).toBe(true)
    await act(async () => { third.reject(new ApiError('unavailable', 'Stale error.')) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('pre-flight in history (phase 1, 1.5)', () => {
  it('lists the checklist kept with revision 1, and says when a plan from before it has none', async () => {
    const recorded: PlayHistory = { statusChanges: [], revisions: [{ ...history('Planned').revisions[0]!,
      preflight: [{ code: 'priced-stops', outcome: 'skipped' }, { code: 'thesis', outcome: 'passed' }] }] }
    const view = render(<HistoryDialog api={{ playHistory: vi.fn().mockResolvedValue(recorded) } as unknown as WorkspaceApi} playId="one" onClose={vi.fn()} />)
    expect(await screen.findByText('Pre-flight: a priced stop for every priced entry · not met; thesis and invalidation are written · met.')).toBeInTheDocument()
    view.unmount()
    render(<HistoryDialog api={{ playHistory: vi.fn().mockResolvedValue(history('Planned')) } as unknown as WorkspaceApi} playId="two" onClose={vi.fn()} />)
    expect(await screen.findByText('The plan as first committed.')).toBeInTheDocument()
    expect(screen.getByText('Pre-flight not recorded.')).toBeInTheDocument()
  })
})
