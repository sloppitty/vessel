import type { PriceStep } from '@/lib/price-step'
import { useEffect, useLayoutEffect, useRef, useState, type FormEvent } from 'react'
import { Plus, Trash2, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import type { DraftEntry, PlayDraft } from './draft'
import { levelName } from './execution'
import { planShare, planTextState, positiveFinitePlanNumber } from './numbers'
import {
  addChartExit, exitsOf, formatDraggedPrice, levelTag, overlayPrice, parseOverlayId, removeExit, setExitShare, withExits,
} from './levels'

/**
 * In-chart editor for one planned level. Changing a price switches percentage levels to price
 * units, as dragging does. A share-only edit leaves the level's unit and value unchanged.
 */
export function LevelEditor({ entries, entry, overlayId, anchor, direction, leverage, priceStep = null, onApply, onClose }: {
  entries: readonly DraftEntry[]
  entry: DraftEntry
  overlayId: string
  anchor: { x: number; y: number }
  direction: PlayDraft['direction']
  leverage: number
  priceStep?: PriceStep
  onApply: (next: DraftEntry, label: string) => void
  onClose: () => void
}) {
  const ref = parseOverlayId(overlayId)
  const exit = ref && ref.kind !== 'entry' ? exitsOf(entry, ref.kind).find(current => current.id === ref.levelId) ?? null : null
  const current = overlayPrice(entry, overlayId, direction, leverage)
  const [initialPrice] = useState(ref?.kind === 'entry' ? entry.price : exit?.unit === 'price' ? exit.value : current === null ? '' : formatDraggedPrice(current, priceStep))
  const [price, setPrice] = useState(initialPrice)
  const [priceEdited, setPriceEdited] = useState(false)
  const [share, setShare] = useState(exit?.share ?? '')
  const [error, setError] = useState('')
  const input = useRef<HTMLInputElement>(null)
  const form = useRef<HTMLFormElement>(null)
  const [bounds, setBounds] = useState({ width: 600, height: 400, own: 190 })
  useEffect(() => { input.current?.select() }, [])
  // Keep the editor inside the chart area next to its level, re-measuring whenever the chart or
  // the editor itself changes size (responsive resize, sidebar collapse, a validation message).
  useLayoutEffect(() => {
    const element = form.current
    const parent = element?.parentElement
    if (!element || !parent) return
    const measure = () => {
      if (!parent.clientWidth) return
      setBounds(current => {
        const next = { width: parent.clientWidth, height: parent.clientHeight, own: element.offsetHeight || 190 }
        return next.width === current.width && next.height === current.height && next.own === current.own ? current : next
      })
    }
    measure()
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure)
    observer?.observe(parent)
    observer?.observe(element)
    window.addEventListener('resize', measure)
    return () => {
      observer?.disconnect()
      window.removeEventListener('resize', measure)
    }
  }, [])
  if (!ref) return null

  const tag = levelTag(entries, ref)
  const readable = levelName(entries, { role: ref.kind, entryId: entry.id, levelId: ref.kind === 'entry' ? null : ref.levelId })
  const name = ref.kind === 'entry' ? 'Entry' : ref.kind === 'stop' ? 'Stop' : 'Target'
  const entryTag = levelTag(entries, { entryId: entry.id, kind: 'entry' })
  const percentUnit = exit?.unit === 'percent'
  const entryPriced = overlayPrice(entry, `${entry.id}|entry`, direction) !== null

  function save(event: FormEvent) {
    event.preventDefault()
    // Text the server would refuse gets its reason, named as the server names the field; then the range checks.
    const label = ref?.kind === 'entry' || !ref || !exit ? `${entry.name} price` : `${entry.name} ${ref.kind} ${exitsOf(entry, ref.kind).indexOf(exit) + 1}`
    const priceText = planTextState(price, label)
    if (priceText.status === 'invalid') { setError(priceText.reason); return }
    if (positiveFinitePlanNumber(price) === null) { setError('Enter a price above zero.'); return }
    const shareText = planTextState(share, `${label} share`)
    if (exit && shareText.status === 'invalid') { setError(shareText.reason); return }
    if (exit && share.trim() !== '' && planShare(share) === null) { setError('Share must be 0 to 100%.'); return }
    let next = entry
    if (priceEdited) {
      // The draft keeps the text as it will be saved, without surrounding whitespace.
      const text = price.trim()
      next = ref?.kind === 'entry' ? { ...entry, price: text } : ref ? withExits(entry, ref.kind, exitsOf(entry, ref.kind).map(level =>
        level.id === ref.levelId ? { ...level, unit: 'price', value: text } : level)) : entry
    }
    if (exit && ref?.kind !== 'entry') next = setExitShare(next, ref!.kind, exit.id, share.trim())
    onApply(next, `Edit ${readable.toLowerCase()}`)
    onClose()
  }
  const act = (next: DraftEntry | null, label: string) => { if (next) { onApply(next, label); onClose() } }

  const width = 236
  const fitted = Math.min(width, bounds.width - 16)
  const left = Math.max(8, Math.min(anchor.x + 10, bounds.width - fitted - 8))
  const top = Math.max(8, Math.min(anchor.y - 24, bounds.height - bounds.own - 8))
  return <form ref={form} className="level-editor" style={{ left, top, width: fitted }} aria-label={`Edit ${readable}`} onSubmit={save}
    onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose() } }}>
    <header>
      <i style={{ background: entry.color }} aria-hidden="true" />
      <strong>{tag === name ? name : `${tag} · ${name}`}</strong>
      <button type="button" className="level-editor-close" aria-label="Close level editor" onClick={onClose}><X size={13} aria-hidden="true" /></button>
    </header>
    <div className="level-editor-fields">
      <label>Price<input ref={input} inputMode="decimal" value={price} aria-invalid={error !== ''} onChange={event => { setPrice(event.target.value); setPriceEdited(true); setError('') }} /></label>
      {exit && <label>Share <small>% of entry</small><input inputMode="decimal" value={share} onChange={event => { setShare(event.target.value); setError('') }} /></label>}
    </div>
    {percentUnit && priceEdited && <p className="level-editor-note">Editing this price switches the level from % to price units.</p>}
    {error && <p className="level-editor-error" role="alert">{error}</p>}
    <div className="level-editor-actions">
      <Button type="submit" size="sm">Save</Button>
      <Button type="button" size="sm" variant="ghost" onClick={onClose}>Cancel</Button>
    </div>
    <div className="level-editor-more">
      {(['target', 'stop'] as const).map(kind => <button key={kind} type="button" disabled={!entryPriced} title={entryPriced ? undefined : 'Set the entry price first'}
        onClick={() => act(addChartExit(entry, kind, direction, leverage, priceStep), `Add ${entryTag} ${kind}`)}><Plus size={12} aria-hidden="true" />Add {kind}</button>)}
      {exit && ref.kind !== 'entry' && <button type="button" onClick={() => act(removeExit(entry, ref.kind, exit.id), `Remove ${tag}`)}>
        <Trash2 size={12} aria-hidden="true" />Remove {ref.kind}</button>}
    </div>
  </form>
}
