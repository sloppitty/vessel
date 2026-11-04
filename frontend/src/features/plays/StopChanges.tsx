import { InfoTip } from '@/components/ui/info-tip'
import type { DraftEntry } from './draft'
import { stopText, type EntryStopHistory } from './stop-history'

const levels = (list: EntryStopHistory['original']) => list.length ? list.map(stopText).join(' · ') : 'none'

/** A closed Play's moved stops: each entry's original stops beside the ones it closed with, from the plan revisions. */
export function StopChanges({ history, entries }: { history: readonly EntryStopHistory[]; entries: readonly DraftEntry[] }) {
  const moved = history.filter(entry => entry.moves > 0)
  if (!moved.length) return null
  return <section aria-label="Stop changes" className="execution-section receipt-entries stop-changes">
    <h3>Stops moved<InfoTip label="About moved stops">The stops as first planned beside the stops the Play closed with, from its saved plan revisions. On the chart, an original stop is the faint “orig” line.</InfoTip></h3>
    <ul className="execution-list">{moved.map(entry => {
      const index = entries.findIndex(item => item.id === entry.entryId)
      return <li key={entry.entryId}>
        <span><strong>{entries[index]?.name || `Entry ${index + 1 || ''}`.trim()}</strong>
          <small>Moved {entry.moves === 1 ? 'once' : `${entry.moves} times`}</small></span>
        <span className="receipt-figures">
          <span>Original {levels(entry.original)} <small>(revision {entry.fromRevision})</small></span>
          <span>Final {levels(entry.final)}</span>
        </span>
      </li>
    })}</ul>
  </section>
}
