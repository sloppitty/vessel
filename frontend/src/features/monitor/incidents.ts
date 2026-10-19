import type { AccountMonitor, MonitorDiscrepancy } from '@/api/monitor'

/**
 * Monitor incidents: what still needs the owner, derived from stored monitor records only. Notifications are not a
 * source (preferences, read state and withdrawal make them incomplete). One helper serves the Plays navigation entry,
 * the Plays list, the Play header, the Overview row and the monitor headings, so they always agree.
 */
export type IncidentTone = 'critical' | 'warning' | 'info'
export interface Incident {
  record: MonitorDiscrepancy
  accountId: string
  tone: IncidentTone
  /** The Plays it concerns: its own Play, or the Plays listed for an instrument-level record. */
  playIds: readonly string[]
}
export interface IncidentCounts { total: number; critical: number; warning: number; info: number }

const noIncidents: IncidentCounts = { total: 0, critical: 0, warning: 0, info: 0 }
const toneRank: Record<IncidentTone, number> = { critical: 0, warning: 1, info: 2 }

/** A UTC timestamp as a sortable key with seven fractional digits; the guards only accept `Z` or `+00:00`. */
const utcKey = (value: string) => {
  const [seconds, fraction = ''] = value.replace(/(Z|\+00:00)$/, '').split('.')
  return `${seconds}.${fraction.padEnd(7, '0')}`
}

/**
 * An open record, or an unverifiable record that has not been dismissed since it became unverifiable: the records
 * whose announcement still awaits its close, which are also the ones the server lets the owner dismiss.
 */
export function isIncident(record: MonitorDiscrepancy): boolean {
  if (record.outcome === 'unverifiable' && record.unverifiableAtUtc !== null) {
    return record.dismissedAtUtc === null || utcKey(record.dismissedAtUtc) < utcKey(record.unverifiableAtUtc)
  }
  return record.state === 'open'
}

/** An open record keeps its severity; an outstanding unverifiable record that is not open is a warning, as announced. */
export function incidentTone(record: MonitorDiscrepancy): IncidentTone {
  return record.state === 'open' ? record.severity : 'warning'
}

/** Its own Play, or the instrument's listed Plays (the server lists at most 50; the account still counts the rest). */
export function incidentPlayIds(record: MonitorDiscrepancy): string[] {
  if (record.playId !== null) return [record.playId]
  const listed = record.details.playIds
  return Array.isArray(listed) ? [...new Set(listed.filter((id): id is string => typeof id === 'string' && id.length > 0))] : []
}

/** Incidents among records, most severe first; equal tones keep their stored order. */
export function incidentsOf(records: readonly MonitorDiscrepancy[]): Incident[] {
  return records.filter(isIncident)
    .map(record => ({ record, accountId: record.accountId, tone: incidentTone(record), playIds: incidentPlayIds(record) }))
    .sort((a, b) => toneRank[a.tone] - toneRank[b.tone])
}

/** Every incident in the monitor summary, most severe first. */
export function monitorIncidents(accounts: readonly AccountMonitor[]): Incident[] {
  return incidentsOf(accounts.flatMap(account => account.discrepancies))
}

export function countIncidents(incidents: readonly Pick<Incident, 'tone'>[]): IncidentCounts {
  const counts = { ...noIncidents }
  for (const incident of incidents) { counts.total++; counts[incident.tone]++ }
  return counts
}

/** Counts per Play; Plays without an incident are absent. */
export function incidentsByPlay(incidents: readonly Incident[]): ReadonlyMap<string, IncidentCounts> {
  const byPlay = new Map<string, Incident[]>()
  for (const incident of incidents) for (const id of incident.playIds) byPlay.set(id, [...byPlay.get(id) ?? [], incident])
  return new Map([...byPlay].map(([id, items]) => [id, countIncidents(items)]))
}

export function topTone(counts: IncidentCounts): IncidentTone | null {
  return counts.critical ? 'critical' : counts.warning ? 'warning' : counts.info ? 'info' : null
}

/** "1 critical · 2 warnings · 1 notice". */
export function incidentSummary(counts: IncidentCounts): string {
  return [
    counts.critical ? `${counts.critical} critical` : '',
    counts.warning ? `${counts.warning} ${counts.warning === 1 ? 'warning' : 'warnings'}` : '',
    counts.info ? `${counts.info} ${counts.info === 1 ? 'notice' : 'notices'}` : '',
  ].filter(Boolean).join(' · ')
}

/** "Needs attention: 1 critical · 1 warning", for descriptions that accompany a shorter visible mark. */
export const incidentDescription = (counts: IncidentCounts) => `Needs attention: ${incidentSummary(counts)}`
