import { useId, useState } from 'react'
import { Receipt } from 'lucide-react'
import type { VenueInfo } from '@/api/system'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useVenues } from '@/api/venues'
import { VenueBadge } from '@/components/venues/VenueBadge'
import { costPercentText, maxCostPercent, useCostAssumptions, type CostAssumption } from '@/features/plays/cost-assumptions'
import './cost-settings.css'

/** One venue's two fields, saved together in this browser. */
function VenueCosts({ venue, saved, onSave }: { venue: VenueInfo; saved: CostAssumption | undefined; onSave: (value: CostAssumption) => void }) {
  const id = useId()
  const [fee, setFee] = useState(saved?.feePercent ?? '')
  const [slippage, setSlippage] = useState(saved?.slippagePercent ?? '')
  // Another tab or user scope can change the stored value; show it unless an edit is pending.
  const [shown, setShown] = useState(saved)
  const [note, setNote] = useState<string | null>(null)
  if (shown !== saved) {
    setShown(saved)
    setFee(saved?.feePercent ?? '')
    setSlippage(saved?.slippagePercent ?? '')
  }
  const feePercent = costPercentText(fee)
  const slippagePercent = costPercentText(slippage)
  const valid = feePercent !== undefined && slippagePercent !== undefined
  const dirty = valid && (feePercent !== (saved?.feePercent ?? null) || slippagePercent !== (saved?.slippagePercent ?? null))
  return <form className="cost-settings-venue" aria-label={`${venue.name} cost assumptions`} noValidate
    onSubmit={event => {
      event.preventDefault()
      if (!dirty) return
      onSave({ feePercent: feePercent ?? null, slippagePercent: slippagePercent ?? null })
      setNote(feePercent === null && slippagePercent === null ? 'Cleared.' : 'Saved in this browser.')
    }}>
    <VenueBadge venueId={venue.id} />
    <span className="cost-settings-field">
      <label htmlFor={`${id}-fee`}>Round-trip fee (%)</label>
      <Input id={`${id}-fee`} inputMode="decimal" autoComplete="off" placeholder="Not set" value={fee}
        aria-invalid={feePercent === undefined} {...(valid ? {} : { 'aria-describedby': `${id}-help` })}
        onChange={event => { setFee(event.target.value); setNote(null) }} />
    </span>
    <span className="cost-settings-field">
      <label htmlFor={`${id}-slippage`}>Slippage (%)</label>
      <Input id={`${id}-slippage`} inputMode="decimal" autoComplete="off" placeholder="Not set" value={slippage}
        aria-invalid={slippagePercent === undefined} {...(valid ? {} : { 'aria-describedby': `${id}-help` })}
        onChange={event => { setSlippage(event.target.value); setNote(null) }} />
    </span>
    <Button type="submit" variant="outline" size="sm" disabled={!dirty}>Save</Button>
    {!valid && <p id={`${id}-help`} className="cost-settings-error">Enter a percentage from 0 to {maxCostPercent} with up to four decimals, or leave it blank.</p>}
    {valid && note && <p role="status">{note}</p>}
  </form>
}

/**
 * Planning cost assumptions (Phase 2, item 10a): per venue, a round-trip fee and a slippage percentage of the position
 * size. Kept in this browser, with no defaults. The Play summary shows them beside its gross figures; sizing, Review and
 * every result ignore them. The Manual venue has none: one manual account can stand for any broker.
 */
export function CostSettings({ venues }: { venues: readonly VenueInfo[] }) {
  const id = useId()
  const registry = useVenues()
  const [assumptions, update] = useCostAssumptions()
  return <section className="shell-panel settings-panel cost-settings" aria-labelledby={`${id}-heading`}>
    <Receipt size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Cost assumptions</h2>
    <p>Your estimate of a round trip's fees and slippage, as a percentage of the position size. The Play summary shows it
      beside the planned figures, never inside them. Blank is not set; nothing is assumed by default.</p>
    {venues.filter(venue => !registry.isManual(venue.id)).map(venue => <VenueCosts key={venue.id} venue={venue} saved={assumptions[venue.id]} onSave={value => update(venue.id, value)} />)}
  </section>
}
