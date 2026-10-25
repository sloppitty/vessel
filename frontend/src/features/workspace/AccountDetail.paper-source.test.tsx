import { screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import { render } from '@/test/render'
import { accountFixture } from '@/test/workspace-fixture'
import { AccountDetail } from './AccountDetail'

vi.mock('./ActivityOperations', () => ({ ActivityOperations: () => null }))

// Literal October 24 E2E acceptance: no standalone "Paper" in account detail.
// Currently fails on the retained cash source pill, not the Enabled status.
it('explains simulated cash without a standalone Paper marker', () => {
  render(<AccountDetail account={{ ...accountFixture, venueId: 'hyperliquid', tradingMode: 'paper', syncStatus: 'paper' }}
    api={{ tradingMode: 'paper' } as WorkspaceApi}
    refreshing={false} onSync={vi.fn()} onBack={vi.fn()} onManage={vi.fn()} />)
  expect(screen.getByText('Enabled')).toBeVisible()
  expect(screen.queryByText(/^Paper$/)).not.toBeInTheDocument()
})
