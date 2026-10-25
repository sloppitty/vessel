import { useState } from 'react'
import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import type { SavedPlay } from '@/api/plays'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import { accountFixture, playApiStubs, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { PlaysPage } from './PlaysPage'
import { draftFromSaved, savedState, type PlaysSession } from './saved'
import { emptyManualDraft } from './manual'
import { credentialsFixture } from '@/test/identity-fixture'

const first: SavedPlay = { ...savedPlayFixture, summary: { ...savedPlayFixture.summary, status: 'open', planRevision: 1 } }
const second = { ...first, summary: { ...first.summary, id: '33333333-4444-5555-6666-777777777777', title: 'Second saved Play' } }
function sessionFor(play: SavedPlay): PlaysSession {
  const draft = draftFromSaved(play, [])
  return { view: 'editor', draft, saved: savedState(play, draft) }
}
function client(): WorkspaceApi {
  return { ...createWorkspaceApi(credentialsFixture), ...playApiStubs(),
    play: vi.fn(id => Promise.resolve(id === second.summary.id ? second : first)) }
}
function Harness({ api, initial, onSession }: {
  api: WorkspaceApi; initial: PlaysSession; onSession: (session: PlaysSession) => void
}) {
  const [session, setSession] = useState(initial)
  const [requested, setRequested] = useState<string | null>(null)
  return <NotificationProvider>
    <button onClick={() => setRequested(first.summary.id)}>Restore first</button>
    <button onClick={() => setRequested(second.summary.id)}>Restore second</button>
    <button onClick={() => {
      const next = { ...session, manual: { ...emptyManualDraft(), price: '123' } }
      setSession(next); onSession(next)
    }}>Intervening manual edit</button>
    <button onClick={() => {
      const next = { ...session, log: { playId: second.summary.id, text: 'New log edit', state: '' as const } }
      setSession(next); onSession(next)
    }}>Intervening log edit</button>
    <PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api}
      session={session} onSession={next => { setSession(next); onSession(next) }}
      active={false} requestedPlayId={requested} requestedPlayRestore
      onRequestedPlayHandled={() => setRequested(null)} />
  </NotificationProvider>
}

describe('restored Play drafts', () => {
  it('keeps dirty work when a pending restore is superseded by returning to the current saved Play', async () => {
    const api = client()
    let finish!: (play: SavedPlay) => void
    vi.mocked(api.play).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    let current: PlaysSession = sessionFor(second)
    current = { ...current, draft: { ...current.draft, title: 'Second with unsaved edits' } }
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Harness api={api} initial={current} onSession={next => { current = next }} />)
    fireEvent.click(screen.getByText('Restore first'))
    await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
    fireEvent.click(screen.getByText('Restore second'))
    await act(async () => finish(first))
    await waitFor(() => expect(current.saved?.summary.id).toBe(second.summary.id))
    expect(current.draft.title).toBe('Second with unsaved edits')
  })

  it('does not resurrect a log entry after explicitly confirming its discard', async () => {
    const api = client()
    const originalLog = { playId: second.summary.id, text: 'Discard this entry', state: 'fomo' as const }
    let current: PlaysSession = { ...sessionFor(second), log: originalLog }
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Harness api={api} initial={current} onSession={next => { current = next }} />)
    fireEvent.click(screen.getByText('Restore first'))
    await waitFor(() => expect(current.saved?.summary.id).toBe(first.summary.id))
    fireEvent.click(screen.getByText('Restore second'))
    await waitFor(() => expect(current.saved?.summary.id).toBe(second.summary.id))
    expect(window.confirm).toHaveBeenCalledExactlyOnceWith('Discard unsaved Play work and open this Play?')
    expect(current.log?.text ?? '').toBe('')
  })

  it.each(['manual', 'log'] as const)('preserves an intervening %s draft while a hidden restore loads', async kind => {
    const api = client()
    let finish!: (play: SavedPlay) => void
    vi.mocked(api.play).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    let current = sessionFor(second)
    render(<Harness api={api} initial={current} onSession={next => { current = next }} />)
    fireEvent.click(screen.getByText('Restore first'))
    await waitFor(() => expect(api.play).toHaveBeenCalledOnce())
    fireEvent.click(screen.getByText(kind === 'manual' ? 'Intervening manual edit' : 'Intervening log edit'))
    await act(async () => finish(first))
    expect(current.saved?.summary.id).toBe(second.summary.id)
    if (kind === 'manual') expect(current.manual?.price).toBe('123')
    else expect(current.log?.text).toBe('New log edit')
  })
})
