import { render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { sessionFixture } from '@/test/identity-fixture'
import { systemFixture } from '@/test/system-fixture'
import { WorkspacePage } from './WorkspacePage'

const shell = vi.hoisted(() => {
  let release!: () => void
  const ready = new Promise<void>(resolve => { release = resolve })
  return { loads: 0, ready, release }
})
vi.mock('@/features/workspace/ApplicationShell', async () => {
  shell.loads++
  await shell.ready
  return { ApplicationShell: ({ system, identity }: { system: { owner: { displayName: string } }; identity: { session: { user: { displayName: string } } } }) =>
    <p>Shell for {identity.session.user.displayName} · {system.owner.displayName}</p> }
})

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
function serve(session: () => Response) {
  // No cross-tab channel in this test: sign-in state stays in this tab.
  vi.stubGlobal('BroadcastChannel', undefined)
  const fetch = vi.fn().mockImplementation(async (path: string) =>
    path.startsWith('/api/auth/config') ? json({ registration: 'closed', originAccepted: true })
      : path === '/api/auth/session' ? session()
        : path === '/api/system' ? json(systemFixture) : new Response(null, { status: 404 }))
  vi.stubGlobal('fetch', fetch)
  return fetch
}
afterEach(() => vi.unstubAllGlobals())

it('shows wallet sign-in when there is no session, while the workspace downloads', async () => {
  const fetch = serve(() => json({ code: 'session-required' }, 401))
  render(<WorkspacePage />)
  expect(await screen.findByRole('heading', { name: 'Sign in to Vessel' })).toBeVisible()
  await vi.waitFor(() => expect(shell.loads).toBe(1))
  expect(fetch.mock.calls.map(([path]) => path)).toEqual([`/api/auth/config?${new URLSearchParams({ origin: window.location.origin })}`, '/api/auth/session'])
})

it('opens the workspace for a live session once the workspace code is ready', async () => {
  serve(() => json(sessionFixture))
  render(<WorkspacePage />)
  expect(await screen.findByText('Loading your workspace…')).toHaveAttribute('role', 'status')

  shell.release()
  expect(await screen.findByText(`Shell for ${sessionFixture.user.displayName} · ${systemFixture.owner.displayName}`)).toBeVisible()
  expect(screen.queryByText('Loading your workspace…')).not.toBeInTheDocument()
  expect(shell.loads).toBe(1)
})
