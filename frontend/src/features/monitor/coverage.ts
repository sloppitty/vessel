import type { MonitorCoverage } from '@/api/monitor'
import type { PlayExecution } from '@/api/plays'
import type { LevelEvidence, StopEvidenceOf } from '@/features/plays/levels'

/**
 * Stop coverage as the monitor recorded it (Phase 0, 0.4c). Shared helpers for the monitor heading, its coverage lines
 * and the chart's stop labels, so "Monitored" never stands beside a structural unknown, shared or unsupported coverage.
 */

/** Why coverage is what it is, keyed by the server's reason code. Unknown codes fall back to a neutral sentence. */
const reasons: Record<string, string> = {
  'stop-orders': 'Resting stop orders cover the open quantity.',
  'whole-position-stop': 'A whole-position stop covers the position.',
  'no-stop': 'No resting stop was seen at the venue.',
  short: 'The resting stops cover less than the open quantity.',
  cancelled: 'A linked stop was cancelled at the venue.',
  'shared-whole-position-stop': 'A whole-position stop covers this position together with other Plays on the instrument.',
  'opposing-plays': 'Opposite Plays net out on this instrument, so one stop cannot be allocated to each Play.',
  'position-mismatch': 'A whole-position stop is shared, but the venue position differs from the Plays\' total.',
  'position-unknown': 'The venue position could not be compared with the Plays.',
  'history-gap': 'The fill history may have a gap.',
  'omitted-executions': 'The venue reported executions Vessel could not verify.',
  'incomplete-book': 'The open-order read was incomplete.',
  'orders-unavailable': 'Orders could not be read.',
  'fills-after-read': 'Fills arrived after the order read.',
  mechanics: 'A linked stop\'s trigger mechanics were not recognized.',
  'quantity-unknown': 'The Play\'s open quantity could not be settled from its fills.',
  'instrument-unknown': 'The instrument could not be identified at the venue.',
}

/** Said wherever the venue's order reader does not return the current order book. */
export const currentBookNotRead = 'Missing, short and cancelled stop checks do not run here: this venue\'s order reader does not return the current order book.'

export const coverageReason = (code: string) => reasons[code] ?? 'The monitor recorded no further detail.'

const uncovered = (row: MonitorCoverage) => row.state === 'known' && row.covered === false

/**
 * The heading for the coverage in scope, least certain first, or null when every recorded Play is covered (or none is
 * recorded), so the run state ("Monitored") may head the panel.
 */
export function coverageHeading(rows: readonly MonitorCoverage[]): string | null {
  if (rows.some(uncovered)) return 'Stop not seen at the venue'
  if (rows.some(row => row.state === 'unknown')) return 'Stop coverage unknown'
  if (rows.some(row => row.state === 'stale')) return 'Stop coverage stale'
  if (rows.some(row => row.state === 'unsupported')) return 'Current book not read'
  if (rows.some(row => row.state === 'shared')) return 'Stop shared with other Plays'
  return null
}

/** The heading's tone: uncovered and unknown coverage warn; stale, unsupported and shared stay neutral. */
export const coverageTone = (rows: readonly MonitorCoverage[]): 'warning' | 'info' | null =>
  rows.some(uncovered) || rows.some(row => row.state === 'unknown') ? 'warning'
    : rows.some(row => row.state !== 'known') ? 'info' : null

/**
 * One line per distinct outcome that is not plain covered, with the number of Plays when there are several.
 * `currentBookRead: false` (the venue capability) states the unsupported checks even before a Play is open.
 */
export function coverageLines(rows: readonly MonitorCoverage[], time: (value: string) => string, currentBookRead = true): string[] {
  const lines = new Map<string, { text: string; count: number }>()
  const add = (key: string, text: string) => { const line = lines.get(key); lines.set(key, { text, count: (line?.count ?? 0) + 1 }) }
  for (const row of rows) {
    if (row.state === 'known' && row.covered) continue
    if (row.state === 'stale') add(`stale|${row.checkedAtUtc}`, `Stop coverage was last checked ${time(row.checkedAtUtc)} and is out of date.`)
    else if (row.state === 'unsupported') add('unsupported', currentBookNotRead)
    else if (row.state === 'unknown') add(`unknown|${row.reason}`, `Stop coverage is unknown. ${coverageReason(row.reason)}`)
    else add(`${row.state}|${row.reason}`, coverageReason(row.reason))
  }
  if (!currentBookRead && !lines.has('unsupported')) lines.set('unsupported', { text: currentBookNotRead, count: 0 })
  return [...lines.values()].map(line => line.count > 1 ? `${line.count} Plays · ${line.text}` : line.text)
}

const defaultTime = (value: string) => new Date(value).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
const unknown: LevelEvidence = { text: 'unknown', confirmed: false }
const notRead: LevelEvidence = { text: 'not read', confirmed: false }

/**
 * What the venue evidence says about each planned stop of an Open, tracked Play, for the chart's level tags. Only a stop
 * the last current check saw resting is confirmed ("resting as of 14:02"); anything else keeps the evidence's own words:
 * "not seen at the venue", "unknown", or "not read" where nothing reads the venue's current book. Undefined where the
 * plan makes no venue claim (drafts, plans before an entry fills, untracked Plays), so the plan keeps its plain tags.
 */
export function stopEvidenceFor(execution: PlayExecution | null | undefined, time: (value: string) => string = defaultTime): StopEvidenceOf | undefined {
  if (!execution || execution.status !== 'open' || !execution.tracked) return undefined
  if (!execution.monitor) return () => notRead
  const coverage = execution.monitor.coverage?.find(row => row.playId === execution.playId)
  if (!coverage) return () => unknown
  const stale = coverage.state === 'stale'
  return (entryId, levelId) => {
    const stop = coverage.stops.find(item => item.entryId === entryId && item.levelId === levelId)
    if (!stop) return coverage.recordedState === 'unsupported' ? notRead : unknown
    switch (stop.evidence) {
      // Stale evidence keeps its date but no longer confirms anything.
      case 'resting': return stop.observedAtUtc ? { text: `resting as of ${time(stop.observedAtUtc)}`, confirmed: !stale } : unknown
      case 'not-seen': return stale ? unknown : { text: 'not seen at the venue', confirmed: false }
      case 'not-read': return notRead
      default: return unknown
    }
  }
}
