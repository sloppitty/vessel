import { useState } from 'react'
import { errorMessage } from '@/api/errors'
import type { MonitorDiscrepancy } from '@/api/monitor'
import type { OrderLink, PlaySummary, RebindLevel } from '@/api/plays'
import { Button } from '@/components/ui/button'
import type { DraftEntry } from './draft'
import { levelDrifts, levelName } from './execution'
import { useJournalState } from './journal-state'

const noReasons: Record<string, string> = {}

/**
 * The answer to a `level.drift` record: each drifted linked stop or target can take the price its order rests at, as one
 * plan revision with a reason. Vessel never edits the order, and an order's price is not a fill. The server refuses when
 * the Play or the order changed since; the monitor clears the record on its next check.
 */
export function LevelDriftAction({ discrepancy, links, entries, origin, playId, version, blocked, onRebind }: {
  discrepancy: MonitorDiscrepancy
  links: readonly OrderLink[]
  entries: readonly DraftEntry[]
  origin: PlaySummary['origin']
  playId: string
  /** The saved Play's version the rebind expects. */
  version: number
  /** Why the rebind is not available now, such as unsaved plan edits. */
  blocked?: string | undefined
  onRebind: (linkId: string, request: RebindLevel) => Promise<void>
}) {
  // Kept by the journal, so a half-written reason survives expanding it.
  const [reasons, setReasons] = useJournalState(`rebind-reasons:${playId}`, noReasons)
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const drifts = discrepancy.state === 'open' ? levelDrifts(discrepancy, links, entries) : []
  if (!drifts.length) return null

  async function rebind(linkId: string, orderId: string, observedPrice: string) {
    const reason = reasons[linkId]?.trim()
    if (!reason || pending || blocked) return
    setPending(linkId); setError(null)
    try {
      await onRebind(linkId, { expectedVersion: version, orderId, observedPrice, reason })
      setReasons(current => ({ ...current, [linkId]: '' }))
    } catch (cause) {
      setError(errorMessage(cause, 'The level was not moved. Reload the play and check again.'))
    } finally { setPending(null) }
  }

  return <div className="level-drift">
    {error && <p className="error" role="alert">{error}</p>}
    {drifts.map(({ link, observed, planned, applied }) => {
      const name = levelName(entries, link, origin)
      const orderId = link.order!.orderId
      const reason = reasons[link.id] ?? ''
      return <div key={link.id} className="level-drift-row">
        <p>{name}: order {orderId} rests at {observed}; the plan said {planned}.</p>
        {applied ? <p className="muted">The plan uses {observed} now. The monitor clears this on its next check.</p>
          : <div className="monitor-dismiss">
            <label><span className="sr-only">Reason to move {name} to {observed}</span>
              <input maxLength={2000} placeholder="Why the level moves (kept with the revision)" value={reason} disabled={pending !== null}
                onChange={event => { const value = event.target.value; setReasons(current => ({ ...current, [link.id]: value })) }} /></label>
            <Button size="sm" variant="outline" disabled={!!blocked || pending !== null || !reason.trim()}
              title={blocked ?? `Move ${name} to the order's price as a plan revision. The order is not changed.`}
              aria-label={pending === link.id ? undefined : `Use ${observed} for ${name}`} onClick={() => { void rebind(link.id, orderId, observed) }}>
              {pending === link.id ? 'Moving…' : `Use ${observed}`}</Button>
          </div>}
      </div>
    })}
  </div>
}
