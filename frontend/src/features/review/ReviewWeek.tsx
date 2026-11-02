import { useEffect, useMemo, useState } from 'react'
import { ArrowLeft, ChevronLeft, ChevronRight, ClipboardList, ShieldCheck, Tags } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import type { NextAction } from '@/api/plays'
import type { ReviewDocument, ReviewProcessSelection, ReviewQuery } from '@/api/review'
import type { WorkspaceApi } from '@/api/workspace'
import { Input } from '@/components/ui/input'
import { InfoTip } from '@/components/ui/info-tip'
import { Time } from '@/components/ui/time'
import { reviewDate, reviewMoney, reviewPercent } from '@/lib/format'
import { tone } from './format'
import { minimumRateN, ProcessSection, ReviewProcessList } from './ReviewProcess'
import { ReviewExclusions } from './ReviewExclusions'

const localDate = /^\d{4}-\d{2}-\d{2}$/
/** A local date seven days away, by calendar days (no time zone is involved in date-only arithmetic). */
const shift = (date: string, days: number) => new Date(Date.parse(`${date}T00:00:00Z`) + days * 86_400_000).toISOString().slice(0, 10)
const day = (date: string) => reviewDate(`${date}T00:00:00Z`, true)
/** Today in the reporting zone, as the date picker's upper bound; UTC if this browser does not know the zone. */
const today = (zone: string, at: string) => {
  try { return new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(at)) }
  catch { return at.slice(0, 10) }
}
/** "3 Plays", or "3 Plays · 2 positions" when imported positions are among `count`, as Review says it. */
const results = (count: number, positions = 0) => {
  const plays = count - positions
  return `${plays} ${plays === 1 ? 'Play' : 'Plays'}${positions ? ` · ${positions} ${positions === 1 ? 'position' : 'positions'}` : ''}`
}
const weekday = (date: string) => new Date(`${date}T00:00:00Z`).toLocaleDateString('en-GB', { weekday: 'long', timeZone: 'UTC' })

/** The Plays page's scope for Review's filters: an account, a portfolio, Unassigned or all. */
function playScope(query: ReviewQuery) {
  return query.accountId ? `account:${query.accountId}` : query.portfolio === 'all' || query.portfolio === 'unassigned'
    ? query.portfolio : `portfolio:${query.portfolio}`
}

type Drill = { kind: 'process'; title: string; selection: ReviewProcessSelection } | { kind: 'exclusions' }

/**
 * The week page (Phase 2, item 2): on demand, for a chosen reporting week, Review's own computations over the week's
 * half-open range in the owner's reporting zone, with the week's repeated mistake tags, the latest next actions and the
 * Needs review count. No reminder, schedule or generated text.
 */
