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
  return <time dateTime={at} title={otherZoneTime(date, zone)} className={className}>{formatTime(date, zone, style)}</time>
}
