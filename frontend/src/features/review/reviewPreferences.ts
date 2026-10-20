import { useSyncExternalStore } from 'react'
import { onPreferenceScope, preferenceKey } from '@/lib/preference-scope'

/**
 * Review view preferences, kept in the browser like chart preferences. Review adds imported positions automatically
 * while the selection has fewer than `minPlays` scored Plays; 0 never adds them automatically.
 */
export interface ReviewPreferences { minPlays: number }

export const defaultReviewPreferences: ReviewPreferences = { minPlays: 5 }
export const maxMinPlays = 1000

export const isMinPlays = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= maxMinPlays

export function readReviewPreferences(): ReviewPreferences {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(preferenceKey('review.preferences.v1')) ?? 'null')
    if (typeof stored !== 'object' || stored === null) return defaultReviewPreferences
    const { minPlays } = stored as Record<string, unknown>
    return { minPlays: isMinPlays(minPlays) ? minPlays : defaultReviewPreferences.minPlays }
  } catch {
    return defaultReviewPreferences
  }
}

let current: ReviewPreferences | undefined
let storedText: string | null = null
const listeners = new Set<() => void>()
const readStorage = () => {
  try { return localStorage.getItem(preferenceKey('review.preferences.v1')) } catch { return null }
}
// Reread another tab's change when the first view mounts; mounted views keep one stable snapshot until notified.
const snapshot = () => {
  if (!current || (!listeners.size && readStorage() !== storedText)) {
    current = readReviewPreferences()
    storedText = readStorage()
  }
  return current
}
const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
const reset = () => {
  current = undefined
  for (const listener of [...listeners]) listener()
}
onPreferenceScope(reset)
if (typeof window !== 'undefined') window.addEventListener('storage', event => {
  if (event.key === null || event.key === preferenceKey('review.preferences.v1')) reset()
})

function update(patch: Partial<ReviewPreferences>) {
  current = { ...snapshot(), ...patch }
  try { localStorage.setItem(preferenceKey('review.preferences.v1'), JSON.stringify(current)) } catch { /* Preferences stay in memory. */ }
  storedText = readStorage()
  for (const listener of [...listeners]) listener()
}

export function useReviewPreferences() {
  return [useSyncExternalStore(subscribe, snapshot), update] as const
}
