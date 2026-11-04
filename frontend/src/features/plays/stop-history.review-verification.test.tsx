import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PlayHistory, PlayPlan, PlanRevision, PlayStatus, SavedPlay } from '@/api/plays'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { NotificationProvider } from '@/components/notifications/NotificationProvider'
import type { ChartCallbacks, ChartAdapterFactory, PriceOverlay } from '@/components/chart/types'
import { contrastRatio, mix, themes } from '@/features/settings/themes'
import { render } from '@/test/render'
import { accountFixture, candleSeriesFixture, instrumentCatalogFixture, playApiStubs, portfolioFixture, savedPlayFixture } from '@/test/workspace-fixture'
import { credentialsFixture } from '@/test/identity-fixture'
import { ChartPanel } from './PlayChart'
import { PlaysPage } from './PlaysPage'
import { parseOverlayId } from './levels'
import { draftFromSaved, savedState, type PlaysSession } from './saved'
import { originalStopOverlays, stopHistory, stopText } from './stop-history'

const plan = (values: string[], leverage = '5', direction: PlayPlan['direction'] = 'long', unit: 'price' | 'percent' = 'price'): PlayPlan => ({
  ...savedPlayFixture.plan, direction, leverage,
  entries: [{ ...savedPlayFixture.plan.entries[0]!, price: '100', stops: values.map((value, index) => ({ id: `s${index}`, unit, value, share: '100' })) }],
})
const rev = (number: number, value: PlayPlan): PlanRevision => ({
  number, plan: value, status: 'open', reason: 'Changed', createdAtUtc: '2026-11-04T00:00:00Z',
})
const history = (original = '95', final = '100'): PlayHistory => ({
  revisions: [rev(2, plan([final])), rev(1, plan([original]))], statusChanges: [],
})
const closed = (id = savedPlayFixture.summary.id, version = 3): SavedPlay => ({
  ...savedPlayFixture, plan: plan(['100']),
  summary: { ...savedPlayFixture.summary, id, version, status: 'closed', planRevision: 2 },
})
const sessionFor = (value: SavedPlay): PlaysSession => {
  const draft = draftFromSaved(value, [])
  return { view: 'editor', draft, saved: savedState(value, draft) }
}
const client = (): WorkspaceApi => ({
  ...createWorkspaceApi(credentialsFixture), ...playApiStubs(),
  playHistory: vi.fn().mockResolvedValue(history()),
})
const page = (api: WorkspaceApi, value: SavedPlay, active = true) => <NotificationProvider>
  <PlaysPage accounts={[accountFixture]} portfolios={[portfolioFixture]} api={api}
    session={sessionFor(value)} onSession={vi.fn()} active={active} />
</NotificationProvider>

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Review tests must not make network requests.'))))
})

