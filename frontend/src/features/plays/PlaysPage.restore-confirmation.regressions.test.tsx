import { useEffect, useState } from 'react'
import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, playApiStubs, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { PlaysPage } from './PlaysPage'
import { draftFromSaved, savedState, type PlaysSession } from './saved'

const first: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'open', planRevision: 1 } }
const second: SavedPlay = { ...first, summary: { ...first.summary, id: '33333333-4444-5555-6666-777777777777', title: 'Play B' } }
const sessionFor = (play: SavedPlay): PlaysSession => {
  const draft = draftFromSaved(play, [])
  return { view: 'editor', draft, saved: savedState(play, draft) }
}
const client = (): WorkspaceApi => ({ ...createWorkspaceApi('synthetic-only'), ...playApiStubs(), play: vi.fn().mockResolvedValue(first) })

it('does not strand a queued restore when its owner rerenders before the microtask starts', async () => {
  const api = client()
  const handled = vi.fn()
  let current = sessionFor(second)
  function Harness() {
    const [session, setSession] = useState(current)
    const [requested, setRequested] = useState<string | null>(null)
    const [, repaint] = useState(0)
    // A shell update after the child's request effect has queued its microtask.
    useEffect(() => { if (requested) repaint(value => value + 1) }, [requested])
    return <NotificationProvider>
      <button onClick={() => setRequested(first.summary.id)}>Restore A</button>
      <PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api}
        session={session} onSession={next => { current = next; setSession(next) }}
        active={false} requestedPlayId={requested} requestedPlayRestore
        onRequestedPlayHandled={opened => { handled(opened); setRequested(null) }} />
    </NotificationProvider>
  }
  render(<Harness />)
  fireEvent.click(screen.getByText('Restore A'))
  await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
  expect(current.saved?.summary.id).toBe(first.summary.id)
  expect(handled).toHaveBeenCalledWith(true)
})

it('does not acknowledge an old restore after replacing its API with a new request for the same Play', async () => {
  const oldApi = client(), newApi = client()
  let finishOld!: (play: SavedPlay) => void
  let finishNew!: (play: SavedPlay) => void
  vi.mocked(oldApi.play).mockImplementation(() => new Promise(resolve => { finishOld = resolve }))
  vi.mocked(newApi.play).mockImplementation(() => new Promise(resolve => { finishNew = resolve }))
  const oldHandled = vi.fn(), newHandled = vi.fn(), onSession = vi.fn()
  const page = (api: WorkspaceApi, onRequestedPlayHandled: (opened?: boolean) => void) => <NotificationProvider>
    <PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api}
      session={sessionFor(second)} onSession={onSession} active={false}
      requestedPlayId={first.summary.id} requestedPlayRestore onRequestedPlayHandled={onRequestedPlayHandled} />
  </NotificationProvider>
  const view = render(page(oldApi, oldHandled))
  await waitFor(() => expect(oldApi.play).toHaveBeenCalledOnce())
  view.rerender(page(newApi, newHandled))
  await waitFor(() => expect(newApi.play).toHaveBeenCalledOnce())
  await act(async () => finishOld(first))
  expect(oldHandled).not.toHaveBeenCalled()
  expect(newHandled).not.toHaveBeenCalled()
  await act(async () => finishNew(first))
  expect(newHandled).toHaveBeenCalledWith(true, undefined)
})
