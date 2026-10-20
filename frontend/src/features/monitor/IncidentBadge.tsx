import { TriangleAlert } from 'lucide-react'
import { incidentDescription, incidentSummary, topTone, type IncidentCounts } from './incidents'
import './incidents.css'

/**
 * A pill naming open monitor incidents by severity, e.g. on a Plays list row. Nothing without an incident. With `id`,
 * a hidden "Needs attention: …" text carries that id for an `aria-describedby`.
 */
export function IncidentPill({ counts, id }: { counts: IncidentCounts | undefined; id?: string }) {
  const tone = counts ? topTone(counts) : null
  if (!counts || !tone) return null
  return <>
    <span className="incident-pill" data-tone={tone}><TriangleAlert size={12} aria-hidden="true" />{incidentSummary(counts)}</span>
    {id && <span id={id} className="sr-only">{incidentDescription(counts)}</span>}
  </>
}

/**
 * The persistent count on the Plays navigation entry. Its warning triangle and severity tone set it apart from the
 * unread notification count; the link keeps its name and is described by `id`'s text.
 */
export function IncidentCount({ counts, id }: { counts: IncidentCounts; id: string }) {
  const tone = topTone(counts)
  if (!tone) return null
  return <>
    <span className="incident-count" data-tone={tone} aria-hidden="true"><TriangleAlert size={11} />{counts.total > 99 ? '99+' : counts.more ? `${counts.total}+` : counts.total}</span>
    <span id={id} className="sr-only">{incidentDescription(counts)}</span>
  </>
}
