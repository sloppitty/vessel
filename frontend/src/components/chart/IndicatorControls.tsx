import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react'
import { ChevronLeft, ChevronRight, ChevronsDownUp, ChevronsUpDown, Expand, Eye, EyeOff, Maximize2, Minimize2, RotateCcw, Settings2 } from 'lucide-react'
import { Popover } from 'radix-ui'
import { ChartIconButton } from './ChartToolbar'
import {
  defaultIndicators, emaLabel, emaPeriodLimits, indicatorColors, resizePane, rsiLabel, rsiPeriodLimits,
  type IndicatorPaneId, type IndicatorSettings, type IndicatorView, type PaneLayout, type PaneSize,
} from './indicators'
import { numberFormat } from '@/lib/format'

const priceValue = numberFormat({ maximumSignificantDigits: 6 })
const volumeValue = numberFormat({ notation: 'compact', maximumFractionDigits: 2 })
const rsiValue = numberFormat({ minimumFractionDigits: 2, maximumFractionDigits: 2 })
const icon = { size: 13, 'aria-hidden': true } as const

/** A whole-number period, applied on Enter or blur; invalid text returns to the current period. */
function PeriodInput({ label, value, limits, onChange }: { label: string; value: number; limits: { min: number; max: number }; onChange: (value: number) => void }) {
  const [text, setText] = useState<string | null>(null)
  const commit = () => {
    if (text === null) return
    const next = Number(text)
    if (Number.isInteger(next) && next >= limits.min && next <= limits.max && next !== value) onChange(next)
    setText(null)
  }
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') commit()
    if (event.key === 'Escape' && text !== null) { event.stopPropagation(); setText(null) }
  }
  return <input className="indicator-period" type="number" inputMode="numeric" min={limits.min} max={limits.max} step={1}
    aria-label={label} title={`${limits.min}–${limits.max}`} value={text ?? String(value)}
    onChange={event => setText(event.target.value)} onBlur={commit} onKeyDown={onKeyDown} />
}

function ColorChoice({ label, value, onChange }: { label: string; value: string; onChange: (color: string) => void }) {
  return <Popover.Root>
    <Popover.Trigger asChild>
      <button type="button" className="chart-color-trigger" aria-label={label} title={label}>
        <i style={{ '--swatch': value } as CSSProperties} />
      </button>
    </Popover.Trigger>
    <Popover.Portal>
      <Popover.Content className="chart-popover indicator-colors" side="right" align="start" sideOffset={6} aria-label={label}>
        <div className="chart-color-grid" role="group" aria-label="Colors">
          {indicatorColors.map(color => <button key={color} type="button" aria-label={color} aria-pressed={color === value}
            style={{ '--swatch': color } as CSSProperties} onClick={() => onChange(color)} />)}
        </div>
        <label className="indicator-custom-color">
          <span>Custom</span>
          <input type="color" value={value} onChange={event => onChange(event.target.value.toLowerCase())} />
        </label>
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>
}

/** Indicator settings: which are shown, their periods and colors. Changes apply at once. */
export function IndicatorSettingsPanel({ settings, onChange }: { settings: IndicatorSettings; onChange: (next: IndicatorSettings) => void }) {
  const setEma = (index: number, patch: Partial<IndicatorSettings['emas'][number]>) =>
    onChange({ ...settings, emas: settings.emas.map((ema, at) => at === index ? { ...ema, ...patch } : ema) })
  const unchanged = JSON.stringify(settings) === JSON.stringify(defaultIndicators)
  return <div className="indicator-settings">
    <p className="chart-tool-flyout-title">Moving averages</p>
    <ul>
      {settings.emas.map((ema, index) => <li key={ema.id}>
        <label className="indicator-toggle">
          <input type="checkbox" checked={ema.enabled} onChange={event => setEma(index, { enabled: event.target.checked })} aria-label={`Show EMA ${index + 1}`} />
          <span>EMA {index + 1}</span>
        </label>
        <PeriodInput label={`EMA ${index + 1} period`} value={ema.period} limits={emaPeriodLimits} onChange={period => setEma(index, { period })} />
        <ColorChoice label={`EMA ${index + 1} color`} value={ema.color} onChange={color => setEma(index, { color })} />
      </li>)}
    </ul>
    <p className="chart-tool-flyout-title">Panes</p>
    <ul>
      <li>
        <label className="indicator-toggle">
          <input type="checkbox" checked={settings.volume.enabled} aria-label="Show volume"
            onChange={event => onChange({ ...settings, volume: { ...settings.volume, enabled: event.target.checked } })} />
          <span>Volume</span>
        </label>
      </li>
      <li>
        <label className="indicator-toggle">
          <input type="checkbox" checked={settings.rsi.enabled} aria-label="Show RSI"
            onChange={event => onChange({ ...settings, rsi: { ...settings.rsi, enabled: event.target.checked } })} />
          <span>RSI</span>
        </label>
        <PeriodInput label="RSI period" value={settings.rsi.period} limits={rsiPeriodLimits} onChange={period => onChange({ ...settings, rsi: { ...settings.rsi, period } })} />
        <ColorChoice label="RSI color" value={settings.rsi.color} onChange={color => onChange({ ...settings, rsi: { ...settings.rsi, color } })} />
      </li>
    </ul>
    <button type="button" className="indicator-reset" disabled={unchanged} onClick={() => onChange(defaultIndicators)}>
      <RotateCcw {...icon} />Reset to defaults
    </button>
  </div>
}

