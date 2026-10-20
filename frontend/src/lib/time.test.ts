import { afterEach, describe, expect, it } from 'vitest'
import { displayZoneStore, parseDisplayZone } from './display-zone'
import { legacyPreferenceKeys, resetPreferenceScope, scopePreferences, userPreferenceKey } from './preference-scope'
import { formatIn, formatTime, otherZoneTime, zoneLabel } from './time'

const originalZone = process.env.TZ
afterEach(() => {
  process.env.TZ = originalZone
  localStorage.clear()
  resetPreferenceScope()
  displayZoneStore.reload()
})

describe('displayed times (phase 1, 1.6e)', () => {
  it('shows one instant in either zone, naming it, with the other zone for a tooltip (U17)', () => {
    process.env.TZ = 'Europe/Madrid'
    const fill = '2026-10-13T10:00:00.123456Z'
    expect(formatTime(fill, 'utc')).toBe('13 Oct 2026, 10:00 UTC')
    expect(formatTime(fill, 'local')).toBe('13 Oct 2026, 12:00 CEST')
    expect(formatTime(fill, 'local', 'time-seconds')).toBe('12:00:00 CEST')
    expect(otherZoneTime(fill, 'local')).toBe('13 Oct 2026, 10:00:00 UTC')
    expect(otherZoneTime(fill, 'utc')).toBe('13 Oct 2026, 12:00:00 CEST')
    // Dates follow the zone too, and name none.
    expect(formatTime('2026-10-13T23:30:00Z', 'local', 'date')).toBe('14 Oct 2026')
    expect(formatTime('2026-10-13T23:30:00Z', 'utc', 'date')).toBe('13 Oct 2026')
    // Winter time has its own name.
    expect(zoneLabel('2026-12-01T10:00:00Z', 'local')).toBe('CET')
    expect(formatIn('2026-12-01T10:00:00Z', 'local', 'day-time')).toBe('1 Dec, 11:00')
  })

  it('keeps the choice per signed-in user, Local by default, and reads only known values', () => {
    expect(parseDisplayZone(null)).toBe('local')
    expect(parseDisplayZone('{"timeZone":"utc"}')).toBe('utc')
    expect(parseDisplayZone('{"timeZone":"Mars"}')).toBe('local')
    expect(parseDisplayZone('not json')).toBe('local')
    expect(displayZoneStore.get()).toBe('local')
    displayZoneStore.set('utc')
    expect(localStorage.getItem(legacyPreferenceKeys.display)).toBe('{"timeZone":"utc"}')
    // Another user starts from their own key.
    scopePreferences('00000000-0000-4000-8000-0000000000aa', false)
    expect(displayZoneStore.get()).toBe('local')
    displayZoneStore.set('utc')
    expect(localStorage.getItem(userPreferenceKey('00000000-0000-4000-8000-0000000000aa', 'display'))).toBe('{"timeZone":"utc"}')
  })
})
