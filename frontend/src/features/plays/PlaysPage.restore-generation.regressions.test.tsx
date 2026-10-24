import { useState } from 'react'
import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, playApiStubs, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { PlaysPage } from './PlaysPage'
import { draftFromSaved, savedState, type PlaysSession } from './saved'

it('does not treat a fresh non-restore request for A as permission to complete a cancelled restore of A', async () => {
  const first: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'open', planRevision: 1 } }
  const second: SavedPlay = { ...first, summary: { ...first.summary, id: '33333333-4444-5555-6666-777777777777', title: 'Play B' } }
  const stored = draftFromSaved(second, [])
  let current: PlaysSession = { view: 'editor', draft: { ...stored, title: 'Keep B unsaved' }, saved: savedState(second, stored) }
  let finish!: (play: SavedPlay) => void
  const api = { ...createWorkspaceApi('synthetic-only'), ...playApiStubs(),
    play: vi.fn(() => new Promise<SavedPlay>(resolve => { finish = resolve })) }
  function Harness() {
    const [session, setSession] = useState(current)
    const [request, setRequest] = useState<{ id: string; restore: boolean } | null>(null)
    return <NotificationProvider>
      <button onClick={() => setRequest({ id: first.summary.id, restore: true })}>Restore A</button>
      <button onClick={() => setRequest(null)}>Cancel restore</button>
      <button onClick={() => setRequest({ id: first.summary.id, restore: false })}>Request A from Review</button>
      <PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api}
        session={session} onSession={next => { current = next; setSession(next) }}
        requestedPlayId={request?.id ?? null} requestedPlayRestore={request?.restore ?? false}
        onRequestedPlayHandled={() => setRequest(null)} />
    </NotificationProvider>
  }
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  render(<Harness />)
  fireEvent.click(screen.getByText('Restore A'))
  await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
  fireEvent.click(screen.getByText('Cancel restore'))
  fireEvent.click(screen.getByText('Request A from Review'))
  await act(async () => finish(first))
  expect(current.saved?.summary.id).toBe(second.summary.id)
  expect(current.draft.title).toBe('Keep B unsaved')
})

