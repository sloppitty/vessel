import { useState } from 'react'
import { act, fireEvent, screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/errors'
import { createWorkspaceApi, type CandleSeries, type MarketStreamEvent, type WorkspaceApi } from '@/api/workspace'
import type { ChartDrawing, DrawingKind } from '@/components/chart/drawings'
import type { IndicatorView } from '@/components/chart/indicators'
import type { ChartAdapterFactory, ChartCallbacks, ChartCandle, ChartFocus, ChartMarker, PriceOverlay } from '@/components/chart/types'
import { accountFixture, candleSeriesFixture, idleMarketStream, marketContextFixture } from '@/test/workspace-fixture'
import { createEntry, createExit, type DraftEntry } from './draft'
import { ChartPanel } from './PlayChart'
import type { ChartFill } from './chart-fills'
import { plainDecimalRule } from './numbers'
import { credentialsFixture } from '@/test/identity-fixture'

function fakeAdapter() {
  const state = {
    candles: [] as readonly ChartCandle[], resets: [] as boolean[], overlays: [] as readonly PriceOverlay[], callbacks: null as ChartCallbacks | null,
    created: 0, destroyed: 0, drawings: [] as readonly ChartDrawing[], selectedDrawingId: null as string | null, tool: null as DrawingKind | null, magnet: false,
    captions: [] as string[], picking: false, indicators: null as IndicatorView | null,
    markers: [] as readonly ChartMarker[], focus: null as ChartFocus | null,
  }
  const factory: ChartAdapterFactory = (_container, callbacks) => {
    state.created++
    state.callbacks = callbacks
    return {
      setCandles(candles, reset) { state.candles = candles; state.resets.push(reset) },
      setOverlays(overlays) { state.overlays = overlays },
      setDrawings(drawings, selectedId) { state.drawings = drawings; state.selectedDrawingId = selectedId },
      setDrawingTool(tool, magnet) { state.tool = tool; state.magnet = magnet },
      setPricePicker(active) { state.picking = active },
      setIndicators(view) { state.indicators = view },
      setMarkers(markers) { state.markers = markers },
      setFocus(range) { state.focus = range },
      setTimeZone() {},
      capture(caption) { state.captions.push(caption); return Promise.resolve(new Blob(['png'], { type: 'image/png' })) },
      destroy() { state.destroyed++ },
    }
  }
  return { state, factory }
}

function chartApi(candles: WorkspaceApi['candles'], marketContext: WorkspaceApi['marketContext'] = vi.fn().mockResolvedValue(marketContextFixture),
  marketStream: WorkspaceApi['marketStream'] = idleMarketStream) {
  return { ...createWorkspaceApi(credentialsFixture), candles, marketContext, marketStream }
}

beforeEach(() => localStorage.clear())

const entries = (): DraftEntry[] => [
  { ...createEntry(0), price: '101', stops: [{ id: 'stop-1', unit: 'price', value: '99', share: '100' }] },
  { ...createEntry(1), price: '100' },
]

describe('Chart panel', () => {
  it('keeps the placeholder without a market-data source and selects through the legend', async () => {
    const list = entries()
    const onSelect = vi.fn()
    const { rerender } = render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="ETH-PERP" onSelect={onSelect} />)
    const legend = screen.getByRole('group', { name: 'Planned entries' })
    await userEvent.click(within(legend).getByRole('button', { name: /Entry 2/ }))
    expect(onSelect).toHaveBeenCalledExactlyOnceWith(list[1]!.id)
    rerender(<ChartPanel entries={list} selectedId={list[1]!.id} instrument="ETH-PERP" onSelect={onSelect} />)
    expect(within(legend).getByRole('button', { name: /Entry 2/ })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByText('No market data for this instrument')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Expand chart' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Capture chart' })).toBeDisabled()
    expect(screen.getByTestId('chart-panel').querySelector('canvas')).toBeNull()
  })

  it('does not invent an instrument or entries for an empty draft', () => {
    render(<ChartPanel entries={[]} selectedId="" instrument="" onSelect={vi.fn()} />)
    expect(screen.getByText('No perpetual instrument selected')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Planned entries' })).not.toBeInTheDocument()
  })

  it('loads venue candles, plots planned levels and maps level selection to the entry', async () => {
    const list = entries()
    const candles = vi.fn().mockResolvedValue(candleSeriesFixture)
    const { state, factory } = fakeAdapter()
    const onSelect = vi.fn()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" venue="Hyperliquid" onSelect={onSelect}
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    expect(await screen.findByText(/Updated .* UTC/)).toBeInTheDocument()
    expect(candles).toHaveBeenCalledWith(accountFixture.id, { instrument: 'BTC', interval: '1h' }, expect.any(AbortSignal))
    expect(state.candles).toEqual([
      { time: 1_790_000_000_000, open: 100.5, high: 102, low: 99.25, close: 101, volume: 12.5 },
      { time: 1_790_003_600_000, open: 101, high: 103.75, low: 100, close: 103, volume: 9 },
    ])
    expect(state.resets.at(-1)).toBe(true)
    expect(state.overlays.map(o => [o.label, o.draggable, o.emphasis])).toEqual([['E1', true, 'selected'], ['E1 SL', true, 'selected'], ['E2', true, 'normal']])
    expect(screen.getByText(/BTC · Hyperliquid trade candles/)).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'BTC' })).toBeNull()

    act(() => state.callbacks!.onLevelSelect(state.overlays[2]!.id))
    expect(onSelect).toHaveBeenCalledWith(list[1]!.id)
  })

  it('styles a planned stop without a confirmed resting order apart, labelled in the evidence\'s own words (Phase 0, 0.4c)', async () => {
    const list = entries()
    const candles = vi.fn().mockResolvedValue(candleSeriesFixture)
    const { state, factory } = fakeAdapter()
    const props = { entries: list, selectedId: list[0]!.id, instrument: 'BTC', onSelect: vi.fn(), createAdapter: factory,
      source: { api: chartApi(candles), accountId: accountFixture.id } }
    const evidence = vi.fn((entryId: string, levelId: string) =>
      entryId === list[0]!.id && levelId === 'stop-1' ? { text: 'not seen at the venue', confirmed: false } : null)
    const view = render(<ChartPanel {...props} stopEvidence={evidence} />)
    expect(await screen.findByText(/Updated .* UTC/)).toBeInTheDocument()
    // The evidence is asked at the price the line is drawn, so a stop the draft moved is judged where it now stands.
    expect(evidence).toHaveBeenCalledWith(list[0]!.id, 'stop-1', 99)
    const stop = () => state.overlays.find(overlay => overlay.kind === 'stop')!
    expect([stop().label, stop().unconfirmed]).toEqual(['E1 SL · not seen at the venue', true])
    // Entries carry no venue claim of their own here.
    expect(state.overlays.filter(overlay => overlay.kind === 'entry').map(o => [o.label, o.unconfirmed])).toEqual([['E1', undefined], ['E2', undefined]])
    view.rerender(<ChartPanel {...props} stopEvidence={() => ({ text: 'resting as of 10:00', confirmed: true })} />)
    expect([stop().label, stop().unconfirmed]).toEqual(['E1 SL · resting as of 10:00', false])
    // Without evidence (a draft, or a Play that makes no venue claim) the plan keeps its plain tags.
    view.rerender(<ChartPanel {...props} />)
    expect([stop().label, stop().unconfirmed]).toEqual(['E1 SL', undefined])
  })

  it('refreshes on request only for a venue without a live stream, offering its own intervals', async () => {
    const list = entries()
    const candles = vi.fn().mockResolvedValue(candleSeriesFixture)
    const { factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" venue="RISEx" onSelect={vi.fn()} streamable={false}
      intervals={['1m', '5m', '15m', '1h', '4h', '1d', '1w']} source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    expect(await screen.findByText(/Updated .* UTC/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Live updates' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '3 minutes' })).not.toBeInTheDocument()
  })

  it('previews a drag only on the chart, then updates the workspace once on release', async () => {
    const list = entries()
    const onEntriesChange = vi.fn()
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" onSelect={vi.fn()} onEntriesChange={onEntriesChange}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    const stop = state.overlays[1]!.id
    const resets = [...state.resets]
    // Moving close to (and across) entry must not reflow the workspace's Suggestions/PlanNotices.
    for (const price of [100.9, 100.99, 101, 101.01]) {
      act(() => state.callbacks!.onLevelDrag(stop, price, 'move'))
      expect(onEntriesChange).not.toHaveBeenCalled()
      expect(state.overlays.find(overlay => overlay.id === stop)?.price).toBe(price)
    }
    expect(state.created).toBe(1)
    expect(state.resets).toEqual(resets)
    act(() => state.callbacks!.onLevelDrag(stop, 97.123456, 'end'))
    expect(onEntriesChange).toHaveBeenCalledExactlyOnceWith([{ ...list[0], stops: [{ id: 'stop-1', unit: 'price', value: '97.123', share: '100' }] }, list[1]])
  })

  it('discards an interrupted preview without changing the draft or recording undo', async () => {
    const list = entries()
    const onEntriesChange = vi.fn()
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" onSelect={vi.fn()} onEntriesChange={onEntriesChange}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    const stop = state.overlays[1]!.id
    act(() => state.callbacks!.onLevelDrag(stop, 100.99, 'move'))
    act(() => state.callbacks!.onLevelCancel!())
    expect(state.overlays.find(overlay => overlay.id === stop)?.price).toBe(99)
    expect(onEntriesChange).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled()
  })

  it('switches between all entries and one entry from the legend', async () => {
    const list = entries()
    const onSelect = vi.fn()
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" onSelect={onSelect}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    const legend = within(screen.getByRole('group', { name: 'Planned entries' }))
    expect(legend.getByRole('button', { name: 'All entries' })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.click(legend.getByRole('button', { name: /^Entry 1/ }))
    expect(onSelect).toHaveBeenCalledWith(list[0]!.id)
    expect(state.overlays.map(o => o.label)).toEqual(['E1', 'E1 SL'])
    expect(legend.getByRole('button', { name: /^Entry 1/ })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.click(legend.getByRole('button', { name: 'All entries' }))
    expect(state.overlays.map(o => o.label)).toEqual(['E1', 'E1 SL', 'E2'])
  })

  it('reloads on timeframe change and refreshes manually without dropping loaded candles', async () => {
    const newer: CandleSeries = { ...candleSeriesFixture, interval: '4h', candles: [{ ...candleSeriesFixture.candles[1]!, close: '104' }], retrievedAt: '2026-10-01T12:05:00Z' }
    const candles = vi.fn()
      .mockResolvedValueOnce(candleSeriesFixture)
      .mockResolvedValueOnce({ ...candleSeriesFixture, interval: '4h' })
      .mockResolvedValueOnce(newer)
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    await userEvent.click(within(screen.getByRole('group', { name: 'Timeframe' })).getByRole('button', { name: '4 hours' }))
    expect(candles).toHaveBeenLastCalledWith(accountFixture.id, { instrument: 'BTC', interval: '4h' }, expect.any(AbortSignal))
    expect(screen.getByRole('button', { name: '4 hours' })).toHaveAttribute('aria-pressed', 'true')
    await screen.findByText(/Updated/)
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    await vi.waitFor(() => expect(state.candles.at(-1)!.close).toBe(104))
    expect(state.candles).toHaveLength(2)
    expect(state.resets.at(-1)).toBe(false)
  })

  it('loads older candles until the venue history is exhausted', async () => {
    const older: CandleSeries = { ...candleSeriesFixture, candles: [{ ...candleSeriesFixture.candles[0]!, openTime: 1_789_996_400_000, closeTime: 1_789_999_999_999 }] }
    const candles = vi.fn()
      .mockResolvedValueOnce(candleSeriesFixture)
      .mockResolvedValueOnce(older)
      .mockResolvedValueOnce({ ...candleSeriesFixture, candles: [], historyExhausted: true })
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onNeedOlder())
    await vi.waitFor(() => expect(state.candles).toHaveLength(3))
    // Other refreshes may interleave on slow runners; the older window request is what matters.
    expect(candles).toHaveBeenCalledWith(accountFixture.id, { instrument: 'BTC', interval: '1h', endTime: 1_789_999_999_999 }, expect.any(AbortSignal))
    act(() => state.callbacks!.onNeedOlder())
    expect(await screen.findByText(/Start of available venue history/)).toBeInTheDocument()
    act(() => state.callbacks!.onNeedOlder())
    expect(candles).toHaveBeenCalledTimes(3)
  })

  it('shows load failures with a retry and keeps previous candles when a refresh fails', async () => {
    const candles = vi.fn()
      .mockRejectedValueOnce(new ApiError('http', 'Venue candles are unavailable. Try refreshing the chart.', 502))
      .mockResolvedValueOnce(candleSeriesFixture)
      .mockRejectedValueOnce(new ApiError('unavailable', 'The request did not complete. Check the API and try again.'))
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Venue candles are unavailable.')
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await screen.findByText(/Updated/)
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Refresh failed: The request did not complete.')
    expect(state.candles).toHaveLength(2)
  })

  it('shows the candle-refresh wait and keeps loaded candles without retrying', async () => {
    const candles = vi.fn().mockResolvedValueOnce(candleSeriesFixture)
      .mockRejectedValueOnce(new ApiError('http', 'Generic failure.', 429, new Date(Date.now() + 60_000).toISOString()))
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Wait 60 seconds before trying again.')
    expect(state.candles).toHaveLength(2)
    expect(candles).toHaveBeenCalledTimes(2)
  })

  it('opens the same chart in an expanded dialog and restores focus on Escape', async () => {
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    const expand = screen.getByRole('button', { name: 'Expand chart' })
    await userEvent.click(expand)
    const dialog = screen.getByRole('dialog', { name: 'BTC chart' })
    expect(within(dialog).getByTestId('chart-dialog-body')).toBeInTheDocument()
    expect(screen.queryByTestId('chart-body')).toBeNull()
    expect(state.created).toBe(2)
    expect(state.candles).toHaveLength(2)
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('button', { name: 'Expand chart' })).toHaveFocus()
    expect(screen.getByTestId('chart-body')).toBeInTheDocument()
  })
})

describe('Chart panel refresh during older loading', () => {
  it('lets a manual refresh replace a pending older read without blocking later paging', async () => {
    let resolveOlder!: (value: CandleSeries) => void
    const candles = vi.fn()
      .mockResolvedValueOnce(candleSeriesFixture)
      .mockImplementationOnce(() => new Promise<CandleSeries>(resolve => { resolveOlder = resolve }))
      .mockResolvedValueOnce(candleSeriesFixture)
      .mockResolvedValueOnce({ ...candleSeriesFixture, candles: [], historyExhausted: true })
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onNeedOlder())
    expect(await screen.findByText('Loading older candles…')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    await screen.findByText(/Updated/)
    resolveOlder(candleSeriesFixture)
    act(() => state.callbacks!.onNeedOlder())
    await vi.waitFor(() => expect(candles).toHaveBeenCalledTimes(4))
  })
})

describe('Chart panel market header and timeframes', () => {
  const render4 = (marketContext?: WorkspaceApi['marketContext']) => {
    const { factory } = fakeAdapter()
    return render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture), marketContext), accountId: accountFixture.id }} createAdapter={factory} />)
  }

  it('shows exact venue statistics with a derived 24h change', async () => {
    render4()
    const stats = screen.getByLabelText('BTC market statistics')
    await vi.waitFor(() => expect(stats).toHaveTextContent('Mark103.5'))
    expect(stats).toHaveTextContent('Oracle103.4')
    expect(stats).toHaveTextContent('+3.5 / +3.50%')
    expect(stats).toHaveTextContent('42.5 BTC')
    expect(stats).toHaveTextContent('0.0013%')
  })

  it('does not request or show statistics for a venue without market context', async () => {
    const marketContext = vi.fn().mockResolvedValue(marketContextFixture)
    const { factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()} streamable={false} marketContext={false}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture), marketContext), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    await screen.findByText(/Updated/)
    expect(marketContext).not.toHaveBeenCalled()
    expect(screen.queryByLabelText('BTC market statistics')).toBeNull()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('keeps the chart usable when statistics fail', async () => {
    render4(vi.fn().mockRejectedValue(new ApiError('http', 'Venue market statistics are unavailable.', 502)))
    expect(await screen.findByRole('alert')).toHaveTextContent('Venue market statistics are unavailable.')
    expect(await screen.findByText(/Updated/)).toBeInTheDocument()
  })

  it('pins favorite timeframes to the bar and remembers them', async () => {
    const view = render4()
    const bar = screen.getByRole('group', { name: 'Timeframe' })
    expect(within(bar).getAllByRole('button').map(button => button.textContent)).toEqual(['5m', '1h', '4h', 'D', ''])
    await userEvent.click(within(bar).getByRole('button', { name: 'All timeframes' }))
    await userEvent.click(screen.getByRole('button', { name: 'Favorite 15 minutes' }))
    await userEvent.click(screen.getByRole('button', { name: 'Favorite 5 minutes' }))
    await userEvent.click(screen.getByRole('button', { name: '1 week' }))
    expect(within(bar).getAllByRole('button').map(button => button.textContent)).toEqual(['15m', '1h', '4h', 'D', 'W', ''])
    expect(within(bar).getByRole('button', { name: '1 week' })).toHaveAttribute('aria-pressed', 'true')
    view.unmount()
    render4()
    const restored = screen.getByRole('group', { name: 'Timeframe' })
    expect(within(restored).getAllByRole('button').map(button => button.textContent)).toEqual(['15m', '1h', '4h', 'D', 'W', ''])
    expect(within(restored).getByRole('button', { name: '1 week' })).toHaveAttribute('aria-pressed', 'true')
  })
})

