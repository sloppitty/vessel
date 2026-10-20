import type { ReactNode } from 'react'
import { useDisplayZone } from '@/lib/display-zone'
import { formatTime, otherZoneTime, type TimeStyle } from '@/lib/time'

/**
 * A time in the display zone, naming it, with the same instant in the other zone in its tooltip (phase 1, 1.6e). A
 * missing time shows `fallback`.
 */
export function Time({ at, style = 'datetime', fallback = '—', className }: {
  at: string | null | undefined; style?: TimeStyle; fallback?: ReactNode; className?: string
}) {
  const zone = useDisplayZone()
  if (!at) return <>{fallback}</>
  const date = new Date(at)
  if (!Number.isFinite(date.getTime())) return <span className={className}>{at}</span>
  // The exact style reads the fraction from the stored text, which a Date would round to milliseconds.
  const exact = style === 'datetime-exact'
  return <time dateTime={at} title={otherZoneTime(exact ? at : date, zone, exact ? 'datetime-exact' : 'datetime-seconds')} className={className}>
    {formatTime(exact ? at : date, zone, style)}</time>
}
