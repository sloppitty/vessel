import { useMemo, useRef, useState, type FormEvent } from 'react'
import { errorMessage } from '@/api/errors'
import { allocationReasonLabels, allocationReasons, statusLabels, type AllocateFill, type AllocationPlay, type AllocationReason, type SharedFill } from '@/api/plays'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { NativeSelect } from '@/components/ui/native-select'
import { allocationRemainder, feeShares, isAllocationQuantity } from './allocation'

/** Statuses that take an exit part, and those that take a flip's entry part, as the server checks them. */
const exitStatuses = ['open', 'unresolved']
const entryStatuses = ['planned', 'paused', 'open']

const fallbackRequestId = () => `${Date.now().toString(16).padStart(12, '0').slice(-12)}`

/** A random request ID; retries of the same payload reuse it, so the server applies the split once. */
const newRequestId = () => globalThis.crypto?.randomUUID?.() ?? `00000000-0000-4000-8000-${fallbackRequestId()}`

/**
 * Phase 2, item 11: the owner splits one fill between Plays. A shared exit takes a quantity per Play of the direction the
 * fill closes; a flip takes this Play's exit and another Play's entry. The remainder of the fill and the fee split are shown
 * as the owner types; the server decides and refuses what does not add up.
 */
export function AllocationDialog({ shared, plays, playId, busy, onSubmit, onClose }: {
  shared: SharedFill
  plays: readonly AllocationPlay[]
  /** The Play whose panel opened the dialog. */
  playId: string
  busy: boolean
  onSubmit: (request: AllocateFill) => Promise<void>
  onClose: () => void
}) {
  const { fill } = shared
  const flip = shared.kind === 'flip'
  // A later execution of an order a flip split only opens plays (Sol round 1 on item 11).
  const opening = shared.kind === 'entry'
  // A sell closes longs and opens shorts; a buy closes shorts and opens longs.
  const closes = fill.side === 'sell' ? 'long' : 'short'
  const byId = useMemo(() => new Map(plays.map(play => [play.playId, play])), [plays])
  const onLines = useMemo(() => new Set(shared.lines.map(line => line.playId)), [shared.lines])
  const openOf = (id: string) => shared.open.find(item => item.playId === id)?.openQuantity ?? null
  const exitPlays = plays.filter(play => play.direction === closes && (exitStatuses.includes(play.status) || onLines.has(play.playId)))
  const entryPlays = plays.filter(play => play.direction !== closes && (entryStatuses.includes(play.status) || onLines.has(play.playId)))

  const currentEntry = shared.lines.find(line => line.role === 'entry')
  const currentExit = shared.lines.find(line => line.role === 'exit')
  const [quantities, setQuantities] = useState<Record<string, string>>(() => {
    if (shared.lines.length) return Object.fromEntries(shared.lines.map(line => [line.playId, line.quantity]))
    if (opening) return entryPlays.some(play => play.playId === playId) ? { [playId]: fill.quantity } : {}
    if (!flip) return {}
    // A flip's exit closes this Play exactly: its open quantity without this fill.
    const open = openOf(playId)
    return open && isAllocationQuantity(open) ? { [playId]: open } : {}
  })
  const [exitPlay, setExitPlay] = useState(currentExit?.playId ?? (byId.get(playId)?.direction === closes ? playId : exitPlays[0]?.playId ?? ''))
  const [entryPlay, setEntryPlay] = useState(currentEntry?.playId ?? '')
  const [entryId, setEntryId] = useState(currentEntry?.entryId ?? '')
  // The entry each opening line fills, by Play; the first plan entry unless the owner chooses another.
  const [entryIds, setEntryIds] = useState<Record<string, string>>(() =>
    Object.fromEntries(shared.lines.filter(line => line.entryId !== null).map(line => [line.playId, line.entryId!])))
  const [reason, setReason] = useState<AllocationReason>(shared.reason ?? (flip || opening ? 'flip' : 'shared-close'))
  const [note, setNote] = useState(shared.note ?? '')
  const [error, setError] = useState<string | null>(null)
  const requests = useRef<{ key: string; id: string } | null>(null)

  const entryPlayInfo = byId.get(entryPlay)
  const entryOptions = entryPlayInfo?.entryIds ?? []
  const chosenEntry = entryOptions.includes(entryId) ? entryId : entryOptions[0] ?? ''
  const entryOf = (id: string) => { const options = byId.get(id)?.entryIds ?? []; return options.includes(entryIds[id] ?? '') ? entryIds[id]! : options[0] ?? '' }
  // The lines as they would be sent: one exit per Play with a quantity, a flip's exit and entry, or entries alone.
  const lines = opening
    ? entryPlays.filter(play => (quantities[play.playId] ?? '').trim() !== '')
      .map(play => ({ playId: play.playId, role: 'entry' as const, entryId: entryOf(play.playId) || null, quantity: quantities[play.playId]!.trim() }))
    : flip
    ? [
        ...(exitPlay ? [{ playId: exitPlay, role: 'exit' as const, entryId: null, quantity: quantities[exitPlay] ?? '' }] : []),
        ...(entryPlay ? [{ playId: entryPlay, role: 'entry' as const, entryId: chosenEntry || null, quantity: quantities[entryPlay] ?? '' }] : []),
      ]
    : exitPlays.filter(play => (quantities[play.playId] ?? '').trim() !== '')
      .map(play => ({ playId: play.playId, role: 'exit' as const, entryId: null, quantity: quantities[play.playId]!.trim() }))
  const remainder = allocationRemainder(fill.quantity, lines.map(line => line.quantity))
  const fees = remainder === '0' ? feeShares(fill.fee, fill.quantity, lines) : null
  const remainderLine = fees?.find(share => share.remainder !== null)
  const title = (id: string) => byId.get(id)?.title || 'This play'
  const ready = remainder === '0' && note.trim() !== '' && lines.length > 0 && (!flip || lines.length === 2 && !!chosenEntry) &&
    lines.every(line => line.role === 'exit' || !!line.entryId)

  const send = async (next: AllocateFill['lines']) => {
    const involved = [...new Set([...shared.lines.map(line => line.playId), ...next.map(line => line.playId)])]
    const versions = involved.map(id => byId.get(id))
    if (versions.some(play => !play)) { setError('Reload the play: one of these plays is not listed any more.'); return }
    const body = {
      fillId: shared.fillId, expectedRevision: shared.revision, reason, note: note.trim(),
      plays: versions.map(play => ({ playId: play!.playId, expectedVersion: play!.version })), lines: next,
    }
    // The same payload is retried with the same request, so the server never applies it twice.
    const key = JSON.stringify(body)
    if (requests.current?.key !== key) requests.current = { key, id: newRequestId() }
    setError(null)
    try { await onSubmit({ requestId: requests.current.id, ...body }); onClose() }
    catch (cause) { setError(errorMessage(cause, 'The allocation could not be saved. Try again.')) }
  }
  const submit = (event: FormEvent) => { event.preventDefault(); if (ready) void send(lines) }

  const quantityField = (id: string, label: string, disabled = false) => <input id={`allocation-${id}`} inputMode="decimal" aria-label={label}
    value={quantities[id] ?? ''} disabled={busy || disabled} onChange={event => setQuantities(current => ({ ...current, [id]: event.target.value }))} />

  return <Dialog open onOpenChange={next => { if (!next && !busy) onClose() }}>
    <DialogContent className="workspace-dialog allocation-dialog">
      <DialogHeader><DialogTitle>{flip ? 'Split this flip' : opening ? 'Allocate this entry' : 'Allocate this fill'}</DialogTitle><DialogDescription>
        {fill.direction} {fill.quantity} at {fill.price} · order {shared.orderId}. The lines must add up to the fill. These plays get
        no venue result; each keeps its own fills' result.
      </DialogDescription></DialogHeader>
      <form className="workspace-form" onSubmit={submit} aria-busy={busy}>
        {flip ? <>
          <label htmlFor="allocation-exit-play">Closes</label>
          <NativeSelect id="allocation-exit-play" value={exitPlay} disabled={busy} onChange={event => setExitPlay(event.target.value)}>
            {exitPlays.map(play => <option key={play.playId} value={play.playId}>{play.title || 'Untitled play'} · {statusLabels[play.status]}</option>)}
          </NativeSelect>
          {exitPlay && <p className="muted">Open without this fill: {openOf(exitPlay) ?? 'unknown'}</p>}
          {exitPlay && quantityField(exitPlay, `Exit quantity for ${title(exitPlay)}`)}
          <label htmlFor="allocation-entry-play">Opens</label>
          <NativeSelect id="allocation-entry-play" value={entryPlay} disabled={busy} onChange={event => setEntryPlay(event.target.value)}>
            <option value="">Choose a play</option>
            {entryPlays.map(play => <option key={play.playId} value={play.playId}>{play.title || 'Untitled play'} · {statusLabels[play.status]}</option>)}
          </NativeSelect>
          {entryPlay && entryOptions.length > 1 && <NativeSelect aria-label="Entry" value={chosenEntry} disabled={busy} onChange={event => setEntryId(event.target.value)}>
            {entryOptions.map((id, index) => <option key={id} value={id}>Entry {index + 1}</option>)}
          </NativeSelect>}
          {entryPlay && quantityField(entryPlay, `Entry quantity for ${title(entryPlay)}`)}
        </> : opening ? <ul className="execution-list allocation-lines">
          {entryPlays.map(play => <li key={play.playId}>
            <span><strong>{play.title || 'Untitled play'}</strong><small>{statusLabels[play.status]}</small></span>
            {play.entryIds.length > 1 && <NativeSelect aria-label={`Entry for ${play.title || 'Untitled play'}`} value={entryOf(play.playId)} disabled={busy}
              onChange={event => setEntryIds(current => ({ ...current, [play.playId]: event.target.value }))}>
              {play.entryIds.map((id, index) => <option key={id} value={id}>Entry {index + 1}</option>)}
            </NativeSelect>}
            {quantityField(play.playId, `Entry quantity for ${play.title || 'Untitled play'}`)}
          </li>)}
        </ul> : <ul className="execution-list allocation-lines">
          {exitPlays.map(play => <li key={play.playId}>
            <span><strong>{play.title || 'Untitled play'}</strong><small>{statusLabels[play.status]} · open without this fill {openOf(play.playId) ?? 'unknown'}</small></span>
            {quantityField(play.playId, `Quantity for ${play.title || 'Untitled play'}`, play.status === 'closed')}
          </li>)}
        </ul>}
        <p className="allocation-remainder" role="status" aria-live="polite">
          {remainder === null ? 'Enter quantities as plain decimals above zero.' : remainder === '0' ? `Lines add up to ${fill.quantity}.` : `Left to allocate: ${remainder}`}
        </p>
        {fees && <dl className="allocation-fees" aria-label="Fee split">
          {fees.map(share => <div key={share.playId}><dt>{title(share.playId)}</dt><dd>{share.fee} {fill.feeToken}</dd></div>)}
          {remainderLine && <div><dt>Rounding remainder</dt><dd>{remainderLine.remainder} {fill.feeToken} to {title(remainderLine.playId)}</dd></div>}
        </dl>}
        <label htmlFor="allocation-reason">Reason</label>
        <NativeSelect id="allocation-reason" value={reason} disabled={busy} onChange={event => setReason(event.target.value as AllocationReason)}>
          {allocationReasons.map(value => <option key={value} value={value}>{allocationReasonLabels[value]}</option>)}
        </NativeSelect>
        <label htmlFor="allocation-note">Note</label>
        <textarea id="allocation-note" value={note} maxLength={2000} rows={2} disabled={busy} onChange={event => setNote(event.target.value)} />
        {error && <p className="error" role="alert">{error}</p>}
        <div className="management-actions">
          {shared.lines.length > 0 && <Button type="button" variant="ghost" disabled={busy || !note.trim()} onClick={() => { void send([]) }}>Remove allocation</Button>}
          <Button type="button" variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" disabled={busy || !ready}>{busy ? 'Saving…' : 'Save allocation'}</Button>
        </div>
      </form>
    </DialogContent>
  </Dialog>
}
