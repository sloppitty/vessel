import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { ActivityOperation } from '@/api/activity'
import type { WorkspaceApi } from '@/api/workspace'
import { accountFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { operationFixture } from '@/test/activity-fixture'
import { OperationFills } from './OperationFills'

describe('explicit paper Activity execution rows', () => {
  it('renders typed simulation facts separately from actual venue fills and flags omitted rows', async () => {
    const operation: ActivityOperation = { ...operationFixture, id: savedPlayFixture.summary.id, tradingMode: 'paper', executionSource: 'paper', fillCount: 0, paperExecutionCount: 2 }
    const api = { activityFills: vi.fn().mockResolvedValue({ operationId: operation.id, tradingMode: 'paper', fills: [], omittedFillCount: 0,
      fundingPayments: [], omittedFundingPaymentCount: 0, omittedPaperExecutionCount: 1, paperExecutions: [
        { id: '12345678-1111-2222-3333-444444444444', entryId: 'd-entry', levelId: 'd-stop', role: 'stop', price: '99.000000000000000000001', quantity: '0.25',
          pnlUsd: '-0.25', occurredAtUtc: '2026-10-20T12:01:00Z', candleOpenAtUtc: '2026-10-20T12:00:00Z', candleCloseAtUtc: '2026-10-20T12:01:00Z' },
      ] }) } as unknown as WorkspaceApi
    render(<OperationFills api={api} operation={operation} accounts={[accountFixture]} />)
    expect(await screen.findByText(/Simulated executions at defined levels/)).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Simulated P&L' })).toBeInTheDocument()
    expect(screen.getByText('stop')).toBeInTheDocument()
    expect(screen.getByTitle('99.000000000000000000001')).toBeInTheDocument()
    expect(screen.getByText(/1 simulated execution rows omitted/)).toBeInTheDocument()
    expect(screen.queryByText(/Execution rows were omitted by the response limit/)).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Venue' })).not.toBeInTheDocument()
  })
})
