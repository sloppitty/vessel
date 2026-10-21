import { useSyncExternalStore } from 'react'
import { onPreferenceScope, preferenceKey } from './preference-scope'

/**
 * The zone every displayed time is shown in (phase 1, 1.6e): the browser's own (the default) or UTC. A browser
 * preference per signed-in user, like appearance; it changes display only, never stored values or the server's UTC
 * day boundaries.
 */
export type DisplayZone = 'local' | 'utc'

export const displayZones: readonly DisplayZone[] = ['local', 'utc']

export function parseDisplayZone(text: string | null): DisplayZone {
  try {
    const raw = text ? JSON.parse(text) as unknown : null
    return raw && typeof raw === 'object' && (raw as { timeZone?: unknown }).timeZone === 'utc' ? 'utc' : 'local'
  } catch {
    return 'local'
  }
}

const read = () => {
  try { return parseDisplayZone(window.localStorage.getItem(preferenceKey('display'))) } catch { return 'local' as const }
}

let current: DisplayZone = read()
const listeners = new Set<() => void>()
const notify = () => { for (const listener of [...listeners]) listener() }

// Signing in switches to the user's key, and another tab's choice (or cleared storage) applies here too.
onPreferenceScope(() => { current = read(); notify() })
if (typeof window !== 'undefined') window.addEventListener('storage', event => {
  if (event.key === null || event.key === preferenceKey('display')) { current = read(); notify() }
})

export const displayZoneStore = {
  get: () => current,
  subscribe(listener: () => void) {
    listeners.add(listener)
    return () => { listeners.delete(listener) }
  },
  set(zone: DisplayZone) {
    if (zone === current) return
    current = zone
    try { window.localStorage.setItem(preferenceKey('display'), JSON.stringify({ timeZone: zone })) } catch { /* Kept for this page only. */ }
    notify()
  },
  /** Tests only: re-read the stored preference. */
  reload() { current = read(); notify() },
}

/** The display zone, re-rendering when the owner changes it. */
export function useDisplayZone() {
  return useSyncExternalStore(displayZoneStore.subscribe, displayZoneStore.get, displayZoneStore.get)
}
