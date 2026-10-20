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
const listeners = new Set<() => void>()
const snapshot = () => current ??= readReviewPreferences()
const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
onPreferenceScope(() => {
  current = undefined
  for (const listener of listeners) listener()
})

function update(patch: Partial<ReviewPreferences>) {
  current = { ...snapshot(), ...patch }
  try { localStorage.setItem(preferenceKey('review.preferences.v1'), JSON.stringify(current)) } catch { /* Preferences stay in memory. */ }
  for (const listener of listeners) listener()
}

export function useReviewPreferences() {
  return [useSyncExternalStore(subscribe, snapshot), update] as const
}
