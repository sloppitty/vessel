import { screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { accountFixture } from '@/test/workspace-fixture'
import type { WorkspaceApi } from '@/api/workspace'
import type { AccountMonitor } from '@/api/monitor'
import { coverageFixture, monitorFixture } from '@/test/monitor-fixture'
import { AccountDetail } from './AccountDetail'

vi.mock('./ActivityOperations', () => ({ ActivityOperations: () => null }))

// The snapshot read stays pending: the value caption comes from the account record and venue descriptor.
const api = { snapshot: vi.fn(() => new Promise(() => {})) } as unknown as WorkspaceApi
const detail = (venueId: string, syncStatus: 'synced' | 'manual') => render(<AccountDetail account={{ ...accountFixture, venueId, syncStatus, sourceId: venueId === 'manual' ? null : '7' }}
  api={api} refreshing={false} onSync={vi.fn()} onBack={vi.fn()} onManage={vi.fn()} />)

describe('Account detail monitor heading (Phase 0, 0.4c)', () => {
  const show = (monitor: AccountMonitor) => render(<AccountDetail account={{ ...accountFixture, venueId: 'hyperliquid', syncStatus: 'synced', sourceId: '7' }}
    api={api} refreshing={false} onSync={vi.fn()} onBack={vi.fn()} onManage={vi.fn()} monitor={monitor} />)
  const panel = () => screen.getByRole('region', { name: 'Background monitor' })
  it('never says Monitored while an Open Play on the account has no recorded stop coverage', () => {
    // After the migration, or once reconciliation opens a Play, until the next check records its coverage.
    const view = show({ ...monitorFixture, coverage: [], uncheckedPlays: 2 })
    expect(within(panel()).queryByText('Monitored')).not.toBeInTheDocument()
    expect(within(panel()).getByText('Stop coverage not checked yet')).toBeInTheDocument()
    expect(within(panel()).getByText('2 Open Plays\' stop coverage has not been checked yet.')).toBeInTheDocument()
    view.unmount()
    // Once a check recorded it, or from an older API that does not say, the run state heads the panel.
    show({ ...monitorFixture, coverage: [coverageFixture], uncheckedPlays: 0 }).unmount()
    show(monitorFixture)
    expect(within(panel()).getByText('Monitored')).toBeInTheDocument()
  })
})

describe('Account detail value unit', () => {
  it('states the venue quote stablecoin for a venue-reported value', () => {
    detail('lighter-rh', 'synced')
    expect(screen.getByText('Account value · reported')).toBeInTheDocument()
    // The caption is a short source pill named by its visible text, with the full description as its description.
    expect(screen.getByRole('button', { name: 'USDG' })).toHaveTextContent(/^USDG$/)
    expect(screen.getByRole('button', { name: 'USDG' })).toHaveAccessibleDescription('Perpetuals equity in USDG · nominal')
    expect(screen.getByText('$1,250.12')).toBeInTheDocument()
  })
  it('keeps manual values as entered', () => {
    detail('manual', 'manual')
    expect(screen.getByText('Known manual value')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'manual' })).toHaveAccessibleDescription('Entered manually')
  })
})