describe('Chart panel tools and average entry', () => {
  it('plots and lists the quantity-weighted average entry in the aggregate view', async () => {
    const list = [{ ...createEntry(0), price: '100', share: '60' }, { ...createEntry(1), price: '90', share: '40' }]
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    expect(state.overlays.find(o => o.label === 'AVG')?.price).toBeCloseTo(96)
    expect(screen.getByText('Average entry')).toHaveTextContent('Average entry 96')
    await userEvent.click(screen.getByRole('button', { name: /^Entry 1/ }))
    expect(state.overlays.some(o => o.label === 'AVG')).toBe(false)
    expect(screen.queryByText('Average entry')).toBeNull()
  })
})

describe('Chart panel drawing tools', () => {
  const line = (id: string): ChartDrawing => ({ id, schemaVersion: 1, kind: 'trend-line', points: [{ time: 1, price: 100 }, { time: 2, price: 101 }] })

  function DrawingHarness({ factory, initial = [] }: { factory: ChartAdapterFactory; initial?: ChartDrawing[] }) {
    const [drawings, setDrawings] = useState<ChartDrawing[]>(initial)
    return <ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()} drawings={drawings} onDrawingsChange={setDrawings}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />
  }

  it('records nothing from a chart torn down by an instrument switch mid-drag', async () => {
    const { state, factory: base } = fakeAdapter()
    // Like the real adapter, a chart destroyed mid-drag reports the cancelled gesture through its own callbacks.
    const factory: ChartAdapterFactory = (container, callbacks) => {
      const adapter = base(container, callbacks)
      return { ...adapter, destroy: () => { callbacks.onDrawingCancel?.(); adapter.destroy() } }
    }
    function Switching() {
      const [instrument, setInstrument] = useState('BTC')
      const [lists, setLists] = useState<Record<string, ChartDrawing[]>>({ BTC: [line('a')], ETH: [line('b')] })
      return <>
        <ChartPanel entries={entries()} selectedId="" instrument={instrument} onSelect={vi.fn()} drawings={lists[instrument]!}
          onDrawingsChange={next => setLists(value => ({ ...value, [instrument]: next }))}
          source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />
        <button type="button" onClick={() => setInstrument('ETH')}>switch</button>
      </>
    }
    render(<Switching />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onDrawingChange({ ...line('a'), points: [{ time: 1, price: 120 }, { time: 2, price: 121 }] }, 'move'))
    await userEvent.click(screen.getByRole('button', { name: 'switch' }))
    await screen.findByText(/Updated/)
    expect(state.drawings).toEqual([line('b')])
    expect(screen.getByRole('button', { name: 'Undo' })).toBeInTheDocument()
  })

  it('creates a drawing with the chosen tool, then returns to the crosshair with it selected', async () => {
    const { state, factory } = fakeAdapter()
    render(<DrawingHarness factory={factory} />)
    const rail = await screen.findByRole('group', { name: 'Chart tools' })
    await userEvent.click(within(rail).getByRole('button', { name: 'Trend line' }))
    expect(state.tool).toBe('trend-line')
    expect(screen.getByText(/Drag, or click twice, to draw a trend line/)).toBeInTheDocument()
    act(() => state.callbacks!.onDrawingCreate(line('a')))
    expect(state.drawings).toEqual([line('a')])
    expect(state.selectedDrawingId).toBe('a')
    expect(state.tool).toBeNull()
    expect(within(rail).getByRole('button', { name: 'Crosshair' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('group', { name: 'Selected drawing' })).toHaveTextContent('Trend line')
  })

  it('deletes with the bar or keyboard and undoes each step', async () => {
    const { state, factory } = fakeAdapter()
    render(<DrawingHarness factory={factory} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onDrawingCreate(line('a')))
    act(() => state.callbacks!.onDrawingCreate(line('b')))
    await userEvent.click(screen.getByRole('button', { name: 'Delete drawing' }))
    expect(state.drawings.map(d => d.id)).toEqual(['a'])
    act(() => state.callbacks!.onDrawingSelect('a'))
    const chart = screen.getByRole('application')
    chart.focus()
    await userEvent.keyboard('{Delete}')
    expect(state.drawings).toEqual([])
    await userEvent.keyboard('{Control>}z{/Control}')
    expect(state.drawings.map(d => d.id)).toEqual(['a'])
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(state.drawings.map(d => d.id)).toEqual(['a', 'b'])
  })

  it('records a whole move as one undo step', async () => {
    const { state, factory } = fakeAdapter()
    render(<DrawingHarness factory={factory} initial={[line('a')]} />)
    await screen.findByText(/Updated/)
    const moved = (price: number) => ({ ...line('a'), points: [{ time: 1, price }, { time: 2, price: price + 1 }] })
    act(() => state.callbacks!.onDrawingChange(moved(110), 'move'))
    act(() => state.callbacks!.onDrawingChange(moved(120), 'move'))
    act(() => state.callbacks!.onDrawingChange(moved(130), 'end'))
    expect(state.drawings).toEqual([moved(130)])
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(state.drawings).toEqual([line('a')])
  })

  it('edits note text, clears all drawings undoably and remembers the magnet', async () => {
    const { state, factory } = fakeAdapter()
    const note: ChartDrawing = { id: 'n', schemaVersion: 1, kind: 'text', points: [{ time: 1, price: 100 }], text: 'Note' }
    render(<DrawingHarness factory={factory} initial={[note, line('a')]} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onDrawingSelect('n'))
    const input = screen.getByRole('textbox', { name: 'Note text' })
    await userEvent.clear(input)
    await userEvent.type(input, 'Breakout retest')
    expect(state.drawings[0]).toMatchObject({ text: 'Breakout retest' })
    await userEvent.click(screen.getByRole('button', { name: 'Clear unlocked BTC drawings' }))
    expect(state.drawings).toEqual([])
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(state.drawings).toHaveLength(2)
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(state.drawings[0]).toMatchObject({ text: 'Note' })
    await userEvent.click(screen.getByRole('button', { name: 'Snap to candles' }))
    expect(state.magnet).toBe(true)
    expect(JSON.parse(localStorage.getItem('vessel.chart.preferences.v1')!)).toMatchObject({ magnet: true })
  })

  it('cancels a tool with Escape', async () => {
    const { state, factory } = fakeAdapter()
    render(<DrawingHarness factory={factory} />)
    await userEvent.click(within(await screen.findByRole('group', { name: 'Chart tools' })).getByRole('button', { name: 'Fibonacci retracement' }))
    expect(state.tool).toBe('fibonacci')
    screen.getByRole('application').focus()
    await userEvent.keyboard('{Escape}')
    expect(state.tool).toBeNull()
  })
})

describe('Chart panel drawing editing', () => {
  const line = (id: string, patch: Partial<ChartDrawing> = {}): ChartDrawing => ({ id, schemaVersion: 1, kind: 'trend-line', points: [{ time: 1, price: 100 }, { time: 2, price: 101 }], ...patch })

  function Harness({ factory, initial }: { factory: ChartAdapterFactory; initial: ChartDrawing[] }) {
    const [drawings, setDrawings] = useState<ChartDrawing[]>(initial)
    return <ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()} drawings={drawings} onDrawingsChange={setDrawings}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />
  }

  it('changes color, line style and width as undoable steps', async () => {
    const { state, factory } = fakeAdapter()
    render(<Harness factory={factory} initial={[line('a')]} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onDrawingSelect('a'))
    const bar = screen.getByRole('group', { name: 'Selected drawing' })
    await userEvent.click(within(bar).getByRole('button', { name: /Drawing color/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Color #f5a97f' }))
    await userEvent.click(within(bar).getByRole('button', { name: 'Line style: Solid' }))
    await userEvent.click(screen.getByRole('button', { name: 'Dashed' }))
    await userEvent.click(within(bar).getByRole('button', { name: 'Line width: 1 px' }))
    await userEvent.click(screen.getByRole('button', { name: '3 px' }))
    expect(state.drawings[0]!.style).toEqual({ color: '#f5a97f', line: 'dashed', width: 3 })
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(state.drawings[0]!.style).toEqual({ color: '#f5a97f', line: 'dashed' })
  })

  it('locks a drawing against deletion and clearing until unlocked', async () => {
    const { state, factory } = fakeAdapter()
    render(<Harness factory={factory} initial={[line('a'), line('b')]} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onDrawingSelect('a'))
    await userEvent.click(screen.getByRole('button', { name: 'Lock drawing' }))
    expect(state.drawings[0]!.locked).toBe(true)
    expect(screen.getByRole('button', { name: 'Delete drawing' })).toBeDisabled()
    screen.getByRole('application').focus()
    await userEvent.keyboard('{Delete}')
    expect(state.drawings).toHaveLength(2)
    await userEvent.click(screen.getByRole('button', { name: 'Clear unlocked BTC drawings' }))
    expect(state.drawings.map(d => d.id)).toEqual(['a'])
    expect(screen.getByRole('button', { name: 'Clear unlocked BTC drawings' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Unlock drawing' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete drawing' }))
    expect(state.drawings).toEqual([])
  })
})

describe('Chart panel level editing', () => {
  function LevelHarness({ factory, onSelect = vi.fn(), initial, leverage = 1 }: { factory: ChartAdapterFactory; onSelect?: (id: string) => void; initial: DraftEntry[]; leverage?: number }) {
    const [list, setList] = useState(initial)
    const [selected, setSelected] = useState(initial[0]!.id)
    const [drawings, setDrawings] = useState<ChartDrawing[]>([])
    return <>
      <ChartPanel entries={list} selectedId={selected} onSelect={id => { setSelected(id); onSelect(id) }} instrument="BTC" leverage={leverage} onEntriesChange={setList}
        drawings={drawings} onDrawingsChange={setDrawings}
        source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />
      <output data-testid="entries">{JSON.stringify(list.map(e => ({ price: e.price, stop: e.stops[0], stops: e.stops.map(s => [s.unit, s.value, s.share]), targets: e.targets.map(t => [t.unit, t.value, t.share]), share: e.share })))}</output>
      <button type="button" onClick={() => setList(current => current.map((e, i) => i === 1 ? { ...e, share: '25' } : e))}>side edit</button>
    </>
  }
  const two = (): DraftEntry[] => [
    { ...createEntry(0), price: '100', share: '50', stops: [{ id: 's1', unit: 'percent', value: '5', share: '100' }], targets: [{ ...createExit('100'), id: 't1', value: '110' }] },
    { ...createEntry(1), price: '90', share: '50', stops: [{ id: 's2', unit: 'price', value: '85', share: '100' }], targets: [] },
  ]
  const shown = () => JSON.parse(screen.getByTestId('entries').textContent!) as { price: string; share: string; stop: { unit: string; value: string }; stops: string[][]; targets: string[][] }[]

  it('previews the average on the canvas without reflowing the HTML legend', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} initial={two()} />)
    await screen.findByText(/Updated/)
    const entry = state.overlays.find(overlay => overlay.label === 'E1')!
    const average = screen.getByRole('group', { name: 'Planned entries' }).querySelector('.chart-legend-average strong')!
    act(() => state.callbacks!.onLevelDrag(entry.id, 150, 'move'))
    expect(state.overlays.find(overlay => overlay.label === 'AVG')?.price).toBe(120)
    expect(average).toHaveTextContent('95')
    expect(shown()[0]!.price).toBe('100')
    act(() => state.callbacks!.onLevelDrag(entry.id, 150, 'end'))
    expect(average).toHaveTextContent('120')
    expect(shown()[0]!.price).toBe('150')
  })

  it('drags any entry level, selects that entry, and undoes and redoes the whole move', async () => {
    const { state, factory } = fakeAdapter()
    const onSelect = vi.fn()
    render(<LevelHarness factory={factory} onSelect={onSelect} initial={two()} />)
    await screen.findByText(/Updated/)
    const second = state.overlays.find(o => o.label === 'E2 SL')!
    expect(second.draggable).toBe(true)
    act(() => state.callbacks!.onLevelDrag(second.id, 84, 'move'))
    expect(shown()[1]!.stop.value).toBe('85')
    // A side edit that arrives during the gesture is kept, and is not part of its undo record.
    await userEvent.click(screen.getByRole('button', { name: 'side edit' }))
    act(() => state.callbacks!.onLevelDrag(second.id, 83.5, 'end'))
    expect(shown()[1]!.stop.value).toBe('83.5')
    expect(onSelect).toHaveBeenLastCalledWith(second.id.split('|')[0])
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(shown()[1]).toMatchObject({ share: '25', stop: { value: '85' } })
    await userEvent.click(screen.getByRole('button', { name: /^Redo/ }))
    expect(shown()[1]).toMatchObject({ share: '25', stop: { value: '83.5' } })
    screen.getByRole('application').focus()
    await userEvent.keyboard('{Control>}z{/Control}')
    expect(shown()[1]!.stop.value).toBe('85')
    await userEvent.keyboard('{Control>}{Shift>}z{/Shift}{/Control}')
    expect(shown()[1]!.stop.value).toBe('83.5')
    await userEvent.keyboard('{Control>}z{/Control}{Control>}y{/Control}')
    expect(shown()[1]!.stop.value).toBe('83.5')
  })

  it.each(['stop', 'target'] as const)('switches a dragged percentage %s to price units and restores both fields on undo/redo', async kind => {
    const { state, factory } = fakeAdapter()
    const initial = two()
    initial[0]!.targets[0] = { ...initial[0]!.targets[0]!, unit: 'percent', value: '15' }
    render(<LevelHarness factory={factory} initial={initial} leverage={3} />)
    await screen.findByText(/Updated/)
    const level = state.overlays.find(o => o.label === (kind === 'stop' ? 'E1 SL' : 'E1 TP1'))!
    const price = kind === 'stop' ? 92.5 : 118.125
    const value = kind === 'stop' ? '92.5' : '118.13'
    const selectedExit = () => kind === 'stop' ? shown()[0]!.stops[0] : shown()[0]!.targets[0]
    act(() => state.callbacks!.onLevelDrag(level.id, price, 'move'))
    expect(selectedExit()).toEqual(['percent', kind === 'stop' ? '5' : '15', '100'])
    expect(state.overlays.find(overlay => overlay.id === level.id)?.price).toBe(Number(value))
    act(() => state.callbacks!.onLevelDrag(level.id, price, 'end'))
    expect(selectedExit()).toEqual(['price', value, '100'])
    expect(shown()[1]!.stop).toMatchObject({ unit: 'price', value: '85' })
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(selectedExit()).toEqual(['percent', kind === 'stop' ? '5' : '15', '100'])
    await userEvent.click(screen.getByRole('button', { name: /^Redo/ }))
    expect(selectedExit()).toEqual(['price', value, '100'])
  })

  it('records no undo step for a level drag that ends where it started', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} onSelect={vi.fn()} initial={two()} />)
    await screen.findByText(/Updated/)
    const second = state.overlays.find(o => o.label === 'E2 SL')!
    act(() => state.callbacks!.onLevelDrag(second.id, 84, 'move'))
    act(() => state.callbacks!.onLevelDrag(second.id, 85, 'end'))
    expect(shown()[1]!.stop.value).toBe('85')
    expect(screen.getByRole('button', { name: 'Undo' })).toBeInTheDocument()
  })

  it('discards a level drag cut short by a chart reset without an undo step', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} onSelect={vi.fn()} initial={two()} />)
    await screen.findByText(/Updated/)
    const second = state.overlays.find(o => o.label === 'E2 SL')!
    act(() => state.callbacks!.onLevelDrag(second.id, 84, 'move'))
    act(() => state.callbacks!.onLevelCancel!())
    expect(shown()[1]!.stop.value).toBe('85')
    expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled()
  })

  it('edits a percentage level price on the chart and switches it to price units', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} initial={two()} />)
    await screen.findByText(/Updated/)
    const stop = state.overlays.find(o => o.label === 'E1 SL')!
    act(() => state.callbacks!.onLevelEdit(stop.id, { x: 40, y: 120 }))
    const form = screen.getByRole('form', { name: 'Edit Entry 1 stop' })
    const price = within(form).getByRole('textbox', { name: 'Price' })
    expect(price).toHaveValue('95')
    // The unit switch is explained only once the price is actually edited.
    expect(within(form).queryByText(/Editing this price switches the level from % to price units/)).not.toBeInTheDocument()
    await userEvent.clear(price)
    expect(within(form).getByText(/Editing this price switches the level from % to price units/)).toBeInTheDocument()
    await userEvent.type(price, '92{Enter}')
    expect(shown()[0]!.stop).toMatchObject({ unit: 'price', value: '92' })
    expect(screen.queryByRole('form')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(shown()[0]!.stop).toMatchObject({ unit: 'percent', value: '5' })
    await userEvent.click(screen.getByRole('button', { name: /^Redo/ }))
    expect(shown()[0]!.stop).toMatchObject({ unit: 'price', value: '92' })
  })

  it('keeps percentage units when only the exit share is edited on the chart', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} initial={two()} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'E1 SL')!.id, { x: 40, y: 120 }))
    const form = screen.getByRole('form', { name: 'Edit Entry 1 stop' })
    await userEvent.clear(within(form).getByRole('textbox', { name: /Share/ }))
    await userEvent.type(within(form).getByRole('textbox', { name: /Share/ }), '60')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(shown()[0]!.stops[0]).toEqual(['percent', '5', '60'])
  })

  it('edits target share, adds and removes targets and adds a missing stop from the chart', async () => {
    const { state, factory } = fakeAdapter()
    const initial = two()
    initial[1] = { ...initial[1]!, stops: [{ id: 's2', unit: 'price', value: '', share: '100' }] }
    render(<LevelHarness factory={factory} initial={initial} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'E1 TP1')!.id, { x: 10, y: 10 }))
    let form = screen.getByRole('form', { name: 'Edit Entry 1 target 1' })
    await userEvent.clear(within(form).getByRole('textbox', { name: /Share/ }))
    await userEvent.type(within(form).getByRole('textbox', { name: /Share/ }), '60')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(shown()[0]!.targets).toEqual([['price', '110', '60']])
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'E1 TP1')!.id, { x: 10, y: 10 }))
    await userEvent.click(screen.getByRole('button', { name: 'Add target' }))
    expect(shown()[0]!.targets).toEqual([['price', '110', '60'], ['price', '104', '']])
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'E1 TP1')!.id, { x: 10, y: 10 }))
    await userEvent.click(screen.getByRole('button', { name: 'Remove target' }))
    expect(shown()[0]!.targets).toEqual([['price', '104', '']])
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'E2')!.id, { x: 10, y: 10 }))
    form = screen.getByRole('form', { name: 'Edit Entry 2' })
    await userEvent.click(within(form).getByRole('button', { name: 'Add stop' }))
    expect(shown()[1]!.stop).toMatchObject({ unit: 'price', value: '88.2' })
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(shown()[1]!.stop.value).toBe('')
  })

  it('places the selected entry price, stops and targets by clicking the chart', async () => {
    const { state, factory } = fakeAdapter()
    const initial = two()
    initial[0] = { ...initial[0]!, price: '', stops: [{ id: 's1', unit: 'price', value: '', share: '100' }], targets: [] }
    render(<LevelHarness factory={factory} initial={initial} />)
    await screen.findByText(/Updated/)
    const plan = screen.getByRole('group', { name: 'Plan levels for Entry 1' })
    expect(within(plan).getByRole('button', { name: 'Add a stop to Entry 1 on the chart' })).toBeDisabled()
    await userEvent.click(within(plan).getByRole('button', { name: 'Add an entry on the chart' }))
    expect(state.picking).toBe(true)
    expect(screen.getByText(/Click to set Entry 1 price/)).toBeInTheDocument()
    act(() => state.callbacks!.onPricePick(101.234))
    expect(shown()[0]!.price).toBe('101.23')
    expect(state.picking).toBe(false)

    // The first stop fills the blank one; the next adds another without a share.
    for (const price of [97, 95]) {
      await userEvent.click(within(plan).getByRole('button', { name: 'Add a stop to Entry 1 on the chart' }))
      act(() => state.callbacks!.onPricePick(price))
    }
    expect(shown()[0]!.stops).toEqual([['price', '97', '100'], ['price', '95', '']])
    expect(state.overlays.filter(o => o.kind === 'stop').map(o => o.label)).toEqual(['E1 SL1', 'E1 SL2', 'E2 SL'])
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(shown()[0]!.stops).toEqual([['price', '97', '100']])

    await userEvent.click(within(plan).getByRole('button', { name: 'Add a target to Entry 1 on the chart' }))
    screen.getByRole('application').focus()
    await userEvent.keyboard('{Escape}')
    expect(state.picking).toBe(false)
    expect(shown()[0]!.targets).toEqual([])
  })

  it('disarms the plan picker after a measurement is kept, but not after cancellation', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} initial={two()} />)
    await screen.findByText(/Updated/)
    await userEvent.click(screen.getByRole('button', { name: 'Add a target to Entry 1 on the chart' }))
    expect(state.picking).toBe(true)
    act(() => state.callbacks!.onDrawingCancel?.())
    expect(state.picking).toBe(true)

    const measurement: ChartDrawing = {
      id: 'measurement', schemaVersion: 1, kind: 'price-range',
      points: [{ time: 1_000, price: 100 }, { time: 2_000, price: 110 }],
    }
    const before = shown()
    act(() => state.callbacks!.onDrawingCreate(measurement))
    expect(state.drawings).toEqual([measurement])
    expect(state.selectedDrawingId).toBe(measurement.id)
    expect(state.picking).toBe(false)
    expect(screen.getByRole('button', { name: 'Delete drawing' })).toBeInTheDocument()
    expect(screen.queryByText(/Click to add a target to Entry 1/)).not.toBeInTheDocument()
    act(() => state.callbacks!.onPricePick(550))
    expect(shown()).toEqual(before)
  })

  it('treats an entry price that is not plan text as unpriced, as its chart line does', async () => {
    const { state, factory } = fakeAdapter()
    const initial = two()
    initial[0] = { ...initial[0]!, price: '1e2' }
    render(<LevelHarness factory={factory} initial={initial} />)
    await screen.findByText(/Updated/)
    expect(state.overlays.some(o => o.label === 'E1')).toBe(false)
    const plan = screen.getByRole('group', { name: 'Plan levels for Entry 1' })
    expect(within(plan).getByRole('button', { name: 'Add a stop to Entry 1 on the chart' })).toBeDisabled()
    // The entry tool prices Entry 1 instead of adding a third entry beside an invisible one.
    await userEvent.click(within(plan).getByRole('button', { name: 'Add an entry on the chart' }))
    expect(screen.getByText(/Click to set Entry 1 price/)).toBeInTheDocument()
    act(() => state.callbacks!.onPricePick(101.234))
    expect(shown().map(entry => entry.price)).toEqual(['101.23', '90'])
  })

  it('adds a new entry with the entry tool once the selected entry has a price, and undoes it', async () => {
    const { state, factory } = fakeAdapter()
    const onSelect = vi.fn()
    render(<LevelHarness factory={factory} onSelect={onSelect} initial={two()} />)
    await screen.findByText(/Updated/)
    await userEvent.click(within(screen.getByRole('group', { name: 'Plan levels for Entry 1' })).getByRole('button', { name: 'Add an entry on the chart' }))
    expect(screen.getByText(/Click to add an entry/)).toBeInTheDocument()
    act(() => state.callbacks!.onPricePick(95.555))
    expect(shown().map(entry => entry.price)).toEqual(['100', '90', '95.555'])
    expect(onSelect).toHaveBeenLastCalledWith(expect.any(String))
    expect(state.overlays.some(o => o.label === 'E3')).toBe(true)
    await userEvent.click(screen.getByRole('button', { name: /^Undo: Add Entry 3/ }))
    expect(shown()).toHaveLength(2)
  })

  it('shows one entry without the aggregate view, legend or entry prefixes', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} initial={[two()[0]!]} />)
    await screen.findByText(/Updated/)
    expect(screen.queryByRole('button', { name: 'All entries' })).toBeNull()
    expect(screen.queryByRole('group', { name: 'Planned entries' })).toBeNull()
    expect(state.overlays.map(o => o.label)).toEqual(['Entry', 'SL', 'TP1'])
    expect(screen.getByRole('group', { name: 'Plan levels for Entry 1' })).toBeInTheDocument()
  })

  it('rejects invalid prices and closes with Escape', async () => {
    const { state, factory } = fakeAdapter()
    render(<LevelHarness factory={factory} initial={two()} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'E1')!.id, { x: 10, y: 10 }))
    const price = screen.getByRole('textbox', { name: 'Price' })
    await userEvent.clear(price)
    await userEvent.type(price, '-5{Enter}')
    expect(screen.getByRole('alert')).toHaveTextContent(`Entry 1 price: ${plainDecimalRule}`)
    expect(shown()[0]!.price).toBe('100')
    await userEvent.clear(price)
    await userEvent.type(price, '0{Enter}')
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a price above zero.')
    expect(shown()[0]!.price).toBe('100')
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('form')).toBeNull()
  })
})

