import { useEffect, type RefObject } from 'react'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { MonitorStreamEvent } from '@/api/monitor'
import type { WorkspaceApi } from '@/api/workspace'
import { createPlaysSession, type PlaysSession } from '@/features/plays/saved'
import { emptyOverview, playApiStubs } from '@/test/workspace-fixture'
import { eventCursor } from '@/test/monitor-fixture'
import { systemFixture } from '@/test/system-fixture'
import { shellIdentityFixture } from '@/test/identity-fixture'
import { ApplicationShell } from './ApplicationShell'
import { reloadPage } from './reload-page'

type PlaysProps = { session: PlaysSession; onSession: (session: PlaysSession) => void; workRef: RefObject<symbol | null> }
let plays: PlaysProps
vi.mock('@/features/plays/PlaysPage', () => ({ PlaysPage: function TestPlays(props: PlaysProps) {
  useEffect(() => { plays = props })
  return <p>Test Play editor: {props.session.draft.title || 'blank'}</p>
} }))
vi.mock('./reload-page', () => ({ reloadPage: vi.fn() }))

const streams: { emit: (frame: MonitorStreamEvent) => void; finish: () => void }[] = []
function client(overrides: Partial<WorkspaceApi> = {}): WorkspaceApi {
  return {
    ...playApiStubs(), overview: vi.fn().mockResolvedValue(emptyOverview),
    monitorStream: vi.fn((_after: string, signal: AbortSignal, emit: (frame: MonitorStreamEvent) => void) => new Promise<void>(resolve => {
      streams.push({ emit, finish: resolve })
      signal.addEventListener('abort', () => resolve(), { once: true })
    })),
    apiVersion: vi.fn().mockResolvedValue(systemFixture.version),
    ...overrides,
  } as WorkspaceApi
}
async function mount(api: WorkspaceApi, version = systemFixture.version) {
  render(<ApplicationShell identity={shellIdentityFixture()} system={{ ...systemFixture, version }} api={api} disconnect={vi.fn()} />)
  await screen.findByText('Test Play editor: blank')
  await waitFor(() => expect(plays).toBeDefined())
}
const notice = () => screen.queryByText(/is running on the server/)

beforeEach(() => {
  plays = undefined as unknown as PlaysProps
  streams.length = 0
  window.history.replaceState(null, '', '/#plays')
  vi.stubGlobal('__VESSEL_VERSION__', '1.2.3')
})
afterEach(() => vi.restoreAllMocks())

describe('version handshake in the shell', () => {
  it('shows no notice when the versions agree', async () => {
    await mount(client())
    expect(notice()).toBeNull()
  })

  it.each(['unknown', '0.0.0'])('never shows the notice for an API placeholder %s', async version => {
    await mount(client(), version)
    expect(notice()).toBeNull()
  })

  it('never shows the notice for a development page', async () => {
    vi.stubGlobal('__VESSEL_VERSION__', undefined)
    await mount(client(), '1.3.0')
    expect(notice()).toBeNull()
  })

  it('shows the notice on load and keeps an unsaved draft until the owner confirms the reload', async () => {
    const user = userEvent.setup()
    await mount(client(), '1.3.0')
    expect(notice()!.closest('[role="status"]')).not.toBeNull()
    expect(notice()).toHaveTextContent('Vessel 1.3.0 is running on the server; this page is 1.2.3. Reload to use it.')
    const session = { ...createPlaysSession(), draft: { ...createPlaysSession().draft, title: 'Unsaved idea' } }
    act(() => plays.onSession(session))
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await user.click(screen.getByRole('button', { name: 'Reload' }))
    expect(confirm).toHaveBeenCalledWith('Reload and discard unsaved Play work?')
    expect(reloadPage).not.toHaveBeenCalled()
    expect(plays.session).toBe(session)
    // The notice never reloads by itself, and the draft stays protected on unload.
    const unload = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(unload)
    expect(unload.defaultPrevented).toBe(true)
    confirm.mockReturnValue(true)
    await user.click(screen.getByRole('button', { name: 'Reload' }))
    expect(reloadPage).toHaveBeenCalledOnce()
    // Confirmed once: the unload that follows does not ask again.
    const confirmed = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(confirmed)
    expect(confirmed.defaultPrevented).toBe(false)
  })

  it('reloads at once without unsaved work', async () => {
    const confirm = vi.spyOn(window, 'confirm')
    await mount(client(), '1.3.0')
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }))
    expect(confirm).not.toHaveBeenCalled()
    expect(reloadPage).toHaveBeenCalledOnce()
  })

  it('checks the version again when the event stream reconnects after an upgrade', async () => {
    const apiVersion = vi.fn<NonNullable<WorkspaceApi['apiVersion']>>().mockResolvedValue('1.2.3')
    await mount(client({ apiVersion }))
    await waitFor(() => expect(streams).toHaveLength(1))
    const ready = { type: 'status', status: { state: 'ready', nextCursor: eventCursor('0') } } as const
    act(() => streams[0]!.emit(ready))
    expect(apiVersion).not.toHaveBeenCalled()
    apiVersion.mockResolvedValue('1.3.0')
    act(() => streams[0]!.finish())
    await waitFor(() => expect(streams).toHaveLength(2), { timeout: 5_000 })
    act(() => streams[1]!.emit(ready))
    expect(apiVersion).toHaveBeenCalledOnce()
    expect(await screen.findByText(/Vessel 1\.3\.0 is running on the server/)).toBeVisible()
    expect(reloadPage).not.toHaveBeenCalled()
  })
})
