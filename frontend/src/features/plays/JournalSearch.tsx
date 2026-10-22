import { useEffect, useRef, useState } from 'react'
import { errorMessage } from '@/api/errors'
import { isEndedStatus, journalFieldLabels, journalSearchPageSize, statusLabels, type JournalSearchHit, type JournalSearchPage, type PlaySummary } from '@/api/plays'
import type { BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Time } from '@/components/ui/time'
import { reviewResult } from '@/lib/format'
import { useDelayedFlag } from '@/lib/use-delayed-flag'
import { PlayIdentity } from './PlayIdentity'
import { multiple } from './receipt-format'

const count = new Intl.NumberFormat('en-US')

/**
 * An ended Play's outcome on a list or search row (Phase 2, item 13, F-07): its latest actual exit, Review's scored result
 * and estimated R, from the search projection and never the plan. Unknown parts read as unknown, with the reason for
 * screen readers; the row is a button, so it holds no tip of its own.
 */
export function PlayOutcomeLine({ play, id }: { play: PlaySummary; id: string }) {
  const outcome = play.outcome
  if (!outcome || !isEndedStatus(play.status)) return null
  const reason = outcome.reason ? <span className="sr-only">. {outcome.reason}</span> : null
  if (outcome.state === 'not-indexed') return <span className="plays-list-outcome" id={id}>Result not indexed yet{reason}</span>
  if (outcome.state === 'cancelled') return <span className="plays-list-outcome" id={id}>No result{reason}</span>
  if (outcome.state === 'unresolved') return <span className="plays-list-outcome" id={id}>Unresolved · no result{reason}</span>
  const result = outcome.resultUsd
  const tone = result === null ? '' : result.startsWith('-') ? ' negative' : /[1-9]/.test(result) ? ' positive' : ''
  return <span className="plays-list-outcome" id={id}>
    Exit <Time at={outcome.exitAtUtc} style="date" fallback="unknown" />{' · '}
    <span className={`plays-outcome-result${tone}`}>{result === null ? 'Result unknown' : reviewResult(result)}</span>
    {result !== null && <>{' · '}{outcome.estimatedR === null ? 'Est. R unknown' : `Est. ${multiple(outcome.estimatedR)}`}</>}
    {reason}
  </span>
}

/** The excerpt with its first match marked; the server says where the match sits. */
function Excerpt({ hit }: { hit: JournalSearchHit }) {
  const { excerpt, matchStart: start, matchLength: length } = hit
  return <span className="journal-hit-excerpt">{excerpt.slice(0, start)}<mark>{excerpt.slice(start, start + length)}</mark>
    {excerpt.slice(start + length)}</span>
}

/**
 * Journal-wide search results (Phase 2, item 13), in place of the list while a search is active: each Play as the list
 * shows it, with the field that matched and an excerpt, 50 per page. Plays the projection does not hold yet are counted,
 * never silently left out.
 */
