import { StrictMode, useEffect, type RefObject } from 'react'
import { flushSync } from 'react-dom'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { PageLoadFallback } from '@/components/PageErrorBoundary'
import { createEvidence } from '@/features/plays/evidence'
import { emptyManualDraft } from '@/features/plays/manual'
import { createPlaysSession, draftFromSaved, savedState, type PlaysSession } from '@/features/plays/saved'
import { accountFixture, emptyOverview, playApiStubs, savedPlayFixture } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { ApplicationShell } from './ApplicationShell'
import { credentialsFixture, shellIdentityFixture } from '@/test/identity-fixture'

type PlaysProps = { session: PlaysSession; onSession: (session: PlaysSession) => void; workRef: RefObject<symbol | null> }
let plays: PlaysProps
const faults = { review: '', mounts: 0, unmounts: 0 }
vi.mock('@/features/plays/PlaysPage', () => ({ PlaysPage: function TestPlays(props: PlaysProps) {
  useEffect(() => { plays = props })
  useEffect(() => { faults.mounts++; return () => { faults.unmounts++ } }, [])
  return <p>Test Play editor: {props.session.draft.title || 'blank'}</p>
} }))
vi.mock('@/features/review/ReviewPage', () => ({ ReviewPage: () => {
  if (faults.review === 'render') throw new Error('Synthetic render failure')
  return faults.review === 'module' ? <PageLoadFallback /> : <p>Healthy review</p>
} }))

const client = (overrides: Partial<WorkspaceApi> = {}) => ({
  ...createWorkspaceApi(credentialsFixture), ...playApiStubs(),
  overview: vi.fn().mockResolvedValue(emptyOverview), ...overrides,
})
function unload() {
  const event = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(event)
  return event.defaultPrevented
}
function update(session: PlaysSession) { act(() => plays.onSession(session)) }
function cleanSaved(withEvidence = false): PlaysSession {
  const item = { ...createEvidence('upload', new Blob(['synthetic image']), 'test.png'),
    serverId: 'saved-image', savedState: { note: '', markup: null } }
  const draft = draftFromSaved(savedPlayFixture, withEvidence ? [item] : [])
  return { view: 'editor', draft, saved: savedState(savedPlayFixture, draft) }
}
async function mount(api = client(), disconnect = vi.fn()) {
  const view = render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={api} disconnect={disconnect} />, { onCaughtError: () => {} })
  await screen.findByText('Test Play editor: blank')
  // The mock publishes its props in a passive effect, which can run a tick after its text appears.
  await waitFor(() => expect(plays).toBeDefined())
  return { ...view, api, disconnect, user: userEvent.setup() }
}
/** Sign out from the account menu or from Settings' Profile & sign-in tab, which must already be open. */
async function signOut(user: ReturnType<typeof userEvent.setup>, entry: 'owner' | 'settings' = 'owner') {
  if (entry === 'settings') { await user.click(screen.getByRole('button', { name: 'Sign out' })); return }
  await user.click(screen.getByRole('button', { name: /^Sam Rivera, account menu/ }))
  await user.click(await screen.findByRole('menuitem', { name: 'Sign out' }))
}
async function openProfile(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('link', { name: 'Settings' }))
  await user.click(await screen.findByRole('tab', { name: 'Profile & sign-in' }))
}

beforeEach(() => {
  plays = undefined as unknown as PlaysProps
  window.history.replaceState(null, '', '/#plays')
  Object.assign(faults, { review: '', mounts: 0, unmounts: 0 })
})
afterEach(() => vi.restoreAllMocks())