describe('Chart panel live updates', () => {
  interface Stream { signal: AbortSignal; emit: (event: MarketStreamEvent) => void; end: () => void; fail: (error?: ApiError) => void }
  function streams() {
    const opened: Stream[] = []
    const marketStream = vi.fn<WorkspaceApi['marketStream']>((_id, _query, signal, onEvent) => new Promise<void>((resolve, reject) => {
      opened.push({ signal, emit: event => act(() => onEvent(event)), end: resolve, fail: error => reject(error ?? new ApiError('unavailable', 'Interrupted.')) })
      signal.addEventListener('abort', () => resolve(), { once: true })
    }))
    return { opened, marketStream, last: () => opened.at(-1)! }
  }
  const flush = () => act(() => vi.advanceTimersByTimeAsync(0))
  const advance = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms))
  const toggle = () => screen.getByRole('button', { name: 'Live updates' })
  const live = { type: 'status', status: { state: 'live', observedAt: '2026-10-02T08:00:00Z' } } as const
  let visibility: DocumentVisibilityState = 'visible'

  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-02T08:00:00Z'))
    visibility = 'visible'
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility })
  })
  afterEach(() => {
    vi.useRealTimers()
    Reflect.deleteProperty(document, 'visibilityState')
  })

  async function setup(stored?: object) {
    if (stored) localStorage.setItem('vessel.chart.preferences.v1', JSON.stringify(stored))
    const driver = streams()
    const candles = vi.fn().mockResolvedValue(candleSeriesFixture)
    const marketContext = vi.fn().mockResolvedValue(marketContextFixture)
    const { state, factory } = fakeAdapter()
    const view = render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(candles, marketContext, driver.marketStream), accountId: accountFixture.id }} createAdapter={factory} />)
    await flush()
    return { ...driver, candles, marketContext, state, view }
  }

  it('starts after the first candle load and shows the live state', async () => {
    const { marketStream, last } = await setup()
    expect(marketStream).toHaveBeenCalledExactlyOnceWith(accountFixture.id, { instrument: 'BTC', interval: '1h' }, expect.any(AbortSignal), expect.any(Function))
    expect(toggle()).toHaveAttribute('aria-pressed', 'true')
    expect(toggle()).toHaveAttribute('data-state', 'connecting')
    last().emit(live)
    expect(toggle()).toHaveAttribute('data-state', 'live')
    expect(toggle()).toHaveTextContent('Live')
    last().emit({ type: 'status', status: { state: 'stale', observedAt: '2026-10-02T08:01:00Z' } })
    expect(toggle()).toHaveTextContent('Stale')
  })

  it('renders a stream wait notice and reconnects only after the Live toggle', async () => {
    const { last, marketStream } = await setup()
    last().fail(new ApiError('http', 'Generic failure.', 429, new Date(Date.now() + 5_000).toISOString()))
    await flush()
    expect(screen.getByText(/Wait 5 seconds before trying again/)).toHaveTextContent('Turn Live off and on to reconnect.')
    await advance(60_000)
    expect(marketStream).toHaveBeenCalledOnce()
    fireEvent.click(toggle())
    fireEvent.click(toggle())
    await flush()
    expect(marketStream).toHaveBeenCalledTimes(2)
    expect(screen.queryByText(/Turn Live off/)).not.toBeInTheDocument()
  })

  it('upserts streamed candles without resetting the view and replaces the statistics', async () => {
    const { state, last, candles } = await setup()
    const [, forming] = candleSeriesFixture.candles
    vi.setSystemTime(new Date('2026-10-02T08:00:07Z'))
    last().emit({ type: 'candle', candle: { ...forming!, high: '104.5', close: '104.5' } })
    expect(state.candles).toHaveLength(2)
    expect(state.candles.at(-1)).toMatchObject({ high: 104.5, close: 104.5 })
    expect(state.resets.at(-1)).toBe(false)
    last().emit({ type: 'candle', candle: { ...forming!, openTime: 1_790_007_200_000, closeTime: 1_790_010_799_999, open: '104.5', close: '105' } })
    expect(state.candles.map(c => c.close)).toEqual([101, 104.5, 105])
    expect(state.resets.at(-1)).toBe(false)
    expect(screen.getByText('Updated 08:00:07 UTC')).toBeInTheDocument()
    last().emit({ type: 'context', context: { ...marketContextFixture, markPrice: '105.25', observedAt: '2026-10-02T08:00:08Z' } })
    expect(screen.getByLabelText('BTC market statistics')).toHaveTextContent('Mark105.25')
    expect(toggle()).toHaveAttribute('data-state', 'live')
    expect(candles).toHaveBeenCalledOnce()
  })

  it('reconnects with backoff and refreshes candles over REST to fill the gap', async () => {
    const { marketStream, last, candles } = await setup()
    last().emit(live)
    last().fail()
    await flush()
    expect(toggle()).toHaveAttribute('data-state', 'reconnecting')
    await advance(999)
    expect(marketStream).toHaveBeenCalledOnce()
    await advance(1)
    expect(marketStream).toHaveBeenCalledTimes(2)
    expect(candles).toHaveBeenCalledOnce()
    last().emit(live)
    await flush()
    expect(candles).toHaveBeenCalledTimes(2)
    expect(toggle()).toHaveAttribute('data-state', 'live')
  })

  it('falls back to polling after three failed streams and keeps retrying the stream', async () => {
    const { marketStream, last, candles, marketContext } = await setup()
    last().end()
    await advance(1_000)
    last().fail()
    await advance(2_000)
    expect(marketStream).toHaveBeenCalledTimes(3)
    last().end()
    await flush()
    expect(toggle()).toHaveAttribute('data-state', 'polling')
    expect(toggle()).toHaveTextContent('Polling')
    expect(candles).toHaveBeenCalledTimes(2)
    expect(marketContext).toHaveBeenCalledTimes(2)
    await advance(15_000)
    expect(candles).toHaveBeenCalledTimes(3)
    expect(marketContext).toHaveBeenCalledTimes(3)
    await advance(45_000)
    expect(marketStream).toHaveBeenCalledTimes(4)
    expect(candles).toHaveBeenCalledTimes(6)
    last().emit(live)
    await flush()
    expect(toggle()).toHaveAttribute('data-state', 'live')
    const afterRecovery = candles.mock.calls.length
    await advance(30_000)
    expect(candles).toHaveBeenCalledTimes(afterRecovery)
  })

  it('turns off, aborts the stream and remembers the choice', async () => {
    const { marketStream, last, view } = await setup()
    const first = last()
    fireEvent.click(toggle())
    expect(first.signal.aborted).toBe(true)
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')
    expect(toggle()).toHaveTextContent('Off')
    expect(JSON.parse(localStorage.getItem('vessel.chart.preferences.v1')!)).toMatchObject({ live: false })
    await advance(120_000)
    expect(marketStream).toHaveBeenCalledOnce()
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled()
    fireEvent.click(toggle())
    expect(marketStream).toHaveBeenCalledTimes(2)
    view.unmount()
    expect(last().signal.aborted).toBe(true)
  })

  it('stays off when the stored preference is off', async () => {
    const { marketStream } = await setup({ live: false })
    expect(marketStream).not.toHaveBeenCalled()
    expect(toggle()).toHaveTextContent('Off')
  })

  it('pauses while the tab is hidden and refreshes when it is visible again', async () => {
    const { marketStream, last, candles, marketContext } = await setup()
    const first = last()
    first.emit(live)
    visibility = 'hidden'
    fireEvent(document, new Event('visibilitychange'))
    expect(first.signal.aborted).toBe(true)
    await advance(120_000)
    expect(marketStream).toHaveBeenCalledOnce()
    visibility = 'visible'
    fireEvent(document, new Event('visibilitychange'))
    await flush()
    expect(candles).toHaveBeenCalledTimes(2)
    expect(marketContext).toHaveBeenCalledTimes(2)
    expect(marketStream).toHaveBeenCalledTimes(2)
  })

  it('restarts the stream for a new timeframe and shares state with the expanded chart', async () => {
    const { marketStream, last } = await setup()
    const first = last()
    first.emit(live)
    fireEvent.click(within(screen.getByRole('group', { name: 'Timeframe' })).getByRole('button', { name: '4 hours' }))
    expect(first.signal.aborted).toBe(true)
    await flush()
    expect(marketStream).toHaveBeenLastCalledWith(accountFixture.id, { instrument: 'BTC', interval: '4h' }, expect.any(AbortSignal), expect.any(Function))
    last().emit(live)
    fireEvent.click(screen.getByRole('button', { name: 'Expand chart' }))
    const dialog = screen.getByRole('dialog', { name: 'BTC chart' })
    expect(within(dialog).getByRole('button', { name: 'Live updates' })).toHaveAttribute('data-state', 'live')
    expect(marketStream).toHaveBeenCalledTimes(2)
  })
})

