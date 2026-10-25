import { useState } from 'react'
import { Link2 } from 'lucide-react'
import type { MonitorDiscrepancy } from '@/api/monitor'
import type { ExecutionOrder, LinkOrder } from '@/api/plays'
import { Button } from '@/components/ui/button'
import { NativeSelect } from '@/components/ui/native-select'
import type { DraftEntry } from './draft'
import { describeOrder, isStopMechanism, levelOptions } from './execution'

/**
 * The answer to a missing or short stop (Phase 2, item 8c): a compatible unlinked stop that rests on the instrument (exit
 * side, stop mechanics) can be linked to one of the plan's stop levels by the owner. Vessel never links it by itself;
 * the server checks the link like any other, and a price away from the level is then a drift to rebind.
 */
export function LinkStopAction({ discrepancy, orders, entries, direction, busy, blocked, onLink }: {
  discrepancy: MonitorDiscrepancy; orders: readonly ExecutionOrder[]; entries: readonly DraftEntry[]; direction: 'long' | 'short'
  busy: boolean; onLink: (request: LinkOrder) => void | Promise<void>
  /** Why linking waits, for example unsaved plan edits whose levels the server does not know yet. */
  blocked?: string | undefined
}) {
  const [choices, setChoices] = useState<Record<string, string>>({})
  // Only an open incident asks for a stop; a resolved or dismissed record is history (Sol round 1 on #43).
  if (discrepancy.state !== 'open' || discrepancy.kind !== 'protection.missing' && discrepancy.kind !== 'protection.short') return null
  const exitSide = direction === 'long' ? 'sell' : 'buy'
  // The server states which roles each offered order can take, its retained fills included; older servers do not.
  const stops = orders.filter(order => order.status === 'open' && order.side === exitSide && isStopMechanism(order) &&
    (order.roles?.includes('stop') ?? true))
  const options = levelOptions(entries, false).filter(option => option.request.role === 'stop')
  if (!stops.length || !options.length) return null
  return <div className="link-stop">{stops.map(order => {
    const chosen = choices[order.orderId] ?? (options.length === 1 ? options[0]!.value : '')
    const option = options.find(item => item.value === chosen)
    return <div key={order.orderId} className="link-stop-row">
      <span>{describeOrder(order)} rests unlinked</span>
      {options.length > 1 && <NativeSelect density="compact" aria-label={`Stop level for order ${order.orderId}`} value={chosen} disabled={busy}
        onChange={event => setChoices(current => ({ ...current, [order.orderId]: event.target.value }))}>
        <option value="">Link as…</option>
        {options.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
      </NativeSelect>}
      <Button size="sm" variant="outline" disabled={busy || !!blocked || !option} title={blocked}
        onClick={() => option && onLink({ orderId: order.orderId, ...option.request })}>
        <Link2 size={13} />Link this stop{options.length === 1 ? ` to ${option?.label ?? ''}` : ''}</Button>
    </div>
  })}</div>
}
