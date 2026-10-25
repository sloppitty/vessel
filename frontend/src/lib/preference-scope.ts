/**
 * Whose browser preferences are in use. The sign-in screen uses the unscoped legacy keys; a signed-in user reads and
 * writes `vessel:u:{userId}:{name}`. Only the theme ID is kept per device, so the sign-in screen
 * shows a familiar theme before anyone signs in. A user change always reloads the page, so the scope moves at most
 * once per page, from none to one user.
 */
export type PreferenceName = 'appearance' | 'chart.preferences.v1' | 'display' | 'review.preferences.v1'

export const legacyPreferenceKeys: Record<PreferenceName, string> = {
  appearance: 'vessel.appearance',
  'chart.preferences.v1': 'vessel.chart.preferences.v1',
  display: 'vessel.display',
  'review.preferences.v1': 'vessel.review.preferences.v1',
}
export const deviceThemeKey = 'vessel:device:theme'

let scopedUser: string | null = null
const listeners = new Set<() => void>()

export const userPreferenceKey = (userId: string, name: PreferenceName) => `vessel:u:${userId}:${name}`

export function preferenceKey(name: PreferenceName) {
  return scopedUser ? userPreferenceKey(scopedUser, name) : legacyPreferenceKeys[name]
}

export const preferenceUser = () => scopedUser

/**
 * Switches the in-memory stores to `userId`. When `adoptsLegacy` is true (the installation owner), each
 * legacy key is copied only if the user's key is absent, then deleted. Other users never see the legacy keys.
 */
export function scopePreferences(userId: string, adoptsLegacy: boolean) {
  if (adoptsLegacy) {
    for (const name of Object.keys(legacyPreferenceKeys) as PreferenceName[]) {
      try {
        const legacy = localStorage.getItem(legacyPreferenceKeys[name])
        if (legacy === null) continue
        const key = userPreferenceKey(userId, name)
        if (localStorage.getItem(key) === null) localStorage.setItem(key, legacy)
        localStorage.removeItem(legacyPreferenceKeys[name])
      } catch { /* Storage unavailable: the user starts from defaults. */ }
    }
  }
  if (scopedUser === userId) return
  scopedUser = userId
  for (const listener of [...listeners]) listener()
}

/** Stores re-read their keys when the scope changes. */
export function onPreferenceScope(listener: () => void) {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

/** Tests only: back to the unscoped keys. */
export function resetPreferenceScope() {
  scopedUser = null
  for (const listener of [...listeners]) listener()
}
