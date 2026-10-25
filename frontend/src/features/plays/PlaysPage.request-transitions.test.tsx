import { useCallback, useState } from 'react'
import { act, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, playApiStubs, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { PlaysPage } from './PlaysPage'
import { draftFromSaved, savedState, type PlaysSession } from './saved'
import { credentialsFixture } from '@/test/identity-fixture'

const a: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'open', planRevision: 1 } }
const b: SavedPlay = { ...a, summary: { ...a.summary, id: '33333333-4444-5555-6666-777777777777', title: 'Play B' } }
const c: SavedPlay = { ...a, summary: { ...a.summary, id: '44444444-5555-6666-7777-888888888888', title: 'Play C' } }
type Request = { id: string; restore: boolean } | null
const sessionFor = (play: SavedPlay): PlaysSession => {
  const draft = draftFromSaved(play, [])
  return { view: 'editor', draft, saved: savedState(play, draft) }
}
const client = (): WorkspaceApi => ({ ...createWorkspaceApi(credentialsFixture), ...playApiStubs(), play: vi.fn().mockResolvedValue(a) })
function Harness({ api, request, initial, onSession, onHandled }: {
  api: WorkspaceApi; request: Request; initial: PlaysSession
  onSession: (session: PlaysSession) => void; onHandled: (opened?: boolean) => void
}) {
  const [session, setSession] = useState(initial)
  const accept = useCallback((next: PlaysSession) => { setSession(next); onSession(next) }, [onSession])
  return <NotificationProvider><PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api}
    session={session} onSession={accept} requestedPlayId={request?.id ?? null}
    requestedPlayRestore={request?.restore ?? false} onRequestedPlayHandled={onHandled} /></NotificationProvider>
}
function mount(api: WorkspaceApi, initial: PlaysSession, initialRequest: Request) {
  const changed = vi.fn(), handled = vi.fn()
  const page = (request: Request, selectedApi = api) => <Harness api={selectedApi} initial={initial} request={request} onSession={changed} onHandled={handled} />
  const view = render(page(initialRequest))
  return { changed, handled, request: (request: Request, selectedApi = api) => view.rerender(page(request, selectedApi)) }
}

it('invalidates a restore changed directly to a non-restore for the same ID', async () => {
  const api = client()
  let finish!: (play: SavedPlay) => void
  vi.mocked(api.play).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  const initial = sessionFor(b)
  initial.draft = { ...initial.draft, title: 'Keep B unsaved' }
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  const owner = mount(api, initial, { id: a.summary.id, restore: true })
  await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
  owner.request({ id: a.summary.id, restore: false })
  await act(async () => finish(a))
  expect(owner.changed).not.toHaveBeenCalled()
  expect(owner.handled).not.toHaveBeenCalled()
  expect(window.confirm).toHaveBeenCalledOnce()
})

it('does not reuse the load or discard authorization across A to B to A', async () => {
  const api = client()
  let finish!: (play: SavedPlay) => void
  vi.mocked(api.play).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  const initial = sessionFor(c)
  initial.draft = { ...initial.draft, title: 'Keep C unsaved' }
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  const owner = mount(api, initial, { id: a.summary.id, restore: true })
  await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
  owner.request({ id: b.summary.id, restore: true })
  owner.request({ id: a.summary.id, restore: true })
  await act(async () => finish(a))
  await waitFor(() => expect(api.play).toHaveBeenCalledTimes(2))
  expect(window.confirm).toHaveBeenCalledTimes(2)
  expect(owner.changed).toHaveBeenCalledOnce()
  expect(owner.changed.mock.calls[0]![0].saved?.summary.id).toBe(a.summary.id)
  expect(owner.handled).toHaveBeenCalledExactlyOnceWith(true, undefined)
})

it('acknowledges an already-retained saved Play without discarding its draft', async () => {
  const api = client()
  const initial = sessionFor(b)
  initial.view = 'list'
  initial.draft = { ...initial.draft, title: 'Keep B unsaved' }
  vi.spyOn(window, 'confirm').mockReturnValue(false)
  const owner = mount(api, initial, { id: b.summary.id, restore: true })
  await waitFor(() => expect(owner.handled).toHaveBeenCalledOnce())
  expect(owner.changed.mock.calls[0]![0]).toMatchObject({ view: 'editor', draft: { title: 'Keep B unsaved' } })
  expect(api.play).not.toHaveBeenCalled()
  expect(window.confirm).not.toHaveBeenCalled()
})

it('does not apply or acknowledge a restore cleared while loading', async () => {
  const api = client()
  let finish!: (play: SavedPlay) => void
  vi.mocked(api.play).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  const owner = mount(api, sessionFor(b), { id: a.summary.id, restore: true })
  await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
  owner.request(null)
  await act(async () => finish(a))
  expect(owner.changed).not.toHaveBeenCalled()
  expect(owner.handled).not.toHaveBeenCalled()
})

it('starts the new generation after the API reset clears busy for a different requested Play', async () => {
  const oldApi = client(), newApi = client()
  let finishOld!: (play: SavedPlay) => void
  vi.mocked(oldApi.play).mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
  vi.mocked(newApi.play).mockResolvedValue(c)
  const owner = mount(oldApi, sessionFor(b), { id: a.summary.id, restore: true })
  await waitFor(() => expect(oldApi.play).toHaveBeenCalledOnce())
  owner.request({ id: c.summary.id, restore: true }, newApi)
  await waitFor(() => expect(newApi.play).toHaveBeenCalledOnce())
  await act(async () => finishOld(a))
  expect(owner.changed).toHaveBeenCalledOnce()
  expect(owner.changed.mock.calls[0]![0].saved?.summary.id).toBe(c.summary.id)
  expect(owner.handled).toHaveBeenCalledExactlyOnceWith(true, undefined)
})
