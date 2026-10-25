import { act, cleanup, fireEvent, render as testingRender, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { ApplicationShell } from '@/features/workspace/ApplicationShell'
import { emptyOverview } from '@/test/workspace-fixture'
import { systemFixture } from '@/test/system-fixture'
import type { WorkspaceApi } from '@/api/workspace'
import { ChartIconButton, ChartToolbar } from './ChartToolbar'
import { shellIdentityFixture } from '@/test/identity-fixture'

function Controls() {
  return <ChartToolbar label="Test chart tools">
    <ChartIconButton label="First tool" description="First description" icon={null} />
    <ChartIconButton label="Second tool" icon={null} />
    <ChartIconButton label="Disabled tool" disabled disabledReason="Needs market data" description="Normal description" icon={null} />
  </ChartToolbar>
}

// Exercise the shell's provider without starting chart, sizing or venue reads.
vi.mock('@/features/plays/PlaysPage', () => ({ PlaysPage: () => <Controls /> }))

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  window.history.replaceState(null, '', '/')
})

async function advance(ms: number) {
  await act(() => vi.advanceTimersByTimeAsync(ms))
}

describe.each(['standalone render helper', 'application shell'])('chart tooltips in the %s', mode => {
  async function mount() {
    if (mode === 'standalone render helper') render(<Controls />)
    else {
      window.history.replaceState(null, '', '/#plays')
      const api = { overview: vi.fn().mockResolvedValue(emptyOverview) } as unknown as WorkspaceApi
      testingRender(<ApplicationShell identity={shellIdentityFixture()} system={systemFixture} api={api} disconnect={vi.fn()} />)
    }
    await act(async () => {})
  }
  const trigger = (name: string) => screen.getByRole('button', { name }).parentElement!

  it('waits 250 ms initially, shares the 400 ms skip window, then restores the delay', async () => {
    vi.useFakeTimers()
    await mount()
    const first = trigger('First tool')
    const second = trigger('Second tool')
    fireEvent.pointerMove(first)
    await advance(249)
    expect(first).toHaveAttribute('data-state', 'closed')
    await advance(1)
    expect(first).toHaveAttribute('data-state', 'delayed-open')
    expect(screen.getByRole('tooltip')).toHaveTextContent('First description')

    fireEvent.keyDown(document, { key: 'Escape' })
    fireEvent.pointerLeave(first)
    await advance(399)
    fireEvent.pointerMove(second)
    expect(second).toHaveAttribute('data-state', 'instant-open')

    fireEvent.keyDown(document, { key: 'Escape' })
    fireEvent.pointerLeave(second)
    await advance(400)
    fireEvent.pointerMove(first)
    expect(first).toHaveAttribute('data-state', 'closed')
    await advance(250)
    expect(first).toHaveAttribute('data-state', 'delayed-open')
  })

  it('keeps keyboard access and explains a disabled action on its wrapper', async () => {
    vi.useFakeTimers()
    await mount()
    const first = screen.getByRole('button', { name: 'First tool' })
    fireEvent.focus(first)
    expect(screen.getByRole('tooltip')).toHaveTextContent('First description')
    fireEvent.blur(first)
    const disabled = screen.getByRole('button', { name: 'Disabled tool' })
    expect(disabled).toBeDisabled()
    fireEvent.pointerMove(disabled.parentElement!)
    expect(screen.getByRole('tooltip')).toHaveTextContent('Needs market data')
    expect(screen.getByRole('tooltip')).not.toHaveTextContent('Normal description')
  })
})
