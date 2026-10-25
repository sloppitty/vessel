import type { PlaysSession } from '@/features/plays/saved'
import type { SettingsTab } from './SettingsPage'
import type { Page } from './useHashPage'

export type ShellLocation =
  | { kind: 'page'; page: Page; label: string }
  | { kind: 'account'; page: 'accounts'; id: string; label: string }
  | { kind: 'play'; page: 'plays'; id: string | null; label: string }
  | { kind: 'settings-tab'; page: 'settings'; tab: SettingsTab; label: string }

export const locationKey = (location: ShellLocation) => location.kind === 'page' ? location.page
  : location.kind === 'settings-tab' ? `settings:${location.tab}` : `${location.kind}:${location.id ?? 'draft'}`

/** A route history, not a second router: revisiting a location removes the loop. */
export function visitLocations(trail: ShellLocation[], ...locations: ShellLocation[]): ShellLocation[] {
  return locations.reduce((history, location) => {
    const index = history.findIndex(item => locationKey(item) === locationKey(location))
    if (index >= 0) return [...history.slice(0, index), location]
    // An unsaved draft can only be the current location, never a link back to lost work.
    return [...history.filter(item => item.kind !== 'play' || item.id !== null), location]
  }, trail)
}

export const playLocation = (session: PlaysSession): ShellLocation => ({
  kind: 'play', page: 'plays', id: session.saved?.summary.id ?? null,
  label: session.saved?.summary.title.trim() || session.draft.title.trim() || 'New play',
})

export const settingsLocation = (tab: SettingsTab): ShellLocation => ({
  kind: 'settings-tab', page: 'settings', tab, label: tab === 'profile' ? 'Profile & sign-in' : 'Appearance',
})
