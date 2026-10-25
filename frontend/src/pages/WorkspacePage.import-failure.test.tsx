import { render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { sessionFixture } from '@/test/identity-fixture'
import { systemFixture } from '@/test/system-fixture'
import { WorkspacePage } from './WorkspacePage'

vi.mock('@/features/workspace/ApplicationShell', () => {
  throw new Error('Transient import failure with private details')
})
afterEach(() => vi.unstubAllGlobals())

it('offers a reload, not a retry, when the workspace code cannot be loaded', async () => {
  vi.stubGlobal('BroadcastChannel', undefined)
  vi.stubGlobal('fetch', vi.fn().mockImplementation(async (path: string) => new Response(JSON.stringify(
    path.startsWith('/api/auth/config') ? { registration: 'closed', originAccepted: true }
      : path === '/api/auth/session' ? sessionFixture : systemFixture))))
  render(<WorkspacePage />)

  expect(await screen.findByRole('alert')).toHaveTextContent('The workspace could not be loaded.')
  expect(screen.queryByText(/private details/)).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Reload browser' })).toBeEnabled()
})
