import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { createWorkspaceApi, type MarketStreamEvent, type WorkspaceApi } from '@/api/workspace'
import { accountFixture, candleSeriesFixture, marketContextFixture } from '@/test/workspace-fixture'
import { ChartPanel } from '@/features/plays/PlayChart'
import { createEntry } from '@/features/plays/draft'
import { CandleChart } from './CandleChart'
import { FrameEdit } from './frameEdit'
import { testFrames } from '@/test/testFrames'
import type { ChartAdapterFactory, ChartCallbacks } from './types'

vi.mock('./IndicatorControls', async importOriginal => ({
  ...await importOriginal<typeof import('./IndicatorControls')>(),
  IndicatorOverlay: vi.fn(() => null),
}))
import { IndicatorOverlay } from './IndicatorControls'

function fakeAdapter() {
  let callbacks: ChartCallbacks
  const destroy = vi.fn()
  const factory: ChartAdapterFactory = (_container, next) => {
    callbacks = next
    return { setCandles: vi.fn(), setOverlays: vi.fn(), setDrawings: vi.fn(), setDrawingTool: vi.fn(), setPricePicker: vi.fn(),
      setIndicators: vi.fn(), setMarkers: vi.fn(), setFocus: vi.fn(), capture: vi.fn().mockResolvedValue(null), destroy }
  }
  return { factory, destroy, callbacks: () => callbacks }
}
function chartApi() {
  let emit: (event: MarketStreamEvent) => void
  const marketStream = vi.fn<WorkspaceApi['marketStream']>((_id, _query, signal, handler) => {
    emit = handler
    return new Promise<void>(resolve => signal.addEventListener('abort', () => resolve(), { once: true }))
  })
  const api = { ...createWorkspaceApi('test-only'), candles: vi.fn().mockResolvedValue(candleSeriesFixture),
    marketContext: vi.fn().mockResolvedValue(marketContextFixture), marketStream }
  return { api, marketStream, emit: (event: MarketStreamEvent) => emit(event) }
}

describe('memoized candle subtree', () => {
  it('does not render for market-context, update-time or stream-status-only changes', async () => {
    localStorage.clear()
    const data = chartApi(); const adapter = fakeAdapter()
    const entry = { ...createEntry(0), price: '101' }
    render(<ChartPanel entries={[entry]} selectedId={entry.id} instrument="BTC" onSelect={vi.fn()}
      source={{ api: data.api, accountId: accountFixture.id }} createAdapter={adapter.factory} />)
    await waitFor(() => expect(data.marketStream).toHaveBeenCalledTimes(1))
    const renders = vi.mocked(IndicatorOverlay).mock.calls.length
    expect(renders).toBeGreaterThan(0)
    act(() => data.emit({ type: 'context', context: { ...marketContextFixture, markPrice: '105.25', observedAt: '2026-10-11T08:00:08Z' } }))
    expect(screen.getByText('105.25')).toBeInTheDocument()
    expect(IndicatorOverlay).toHaveBeenCalledTimes(renders)
    act(() => data.emit({ type: 'status', status: { state: 'stale', observedAt: '2026-10-11T08:00:09Z' } }))
    expect(IndicatorOverlay).toHaveBeenCalledTimes(renders)
  })

  it('updates changed callbacks rather than hiding them behind a custom comparator, and destroys on unmount', () => {
    const adapter = fakeAdapter(); const before = vi.fn(); const after = vi.fn()
    const props = { candles: [], overlays: [], viewKey: 'BTC|1h', label: 'Chart', createAdapter: adapter.factory }
    const view = render(<CandleChart {...props} onLevelSelect={before} />)
    view.rerender(<CandleChart {...props} onLevelSelect={after} />)
    act(() => adapter.callbacks().onLevelSelect('entry'))
    expect(before).not.toHaveBeenCalled(); expect(after).toHaveBeenCalledExactlyOnceWith('entry')
    view.unmount(); expect(adapter.destroy).toHaveBeenCalledTimes(1)
  })

  it('records one undo step for a frame-batched level gesture, including an early release', async () => {
    localStorage.clear()
    const frames = testFrames(); const data = chartApi(); const adapter = fakeAdapter()
    const entry = { ...createEntry(0), price: '101' }
    const onChange = vi.fn()
    render(<ChartPanel entries={[entry]} selectedId={entry.id} instrument="BTC" onSelect={vi.fn()} onEntriesChange={onChange}
      source={{ api: data.api, accountId: accountFixture.id }} createAdapter={adapter.factory} />)
    await waitFor(() => expect(data.marketStream).toHaveBeenCalledTimes(1))
    const id = `${entry.id}|entry`
    const edit = new FrameEdit<number>((price, phase) => adapter.callbacks().onLevelDrag(id, price, phase))
    act(() => { edit.move(102); frames.run() })
    act(() => { edit.move(103); frames.run() })
    act(() => { edit.move(104); edit.end(105); frames.run() })
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ price: '105' })])
    const undo = screen.getByRole('button', { name: /Undo: Move/ })
    fireEvent.click(undo)
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ price: '101' })])
    expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: /Redo: Move/ }))
    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ price: '105' })])
  })
})

describe('fill markers (phase 1, 1.1b)', () => {
  it('shows the fill under the pointer as a tooltip and lists every fill for assistive technology', () => {
    const { factory, callbacks } = fakeAdapter()
    const markers = [{ id: 'f1', time: 1_790_000_000_000, price: 100.4, side: 'buy' as const, tooltip: 'Entry fill · Buy 0.5 at 100.4', span: 3_600_000 },
      { id: 'f2', time: 1_790_003_600_000, price: 110, side: 'sell' as const, tooltip: 'Exit fill · Sell 0.5 at 110', span: 3_600_000 }]
    render(<CandleChart candles={[]} overlays={[]} viewKey="BTC|1h" label="BTC" markers={markers} createAdapter={factory} />)
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Fills on the chart' })).toHaveTextContent('Entry fill · Buy 0.5 at 100.4Exit fill · Sell 0.5 at 110')
    act(() => callbacks().onMarkerHover!('f2', { x: 120, y: 40 }))
    expect(screen.getByRole('tooltip')).toHaveTextContent('Exit fill · Sell 0.5 at 110')
    act(() => callbacks().onMarkerHover!(null, null))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })
})
