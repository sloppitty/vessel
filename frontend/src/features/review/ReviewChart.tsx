import { useId, useState } from 'react'
import type { ReviewDocument } from '@/api/review'
import { reviewDate, reviewMoney } from '@/lib/format'
import { decimalLess } from '@/lib/decimal'
import { tone } from './format'

export function ReviewChart({ report }: { report: ReviewDocument }) {
  const [mode, setMode] = useState<'cumulative' | 'daily' | 'drawdown'>('cumulative')
  const [hovered, setHovered] = useState<number | null>(null)
  const id = useId().replaceAll(':', '')
  const series = report.series
  const values = [0, ...series.map(point => Number(mode === 'daily' ? point.net : point[mode]))]
  const min = Math.min(...values), max = Math.max(...values)
  const spread = Math.max(max - min, 1)
  const low = min - spread * .12, high = max + spread * .12
  const x = (index: number) => 14 + (index + 1) / series.length * 716
  const y = (value: number) => 15 + (high - value) / (high - low) * 196
  const line = [{ x: 14, value: 0 }, ...series.map((point, i) => ({ x: x(i), value: Number(mode === 'daily' ? point.net : point[mode]) }))]
    .map((point, i) => `${i ? 'L' : 'M'}${point.x},${y(point.value)}`).join(' ')
  const selected = hovered === null ? undefined : series[hovered]
  const selectedValue = selected ? mode === 'daily' ? selected.net : selected[mode] : null
  // Retain the exact API value for the minimum; floating point is only for geometry.
  const chartValue = mode === 'drawdown' ? series.reduce<string | null>((lowest, point) =>
    lowest === null || decimalLess(point.drawdown, lowest) ? point.drawdown : lowest, null) : report.metrics.net
  const labels = [...new Set([0, Math.floor((series.length - 1) / 3), Math.floor((series.length - 1) * 2 / 3), series.length - 1])]
  // Imported positions are counted with Plays, so the unit is results.
  const counted = (n: number) => `${n} ${report.metrics.positions ? n === 1 ? 'result' : 'results' : n === 1 ? 'Play' : 'Plays'}`
  const bucketLabel = report.bucketDays === 1 ? 'Daily' : `${report.bucketDays}-day`
  return <section className="pr-panel pr-chart">
    <header className="pr-panel-heading">
      <h2>{mode === 'drawdown' ? `${bucketLabel}-closing drawdown` : mode === 'daily' ? `${bucketLabel} P&L` : 'Cumulative P&L'}</h2>
      <div className="pr-segmented" role="group" aria-label="Chart view">
        {(['cumulative', 'daily', 'drawdown'] as const).map(view => <button key={view} aria-pressed={mode === view} onClick={() => { setMode(view); setHovered(null) }}>{view === 'cumulative' ? 'P&L' : view === 'daily' ? bucketLabel : 'Drawdown'}</button>)}
      </div>
    </header>
    <div className="pr-chart-caption" aria-live="polite">
      {selected ? <><strong className={tone(selectedValue)}>{reviewMoney(selectedValue, true)}</strong><span>{reviewDate(selected.atUtc)} · {counted(selected.count)}</span></>
        : <strong className={tone(chartValue)}>{reviewMoney(chartValue, true)}</strong>}
    </div>
    {!series.length ? <div className="pr-chart-empty">No scored Plays in this selection.</div> : <svg className="pr-chart-svg" viewBox="0 0 810 252" role="img" aria-label={`${mode} closed-play P&L chart. ${reviewMoney(chartValue)}.`}>
      <defs><linearGradient id={id} x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="var(--pr-green)" stopOpacity=".19" /><stop offset="100%" stopColor="var(--pr-green)" stopOpacity="0" /></linearGradient></defs>
      {[max, (max + min) / 2, min].filter((v, i, list) => list.indexOf(v) === i).map(value => <g key={value}><line x1="14" x2="735" y1={y(value)} y2={y(value)} stroke="var(--pr-border)" strokeDasharray="3 5" /><text x="805" y={y(value) + 4} textAnchor="end">{reviewMoney(String(value))}</text></g>)}
      <line x1="14" x2="735" y1={y(0)} y2={y(0)} stroke="var(--pr-muted)" opacity=".3" />
      {mode === 'daily' ? series.map((point, i) => <rect key={point.atUtc} x={x(i) - Math.max(1, 620 / series.length) / 2} y={Math.min(y(Number(point.net)), y(0))} width={Math.max(1, 620 / series.length)} height={Math.max(1, Math.abs(y(Number(point.net)) - y(0)))} fill={Number(point.net) >= 0 ? 'var(--pr-green)' : 'var(--pr-red)'} opacity=".8" rx="1" />) : <>
        <path d={`${line} L730,${y(0)} L14,${y(0)} Z`} fill={mode === 'drawdown' ? 'var(--pr-red)' : `url(#${id})`} opacity={mode === 'drawdown' ? .12 : 1} />
        <path d={line} fill="none" stroke={mode === 'drawdown' || Number(chartValue) < 0 ? 'var(--pr-red)' : 'var(--pr-green)'} strokeWidth="2.4" strokeLinejoin="round" strokeLinecap="round" />
      </>}
      {labels.map(i => <text key={i} x={x(i)} y="242" textAnchor={i === series.length - 1 ? 'end' : 'start'}>{reviewDate(series[i]!.atUtc)}</text>)}
      {series.map((point, i) => <rect key={point.atUtc} x={x(i) - 358 / series.length} width={716 / series.length} y="0" height="217" fill="transparent" onMouseEnter={() => setHovered(i)} onMouseLeave={() => setHovered(null)}><title>{reviewDate(point.atUtc)}: {reviewMoney(point.net, true)}, {counted(point.count)}</title></rect>)}
      {selected && <line x1={x(hovered!)} x2={x(hovered!)} y1="10" y2="216" stroke="var(--pr-muted)" strokeDasharray="3 3" pointerEvents="none" />}
    </svg>}
    <details className="pr-chart-data"><summary>Chart data{report.bucketDays > 1 ? ` · ${report.bucketDays}-day buckets` : ''}</summary><div className="pr-table-scroll"><table><caption className="sr-only">Chart values in nominal USD, by bucket ending date UTC</caption><thead><tr><th>Ending UTC</th><th>{report.metrics.positions ? 'Results' : 'Plays'}</th><th>Net P&L</th><th>Cumulative</th><th>Drawdown</th></tr></thead><tbody>{series.map(point => <tr key={point.atUtc}><th scope="row">{reviewDate(point.atUtc, true)}</th><td>{point.count}</td><td>{reviewMoney(point.net, true)}</td><td>{reviewMoney(point.cumulative, true)}</td><td>{reviewMoney(point.drawdown)}</td></tr>)}</tbody></table></div></details>
  </section>
}
