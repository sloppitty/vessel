import { render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkspaceApi } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { operationFixture } from '@/test/activity-fixture'
import { OperationsTable } from './OperationsTable'

const api = { activityFills: vi.fn() } as unknown as WorkspaceApi
const closed = { ...operationFixture, status: 'closed' as const, closedAtUtc: '2026-10-12T11:00:00Z', totalExitedQuantity: '2', openQuantity: '0',
  averageExitPrice: '101' }
const feeReason = 'A fill\'s fee is unverified or not in a listed USD stablecoin.'

describe('Activity net result coverage (#119)', () => {
  it('marks an unknown net result with a coverage pill on the row, not only a hover title', () => {
    render(<OperationsTable operations={[{ ...closed, netResultUsd: null, coverage: [feeReason] }]} accounts={[accountFixture]} api={api} />)
    const net = document.querySelector('td.operation-net')!
    expect(net).toHaveTextContent('—')
    const pill = within(net as HTMLElement).getByRole('button', { name: 'unavailable Net result coverage' })
    expect(pill).toHaveAccessibleDescription(expect.stringContaining(feeReason))
  })
  it('marks a known net result whose coverage is incomplete as partial', () => {
    render(<OperationsTable operations={[{ ...closed, netResultUsd: '4', coverage: ['Peak quantity is unknown.'] }]} accounts={[accountFixture]} api={api} compact />)
    expect(within(document.querySelector('td.operation-net') as HTMLElement).getByRole('button', { name: 'partial Net result coverage' }))
      .toHaveAccessibleDescription(expect.stringContaining('Peak quantity is unknown.'))
  })
  it('shows no pill when the result is known and coverage complete', () => {
    render(<OperationsTable operations={[{ ...closed, netResultUsd: '4' }]} accounts={[accountFixture]} api={api} />)
    expect(screen.queryByRole('button', { name: /coverage/ })).not.toBeInTheDocument()
  })
})
