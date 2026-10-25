import { useEffect, useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import type { PlaysSession } from '@/features/plays/saved'
import { emptyOverview, playApiStubs } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { ApplicationShell } from './ApplicationShell'
import { credentialsFixture, shellIdentityFixture } from '@/test/identity-fixture'

const faults = vi.hoisted(() => ({ review: false, plays: false, mounts: 0, unmounts: 0 }))
vi.mock('@/features/review/ReviewPage', () => ({ ReviewPage: () => {
  if (faults.review) throw new Error('private credential')
  return <p>Healthy review</p>
} }))
vi.mock('@/features/plays/PlaysPage', () => ({ PlaysPage: function TestPlays({ session, onSession, active }: {
  session: PlaysSession; onSession: (session: PlaysSession) => void; active: boolean
}) {
  const [local, setLocal] = useState(0)
  useEffect(() => { faults.mounts++; return () => { faults.unmounts++ } }, [])
  if (faults.plays) throw new Error('private credential')
  return <div hidden={!active}><label>Draft title<input value={session.draft.title}
    onChange={event => onSession({ ...session, draft: { ...session.draft, title: event.target.value } })} /></label>
    <button onClick={() => setLocal(n => n + 1)}>Local owner {local}</button></div>
} }))

const client = () => ({ ...createWorkspaceApi(credentialsFixture), ...playApiStubs(), overview: vi.fn().mockResolvedValue(emptyOverview) })
beforeEach(() => {
  window.history.replaceState(null, '', '/#plays')
  Object.assign(faults, { review: false, plays: false, mounts: 0, unmounts: 0 })
})

describe('shell page isolation', () => {
  it('preserves shell, draft and mounted Plays owner when another page throws, and recovers on ordinary navigation', async () => {
    const user = userEvent.setup()
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={client()} disconnect={vi.fn()} />, { onCaughtError: () => {} })
    await user.type(await screen.findByRole('textbox', { name: 'Draft title' }), 'My retained idea')
    await user.click(screen.getByRole('button', { name: 'Local owner 0' }))
    faults.review = true
    await user.click(screen.getByRole('link', { name: 'Review' }))
    await screen.findByText('This page could not be displayed.')
    expect(screen.getByRole('link', { name: 'Accounts' })).toBeVisible()
    expect(screen.queryByText('private credential')).not.toBeInTheDocument()
    expect(faults.unmounts).toBe(0)
    await user.click(screen.getByRole('link', { name: 'Plays' }))
    expect(screen.getByRole('textbox', { name: 'Draft title' })).toHaveValue('My retained idea')
    expect(screen.getByRole('button', { name: 'Local owner 1' })).toBeInTheDocument()
    faults.review = false
    await user.click(screen.getByRole('link', { name: 'Review' }))
    expect(await screen.findByText('Healthy review')).toBeInTheDocument()
    expect(faults.mounts).toBe(1)
  })

  it('retains the shell-owned draft after a Plays crash and retry, but resets it on API replacement', async () => {
    const user = userEvent.setup(), api = client()
    const view = render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={api} disconnect={vi.fn()} />, { onCaughtError: () => {} })
    await user.type(await screen.findByRole('textbox', { name: 'Draft title' }), 'Kept outside the page')
    faults.plays = true
    view.rerender(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={api} disconnect={vi.fn()} />)
    expect(await screen.findByText('This page could not be displayed.')).toBeInTheDocument()
    faults.plays = false
    await user.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('textbox', { name: 'Draft title' })).toHaveValue('Kept outside the page')
    view.rerender(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={client()} disconnect={vi.fn()} />)
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Draft title' })).toHaveValue(''))
  })

  it('focuses content without changing the route when the skip link is activated', async () => {
    render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={client()} disconnect={vi.fn()} />)
    const user = userEvent.setup()
    await user.tab()
    const skip = screen.getByRole('link', { name: 'Skip to content' })
    expect(skip).toHaveFocus()
    await user.keyboard('{Enter}')
    expect(screen.getByRole('main')).toHaveFocus()
    expect(window.location.hash).toBe('#plays')
  })
})
