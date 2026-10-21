import { useEffect, useRef, useState } from 'react'
import type { ReviewProcess, ReviewProcessPage, ReviewProcessSelection, ReviewQuery } from '@/api/review'
import type { WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
import { InfoTip } from '@/components/ui/info-tip'
import { PlayIdentity } from '@/features/plays/PlayIdentity'
import { PostHocBadge } from '@/features/plays/PostHocBadge'
import { reviewDate, reviewMoney, reviewMultiple, reviewPercent } from '@/lib/format'
import { tone } from './format'

/*
 * Process and outcome as cases (phase 1, 1.4b): how the period's ended Plays were answered beside how they turned out.
 * Counts only, in a fixed order; a share appears only where its own denominator is at least `minimumRateN`, and nothing
 * here ranks, labels or prices a mistake. Every count opens exactly the Plays it counts.
 */

/** A share is shown only where its own denominator is at least this. */
export const minimumRateN = 10

export type InspectProcess = (title: string, selection: ReviewProcessSelection) => void

const rows = [['followed', 'Followed'], ['deviated', 'Deviated']] as const
const lanes = [
  ['retrospective', 'Retrospective', 'Imported trades journaled after the fact; they have no contemporaneous plan to follow.'],
  ['unscored', 'Unscored, answered', 'Cancelled, Unresolved or excluded Plays with review answers: they have no scored outcome.'],
  ['unanswered', 'Scored, no adherence answer', 'Scored Plays whose review has not said whether the plan was followed.'],
  ['not-applicable', 'Not applicable', 'Scored Plays answered "not applicable".'],
] as const

function Count({ n, label, onInspect }: { n: number; label: string; onInspect: () => void }) {
  return n > 0 ? <button className="pr-count-link" aria-label={`${n} ${label}`} onClick={onInspect}>{n}</button> : <span>0</span>
}

export function ProcessSection({ process, onInspect }: { process: ReviewProcess; onInspect: InspectProcess }) {
  const answered = rows.reduce((sum, [key]) => sum + process[key].wins + process[key].losses + process[key].scratches, 0)
  return <section className="pr-panel pr-process" aria-label="Process">
    <header className="pr-panel-heading"><h2>Process<InfoTip label="About the process cases">The period&apos;s Closed, Cancelled and Unresolved
      Plays by their review answers as stored, beside their outcome. Each Play counts once, in the first place that applies. A share appears only
      where its own count is at least {minimumRateN}.</InfoTip></h2></header>
    <div className="pr-table-scroll"><table className="pr-process-table"><caption className="sr-only">Adherence by outcome, in a fixed order</caption>
      <thead><tr><th>Plan</th><th>Wins</th><th>Losses</th><th>Scratches</th><th>Win share</th></tr></thead>
      <tbody>{rows.map(([key, name]) => {
        const row = process[key]
        const decided = row.wins + row.losses
        return <tr key={key}><th scope="row">{name}</th>
          <td><Count n={row.wins} label={`${name.toLowerCase()} wins`} onInspect={() => onInspect(`${name} · wins`, { cell: `${key}-win` })} /></td>
          <td><Count n={row.losses} label={`${name.toLowerCase()} losses`} onInspect={() => onInspect(`${name} · losses`, { cell: `${key}-loss` })} /></td>
          <td><Count n={row.scratches} label={`${name.toLowerCase()} scratches`} onInspect={() => onInspect(`${name} · scratches`, { cell: `${key}-scratch` })} /></td>
          <td>{decided >= minimumRateN ? reviewPercent((row.wins / decided).toFixed(6), true) : <span className="pr-muted">—</span>}<small className="pr-sample">n = {decided} decided</small></td>
        </tr>
      })}</tbody></table></div>
    {answered === 0 && <p className="pr-muted">No scored Play in this period says whether its plan was followed.</p>}
    <ul className="pr-process-lanes" aria-label="Other ended Plays">{lanes.map(([key, name, about]) => {
      const n = key === 'not-applicable' ? process.notApplicable : process[key]
      return <li key={key}><span>{name}<InfoTip label={`About ${name.toLowerCase()}`}>{about}</InfoTip></span>
        <Count n={n} label={name.toLowerCase()} onInspect={() => onInspect(name, { lane: key })} /></li>
    })}</ul>
    {process.mistakeTagCount > 0 && <>
      <h3>Mistake tags</h3>
      <div className="pr-table-scroll"><table className="pr-process-table"><caption className="sr-only">Plays per mistake tag, alphabetical</caption>
        <thead><tr><th>Tag</th><th>Wins</th><th>Losses</th><th>Scratches</th><th>Unscored</th></tr></thead>
        <tbody>{process.mistakeTags.map(tag => {
          const total = tag.wins + tag.losses + tag.scratches + tag.unscored
          return <tr key={tag.tag}><th scope="row"><button className="pr-count-link" aria-label={`${tag.tag}: ${total} ${total === 1 ? 'Play' : 'Plays'}`}
            onClick={() => onInspect(`Mistake tag · ${tag.tag}`, { mistakeTag: tag.tag })}>{tag.tag}</button></th>
            <td>{tag.wins}</td><td>{tag.losses}</td><td>{tag.scratches}</td><td>{tag.unscored}</td></tr>
        })}</tbody></table></div>
      {process.mistakeTagCount > process.mistakeTags.length && <p className="pr-muted">Showing {process.mistakeTags.length} of {process.mistakeTagCount} tags.</p>}
    </>}
  </section>
}

const statusNames = { closed: 'Closed', cancelled: 'Cancelled', unresolved: 'Unresolved' } as const
const adherenceNames = { followed: 'Followed', deviated: 'Deviated', 'not-applicable': 'Not applicable' } as const

/** The Plays one process count holds, paged, newest first; each opens its journal. */
export function ReviewProcessList({ api, query, selection, onOpenPlay }: {
  api: WorkspaceApi; query: ReviewQuery; selection: ReviewProcessSelection; onOpenPlay: (id: string, signal: AbortSignal) => Promise<void>
}) {
  const [offset, setOffset] = useState(0)
  const [retry, setRetry] = useState(0)
  const [state, setState] = useState<{ offset: number; retry: number; value: ReviewProcessPage | null; error: string | null } | null>(null)
  const [opening, setOpening] = useState<string | null>(null)
  const [openError, setOpenError] = useState<string | null>(null)
  const request = useRef<AbortController | null>(null)
  useEffect(() => () => request.current?.abort(), [api])
  useEffect(() => {
    const controller = new AbortController()
    api.reviewProcess({ ...query, offset }, selection, controller.signal).then(value => {
      if (!controller.signal.aborted) setState({ offset, retry, value, error: null })
    }).catch(cause => {
      if (!controller.signal.aborted) setState({ offset, retry, value: null, error: errorMessage(cause, 'These Plays could not be loaded.') })
    })
    return () => controller.abort()
  }, [api, query, selection, offset, retry])
  async function open(id: string) {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setOpening(id); setOpenError(null)
    try { await onOpenPlay(id, controller.signal) }
    catch (cause) { if (!controller.signal.aborted) setOpenError(errorMessage(cause, 'The Play could not be opened.')) }
    finally { if (!controller.signal.aborted) setOpening(null) }
  }
  if (state === null || state.offset !== offset || state.retry !== retry) return <p role="status">Loading Plays…</p>
  if (state.error) return <div role="alert"><p>{state.error}</p><button className="pr-outline-button" onClick={() => setRetry(n => n + 1)}>Retry</button></div>
  const page = state.value!
  return <>
    {openError && <p role="alert">{openError}</p>}
    <ul className="pr-exclusion-list">{page.items.map(item => <li key={item.id}>
      <button className="pr-play-link" disabled={opening !== null} onClick={() => { void open(item.id) }}>{item.title || item.instrument}{opening === item.id ? ' · Opening…' : ''}</button>
      <PlayIdentity instrument={item.instrument} venueId={item.venueId} accountName={item.accountName} />
      <small>{statusNames[item.status]} · {item.origin === 'retrospective' ? 'Retrospective' : 'Planned'}{item.direction ? ` · ${item.direction === 'long' ? 'Long' : 'Short'}` : ''} · {reviewDate(item.atUtc, true)}</small>
      <p>{item.net === null ? 'Not scored' : <><span className={tone(item.net)}>{reviewMoney(item.net, true)}</span> · {reviewPercent(item.returnPercent)} return{item.rMultiple !== null && <> · {reviewMultiple(item.rMultiple)}</>}</>}
        {' · '}{item.adherence ? adherenceNames[item.adherence] : 'No adherence answer'}{item.mistakeTag ? ` · ${item.mistakeTag}` : ''}
        {' · '}{item.reviewCompletion ? 'Review complete' : 'Review not complete'}</p>
      {item.plannedAfterExecution && <PostHocBadge />}
    </li>)}</ul>
    {!page.items.length && <p>No Plays in this selection.</p>}
    <div className="pr-pagination pr-exclusion-pagination"><button className="pr-outline-button" disabled={page.offset === 0} onClick={() => setOffset(Math.max(0, page.offset - 50))}>Previous Plays</button>
      <span>{page.items.length ? page.offset + 1 : 0}–{page.offset + page.items.length} of {page.total}</span>
      <button className="pr-outline-button" disabled={page.nextOffset === null} onClick={() => { if (page.nextOffset !== null) setOffset(page.nextOffset) }}>Next Plays</button>
    </div>
  </>
}