/** Opens the indicator settings from any trigger, e.g. a toolbar button or the on-chart legend. */
export function IndicatorSettingsPopover({ settings, onChange, children }: { settings: IndicatorSettings; onChange: (next: IndicatorSettings) => void; children: ReactNode }) {
  return <Popover.Root>
    <Popover.Trigger asChild>{children}</Popover.Trigger>
    <Popover.Portal>
      <Popover.Content className="chart-popover indicator-popover" align="start" sideOffset={6} collisionPadding={8} aria-label="Indicators">
        <IndicatorSettingsPanel settings={settings} onChange={onChange} />
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>
}

/** Value at the hovered candle, or the latest defined value. */
function valueAt(values: readonly (number | null)[], index: number | null) {
  if (index !== null) return values[index] ?? null
  for (let at = values.length - 1; at >= 0; at--) if (values[at] !== null) return values[at]!
  return null
}

/**
 * On-chart indicator controls: a legend on the price pane that shows and hides each average, and a
 * bar at the top of each indicator pane to minimize, maximize or hide it.
 */
export function IndicatorOverlay({ settings, view, panes, hoverIndex, onChange, onExpand }: {
  settings: IndicatorSettings
  view: IndicatorView
  panes: readonly PaneLayout[]
  /** Candle under the crosshair, or null for the latest. */
  hoverIndex: number | null
  onChange: (next: IndicatorSettings) => void
  /** Opens a larger chart, offered when a pane is too short to plot. */
  onExpand?: (() => void) | undefined
}) {
  const [collapsed, setCollapsed] = useState(false)
  const legend = useRef<HTMLDivElement>(null)
  // Hiding a pane removes its bar, so focus moves to the legend button that shows it again.
  const refocus = useRef<IndicatorPaneId | null>(null)
  useEffect(() => {
    const button = refocus.current && legend.current?.querySelector<HTMLElement>(`[data-show-pane="${refocus.current}"]`)
    if (!button) return
    refocus.current = null
    button.focus()
  })
  const toggleEma = (index: number) => onChange({ ...settings, emas: settings.emas.map((ema, at) => at === index ? { ...ema, enabled: !ema.enabled } : ema) })
  const setPane = (id: IndicatorPaneId, patch: Partial<IndicatorSettings[IndicatorPaneId]>) => onChange({ ...settings, [id]: { ...settings[id], ...patch } })
  const resize = (id: IndicatorPaneId, size: PaneSize) => onChange(resizePane(settings, id, size))
  const hide = (id: IndicatorPaneId) => {
    setCollapsed(false)
    setPane(id, { enabled: false })
    refocus.current = id
  }
  const hidden = (['volume', 'rsi'] as const).filter(id => !settings[id].enabled)
  const paneName = (id: IndicatorPaneId) => id === 'volume' ? 'Volume' : rsiLabel(settings.rsi)

  return <>
    <div ref={legend} className="indicator-legend" role="group" aria-label="Indicators">
      {!collapsed && <>
        {settings.emas.map((ema, index) => {
          const line = view.lines.find(item => item.id === ema.id)
          const value = line ? valueAt(line.values, hoverIndex) : null
          return <button key={ema.id} type="button" className="indicator-chip" aria-pressed={ema.enabled}
            aria-label={`${ema.enabled ? 'Hide' : 'Show'} ${emaLabel(ema)}`} title={`${ema.enabled ? 'Hide' : 'Show'} ${emaLabel(ema)}`}
            style={{ '--swatch': ema.color } as CSSProperties} onClick={() => toggleEma(index)}>
            <i className="chart-swatch" aria-hidden="true" />
            <span>{emaLabel(ema)}</span>
            {ema.enabled ? <strong>{value === null ? '—' : priceValue.format(value)}</strong> : <EyeOff {...icon} />}
          </button>
        })}
        {hidden.map(id => <button key={id} type="button" className="indicator-chip" aria-pressed="false" data-show-pane={id}
          aria-label={`Show ${paneName(id)} pane`} title={`Show ${paneName(id)} pane`} onClick={() => setPane(id, { enabled: true })}>
          <span>{paneName(id)}</span><Eye {...icon} />
        </button>)}
        <IndicatorSettingsPopover settings={settings} onChange={onChange}>
          <button type="button" className="indicator-chip indicator-chip-icon" aria-label="Indicator settings" title="Indicator settings"><Settings2 {...icon} /></button>
        </IndicatorSettingsPopover>
      </>}
      <button type="button" className="indicator-chip indicator-chip-icon" aria-expanded={!collapsed}
        aria-label={collapsed ? 'Show indicator legend' : 'Collapse indicator legend'} title={collapsed ? 'Show indicator legend' : 'Collapse indicator legend'}
        onClick={() => setCollapsed(!collapsed)}>{collapsed ? <ChevronRight {...icon} /> : <ChevronLeft {...icon} />}</button>
    </div>
    {panes.map((pane, index) => {
      const chosen = settings[pane.id].size
      const value = pane.id === 'volume'
        ? view.volume && valueAt(view.volume.values, hoverIndex)
        : view.rsi && valueAt(view.rsi.values, hoverIndex)
      const name = paneName(pane.id)
      // The required attribution logo sits in the bottom pane's lower-left corner.
      return <div key={pane.id} className="indicator-pane-bar" data-size={pane.effectiveSize} data-last={index === panes.length - 1 || undefined} role="group" aria-label={`${name} pane`}
        style={{ top: pane.top, left: pane.left, width: pane.width } as CSSProperties}>
        <span className="indicator-pane-name">
          {pane.id === 'rsi' && <i className="chart-swatch" style={{ '--swatch': settings.rsi.color } as CSSProperties} aria-hidden="true" />}
          {name}
          <strong>{value === null || value === undefined ? '—' : pane.id === 'volume' ? volumeValue.format(value) : rsiValue.format(value)}</strong>
          {pane.compacted && <small title="The chart is too short to plot this pane. Enlarge the window, expand the chart or minimize another pane.">Too short to plot</small>}
        </span>
        <span className="indicator-pane-actions">
          {pane.compacted ? <>
            {/* Minimized for lack of room: only actions that can make room, or hide it. */}
            {chosen !== 'maximized' && <ChartIconButton label={`Maximize ${name}`} icon={<Maximize2 {...icon} />} onClick={() => resize(pane.id, 'maximized')} />}
            {onExpand && <ChartIconButton label="Expand chart" icon={<Expand {...icon} />} onClick={onExpand} />}
          </> : <>
            {pane.id === 'rsi' && <IndicatorSettingsPopover settings={settings} onChange={onChange}>
              <ChartIconButton label="RSI settings" icon={<Settings2 {...icon} />} />
            </IndicatorSettingsPopover>}
            <ChartIconButton label={chosen === 'minimized' ? `Restore ${name}` : `Minimize ${name}`}
              icon={chosen === 'minimized' ? <ChevronsUpDown {...icon} /> : <ChevronsDownUp {...icon} />}
              onClick={() => resize(pane.id, chosen === 'minimized' ? 'normal' : 'minimized')} />
            <ChartIconButton label={chosen === 'maximized' ? `Restore ${name}` : `Maximize ${name}`}
              icon={chosen === 'maximized' ? <Minimize2 {...icon} /> : <Maximize2 {...icon} />}
              onClick={() => resize(pane.id, chosen === 'maximized' ? 'normal' : 'maximized')} />
          </>}
          <ChartIconButton label={`Hide ${name}`} icon={<EyeOff {...icon} />} onClick={() => hide(pane.id)} />
        </span>
      </div>
    })}
  </>
}
