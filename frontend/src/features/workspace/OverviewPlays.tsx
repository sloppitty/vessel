import { useEffect, useRef, useState } from 'react'
import { ArrowDownRight, ArrowRight, ArrowUpRight, BookOpen } from 'lucide-react'
import { isEndedStatus, isReviewed, statusLabels, type PlayCounts, type PlaySummary, type PlayView } from '@/api/plays'
import type { BrokerAccount, WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Skeleton, SkeletonStatus } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { PlayIdentity } from '@/features/plays/PlayIdentity'
import { errorMessage } from '@/api/errors'
import { time } from '@/lib/format'
import { useDelayedFlag } from '@/lib/use-delayed-flag'

const filters = [
  { id: 'drafts', label: 'Drafts', description: 'Saved drafts, most recently updated first', empty: 'No drafts' },
  { id: 'progress', label: 'In progress', description: 'Planned, paused and open plays', empty: 'No plays in progress' },
  { id: 'review', label: 'Needs review', description: 'Closed, cancelled or unresolved plays whose review is not complete', empty: 'No plays waiting for review' },
  { id: 'all', label: 'All', description: 'All saved plays, most recently updated first', empty: 'No saved plays yet' },
] as const satisfies readonly { id: PlayView; label: string; description: string; empty: string }[]
type Filter = typeof filters[number]['id']
/** Rows per tab: the most recent few, with View all for the rest. */
const recentRows = 5
const ended = (play: PlaySummary) => isEndedStatus(play.status)

