import { useEffect, useState } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { createWorkspaceApi } from '@/api/workspace'
import type { PlaysSession } from '@/features/plays/saved'
import { emptyOverview, playApiStubs } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import { ApplicationShell } from './ApplicationShell'
import { credentialsFixture, shellIdentityFixture } from '@/test/identity-fixture'

const attempts = vi.hoisted(() => ({ review: 0, settings: 0, plays: 0, mounts: 0, unmounts: 0 }))
vi.mock('@/features/review/ReviewPage', () => {
  attempts.review++
  throw new Error('Transient import failure with private details')
})
vi.mock('./SettingsPage', () => {
  attempts.settings++
  throw new Error('Private module evaluation error')
})
vi.mock('@/features/plays/PlaysPage', () => {
  attempts.plays++
  return { PlaysPage: function TestPlays({ session, onSession }: {
    session: PlaysSession; onSession: (session: PlaysSession) => void
  }) {
    const [local, setLocal] = useState(0)
    useEffect(() => { attempts.mounts++; return () => { attempts.unmounts++ } }, [])
    return <><label>Draft title<input value={session.draft.title}
      onChange={event => onSession({ ...session, draft: { ...session.draft, title: event.target.value } })} /></label>
      <button onClick={() => setLocal(value => value + 1)}>Local owner {local}</button></>
  } }
})

it('shows honest import failure guidance without replacing healthy hidden Plays or losing its draft', async () => {
  window.history.replaceState(null, '', '/#plays')
  const api = { ...createWorkspaceApi(credentialsFixture), ...playApiStubs(), overview: vi.fn().mockResolvedValue(emptyOverview) }
  const user = userEvent.setup()
  render(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={api} disconnect={vi.fn()} />, { onCaughtError: () => {} })
  await user.type(await screen.findByRole('textbox', { name: 'Draft title' }), 'Unsaved idea survives import failures')
  await user.click(screen.getByRole('button', { name: 'Local owner 0' }))

  await user.click(screen.getByRole('link', { name: 'Review' }))
  await screen.findByText('This page could not be loaded.')
  expect(attempts.review).toBe(1)
  expect(screen.queryByText(/private details/)).not.toBeInTheDocument()
  expect(attempts.unmounts).toBe(0)
  expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
  expect(screen.getByRole('alert')).toHaveTextContent('Return to Plays to save any unsaved work before reloading.')
  expect(screen.getByRole('button', { name: 'Reload browser' })).toBeEnabled()

  await user.click(screen.getByRole('link', { name: 'Plays' }))
  expect(screen.getByRole('textbox', { name: 'Draft title' })).toHaveValue('Unsaved idea survives import failures')
  expect(screen.getByRole('button', { name: 'Local owner 1' })).toBeVisible()
  await user.click(screen.getByRole('link', { name: 'Settings' }))
  await screen.findByText('This page could not be loaded.')
  expect(attempts.settings).toBe(1)
  await user.click(screen.getByRole('link', { name: 'Overview' }))
  await screen.findByText('Start with your accounts.')
  expect(attempts.settings).toBe(1)
  await user.click(screen.getByRole('link', { name: 'Settings' }))
  expect(await screen.findByText('This page could not be loaded.')).toBeVisible()
  expect(screen.queryByText('Private module evaluation error')).not.toBeInTheDocument()
  expect(attempts.settings).toBe(1)

  await user.click(screen.getByRole('link', { name: 'Review' }))
  expect(await screen.findByText('This page could not be loaded.')).toBeVisible()
  expect(attempts.review).toBe(1)
  await user.click(screen.getByRole('link', { name: 'Plays' }))
  expect(screen.getByRole('textbox', { name: 'Draft title' })).toHaveValue('Unsaved idea survives import failures')
  expect(screen.getByRole('button', { name: 'Local owner 1' })).toBeVisible()
  expect(attempts.plays).toBe(1)
  expect(attempts.mounts).toBe(1)
  expect(attempts.unmounts).toBe(0)
})
