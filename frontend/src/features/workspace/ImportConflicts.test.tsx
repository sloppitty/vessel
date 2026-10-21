import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import type { FillConflicts } from '@/api/workspace'
import { ImportConflicts } from './ImportConflicts'

const conflicts: FillConflicts = {
  total: 1, notice: 'The venue later reported these fills with different values.',
  recent: [{ contractId: 'BTC', sourceFillId: 'fill-1', fields: ['price', 'closedPnl'], firstSeenAtUtc: '2026-10-21T10:00:00Z',
    lastSeenAtUtc: '2026-10-21T11:00:00Z', reports: 2 }],
}

describe('Import conflicts (Phase 2, item 14c)', () => {
  it('lists the fields that differed, never values, and shows nothing without conflicts', async () => {
    const importConflicts = vi.fn().mockResolvedValue(conflicts)
    const view = render(<ImportConflicts api={{ importConflicts }} accountId="a" reloadKey="1" />)
    expect(await screen.findByRole('heading', { name: 'Import conflicts' })).toBeVisible()
    expect(screen.getByText('fill-1')).toBeVisible()
    expect(screen.getByText('price, closed P&L')).toBeVisible()
    expect(screen.getByText('1 fill')).toBeVisible()
    importConflicts.mockResolvedValue({ total: 0, recent: [], notice: conflicts.notice })
    view.rerender(<ImportConflicts api={{ importConflicts }} accountId="a" reloadKey="2" />)
    await vi.waitFor(() => expect(screen.queryByRole('heading', { name: 'Import conflicts' })).toBeNull())
    expect(importConflicts).toHaveBeenCalledTimes(2)
  })
})