export function OverviewPlays({ api, accounts, reloadGeneration, onBrowse, onOpen, scope = 'all' }: {
  api: WorkspaceApi
  accounts: BrokerAccount[]
  /** Overview scope: `all`, `unassigned`, `portfolio:{id}` or `account:{id}`. */
  scope?: string
  reloadGeneration: number
  /** Opens the Plays page on this view. */
  onBrowse: (view: PlayView) => void
  onOpen: (id: string, signal: AbortSignal) => Promise<void>
}) {
  const [filter, setFilter] = useState<Filter>('progress')
  const [retry, setRetry] = useState(0)
  const [result, setResult] = useState<{ api: WorkspaceApi; key: string; plays?: PlaySummary[]; error?: string } | null>(null)
  const [counts, setCounts] = useState<{ api: WorkspaceApi; key: string; counts: PlayCounts | null } | null>(null)
  const [opening, setOpening] = useState<string | null>(null)
  const [openError, setOpenError] = useState<string | null>(null)
  const openRequest = useRef<AbortController | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    openRequest.current = controller
    return () => controller.abort()
  }, [])
  const key = JSON.stringify([filter, scope, reloadGeneration, retry])
  useEffect(() => {
    const request = new AbortController()
    api.plays({ view: filter, scope, pageSize: recentRows }, request.signal)
      // Drafts and In progress may come back in full; Overview shows the latest few.
      .then(list => { if (!request.signal.aborted) setResult({ api, key, plays: list.plays.slice(0, recentRows) }) })
      .catch(cause => { if (!request.signal.aborted) setResult({ api, key, error: errorMessage(cause, 'The change could not be completed. Try again.') }) })
    return () => request.abort()
  }, [api, filter, scope, key])
  const countsKey = JSON.stringify([scope, reloadGeneration, retry])
  useEffect(() => {
    const request = new AbortController()
    api.playCounts({ scope }, request.signal)
      .then(next => { if (!request.signal.aborted) setCounts({ api, key: countsKey, counts: next }) })
      .catch(() => { if (!request.signal.aborted) setCounts({ api, key: countsKey, counts: null }) })
    return () => request.abort()
  }, [api, scope, countsKey])
  const loaded = result?.api === api && result.key === key
  const previous = result?.api === api ? result.plays ?? null : null
  const skeleton = useDelayedFlag(!loaded && previous === null)
  const plays = loaded ? result.plays ?? [] : previous ?? []
  const tabCounts = counts?.api === api && counts.key === countsKey ? counts.counts : null

  async function open(id: string) {
    const signal = openRequest.current!.signal
    setOpening(id); setOpenError(null)
    try { await onOpen(id, signal) }
    catch (cause) { if (!signal.aborted) setOpenError(errorMessage(cause, 'The change could not be completed. Try again.')) }
    finally { if (!signal.aborted) setOpening(null) }
  }

  return <section className="shell-panel overview-plays" aria-label="Plays overview">
    <header className="shell-panel-heading"><h2>Plays</h2>
      <button className="text-action" onClick={() => onBrowse(filter)}>View all<ArrowRight size={14} /></button>
    </header>
    <Tabs value={filter} onValueChange={value => setFilter(value as Filter)} className="overview-play-tabs">
      <TabsList aria-label="Show overview plays">
        {filters.map(item => <TabsTrigger key={item.id} value={item.id} title={item.description}>
          {item.label}{' '}<span className="overview-play-count">{tabCounts ? tabCounts[item.id] : '—'}</span>
        </TabsTrigger>)}
      </TabsList>
      {loaded && result.error && <div className="workspace-alert" role="alert"><span>{result.error}</span>
        <Button variant="ghost" size="sm" onClick={() => setRetry(current => current + 1)}>Try again</Button></div>}
      {openError && <div className="workspace-alert" role="alert">{openError}</div>}
      {filters.filter(item => item.id === filter).map(item => <TabsContent key={item.id} value={item.id}>
        {!loaded && previous === null && (skeleton ? <SkeletonStatus label="Loading plays…"><ul className="overview-play-list">
          {[0, 1, 2].map(row => <li key={row}><span className="overview-play-row overview-play-skeleton">
            <span className="overview-play-copy"><Skeleton className="skeleton-title" /><Skeleton className="skeleton-identity" /></span>
            <span className="overview-play-state"><Skeleton className="skeleton-badge" /><Skeleton className="skeleton-time" /></span>
          </span></li>)}
        </ul></SkeletonStatus> : <div className="overview-play-pending" aria-busy="true" />)}
        {(loaded ? !result.error : previous !== null) && (plays.length ? <ul className={loaded ? 'overview-play-list' : 'overview-play-list list-refetching'} aria-busy={!loaded || undefined}>
          {plays.map(play => <li key={play.id}>
            <button className="overview-play-row" disabled={opening !== null} aria-busy={opening === play.id}
              aria-label={`Open ${play.title || 'Untitled play'}`} aria-describedby={`overview-play-identity-${play.id}`} onClick={() => { void open(play.id) }}>
              <span className="overview-play-copy"><strong>{play.title || 'Untitled play'}</strong>
                <PlayIdentity id={`overview-play-identity-${play.id}`} instrument={play.instrument} source={play.instrumentSource} venueId={play.venueId}
                  accountName={accounts.find(account => account.id === play.accountId)?.name ?? 'Unknown account'} />
                <span className={`overview-play-direction ${play.direction}`}>{play.direction === 'long' ? <ArrowUpRight size={12} /> : <ArrowDownRight size={12} />}{play.direction === 'long' ? 'Long' : 'Short'}</span>
              </span>
              <span className="overview-play-state">
                <span className={`workspace-badge ${play.status === 'open' ? 'positive-badge' : ended(play) && !isReviewed(play) ? 'warning-badge' : ''}`}>{statusLabels[play.status]}</span>
                <small>{opening === play.id ? 'Opening…' : time(play.updatedAtUtc)}</small>
              </span>
            </button>
          </li>)}
        </ul> : <div className={loaded ? 'workspace-empty small-empty' : 'workspace-empty small-empty list-refetching'}><BookOpen size={25} />
          <strong>{item.empty}</strong>
          <Button variant="outline" size="sm" onClick={() => onBrowse(item.id)}>Open Plays<ArrowRight size={13} /></Button>
        </div>)}
      </TabsContent>)}
    </Tabs>
  </section>
}