export function ReviewWeek({ api, filters, onOpenPlay }: {
  api: WorkspaceApi
  /** Review's filters and positions decision; the period is replaced by the week. */
  filters: ReviewQuery
  onOpenPlay: (id: string, signal: AbortSignal) => Promise<void>
}) {
  const [week, setWeek] = useState('current')
  const [attempt, setAttempt] = useState(0)
  const [drill, setDrill] = useState<Drill | null>(null)
  const query = useMemo<ReviewQuery>(() => ({ ...filters, week, offset: 0 }), [filters, week])
  const [loaded, setLoaded] = useState<{ query: ReviewQuery; attempt: number; report: ReviewDocument | null; error: string | null } | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    api.review(query, controller.signal)
      .then(report => { if (!controller.signal.aborted) setLoaded({ query, attempt, report, error: null }) })
      .catch(cause => { if (!controller.signal.aborted) setLoaded({ query, attempt, report: null, error: errorMessage(cause, 'The week could not be loaded.') }) })
    return () => controller.abort()
  }, [api, query, attempt])
  const current = loaded?.query === query && loaded.attempt === attempt ? loaded : null
  const report = current?.report ?? null
  const shown = report?.week
  // Drill-downs read the same week by its first day, so a new week boundary never changes what a count holds.
  const weekQuery = useMemo<ReviewQuery>(() => ({ ...filters, week: shown?.start ?? week, offset: 0 }), [filters, shown?.start, week])

  const [extras, setExtras] = useState<{ api: WorkspaceApi; scope: string; actions: NextAction[] | null; review: number | null } | null>(null)
  const scope = playScope(filters)
  useEffect(() => {
    const controller = new AbortController()
    // Current state, not the week's: the newest next actions written, and the Plays waiting for a review now.
    Promise.allSettled([api.nextActions(controller.signal), api.playCounts({ scope }, controller.signal)]).then(([actions, counts]) => {
      if (!controller.signal.aborted) setExtras({ api, scope, actions: actions.status === 'fulfilled' ? actions.value : null,
        review: counts.status === 'fulfilled' ? counts.value.review : null })
    })
    return () => controller.abort()
  }, [api, scope])
  const known = extras?.api === api && extras.scope === scope ? extras : null

  if (drill) return <section className="pr-week" aria-label="Week drill-down">
    <button className="pr-outline-button" onClick={() => setDrill(null)}><ArrowLeft size={15} />Back to the week</button>
    {drill.kind === 'process'
      ? <><h3>{drill.title}</h3><ReviewProcessList api={api} query={weekQuery} selection={drill.selection} onOpenPlay={onOpenPlay} /></>
      : <><h3>Excluded Plays</h3><ReviewExclusions api={api} initialQuery={weekQuery} onOpenPlay={onOpenPlay} /></>}
  </section>

  const stats = report?.metrics
  const decided = stats ? stats.wins + stats.losses : 0
  // Plays only: positions Review cannot score are counted apart, never as excluded Plays (Sol round 1 on item 2).
  const excluded = report ? report.coverage.incompleteFees + report.coverage.incompleteExecutions + (report.coverage.unresolved ?? 0) +
    (report.coverage.cancelled ?? 0) : 0
  const positions = report?.positions
  const omitted = positions?.included ? positions.omitted ?? 0 : 0
  // Repeated: a tag on at least two of the week's Plays.
  const repeated = report?.process?.mistakeTags.filter(t => t.wins + t.losses + t.scratches + t.unscored >= 2) ?? []
  const latest = shown && report ? Date.parse(shown.toUtc) > Date.parse(report.asOfUtc) : false
  return <section className="pr-week" aria-label="Week review">
    <div className="pr-week-nav" role="group" aria-label="Choose a week">
      <button className="pr-outline-button" disabled={!shown} onClick={() => shown && setWeek(shift(shown.start, -7))}><ChevronLeft size={15} />Previous week</button>
      <label className="pr-week-date"><span>Week of</span>
        <Input type="date" value={shown?.start ?? ''} max={shown && report ? today(shown.timeZone, report.asOfUtc) : undefined} aria-label="A date in the week"
          onChange={event => { if (localDate.test(event.target.value)) setWeek(event.target.value) }} /></label>
      <button className="pr-outline-button" disabled={!shown || latest} onClick={() => shown && setWeek(shift(shown.start, 7))}>Next week<ChevronRight size={15} /></button>
      <button className="pr-outline-button" disabled={latest} onClick={() => setWeek('current')}>This week</button>
    </div>
    {!current && <p role="status">Loading the week…</p>}
    {current?.error && <div className="pr-empty" role="alert"><p>{current.error}</p>
      <button className="pr-outline-button" onClick={() => setAttempt(n => n + 1)}>Retry the week</button></div>}
    {report && shown && stats && <>
      <p className="pr-week-range">{day(shown.start)} to {day(shift(shown.start, 6))} · {shown.timeZone} · weeks start on {weekday(shown.start)}
        <InfoTip label="About the reporting week">The reporting week from Settings: from midnight on its first day in this zone, up to the next
          week. Results are Review&apos;s, counted by close time; facts are as of now.</InfoTip></p>
      {shown.data === 'none' && <p className="pr-week-state" role="note">Vessel has no data for this week in this scope.</p>}
      {shown.data === 'empty' && <p className="pr-week-state" role="note">No Play closed, was cancelled or was declared Unresolved this week.</p>}
      {shown.data === 'some' && <section className="pr-scorecard" aria-label="Week scorecard">
        <article><span>Net P&L</span><strong className={tone(stats.net)}>{reviewMoney(stats.net, true)}</strong>
          <small className="pr-sample">{results(stats.count, stats.positions)} · {stats.wins} W · {stats.losses} L · {stats.scratches} S</small></article>
        <article><span>Batting average</span><strong>{decided >= minimumRateN ? reviewPercent(stats.batting, true) : '—'}</strong>
          <small className="pr-sample">n = {decided} decided{decided < minimumRateN ? ` · no rate below ${minimumRateN}` : ''}</small></article>
        <article><span>Expectancy per Play</span><strong className={tone(stats.expectancy)}>{reviewMoney(stats.expectancy, true)}</strong>
          <small className="pr-sample">n = {stats.count}</small></article>
        <article><span>Excluded Plays</span><strong>{excluded}</strong>
          {excluded > 0 ? <button className="pr-count-link" onClick={() => setDrill({ kind: 'exclusions' })}>Inspect exclusions</button>
            : <small className="pr-sample">None this week</small>}
          {omitted > 0 && <small className="pr-sample">{omitted} imported {omitted === 1 ? 'position' : 'positions'} not scored</small>}</article>
      </section>}
      {shown.data === 'some' && stats.positions ? <p className="pr-weight-note">Imported positions are included. Plays weigh {positions?.playWeight ?? '2'}×
        a position in batting average and expectancy; P&L is not weighted.</p> : null}
      {report.process && shown.data === 'some' && <ProcessSection process={report.process}
        onInspect={(title, selection) => setDrill({ kind: 'process', title, selection })} />}
      <section className="pr-coverage" aria-label="Week coverage"><ShieldCheck size={16} /><details>
        <summary>{results(stats.count, stats.positions)} scored · {excluded} excluded {excluded === 1 ? 'Play' : 'Plays'}{omitted ? ` · ${omitted} ${omitted === 1 ? 'position' : 'positions'} not scored` : ''} · {stats.reviewed} {stats.reviewed === 1 ? 'review' : 'reviews'} complete</summary>
        <p>{report.coverage.incompleteFees} incomplete fees · {report.coverage.incompleteExecutions} incomplete executions · {report.coverage.unresolved ?? 0} Unresolved · {report.coverage.cancelled ?? 0} cancelled.</p>
        <p>{report.coverage.unassignedFills} unassigned fills in this week.</p>
        {positions?.included && <p>{positions.positions ?? 0} imported {positions.positions === 1 ? 'position' : 'positions'} included{omitted ? `; ${omitted} closed ${omitted === 1 ? 'position' : 'positions'} could not be scored` : ''}.</p>}
        <p>{report.coverage.notice}</p>
        {report.coverage.firstFillAtUtc && <p>Earliest retained fill in this scope: <Time at={report.coverage.firstFillAtUtc} />.</p>}
      </details></section>
      <section className="pr-panel pr-week-tags" aria-label="Repeated mistake tags">
        <header className="pr-panel-heading"><h2>Repeated mistake tags</h2><Tags size={17} aria-hidden="true" /></header>
        {repeated.length === 0 ? <p className="pr-muted">No mistake tag is on more than one of this week&apos;s Plays.</p>
          : <ul>{repeated.map(t => <li key={t.tag}><button className="pr-count-link"
            onClick={() => setDrill({ kind: 'process', title: `Mistake tag · ${t.tag}`, selection: { mistakeTag: t.tag } })}>
            {t.tag}: {t.wins + t.losses + t.scratches + t.unscored} Plays</button>
            <small> {t.wins} W · {t.losses} L · {t.scratches} S · {t.unscored} unscored</small></li>)}</ul>}
      </section>
      <section className="pr-panel pr-week-actions" aria-label="Next actions and reviews">
        <header className="pr-panel-heading"><h2>Next actions</h2><ClipboardList size={17} aria-hidden="true" /></header>
        <p className="pr-muted">The latest next actions you wrote, as on the Plays page. They have no done state, so this is not limited
          to the week.</p>
        {!known ? <p role="status">Loading…</p> : known.actions === null ? <p role="alert">Next actions could not be loaded.</p>
          : known.actions.length === 0 ? <p className="pr-muted">No next actions written yet.</p>
          : <ul>{known.actions.map(action => <li key={action.playId}><button className="pr-play-link" onClick={() => {
            void onOpenPlay(action.playId, new AbortController().signal)
          }}>{action.title}</button><p>{action.nextAction}</p></li>)}</ul>}
        {known && <p>{known.review === null ? 'The Needs review count could not be loaded.'
          : `${known.review} ${known.review === 1 ? 'Play needs' : 'Plays need'} review now in this scope${filters.instrument ? ', every asset' : ''}.`}</p>}
      </section>
    </>}
  </section>
}
