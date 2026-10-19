import type { LinkOrder, PlayExecution, PlaySummary } from '@/api/plays'
import { Button } from '@/components/ui/button'
import { Link2, Link2Off } from 'lucide-react'
import { MonitorDiscrepancies, MonitorPanel, MonitorWatches } from '@/features/monitor/MonitorPanel'
import { levelKey } from '@/features/monitor/discrepancy-text'
import type { AccountMonitor } from '@/api/monitor'
import type { DraftEntry } from './draft'
import { describeOrder, fillSummary, levelName, levelOptions, orderFitsRole } from './execution'
import { NativeSelect } from '@/components/ui/native-select'
import { InfoTip } from '@/components/ui/info-tip'
import { useJournalState } from './journal-state'

// One shared empty object, so an untouched panel does not look like new state on every render.
const noChoices: Record<string, string> = {}

const time = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Venue orders and fills linked to the play, suggestions to confirm and orders that could be linked by hand. */
const pnlBasisNote = {
  gross: 'As the venue reports it, before fees; fees are listed separately.',
  'net-of-fee': 'As the venue reports it, with fees already taken off; the fees are listed for reference.',
  mixed: 'Some fills report closed PnL before fees and some after.',
} as const

export function ExecutionPanel({ execution, error, busy, entries, direction, origin = 'planned', onCheck, onLink, onUnlink, monitor, monitorBusy = false, monitorError = null, retryAtUtc, monitorUnavailable = false, currentBookRead = true, onDismiss, onManageCredential }: {
  execution: PlayExecution | null
  error: string | null
  busy: boolean
  entries: readonly DraftEntry[]
  direction: 'long' | 'short'
  origin?: PlaySummary['origin']
  monitor?: AccountMonitor | null
  monitorBusy?: boolean
  monitorError?: string | null
  monitorUnavailable?: boolean
  /** False when the account's venue does not return the current order book (a venue capability). */
  currentBookRead?: boolean
  retryAtUtc?: string | undefined
  onDismiss?: (id: string, note?: string) => Promise<void>
  onManageCredential?: (() => void) | undefined
  onCheck: () => void
  onLink: (request: LinkOrder) => void
  onUnlink: (linkId: string) => void
}) {
  // The chosen roles belong to the journal, not to this tab: expanding the journal remounts the panel. Scoped by play.
  const [choices, setChoices] = useJournalState(`execution-choices:${execution?.playId ?? ''}`, noChoices)
  if (!execution) return <div className="execution-panel"><p className="muted" role="status">{error ?? 'Loading execution…'}</p></div>
  const retrospective = origin === 'retrospective'
  const editable = !retrospective && ['planned', 'paused', 'open'].includes(execution.status)
  const options = levelOptions(entries, execution.status === 'open')
  const shownMonitor = monitor === undefined ? execution.monitor : monitor
  // This Play's own recorded stop coverage; an Open Play without one has not been checked since it opened.
  const coverage = (shownMonitor?.coverage ?? []).filter(row => row.playId === execution.playId)
  const { totals } = execution
  // Monitor records and watches name this Play's levels ("Entry 1 stop") rather than only the order's role.
  const recordLevel = (key: unknown) => { const level = levelKey(key); return level ? levelName(entries, level, origin) : null }
  const watchLevel = (watch: { linkId: string }) => { const link = execution.links.find(item => item.id === watch.linkId); return link ? levelName(entries, link, origin) : null }
  return <div className="execution-panel">
    {!retrospective && (!execution.tracked || !shownMonitor) && <div className="execution-header">
      <p className="muted" title={execution.tracked ? 'Orders link automatically by price and side; Vessel asks only when several plays could own one.' : undefined}>
        {execution.tracked ? execution.checkedAtUtc ? `Checked ${time(execution.checkedAtUtc)}` : 'Not checked yet'
          : execution.reason ?? 'Execution is not tracked for this play.'}</p>
    </div>}
    {error && <p className="error" role="alert">{error}</p>}
    {/* The heading follows this Play's own records and stop coverage, the ones listed below it. */}
    {shownMonitor && <MonitorPanel monitor={shownMonitor} discrepancies={execution.discrepancies}
      coverage={coverage} unchecked={execution.status === 'open' && execution.tracked && coverage.length === 0} currentBookRead={currentBookRead}
      busy={busy || monitorBusy} error={monitorError}
      retryAtUtc={retryAtUtc} statusUnavailable={monitorUnavailable} onCheck={onCheck} onManageCredential={onManageCredential} />}
    {onDismiss && <MonitorDiscrepancies discrepancies={execution.discrepancies} busy={busy} onDismiss={onDismiss} levelName={recordLevel} />}
    <MonitorWatches watches={execution.watches} levelName={watchLevel} />
    <dl className="execution-totals">
      <div><dt>Entered</dt><dd>{totals.enteredQuantity}</dd></div>
      <div><dt>Exited</dt><dd>{totals.exitedQuantity}</dd></div>
      <div><dt>Still open</dt><dd>{totals.openQuantity}</dd></div>
      <div title={pnlBasisNote[totals.closedPnlBasis]}><dt>Venue closed PnL{totals.closedPnlBasis === 'net-of-fee' ? ' · after fees' : ''}</dt><dd>{totals.closedPnlUsd} USD</dd></div>
      <div><dt>Fees</dt><dd>{totals.fees.length ? totals.fees.map(fee => `${fee.amount} ${fee.token}`).join(' · ') : '0'}</dd></div>
    </dl>

    {execution.issues.length > 0 && <section aria-label="Not reconciled" className="execution-section execution-issues">
      <h3>Not reconciled<InfoTip label="About unreconciled executions">Vessel neither closes nor scores a play while these remain. Fills that contradict their link are left out of the totals.</InfoTip></h3>
      <ul className="execution-list">{execution.issues.map(issue => <li key={issue}>{issue}</li>)}</ul>
    </section>}

    {execution.suggestions.length > 0 && <section aria-label="Orders to confirm" className="execution-section">
      <h3>Confirm these orders<InfoTip label="Why confirm these orders">Each also matches another play on this instrument, so Vessel does not assign it on its own.</InfoTip></h3>
      <ul className="execution-list">{execution.suggestions.map(link => <li key={link.id}>
        <span><strong>{levelName(entries, link, origin)}</strong>{link.order && <small>{describeOrder(link.order)} · {link.order.status}</small>}</span>
        <span className="execution-actions">
          <Button size="sm" disabled={busy || !editable || !link.order} onClick={() => onLink({ orderId: link.order?.orderId ?? '', role: link.role,
            ...(link.entryId ? { entryId: link.entryId } : {}), ...(link.levelId ? { levelId: link.levelId } : {}) })}>
            <Link2 size={13} />Link here</Button>
          <Button size="sm" variant="ghost" disabled={busy} onClick={() => onUnlink(link.id)}>Not this play</Button>
        </span>
      </li>)}</ul>
    </section>}

    <section aria-label="Linked orders" className="execution-section">
      <h3>Linked orders</h3>
      {execution.links.length ? <ul className="execution-list">{execution.links.map(link => <li key={link.id}>
        <span><strong>{levelName(entries, link, origin)}</strong>
          <small>{link.order ? `${describeOrder(link.order)} · ${link.order.status}` : 'Order details unavailable'} · filled {link.filledQuantity}
            {retrospective ? ' · owner-confirmed retrospective link' : link.source === 'automatic' ? ' · linked automatically' : ' · linked by you'}</small>
          {link.fills.length > 0 && <small className="execution-fills" title={link.fills.map(fill =>
            `${fill.direction} ${fill.quantity} at ${fill.price}`).join('\n')}>{fillSummary(link.fills)}</small>}</span>
        {editable && <Button size="sm" variant="ghost" disabled={busy} onClick={() => onUnlink(link.id)} aria-label={`Unlink ${levelName(entries, link, origin)} order`}
          title="Unlink. Vessel will not link this order to this level again."><Link2Off size={13} />Unlink</Button>}
      </li>)}</ul> : <p className="muted">{retrospective ? 'No execution links are available.' : 'No orders linked yet.'}</p>}
    </section>

    {editable && execution.unlinkedOrders.length > 0 && <section aria-label="Other orders on this instrument" className="execution-section">
      <h3>Other orders on this instrument<InfoTip label="About other orders">Placed since this play was created and not linked to any play. Link one by hand if it belongs here.</InfoTip></h3>
      <ul className="execution-list">{execution.unlinkedOrders.map(order => {
        // Only roles the order's side and flags allow; a contradicting fill is refused by the server.
        const allowed = options.filter(item => orderFitsRole(order, item.request.role, direction))
        const chosen = choices[order.orderId] ?? ''
        const option = allowed.find(item => item.value === chosen)
        return <li key={order.orderId}>
          <span><strong>{describeOrder(order)}</strong><small>{order.status} · placed {time(order.placedAtUtc)}</small></span>
          <span className="execution-actions">
            <NativeSelect density="compact" aria-label={`Level for order ${order.orderId}`} value={chosen} disabled={busy}
              onChange={event => setChoices(current => ({ ...current, [order.orderId]: event.target.value }))}>
              <option value="">Link as…</option>
              {allowed.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
            </NativeSelect>
            <Button size="sm" variant="outline" disabled={busy || !option} onClick={() => option && onLink({ orderId: order.orderId, ...option.request })}>
              <Link2 size={13} />Link</Button>
          </span>
        </li>
      })}</ul>
    </section>}
    {execution.notice && <details className="execution-notice"><summary>About these numbers</summary><p className="muted">{execution.notice}</p></details>}
  </div>
}