export function JournalSearchResults({ api, accounts, query, offset, disabled, onOffset, onOpen, onClear }: {
  api: WorkspaceApi
  accounts: BrokerAccount[]
  query: string
  offset: number
  disabled: boolean
  onOffset: (offset: number) => void
  onOpen: (id: string) => void
  onClear: () => void
}) {
  // Try again reads the same page anew: the attempt is part of the request it answers.
  const [attempt, setAttempt] = useState(0)
  const key = JSON.stringify([query, offset, attempt])
  const [result, setResult] = useState<{ api: WorkspaceApi; key: string; page?: JournalSearchPage; error?: string } | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    const [q, from] = JSON.parse(key) as [string, number, number]
    api.searchJournal({ q, ...(from ? { offset: from } : {}) }, controller.signal)
      .then(page => { if (!controller.signal.aborted) setResult({ api, key, page }) })
      .catch(cause => { if (!controller.signal.aborted) setResult({ api, key, error: errorMessage(cause, 'The journal could not be searched. Try again.') }) })
    return () => controller.abort()
  }, [api, key])
  const pending = result === null || result.api !== api || result.key !== key
  const shown = result?.api === api ? result.page ?? null : null
  const error = pending ? null : result.error ?? null
  const skeleton = useDelayedFlag(pending && shown === null)
  const top = useRef<HTMLElement | null>(null)
  const accountName = (id: string) => accounts.find(account => account.id === id)?.name ?? 'Unknown account'
  const page = (next: number) => { onOffset(next); top.current?.scrollIntoView?.({ block: 'start' }) }

  return <section className="journal-results" aria-label="Journal search results" ref={top}>
    <div className="journal-results-head">
      <p role="status" aria-live="polite">{shown === null ? (skeleton ? 'Searching the journal…' : '')
        : shown.total === 0 ? `No plays match “${shown.query}”.`
        : `${count.format(shown.total)} ${shown.total === 1 ? 'play matches' : 'plays match'} “${shown.query}”`}</p>
      <Button variant="ghost" size="sm" onClick={onClear}>Clear search</Button>
    </div>
    {error && <div className="workspace-alert" role="alert"><span>{error}</span>
      <Button variant="ghost" size="sm" onClick={() => { setResult(null); setAttempt(current => current + 1) }}>Try again</Button></div>}
    {shown && shown.notIndexed > 0 && <p className="workspace-scope-note journal-not-indexed" role="note">
      {count.format(shown.notIndexed)} {shown.notIndexed === 1 ? 'play is' : 'plays are'} not indexed yet, so search cannot find {shown.notIndexed === 1 ? 'it' : 'them'}.
      The journal index is filled once after an upgrade with the play-search-rebuild command.</p>}
    {shown && shown.items.length > 0 && <ul className={pending ? 'plays-list list-refetching' : 'plays-list'} aria-busy={pending || undefined}>
      {shown.items.map(hit => {
        const play = hit.play
        const ids = `journal-identity-${play.id} journal-match-${play.id}${play.outcome && isEndedStatus(play.status) ? ` journal-outcome-${play.id}` : ''}`
        return <li key={play.id}>
          <button type="button" className="plays-list-item journal-hit" disabled={disabled} onClick={() => onOpen(play.id)}
            aria-label={`Open ${play.title || 'Untitled play'}`} aria-describedby={ids}>
            <span className="plays-list-title">
              <span className="plays-list-heading"><strong>{play.title || 'Untitled play'}</strong>
                {play.origin === 'retrospective' && <span className="badge play-retrospective">Retrospective</span>}</span>
              {play.tradingMode === 'paper' && <span className="workspace-badge">Paper</span>}
              <PlayIdentity id={`journal-identity-${play.id}`} instrument={play.instrument} source={play.instrumentSource} venueId={play.venueId}
                accountName={accountName(play.accountId)} />
              <span className="journal-hit-match" id={`journal-match-${play.id}`}>
                <span className="journal-hit-field">{journalFieldLabels[hit.field]}<span className="sr-only">:</span></span>{' '}<Excerpt hit={hit} />
              </span>
            </span>
            <span className={`direction-chip ${play.direction}`}>{play.direction === 'long' ? 'Long' : 'Short'}</span>
            <span className={`badge play-status-${play.status}`}>{statusLabels[play.status]}</span>
            <small className="plays-list-meta"><PlayOutcomeLine play={play} id={`journal-outcome-${play.id}`} />
              <span>Updated <Time at={play.updatedAtUtc} style="date" /></span></small>
          </button>
        </li>
      })}
    </ul>}
    {shown && shown.total > journalSearchPageSize && <nav className="journal-pages" aria-label="Journal search pages">
      <Button variant="outline" size="sm" disabled={pending || shown.offset === 0}
        onClick={() => page(Math.max(0, shown.offset - journalSearchPageSize))}>Previous</Button>
      <span>{count.format(shown.offset + 1)}–{count.format(shown.offset + shown.items.length)} of {count.format(shown.total)}</span>
      <Button variant="outline" size="sm" disabled={pending || shown.nextOffset === null}
        onClick={() => { if (shown.nextOffset !== null) page(shown.nextOffset) }}>Next</Button>
    </nav>}
  </section>
}