describe('Chart panel review regressions', () => {
  function Harness({ factory, initial }: { factory: ChartAdapterFactory; initial: DraftEntry[] }) {
    const [list, setList] = useState(initial)
    return <>
      <ChartPanel entries={list} selectedId={initial[0]!.id} onSelect={vi.fn()} instrument="BTC" onEntriesChange={setList}
        source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />
      <output data-testid="targets">{list[0]!.targets.map(t => t.value).join(',')}</output>
      <button type="button" onClick={() => setList(current => current.map((e, i) => i === 0
        ? { ...e, targets: e.targets.map((t, j) => j === 1 ? { ...t, value: '93000' } : t) } : e))}>sidebar tp2</button>
    </>
  }

  it('undoes a chart TP1 edit without reverting a later sidebar TP2 edit, and redoes it', async () => {
    const { state, factory } = fakeAdapter()
    const initial: DraftEntry[] = [{ ...createEntry(0), price: '85000', share: '100',
      targets: [{ ...createExit('50'), id: 'tp1', value: '90000' }, { ...createExit('50'), id: 'tp2', value: '92000' }] }]
    render(<Harness factory={factory} initial={initial} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'TP1')!.id, { x: 10, y: 10 }))
    const price = screen.getByRole('textbox', { name: 'Price' })
    await userEvent.clear(price)
    await userEvent.type(price, '91000{Enter}')
    await userEvent.click(screen.getByRole('button', { name: 'sidebar tp2' }))
    expect(screen.getByTestId('targets')).toHaveTextContent('91000,93000')
    await userEvent.click(screen.getByRole('button', { name: /^Undo/ }))
    expect(screen.getByTestId('targets')).toHaveTextContent('90000,93000')
    await userEvent.click(screen.getByRole('button', { name: /^Redo/ }))
    expect(screen.getByTestId('targets')).toHaveTextContent('91000,93000')
  })

  it('keeps an open level editor inside the chart when the chart narrows', async () => {
    const { state, factory } = fakeAdapter()
    const initial: DraftEntry[] = [{ ...createEntry(0), price: '100', share: '100', targets: [{ ...createExit('100'), id: 'tp1', value: '110' }] }]
    render(<Harness factory={factory} initial={initial} />)
    await screen.findByText(/Updated/)
    const body = screen.getByTestId('chart-body')
    const size = (width: number, height: number) => {
      Object.defineProperty(body, 'clientWidth', { configurable: true, value: width })
      Object.defineProperty(body, 'clientHeight', { configurable: true, value: height })
    }
    size(735, 360)
    act(() => state.callbacks!.onLevelEdit(state.overlays.find(o => o.label === 'TP1')!.id, { x: 700, y: 100 }))
    const form = screen.getByRole('form', { name: 'Edit Entry 1 target 1' })
    expect(parseFloat(form.style.left) + parseFloat(form.style.width)).toBeLessThanOrEqual(735)
    size(322, 320)
    act(() => { window.dispatchEvent(new Event('resize')) })
    expect(parseFloat(form.style.left)).toBeGreaterThanOrEqual(8)
    expect(parseFloat(form.style.left) + parseFloat(form.style.width)).toBeLessThanOrEqual(322 - 8)
  })
})

