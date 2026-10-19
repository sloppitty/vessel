import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import type { PlayDraft } from './draft'
import { levelName } from './execution'
import { formatDraggedPrice, levelPrice, percentLevels } from './levels'
import { formatPlanNumber, planNumber, positivePlanNumber } from './numbers'
import { sizeChange, type SizeUnits } from './sizing'

/**
 * Asks how percentage stops and targets follow a leverage change: a percentage is a return at the
 * leverage, so keeping it moves the price, and keeping the price changes the percentage.
 */
export function LeverageChangeDialog({ draft, from, to, units, onKeepPrices, onKeepPercentages, onCancel }: {
  draft: PlayDraft
  from: number
  to: number
  units: SizeUnits
  onKeepPrices: () => void
  onKeepPercentages: () => void
  onCancel: () => void
}) {
  const levels = percentLevels(draft.entries)
  const size = sizeChange(draft, from, to, units)
  const price = (value: number | null) => value === null ? '—' : formatDraggedPrice(value)
  return <Dialog open onOpenChange={open => { if (!open) onCancel() }}>
    <DialogContent className="leverage-dialog">
      <DialogHeader>
        <DialogTitle>Change leverage from {from}× to {to}×?</DialogTitle>
        <DialogDescription>
          {levels.length === 1 ? 'One stop or target is' : `${levels.length} stops and targets are`} set in %. A % is the return on margin at the leverage, so the leverage decides its price.
        </DialogDescription>
      </DialogHeader>
      <table className="leverage-levels">
        <thead><tr><th scope="col">Level</th><th scope="col">Now at {from}×</th><th scope="col">Keep prices</th><th scope="col">Keep %</th></tr></thead>
        <tbody>
          {levels.map(({ entry, kind, exit }) => {
            const entryPrice = positivePlanNumber(entry.price)
            return <tr key={exit.id}>
              <th scope="row">{levelName(draft.entries, { role: kind, entryId: entry.id, levelId: exit.id })}</th>
              <td>{exit.value}% · {price(levelPrice(entryPrice, exit, kind, draft.direction, from))}</td>
              <td>{formatPlanNumber((planNumber(exit.value) ?? 0) * to / from, 4)}% · {price(levelPrice(entryPrice, exit, kind, draft.direction, from))}</td>
              <td>{exit.value}% · {price(levelPrice(entryPrice, exit, kind, draft.direction, to))}</td>
            </tr>
          })}
        </tbody>
      </table>
      {size && <p className="leverage-size">{size}</p>}
      <div className="leverage-dialog-actions">
        <Button type="button" onClick={onKeepPrices}>Keep prices</Button>
        <Button type="button" variant="outline" onClick={onKeepPercentages}>Keep % and move the levels</Button>
        <Button type="button" variant="ghost" onClick={onCancel}>Cancel</Button>
      </div>
    </DialogContent>
  </Dialog>
}