describe('adversarial stop history verification', () => {
  it('counts removal and re-addition, but not replacement IDs at the same prices', () => {
    const first = plan(['95', '96'])
    const replacement = { ...first, entries: [{ ...first.entries[0]!, stops: first.entries[0]!.stops.map(stop => ({ ...stop, id: `${stop.id}-new` })) }] }
    const removed = plan([])
    const result = stopHistory([rev(3, removed), rev(1, first), rev(4, replacement), rev(2, replacement)], replacement)
    expect(result[0]!.moves).toBe(2)
    expect(originalStopOverlays(result, () => 'SL')).toEqual([])
  })

  it('counts price ordering changes and a final-plan change absent from the history', () => {
    const first = plan(['95', '96'])
    const final = plan(['96', '95'])
    const result = stopHistory([rev(2, first), rev(1, first)], final)
    expect(result[0]!.moves).toBe(1)
    expect(result[0]!.original.map(stopText)).toEqual(['95', '96'])
    expect(result[0]!.final.map(stopText)).toEqual(['96', '95'])
    expect(originalStopOverlays(result, () => 'SL')).toEqual([])
  })

  it.each(['long', 'short'] as const)('resolves % stops at each revision leverage for %s entries', direction => {
    const first = plan(['10'], '5', direction, 'percent')
    const changed = plan(['10'], '10', direction, 'percent')
    const equivalent = plan([direction === 'long' ? '99' : '101'], '10', direction)
    const result = stopHistory([rev(3, equivalent), rev(1, first), rev(2, changed)], equivalent)
    expect(result[0]!.moves).toBe(1)
    expect(result[0]!.original.map(stopText)).toEqual([direction === 'long' ? '10 % (98)' : '10 % (102)'])
  })

  it('does not parse any generated original-stop overlay as an editable plan level', () => {
    const first = plan(['95'])
    const final = plan(['100'])
    const result = stopHistory([rev(1, first), rev(2, final)], final)
    for (const overlay of originalStopOverlays(result, () => 'SL')) {
      expect(parseOverlayId(overlay.id)).toBeNull()
      expect(overlay.kind).toBe('reference')
      expect(overlay.draggable).toBe(false)
    }
  })

  it('ignores original reference select, drag and edit callbacks and creates no undo step', async () => {
    const first = plan(['95'])
    const final = plan(['100'])
    let callbacks: ChartCallbacks | undefined
    let overlays: readonly PriceOverlay[] = []
    const factory: ChartAdapterFactory = (_element, value) => {
      callbacks = value
      return {
        setCandles: vi.fn(), setOverlays: value => { overlays = value }, setDrawings: vi.fn(), setDrawingTool: vi.fn(),
        setPricePicker: vi.fn(), setIndicators: vi.fn(), setMarkers: vi.fn(), setFocus: vi.fn(), setTimeZone: vi.fn(),
        capture: vi.fn().mockResolvedValue(null), destroy: vi.fn(),
      }
    }
    const api = client()
    api.candles = vi.fn().mockResolvedValue(candleSeriesFixture)
    const select = vi.fn(), change = vi.fn()
    render(<ChartPanel entries={final.entries} selectedId={final.entries[0]!.id} onSelect={select} onEntriesChange={change}
      instrument="BTC" source={{ api, accountId: accountFixture.id }} streamable={false} marketContext={false}
      stopHistory={stopHistory([rev(1, first), rev(2, final)], final)} createAdapter={factory} />)
    await waitFor(() => expect(overlays.some(value => value.label === 'SL orig')).toBe(true))
    const original = overlays.find(value => value.label === 'SL orig')!
    act(() => {
      callbacks!.onLevelSelect(original.id)
      callbacks!.onLevelDrag(original.id, 97, 'move')
      callbacks!.onLevelDrag(original.id, 98, 'end')
      callbacks!.onLevelEdit(original.id, { x: 0, y: 0 })
    })
    expect(select).not.toHaveBeenCalled()
    expect(change).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled()
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it.each(themes)('keeps active and hovered timeframe text readable in $name', ({ colors }) => {
    const ink = mix(colors.ring, colors.foreground, 0.78)
    for (const weight of [0.16, 0.22]) expect(contrastRatio(ink, mix(colors.ring, colors.card, weight))).toBeGreaterThanOrEqual(4.5)
  })
})

describe('closed-Play history effect verification', () => {
  it.each(['draft', 'planned', 'paused', 'open', 'cancelled', 'unresolved'] as PlayStatus[])('does not request history for %s', async status => {
    const api = client()
    const value = closed()
    value.summary.status = status
    render(page(api, value))
    await act(async () => {})
    expect(api.playHistory).not.toHaveBeenCalled()
    expect(screen.queryByRole('region', { name: 'Stop changes' })).toBeNull()
  })

  it('does not request history for a retrospective closed Play or an inactive editor', async () => {
    const api = client()
    const value = closed()
    value.summary.origin = 'retrospective'
    const view = render(page(api, value))
    await act(async () => {})
    expect(api.playHistory).not.toHaveBeenCalled()
    view.rerender(page(api, closed(), false))
    await act(async () => {})
    expect(api.playHistory).not.toHaveBeenCalled()
  })

  it('renders moved stops in Review even when the manual outcome receipt cannot be read', async () => {
    const api = client()
    render(page(api, closed()))
    await waitFor(() => expect(api.playHistory).toHaveBeenCalledOnce())
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    const region = await screen.findByRole('region', { name: 'Stop changes' })
    expect(region).toHaveTextContent('Original 95')
    expect(region).toHaveTextContent('Final 100')
    expect(screen.getByRole('textbox', { name: 'Review' })).toBeEnabled()
    expect(within(region).getAllByRole('listitem')).toHaveLength(1)
  })

  it.each(['id', 'version', 'api'] as const)('discards late history after the %s changes', async change => {
    const api = client()
    let finish!: (value: PlayHistory) => void
    vi.mocked(api.playHistory).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const old = closed()
    const view = render(page(api, old))
    await waitFor(() => expect(api.playHistory).toHaveBeenCalledOnce())
    const oldSignal = vi.mocked(api.playHistory).mock.calls[0]![1]!
    const current = change === 'id' ? closed('33333333-4444-5555-6666-777777777777') : change === 'version' ? closed(old.summary.id, 4) : old
    const nextApi = change === 'api' ? client() : api
    vi.mocked(nextApi.playHistory).mockResolvedValue(history('90'))
    view.rerender(page(nextApi, current))
    expect(oldSignal.aborted).toBe(true)
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    expect(await screen.findByRole('region', { name: 'Stop changes' })).toHaveTextContent('Original 90')
    await act(async () => finish(history('95')))
    expect(screen.getByRole('region', { name: 'Stop changes' })).toHaveTextContent('Original 90')
    expect(screen.getByRole('region', { name: 'Stop changes' })).not.toHaveTextContent('Original 95')
  })

  it('does not display history on a failed initial read', async () => {
    const api = client()
    vi.mocked(api.playHistory).mockRejectedValue(new Error('Unavailable'))
    render(page(api, closed()))
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.queryByRole('region', { name: 'Stop changes' })).toBeNull()
    expect(screen.getByRole('textbox', { name: 'Review' })).toBeEnabled()
    fireEvent.change(screen.getByRole('textbox', { name: 'Review' }), { target: { value: 'Still editable' } })
  })

  it('loads only plan-revision context for a closed paper Play, without reading real executions', async () => {
    const api = { ...client(), tradingMode: 'paper' as const }
    api.instruments = vi.fn().mockResolvedValue(instrumentCatalogFixture)
    api.candles = vi.fn().mockResolvedValue(candleSeriesFixture)
    const value = closed()
    const paperPlay: SavedPlay = { ...value, summary: { ...value.summary, tradingMode: 'paper', venueId: 'hyperliquid', instrumentSource: 'venue' } }
    const account = { ...accountFixture, tradingMode: 'paper' as const, syncStatus: 'paper' as const, venueId: 'hyperliquid',
      balanceUsd: '4000', availableBalanceUsd: '4000', availableBalanceSource: 'paper' as const }
    render(<NotificationProvider>
      <PlaysPage accounts={[account]} portfolios={[portfolioFixture]} api={api}
        session={sessionFor(paperPlay)} onSession={vi.fn()} />
    </NotificationProvider>)
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    expect(await screen.findByRole('region', { name: 'Stop changes' })).toHaveTextContent('Original 95')
    expect(api.playExecution).not.toHaveBeenCalled()
    expect(api.manualExecutions).not.toHaveBeenCalled()
  })

  it('aborts history on unmount and ignores its eventual answer', async () => {
    const api = client()
    let finish!: (value: PlayHistory) => void
    vi.mocked(api.playHistory).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const view = render(page(api, closed()))
    await waitFor(() => expect(api.playHistory).toHaveBeenCalledOnce())
    const signal = vi.mocked(api.playHistory).mock.calls[0]![1]!
    view.unmount()
    expect(signal.aborted).toBe(true)
    await act(async () => finish(history()))
    expect(screen.queryByRole('region', { name: 'Stop changes' })).toBeNull()
  })

  it('omits the previous history after reopening the same closed Play and failing the new read', async () => {
    const api = client()
    const value = closed()
    const view = render(page(api, value))
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    expect(await screen.findByRole('region', { name: 'Stop changes' })).toHaveTextContent('Original 95')
    view.rerender(page(api, value, false))
    vi.mocked(api.playHistory).mockRejectedValueOnce(new Error('History unavailable on reopen'))
    view.rerender(page(api, value))
    await waitFor(() => expect(api.playHistory).toHaveBeenCalledTimes(2))
    await act(async () => {})
    await userEvent.click(screen.getByRole('tab', { name: 'Review' }))
    expect(screen.queryByRole('region', { name: 'Stop changes' })).toBeNull()
  })
})