describe('shell-owned Play exit protection', () => {
  it('allows blank drafts and unchanged saved Plays, including stored evidence and an empty manual form', async () => {
    const { disconnect, user } = await mount()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    expect(unload()).toBe(false)
    update({ ...createPlaysSession(), view: 'editor', manual: emptyManualDraft() })
    expect(unload()).toBe(false)
    update(cleanSaved(true))
    expect(unload()).toBe(false)
    await signOut(user)
    expect(confirm).not.toHaveBeenCalled()
    expect(disconnect).toHaveBeenCalledOnce()
    expect(unload()).toBe(false)
  })

  const edits: Array<[string, (session: PlaysSession) => PlaysSession]> = [
    ['plan', session => ({ ...session, draft: { ...session.draft, leverage: '3' } })],
    ...(['thesis', 'invalidation', 'strategy', 'evidence', 'review'] as const).map(key =>
      [`journal ${key}`, (session: PlaysSession) => ({ ...session,
        draft: { ...session.draft, notes: { ...session.draft.notes, [key]: 'Unsaved journal' } } })] as [string, (session: PlaysSession) => PlaysSession]),
    ['evidence addition', session => ({ ...session, draft: { ...session.draft,
      evidence: [...session.draft.evidence, createEvidence('capture', new Blob(['synthetic image']), 'new.png')] } })],
    ['manual quantity', session => ({ ...session, manual: { ...emptyManualDraft(), quantity: '0.0000000000000000001' } })],
    ['manual correction', session => ({ ...session, manual: { ...emptyManualDraft(), executionId: 'execution-id' } })],
    ['structured review', session => ({ ...session, draft: { ...session.draft, structuredReview: {
      adherence: null, mistakeTag: null, lesson: 'Unsaved lesson', nextAction: '',
    } } })],
  ]
  it.each(edits.flatMap(([name, edit]) => ([false, true] as const).map(saved => [name, saved, edit] as const)))(
    'warns for %s in a saved=%s session and clears when those changes are discarded', async (_name, saved, edit) => {
      await mount()
      const baseline = saved ? cleanSaved() : createPlaysSession()
      const changed = edit(baseline)
      update(changed)
      expect(unload()).toBe(true)
      expect(plays.session).toBe(changed)
      update(baseline)
      expect(unload()).toBe(false)
    })

  it.each(['removal', 'note', 'markup', 'markup removal'] as const)('warns for saved evidence %s without touching the original', async kind => {
    await mount()
    const baseline = cleanSaved(true)
    const item = baseline.draft.evidence[0]!
    const markup = { width: 100, height: 100, shapes: [{ id: 'mark', kind: 'text' as const,
      color: '#ffffff', size: 12, at: { x: 1, y: 1 }, text: 'Unsaved mark' }] }
    if (kind === 'markup removal') {
      item.markup = markup
      item.savedState = { note: '', markup }
    }
    update(baseline)
    expect(unload()).toBe(false)
    const evidence = kind === 'removal' ? [] : [{ ...item, ...(kind === 'note' ? { note: 'Changed note' } :
      { markup: kind === 'markup removal' ? null : markup }) }]
    update({ ...baseline, draft: { ...baseline.draft, evidence } })
    expect(unload()).toBe(true)
    expect(baseline.draft.evidence[0]!.image).toBe(item.image)
    update(baseline)
    expect(unload()).toBe(false)
  })

  it.each(['save', 'status', 'open', 'delete', 'manual'])('reads a %s guard acquired from a clean saved session without a shell render', async kind => {
    await mount()
    update(cleanSaved())
    const before = plays
    expect(unload()).toBe(false)
    before.workRef.current = Symbol(kind)
    expect(plays).toBe(before)
    expect(unload()).toBe(true)
    before.workRef.current = null
    expect(unload()).toBe(false)
  })

  it('reads a session update synchronously before the shell commits its next render', async () => {
    await mount()
    act(() => {
      plays.onSession({ ...plays.session, manual: { ...emptyManualDraft(), price: '123.0000000000000000001' } })
      expect(unload()).toBe(true)
    })
  })

  it('still warns for edits after request completion or failure, until they are saved or discarded', async () => {
    await mount()
    const baseline = cleanSaved()
    update(baseline)
    plays.workRef.current = Symbol('save')
    update({ ...baseline, draft: { ...baseline.draft, title: 'Edited during saving' } })
    plays.workRef.current = null
    expect(unload()).toBe(true)
    // A failed save leaves the baseline unchanged. A successful next save advances it.
    const draft = plays.session.draft
    update({ ...plays.session, saved: savedState(savedPlayFixture, draft) })
    expect(unload()).toBe(false)
  })

  it.each(['owner', 'settings'] as const)('cancel and confirm from %s preserve or discard the same session and pending work', async entry => {
    const { user, disconnect } = await mount()
    update(cleanSaved())
    const guard = plays.workRef
    const session = plays.session
    guard.current = Symbol('save')
    if (entry === 'settings') await openProfile(user)
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await signOut(user, entry)
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Already-sent changes may still complete.'))
    expect(disconnect).not.toHaveBeenCalled()
    expect(plays.session).toBe(session)
    expect(guard.current).not.toBeNull()
    expect(faults.unmounts).toBe(0)
    expect(unload()).toBe(true)
    confirm.mockReturnValue(true)
    await signOut(user, entry)
    expect(disconnect).toHaveBeenCalledOnce()
    expect(faults.unmounts).toBe(1)
    expect(screen.queryByRole('main')).not.toBeInTheDocument()
    expect(unload()).toBe(false)
  })

  it.each(['owner', 'settings'] as const)('cancel from %s does not abort or stop an active sequential account refresh', async entry => {
    const first = { ...accountFixture, venueId: 'hyperliquid', name: 'First' }
    const second = { ...first, id: 'second', name: 'Second' }
    let finish!: () => void
    const sync = vi.fn<WorkspaceApi['sync']>(() => new Promise<typeof accountFixture>(resolve => {
      finish = () => resolve(accountFixture)
    }))
    const api = client({ overview: vi.fn().mockResolvedValue({ ...emptyOverview, accounts: [first, second] }), sync })
    const { user, disconnect } = await mount(api)
    const session = { ...createPlaysSession(), draft: { ...createPlaysSession().draft, title: 'Unsaved idea' } }
    update(session)
    await user.click(screen.getByRole('link', { name: 'Accounts' }))
    await user.click(await screen.findByRole('button', { name: 'Refresh all' }))
    await waitFor(() => expect(sync).toHaveBeenCalledOnce())
    const signal = sync.mock.calls[0]![1]!
    if (entry === 'settings') await openProfile(user)
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await signOut(user, entry)
    expect(confirm).toHaveBeenCalledWith('Sign out and discard unsaved Play work?')
    expect(signal.aborted).toBe(false)
    expect(plays.session).toBe(session)
    expect(disconnect).not.toHaveBeenCalled()
    await act(async () => finish())
    await waitFor(() => expect(sync).toHaveBeenCalledTimes(2))
    expect(sync.mock.calls[1]![0]).toBe('second')
    expect(sync.mock.calls[1]![1]).toBe(signal)
    confirm.mockReturnValue(true)
    await signOut(user, entry)
    expect(signal.aborted).toBe(true)
    expect(disconnect).toHaveBeenCalledOnce()
    await act(async () => finish())
    expect(api.overview).toHaveBeenCalledOnce()
    expect(unload()).toBe(false)
  })

  it.each(['', 'render', 'module'])('keeps the guard and healthy hidden Plays owner across navigation and %s failures', async fault => {
    const added = vi.spyOn(window, 'addEventListener')
    const { user } = await mount()
    update({ ...createPlaysSession(), manual: { ...emptyManualDraft(), reason: 'Unsaved correction' } })
    const guard = plays.workRef
    faults.review = fault
    await user.click(screen.getByRole('link', { name: 'Review' }))
    await screen.findByText(fault === 'render' ? 'This page could not be displayed.' :
      fault === 'module' ? 'This page could not be loaded.' : 'Healthy review')
    expect(unload()).toBe(true)
    expect(faults.unmounts).toBe(0)
    if (fault) {
      expect(screen.getByRole('button', { name: 'Reload browser' })).toBeEnabled()
      expect(screen.queryByRole('button', { name: 'Try again' }) !== null).toBe(fault === 'render')
      // jsdom cannot navigate. Simulate the native unload event from the existing location.reload call.
      const nativeWindow = window
      const reload = vi.fn(() => {
        const event = new Event('beforeunload', { cancelable: true })
        nativeWindow.dispatchEvent(event)
        expect(event.defaultPrevented).toBe(true)
      })
      vi.stubGlobal('window', { location: { reload } })
      fireEvent.click(screen.getByRole('button', { name: 'Reload browser' }))
      vi.stubGlobal('window', nativeWindow)
      expect(reload).toHaveBeenCalledOnce()
    }
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    expect(plays.workRef).toBe(guard)
    expect(faults.mounts).toBe(1)
    expect(added.mock.calls.filter(([name]) => name === 'beforeunload')).toHaveLength(1)
    update(createPlaysSession())
    guard.current = Symbol('pending while hidden')
    await user.click(screen.getByRole('link', { name: 'Accounts' }))
    expect(unload()).toBe(true)
    guard.current = null
    expect(unload()).toBe(false)
  })

  it('cleans up by API identity on replacement and unmount, ignoring stale handlers, session callbacks and work', async () => {
    const added = vi.spyOn(window, 'addEventListener')
    const removed = vi.spyOn(window, 'removeEventListener')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const { rerender, unmount, disconnect } = await mount()
    const old = plays
    const oldHandler = added.mock.calls.find(([name]) => name === 'beforeunload')![1] as EventListener
    update({ ...old.session, draft: { ...old.session.draft, title: 'Old private draft' } })
    old.workRef.current = Symbol('pending save')
    expect(unload()).toBe(true)
    const replacement = client()
    rerender(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={replacement} disconnect={disconnect} />)
    await waitFor(() => expect(plays.workRef).not.toBe(old.workRef))
    expect(plays.session.draft.title).toBe('')
    expect(unload()).toBe(false)
    expect(removed).toHaveBeenCalledWith('beforeunload', oldHandler)
    act(() => old.onSession({ ...old.session, draft: { ...old.session.draft, title: 'Late old completion' } }))
    expect(plays.session.draft.title).toBe('')
    update({ ...plays.session, draft: { ...plays.session.draft, title: 'New private draft' } })
    const event = new Event('beforeunload', { cancelable: true })
    oldHandler(event)
    expect(event.defaultPrevented).toBe(false)
    expect(unload()).toBe(true)
    unmount()
    expect(unload()).toBe(false)
    expect(disconnect).not.toHaveBeenCalled()
    const currentHandler = added.mock.calls.filter(([name]) => name === 'beforeunload').at(-1)![1] as EventListener
    currentHandler(event)
    expect(event.defaultPrevented).toBe(false)
    expect(removed).toHaveBeenCalledWith('beforeunload', currentHandler)
    expect(confirm).not.toHaveBeenCalled()
  })

  it('does not let a stale confirmation disconnect a forcibly replaced API session', async () => {
    const { rerender, user, disconnect } = await mount()
    update({ ...plays.session, manual: { ...emptyManualDraft(), source: 'Unsaved source' } })
    const replacement = client()
    vi.spyOn(window, 'confirm').mockImplementation(() => {
      flushSync(() => rerender(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={replacement} disconnect={disconnect} />))
      return true
    })
    await signOut(user)
    expect(disconnect).not.toHaveBeenCalled()
    expect(plays.session.draft.title).toBe('')
    expect(plays.session.manual).toBeUndefined()
    expect(unload()).toBe(false)
    expect(screen.getByRole('main')).toBeInTheDocument()
  })

  it('does not leak listeners across StrictMode effect cleanup', async () => {
    const added = vi.spyOn(window, 'addEventListener')
    const removed = vi.spyOn(window, 'removeEventListener')
    const view = render(<StrictMode><ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={client()} disconnect={vi.fn()} /></StrictMode>)
    await screen.findByText('Test Play editor: blank')
    update({ ...plays.session, manual: { ...emptyManualDraft(), source: 'Unsaved source' } })
    expect(unload()).toBe(true)
    view.unmount()
    expect(unload()).toBe(false)
    const listeners = added.mock.calls.filter(([name]) => name === 'beforeunload')
    expect(listeners).toHaveLength(2)
    for (const [, listener] of listeners) expect(removed).toHaveBeenCalledWith('beforeunload', listener)
  })
})