describe('Chart captures', () => {
  it('captures the chart with a caption once candles load and reports the result', async () => {
    const list = entries()
    const { state, factory } = fakeAdapter()
    const onCapture = vi.fn<(image: Blob, context: string) => string | null>().mockReturnValueOnce(null).mockReturnValueOnce('A play can hold at most 50 images.')
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" venue="Hyperliquid" onSelect={vi.fn()} onCapture={onCapture}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    const button = screen.getByRole('button', { name: 'Capture chart' })
    expect(button).toBeDisabled()
    await screen.findByText(/Updated/)
    expect(button).toBeEnabled()

    await userEvent.click(button)
    expect(onCapture).toHaveBeenCalledWith(expect.any(Blob), 'BTC · Hyperliquid · 1 hour')
    expect(state.captions[0]).toMatch(/^BTC · Hyperliquid · 1 hour · \d{1,2} [A-Z][a-z]{2} \d{4}, \d{2}:\d{2} UTC · EMA 9\/50\/100\/200 · Volume · RSI 14 · Planned levels are not fills$/)
    expect(await screen.findByText(/Capture added to the Evidence tab/)).toHaveAttribute('role', 'status')

    await userEvent.click(button)
    expect(await screen.findByRole('alert')).toHaveTextContent('A play can hold at most 50 images.')
  })

  it('captures from the expanded chart and again after it closes', async () => {
    const { state, factory } = fakeAdapter()
    const onCapture = vi.fn<(image: Blob, context: string) => string | null>().mockReturnValue(null)
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()} onCapture={onCapture}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    await userEvent.click(screen.getByRole('button', { name: 'Expand chart' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Capture chart' }))
    expect(onCapture).toHaveBeenCalledTimes(1)
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Capture chart' }))
    expect(onCapture).toHaveBeenCalledTimes(2)
    expect(state.captions).toHaveLength(2)
    expect(await screen.findByText(/Capture added to the Evidence tab/)).toBeInTheDocument()
  })

  it('keeps capture unavailable without market data and says why in its tooltip', async () => {
    render(<ChartPanel entries={entries()} selectedId="" instrument="ETH-PERP" onSelect={vi.fn()} onCapture={vi.fn()} />)
    const button = screen.getByRole('button', { name: 'Capture chart' })
    expect(button).toBeDisabled()
    await userEvent.hover(button.parentElement!)
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Capture chartNeeds market data')
  })

  it('names each drawing tool and says how to use it in a tooltip', async () => {
    const { factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    const rail = screen.getByRole('group', { name: 'Chart tools' })
    await userEvent.hover(within(rail).getByRole('button', { name: 'Fibonacci retracement' }))
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Fibonacci retracementDrag from the swing start to the swing end.')
    // Measure tools sit behind one button; its panel lists them all.
    await userEvent.click(within(rail).getByRole('button', { name: 'Measure tools' }))
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Measure tools' })).getByRole('button', { name: 'Date range' }))
    expect(screen.getByText('Drag across time to measure bars and duration. Esc cancels.')).toBeInTheDocument()
    // The group button now shows the tool used last.
    expect(within(rail).getByRole('button', { name: 'Date range' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('pins starred drawing tools to the toolbar and remembers them', async () => {
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={entries()} selectedId="" instrument="BTC" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    const favorites = () => within(screen.getByRole('group', { name: 'Favorite drawing tools' })).getAllByRole('button')
      .map(button => button.getAttribute('aria-label')).filter(label => label !== 'Choose favorite tools')
    expect(favorites()).toEqual(['Trend line', 'Horizontal line', 'Fibonacci retracement'])
    await userEvent.click(screen.getByRole('button', { name: 'Lines tools' }))
    await userEvent.click(screen.getByRole('button', { name: 'Add Vertical line to favorites' }))
    await userEvent.click(screen.getByRole('button', { name: 'Remove Trend line from favorites' }))
    await userEvent.keyboard('{Escape}')
    expect(favorites()).toEqual(['Horizontal line', 'Fibonacci retracement', 'Vertical line'])
    expect(JSON.parse(localStorage.getItem('vessel.chart.preferences.v1')!).drawingFavorites).toEqual(['horizontal-line', 'fibonacci', 'vertical-line'])
    await userEvent.click(within(screen.getByRole('group', { name: 'Favorite drawing tools' })).getByRole('button', { name: 'Vertical line' }))
    expect(state.tool).toBe('vertical-line')
    // Single tools have no group panel; the star picker lists every tool.
    expect(screen.queryByRole('button', { name: 'Fibonacci tools' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Choose favorite tools' }))
    await userEvent.click(screen.getByRole('button', { name: 'Add Text note to favorites' }))
    expect(favorites()).toContain('Text note')
  })
})

describe('Chart indicators', () => {
  it('offers no restore on a maximized pane that is too short to plot', async () => {
    localStorage.setItem('vessel.chart.preferences.v1', JSON.stringify({ indicators: { volume: { enabled: true, size: 'maximized' } } }))
    const list = entries()
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" venue="Hyperliquid" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onPaneLayout([
      { id: 'volume', top: 150, height: 26, left: 0, width: 600, effectiveSize: 'minimized', compacted: true },
      { id: 'rsi', top: 177, height: 26, left: 0, width: 600, effectiveSize: 'minimized', compacted: true },
    ]))
    const volumeBar = screen.getByRole('group', { name: 'Volume pane' })
    expect(within(volumeBar).getAllByRole('button').map(button => button.getAttribute('aria-label'))).toEqual(['Expand chart', 'Hide Volume'])
    const rsiBar = screen.getByRole('group', { name: 'RSI 14 pane' })
    expect(within(rsiBar).getAllByRole('button').map(button => button.getAttribute('aria-label'))).toEqual(['Maximize RSI 14', 'Expand chart', 'Hide RSI 14'])
  })

  const renderChart = async () => {
    const list = entries()
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" venue="Hyperliquid" onSelect={vi.fn()}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    return state
  }

  it('shows the default averages, volume and RSI, and hides an average from the chart legend', async () => {
    const state = await renderChart()
    expect(state.indicators!.lines.map(line => [line.label, line.color])).toEqual([['EMA 9', '#b388ff'], ['EMA 50', '#ef5350'], ['EMA 100', '#f2c94c'], ['EMA 200', '#4cbb6c']])
    expect(state.indicators!.volume!.values).toEqual([12.5, 9])
    expect(state.indicators!.rsi!.label).toBe('RSI 14')

    await userEvent.click(within(screen.getByRole('group', { name: 'Indicators' })).getByRole('button', { name: 'Hide EMA 50' }))
    expect(state.indicators!.lines.map(line => line.label)).toEqual(['EMA 9', 'EMA 100', 'EMA 200'])
    expect(screen.getByRole('button', { name: 'Show EMA 50' })).toHaveAttribute('aria-pressed', 'false')
    expect(JSON.parse(localStorage.getItem('vessel.chart.preferences.v1')!).indicators.emas[1].enabled).toBe(false)
  })

  it('minimizes, maximizes and hides indicator panes from their bars', async () => {
    const state = await renderChart()
    act(() => state.callbacks!.onPaneLayout([
      { id: 'volume', top: 300, height: 80, left: 0, width: 600, effectiveSize: 'normal', compacted: false },
      { id: 'rsi', top: 381, height: 100, left: 0, width: 600, effectiveSize: 'normal', compacted: false },
    ]))
    const rsiBar = screen.getByRole('group', { name: 'RSI 14 pane' })
    await userEvent.click(within(rsiBar).getByRole('button', { name: 'Maximize RSI 14' }))
    expect(state.indicators!.rsi!.size).toBe('maximized')
    await userEvent.click(within(rsiBar).getByRole('button', { name: 'Restore RSI 14' }))
    expect(state.indicators!.rsi!.size).toBe('normal')

    const volumeBar = screen.getByRole('group', { name: 'Volume pane' })
    await userEvent.click(within(volumeBar).getByRole('button', { name: 'Minimize Volume' }))
    expect(state.indicators!.volume!.size).toBe('minimized')
    await userEvent.click(within(volumeBar).getByRole('button', { name: 'Hide Volume' }))
    expect(state.indicators!.volume).toBeNull()
    // Focus moves to the legend button that brings the pane back.
    expect(screen.getByRole('button', { name: 'Show Volume pane' })).toHaveFocus()
    // A hidden pane comes back from the legend with the size it had.
    await userEvent.click(screen.getByRole('button', { name: 'Show Volume pane' }))
    expect(state.indicators!.volume!.size).toBe('minimized')
  })

  it('changes periods and colors in the settings popup and resets them', async () => {
    const state = await renderChart()
    await userEvent.click(screen.getByRole('button', { name: 'Indicators' }))
    const period = screen.getByRole('spinbutton', { name: 'EMA 1 period' })
    await userEvent.clear(period)
    await userEvent.type(period, '12{Enter}')
    expect(state.indicators!.lines[0]!.label).toBe('EMA 12')
    // Out-of-range periods are not applied.
    await userEvent.clear(period)
    await userEvent.type(period, '0{Enter}')
    expect(period).toHaveValue(12)

    await userEvent.click(screen.getByRole('button', { name: 'EMA 1 color' }))
    await userEvent.click(screen.getByRole('button', { name: '#4dd0e1' }))
    expect(state.indicators!.lines[0]!.color).toBe('#4dd0e1')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Show RSI' }))
    expect(state.indicators!.rsi).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: 'Reset to defaults' }))
    expect(state.indicators!.lines[0]).toMatchObject({ label: 'EMA 9', color: '#b388ff' })
    expect(state.indicators!.rsi).not.toBeNull()
  })

  it('marks a pane minimized for lack of room and names it in captures', async () => {
    const list = entries()
    const { state, factory } = fakeAdapter()
    render(<ChartPanel entries={list} selectedId={list[0]!.id} instrument="BTC" venue="Hyperliquid" onSelect={vi.fn()} onCapture={vi.fn().mockReturnValue(null)}
      source={{ api: chartApi(vi.fn().mockResolvedValue(candleSeriesFixture)), accountId: accountFixture.id }} createAdapter={factory} />)
    await screen.findByText(/Updated/)
    act(() => state.callbacks!.onPaneLayout([
      { id: 'volume', top: 150, height: 26, left: 0, width: 600, effectiveSize: 'minimized', compacted: true },
      { id: 'rsi', top: 177, height: 72, left: 0, width: 600, effectiveSize: 'normal', compacted: false },
    ]))
    const volumeBar = screen.getByRole('group', { name: 'Volume pane' })
    expect(volumeBar).toHaveTextContent('Too short to plot')
    // Nothing to minimize or restore; maximize can make room, and hide still works.
    expect(within(volumeBar).queryByRole('button', { name: /Minimize Volume|Restore Volume/ })).toBeNull()
    expect(within(volumeBar).getByRole('button', { name: 'Maximize Volume' })).toBeInTheDocument()
    expect(state.indicators!.volume!.size).toBe('normal')

    await userEvent.click(screen.getByRole('button', { name: 'Capture chart' }))
    expect(state.captions[0]).toContain('EMA 9/50/100/200 · Volume minimized, not plotted · RSI 14 · Planned levels are not fills')
    // A larger chart can make room.
    await userEvent.click(within(volumeBar).getByRole('button', { name: 'Expand chart' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
  })
})

describe('fills on the chart (phase 1, 1.1b)', () => {
  const fills: ChartFill[] = [
    { id: 'link-1:f1', atUtc: '2026-09-29T00:00:00.000Z', price: '100.4', quantity: '0.5', side: 'buy', role: 'entry' },
    { id: 'link-2:f2', atUtc: '2026-09-29T08:00:00.000Z', price: '110', quantity: '0.5', side: 'sell', role: 'exit' },
  ]
  it('marks each fill at its time and price, never as a level, and opens an ended trade on its own dates without a live tail', async () => {
    const { state, factory } = fakeAdapter()
    const candles = vi.fn<WorkspaceApi['candles']>().mockResolvedValue(candleSeriesFixture)
    const stream = vi.fn<WorkspaceApi['marketStream']>(idleMarketStream)
    render(<ChartPanel entries={entries()} selectedId={entries()[0]!.id} instrument="BTC" onSelect={vi.fn()} fills={fills} tradeDates
      source={{ api: chartApi(candles, undefined, stream), accountId: accountFixture.id }} createAdapter={factory} />)
    await vi.waitFor(() => expect(candles).toHaveBeenCalled())
    // The trade ran 8 hours on hourly candles: framed with 2 hours (a quarter) or 5 candles, the larger, either side.
    const from = Date.parse(fills[0]!.atUtc), to = Date.parse(fills[1]!.atUtc), pad = 5 * 3_600_000
    expect(candles.mock.lastCall![1]).toEqual({ instrument: 'BTC', interval: '1h', endTime: to + pad })
    await vi.waitFor(() => expect(state.focus).toEqual({ from: from - pad, to: to + pad }))
    expect(state.markers.map(marker => [marker.id, marker.side, marker.price])).toEqual([['link-1:f1', 'buy', 100.4], ['link-2:f2', 'sell', 110]])
    expect(state.markers[0]!.tooltip).toBe('Entry fill · Buy 0.5 at 100.4\n29 Sept 2026, 00:00:00 UTC')
    expect(state.overlays.some(overlay => overlay.price === 100.4 || overlay.price === 110)).toBe(false)
    expect(stream).not.toHaveBeenCalled()
  })

  it('asks for whole milliseconds when a trade has millisecond fills', async () => {
    const { factory } = fakeAdapter()
    const candles = vi.fn<WorkspaceApi['candles']>().mockResolvedValue(candleSeriesFixture)
    const odd: ChartFill[] = [{ ...fills[0]!, atUtc: '2026-09-01T00:00:00.001Z' }, { ...fills[1]!, atUtc: '2026-09-02T00:00:00.002Z' }]
    render(<ChartPanel entries={entries()} selectedId={entries()[0]!.id} instrument="BTC" onSelect={vi.fn()} fills={odd} tradeDates
      source={{ api: chartApi(candles), accountId: accountFixture.id }} createAdapter={factory} />)
    await vi.waitFor(() => expect(candles).toHaveBeenCalled())
    // A day and a millisecond: a quarter of it is the padding, rounded out to a whole millisecond.
    expect(candles.mock.lastCall![1]).toEqual({ instrument: 'BTC', interval: '1h', endTime: Math.ceil(Date.parse(odd[1]!.atUtc) + 86_400_001 / 4) })
  })

  it('opens on the trade when its fills arrive after today\'s candles', async () => {
    const { state, factory } = fakeAdapter()
    const candles = vi.fn<WorkspaceApi['candles']>().mockResolvedValue(candleSeriesFixture)
    const props = { entries: entries(), selectedId: entries()[0]!.id, instrument: 'BTC', onSelect: vi.fn(), tradeDates: true,
      source: { api: chartApi(candles), accountId: accountFixture.id }, createAdapter: factory }
    const view = render(<ChartPanel {...props} fills={[]} />)
    await vi.waitFor(() => expect(state.candles.length).toBeGreaterThan(0))
    view.rerender(<ChartPanel {...props} fills={fills} />)
    await vi.waitFor(() => expect(candles.mock.lastCall![1]).toHaveProperty('endTime'))
    // The trade's candles arrive as a reset, so the chart opens on the trade, not on today's view.
    await vi.waitFor(() => expect(state.resets.at(-1)).toBe(true))
    expect(state.candles.length).toBeGreaterThan(0)
    expect(state.focus).toEqual({ from: Date.parse(fills[0]!.atUtc) - 5 * 3_600_000, to: Date.parse(fills[1]!.atUtc) + 5 * 3_600_000 })
  })

  it('stays on today for an open trade, with its fills marked', async () => {
    const { state, factory } = fakeAdapter()
    const candles = vi.fn<WorkspaceApi['candles']>().mockResolvedValue(candleSeriesFixture)
    const stream = vi.fn<WorkspaceApi['marketStream']>(idleMarketStream)
    render(<ChartPanel entries={entries()} selectedId={entries()[0]!.id} instrument="BTC" onSelect={vi.fn()} fills={fills.slice(0, 1)}
      source={{ api: chartApi(candles, undefined, stream), accountId: accountFixture.id }} createAdapter={factory} />)
    await vi.waitFor(() => expect(candles).toHaveBeenCalled())
    expect(candles.mock.lastCall![1]).toEqual({ instrument: 'BTC', interval: '1h' })
    await vi.waitFor(() => expect(state.markers).toHaveLength(1))
    expect(state.focus).toBeNull()
    // Live, as before: only an ended trade's chart leaves the stream off.
    await vi.waitFor(() => expect(stream).toHaveBeenCalled())
  })
})
