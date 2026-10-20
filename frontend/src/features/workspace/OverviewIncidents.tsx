import type { BrokerAccount } from '@/api/workspace'
import { TriangleAlert } from 'lucide-react'
import { countIncidents, topTone, type Incident } from '@/features/monitor/incidents'
import { discrepancyTitle } from '@/features/monitor/discrepancy-text'
import { IncidentPill } from '@/features/monitor/IncidentBadge'
import './overview-incidents.css'

const shown = 4

/**
 * A compact row above the Overview metric tiles while a monitor incident is open; nothing otherwise. It covers every
 * monitored account, whatever the Overview scope, so an unprotected position is never scoped out of sight. A Play's
 * record opens the Play; an instrument-level record opens its account, whose detail lists every record.
 */
export function OverviewIncidents({ incidents, more = false, accounts, onOpenPlay, onOpenAccount }: {
  incidents: readonly Incident[]
  /** Further incidents exist beyond the server's record cap: the count is a lower bound. */
  more?: boolean
  accounts: readonly BrokerAccount[]
  onOpenPlay: (id: string) => void
  onOpenAccount: (id: string) => void
}) {
  const counts = countIncidents(incidents, more)
  const tone = topTone(counts)
  if (!tone) return null
  const accountName = (id: string) => accounts.find(account => account.id === id)?.name ?? 'Unknown account'
  const hidden = incidents.length - shown
  return <section className="shell-panel overview-incidents" data-tone={tone} aria-label="Monitor incidents">
    <div className="overview-incidents-head">
      <TriangleAlert size={16} aria-hidden="true" />
      <strong>{counts.total}{counts.more ? '+' : ''} {counts.total === 1 && !counts.more ? 'incident needs' : 'incidents need'} attention</strong>
      <IncidentPill counts={counts} />
    </div>
    <ul>{incidents.slice(0, shown).map(({ record, tone: itemTone }) => {
      // Only a record of one Play opens it; instrument-level records may span several, so they open the account.
      const play = record.playId
      return <li key={record.id} data-tone={itemTone}>
        <button type="button" className="text-action" onClick={() => play ? onOpenPlay(play) : onOpenAccount(record.accountId)}
          title={play ? 'Open the Play' : 'Open the account'}>
          {discrepancyTitle(record.kind)} · {record.contractId}</button>
        <small>{accountName(record.accountId)}</small>
      </li>
    })}</ul>
    {hidden > 0 && <p className="overview-incidents-more">and {hidden} more on the affected accounts</p>}
  </section>
}
