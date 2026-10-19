import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { isActivityOperation, isActivityPage } from '@/api/activity'
import type { WorkspaceApi } from '@/api/workspace'
import { accountFixture } from '@/test/workspace-fixture'
import { activityPage, operationFixture } from '@/test/activity-fixture'
import { OperationsTable } from './OperationsTable'

const api = { activityFills: vi.fn() } as unknown as WorkspaceApi

describe('operation leverage', () => {
  it('shows observed leverage beside the direction, a range when it changed and unknown when never observed', () => {
    const operations = [
      { ...operationFixture, leverage: 10, leverageMin: 10, leverageMax: 10 },
      { ...operationFixture, id: '22345678-1234-1234-1234-123456789abc:long', contractId: 'ETH', leverage: 5, leverageMin: 3, leverageMax: 10 },
      { ...operationFixture, id: '32345678-1234-1234-1234-123456789abc:long', contractId: 'SOL' },
      { ...operationFixture, id: '42345678-1234-1234-1234-123456789abc:long', contractId: 'DOGE', leverage: null, leverageMin: 5, leverageMax: 10 },
    ]
    render(<OperationsTable operations={operations} accounts={[accountFixture]} api={api} compact />)
    const fixed = screen.getByTitle('Venue-reported leverage: 10×.')
    expect(fixed).toHaveTextContent('Leverage 10×')
    expect(fixed).not.toHaveAttribute('data-changed')
    const changed = screen.getByTitle(/changed between 3× and 10×/)
    expect(changed).toHaveTextContent('Leverage 3–10×, latest 5×')
    expect(changed).toHaveAttribute('data-changed', 'true')
    expect(changed.getAttribute('title')).toContain('latest 5×')
    const conflicting = screen.getByTitle(/changed between 5× and 10×/)
    expect(conflicting).toHaveTextContent('Leverage 5–10×, latest unknown')
    expect(conflicting.getAttribute('title')).toContain('latest unknown: the latest observation reported conflicting values')
    const unknown = screen.getByText('Leverage unknown').closest('.operation-leverage')!
    expect(unknown).toHaveClass('unknown')
    expect(unknown.getAttribute('title')).toContain('never inferred')
  })

  it('accepts null or whole-number leverage within its range only', () => {
    expect(isActivityOperation(operationFixture)).toBe(true)
    expect(isActivityOperation({ ...operationFixture, leverage: 5, leverageMin: 3, leverageMax: 10 })).toBe(true)
    expect(isActivityOperation({ ...operationFixture, leverage: 5, leverageMin: 6, leverageMax: 10 })).toBe(false)
    expect(isActivityOperation({ ...operationFixture, leverage: 2.5, leverageMin: 2.5, leverageMax: 2.5 })).toBe(false)
    expect(isActivityOperation({ ...operationFixture, leverage: 0, leverageMin: 0, leverageMax: 0 })).toBe(false)
    expect(isActivityOperation({ ...operationFixture, leverage: null, leverageMin: 3, leverageMax: null })).toBe(false)
    // A conflicting latest is unknown inside a real range only.
    expect(isActivityOperation({ ...operationFixture, leverage: null, leverageMin: 3, leverageMax: 10 })).toBe(true)
    expect(isActivityOperation({ ...operationFixture, leverage: null, leverageMin: 3, leverageMax: 3 })).toBe(false)
    expect(isActivityOperation({ ...operationFixture, leverage: null, leverageMin: 10, leverageMax: 3 })).toBe(false)
    const missing: Record<string, unknown> = { ...operationFixture }
    delete missing.leverage
    expect(isActivityOperation(missing)).toBe(false)
    expect(isActivityPage(activityPage([{ ...operationFixture, leverage: 1000, leverageMin: 1, leverageMax: 1000 }]))).toBe(true)
  })
})
