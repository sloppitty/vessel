import { useSyncExternalStore } from 'react'
import { deviceThemeKey, onPreferenceScope, preferenceKey, preferenceUser } from '@/lib/preference-scope'
import { generatePalette } from './generator'
import { defaultThemeId, isHexColor, mix, sidebarColor, themes, type Theme, type ThemeColorKey, type ThemeColors, type ThemeScheme } from './themes'

/**
 * The owner's theme choice and per-theme colour overrides. Appearance is a browser preference, not workspace
 * data, so it lives in localStorage and survives disconnecting; it never holds anything sensitive. A signed-in
 * user has their own key (`lib/preference-scope`); the device keeps only the last built-in theme ID for the
 * sign-in screen.
 */
export interface Appearance {
  themeId: string
  /** Overrides keyed by theme, so tweaking one theme leaves the others untouched. */
  custom: Record<string, ThemeOverrides>
  /** Themes the owner saved under their own name. */
  saved: SavedTheme[]
  /** The latest random roll, until it is saved or replaced. */
  draft: SavedTheme | null
}

/** A theme stored in the browser, either saved by name or the unsaved random draft. */
export type SavedTheme = Omit<Theme, 'description'>

export const draftThemeId = 'draft'
export const maxSavedThemes = 24
export const maxThemeNameLength = 40

export interface ThemeOverrides {
  colors?: Partial<ThemeColors> | undefined
  radius?: number | undefined
}

export const storageKey = 'vessel.appearance'
export const radiusRange = { min: 0, max: 14 } as const

const colorKeys = Object.keys(themes[0]!.colors) as ThemeColorKey[]
const emptyAppearance = (): Appearance => ({ themeId: defaultThemeId, custom: {}, saved: [], draft: null })

const toTheme = (saved: SavedTheme): Theme =>
  ({ ...saved, description: saved.id === draftThemeId ? 'Random combination, not saved yet.' : `Your ${saved.scheme} theme.` })

/** Built-in themes, then saved ones, then the draft. */
export function themeList(appearance: Appearance): Theme[] {
  return [...themes, ...appearance.saved.map(toTheme), ...appearance.draft ? [toTheme(appearance.draft)] : []]
}

export function findTheme(appearance: Appearance, id: string | null | undefined): Theme {
  const list = themeList(appearance)
  return list.find(theme => theme.id === id) ?? list.find(theme => theme.id === defaultThemeId)!
}

const sanitizeSaved = (value: unknown, draft: boolean): SavedTheme | null => {
  if (!value || typeof value !== 'object') return null
  const raw = value as Record<string, unknown>
  const colors = raw.colors && typeof raw.colors === 'object' ? raw.colors as Record<string, unknown> : {}
  const name = typeof raw.name === 'string' ? raw.name.trim() : ''
  const validId = draft ? raw.id === draftThemeId : typeof raw.id === 'string' && /^custom-[a-z0-9-]{1,40}$/.test(raw.id)
  if (!validId || !name || name.length > maxThemeNameLength || (raw.scheme !== 'dark' && raw.scheme !== 'light')) return null
  if (!colorKeys.every(key => isHexColor(colors[key])) || ![raw.drawing, raw.candleUpEdge, raw.candleDownEdge].every(isHexColor)) return null
  if (typeof raw.radius !== 'number' || !Number.isInteger(raw.radius) || raw.radius < radiusRange.min || raw.radius > radiusRange.max) return null
  return {
    id: raw.id as string, name, scheme: raw.scheme, radius: raw.radius,
    colors: Object.fromEntries(colorKeys.map(key => [key, (colors[key] as string).toLowerCase()])) as unknown as ThemeColors,
    drawing: (raw.drawing as string).toLowerCase(), candleUpEdge: (raw.candleUpEdge as string).toLowerCase(), candleDownEdge: (raw.candleDownEdge as string).toLowerCase(),
  }
}

const sanitizeOverrides = (value: unknown): ThemeOverrides | null => {
  if (!value || typeof value !== 'object') return null
  const raw = value as { colors?: unknown; radius?: unknown }
  const overrides: ThemeOverrides = {}
  if (raw.colors && typeof raw.colors === 'object') {
    const colors = Object.fromEntries(colorKeys.flatMap(key => {
      const color = (raw.colors as Record<string, unknown>)[key]
      return isHexColor(color) ? [[key, color.toLowerCase()]] : []
    }))
    if (Object.keys(colors).length) overrides.colors = colors
  }
  if (typeof raw.radius === 'number' && Number.isInteger(raw.radius) && raw.radius >= radiusRange.min && raw.radius <= radiusRange.max) overrides.radius = raw.radius
  return overrides.colors || overrides.radius !== undefined ? overrides : null
}

/** Reads the stored preference, dropping anything unknown or malformed. */
export function parseAppearance(text: string | null): Appearance {
  try {
    const raw = text ? JSON.parse(text) as unknown : null
    if (!raw || typeof raw !== 'object') return emptyAppearance()
    const { themeId, custom, saved, draft } = raw as { themeId?: unknown; custom?: unknown; saved?: unknown; draft?: unknown }
    const names = new Set<string>()
    const kept = (Array.isArray(saved) ? saved : []).flatMap(value => {
      const theme = sanitizeSaved(value, false)
      if (!theme || names.has(theme.name.toLowerCase()) || names.size >= maxSavedThemes) return []
      names.add(theme.name.toLowerCase())
      return [theme]
    })
    const partial: Appearance = { ...emptyAppearance(), saved: kept, draft: sanitizeSaved(draft, true) }
    const ids = new Set(themeList(partial).map(theme => theme.id))
    const entries = custom && typeof custom === 'object'
      ? Object.entries(custom).flatMap(([id, value]) => {
        const overrides = ids.has(id) ? sanitizeOverrides(value) : null
        return overrides ? [[id, overrides] as const] : []
      })
      : []
    return { ...partial, themeId: typeof themeId === 'string' && ids.has(themeId) ? themeId : defaultThemeId, custom: Object.fromEntries(entries) }
  } catch {
    return emptyAppearance()
  }
}

export interface ResolvedTheme { theme: Theme; colors: ThemeColors; radius: number; candleUpEdge: string; candleDownEdge: string; customized: boolean }

export function resolveTheme(appearance: Appearance): ResolvedTheme {
  const theme = findTheme(appearance, appearance.themeId)
  const overrides = appearance.custom[theme.id] ?? {}
  const colors = { ...theme.colors, ...overrides.colors }
  return {
    theme, colors,
    radius: overrides.radius ?? theme.radius,
    candleUpEdge: overrides.colors?.candleUp ?? theme.candleUpEdge,
    candleDownEdge: overrides.colors?.candleDown ?? theme.candleDownEdge,
    customized: Boolean(overrides.colors || overrides.radius !== undefined),
  }
}

// Categorical series in a fixed order, validated for adjacent slices on each scheme's cards (CVD and contrast).
// Light slots below 3:1 on white are always shown with their labels. Other is a neutral step of the theme.
const seriesColors = {
  dark: ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#9085e9'],
  light: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#4a3aa7'],
} as const

/** CSS custom properties for a resolved theme; component styles only read these tokens. */
export function themeTokens({ theme, colors, radius, candleUpEdge, candleDownEdge }: ResolvedTheme): Record<string, string> {
  return {
    '--background': colors.background,
    '--foreground': colors.foreground,
    '--card': colors.card,
    '--card-foreground': colors.foreground,
    '--popover': colors.card,
    '--popover-foreground': colors.foreground,
    '--primary': colors.primary,
    '--primary-foreground': colors.primaryForeground,
    '--secondary': colors.secondary,
    '--secondary-foreground': colors.foreground,
    '--muted': colors.secondary,
    '--muted-foreground': colors.mutedForeground,
    '--accent': colors.accent,
    '--accent-foreground': colors.foreground,
    '--destructive': colors.negative,
    '--destructive-foreground': colors.background,
    '--border': colors.border,
    '--input': colors.border,
    '--ring': colors.ring,
    '--field': colors.field,
    '--sidebar': sidebarColor(colors),
    '--positive': colors.positive,
    '--negative': colors.negative,
    '--warning': colors.warning,
    '--shadow-color': theme.scheme === 'dark' ? '#000000' : colors.foreground,
    '--chart-drawing': theme.drawing,
    '--chart-candle-up': colors.candleUp,
    '--chart-candle-up-edge': candleUpEdge,
    '--chart-candle-down': colors.candleDown,
    '--chart-candle-down-edge': candleDownEdge,
    ...Object.fromEntries(seriesColors[theme.scheme].map((color, i) => [`--series-${i + 1}`, color])),
    '--series-other': mix(colors.mutedForeground, colors.card, 0.55),
    '--radius': `${radius / 16}rem`,
  }
}

export function applyAppearance(appearance: Appearance, root: HTMLElement = document.documentElement) {
  const resolved = resolveTheme(appearance)
  for (const [name, value] of Object.entries(themeTokens(resolved))) root.style.setProperty(name, value)
  root.classList.toggle('dark', resolved.theme.scheme === 'dark')
  root.ownerDocument.querySelector('meta[name="theme-color"]')?.setAttribute('content', resolved.colors.background)
  root.style.colorScheme = resolved.theme.scheme
  root.dataset.theme = resolved.theme.id
  root.dataset.scheme = resolved.theme.scheme
  return resolved
}

const readStorage = () => {
  try { return window.localStorage.getItem(preferenceKey('appearance')) } catch { return null }
}

/** The device theme: a built-in theme ID only, never colours or a saved theme. */
export function readDeviceTheme(): string | null {
  try {
    const id = window.localStorage.getItem(deviceThemeKey)
    return themes.some(theme => theme.id === id) ? id : null
  } catch { return null }
}

/** A signed-in user's built-in choice becomes the device theme; saved and draft themes leave it unchanged. */
function rememberDeviceTheme(appearance: Appearance) {
  if (!preferenceUser() || !themes.some(theme => theme.id === appearance.themeId)) return
  try { window.localStorage.setItem(deviceThemeKey, appearance.themeId) } catch { /* Only the sign-in screen's theme is lost. */ }
}

let current: Appearance = parseAppearance(readStorage())
const listeners = new Set<() => void>()

function commit(next: Appearance) {
  current = next
  try { window.localStorage.setItem(preferenceKey('appearance'), JSON.stringify(next)) } catch { /* Private mode keeps the choice for this page only. */ }
  rememberDeviceTheme(next)
  applyAppearance(next)
  for (const listener of listeners) listener()
}

/**
 * Applies the stored theme before the first render, so the page never flashes the default. Before anyone signs in
 * the device theme wins when present, otherwise the legacy key.
 */
export function initAppearance() {
  current = parseAppearance(readStorage())
  const device = preferenceUser() ? null : readDeviceTheme()
  applyAppearance(device ? { ...emptyAppearance(), themeId: device } : current)
}

/** Re-reads and applies the stored appearance of the current preference scope. */
function applyStoredAppearance() {
  current = parseAppearance(readStorage())
  applyAppearance(current)
  for (const listener of listeners) listener()
}

// Signing in switches to the user's key: re-read, apply and remember the device theme.
onPreferenceScope(() => {
  applyStoredAppearance()
  rememberDeviceTheme(current)
})

export const appearanceStore = {
  get: () => current,
  subscribe(listener: () => void) {
    listeners.add(listener)
    return () => { listeners.delete(listener) }
  },
  selectTheme(themeId: string) { commit({ ...current, themeId: findTheme(current, themeId).id }) },
  setColor(key: ThemeColorKey, color: string) {
    if (!isHexColor(color)) return
    const theme = findTheme(current, current.themeId)
    const overrides = current.custom[theme.id] ?? {}
    const colors = { ...overrides.colors }
    if (theme.colors[key] === color.toLowerCase()) delete colors[key]
    else colors[key] = color.toLowerCase()
    commit(withOverrides(current, theme.id, { ...overrides, colors: Object.keys(colors).length ? colors : undefined }))
  },
  setRadius(radius: number) {
    const theme = findTheme(current, current.themeId)
    const clamped = Math.round(Math.min(radiusRange.max, Math.max(radiusRange.min, radius)))
    const overrides = current.custom[theme.id] ?? {}
    commit(withOverrides(current, theme.id, { ...overrides, radius: clamped === theme.radius ? undefined : clamped }))
  },
  resetTheme(themeId: string) { commit(withOverrides(current, themeId, {})) },
  /** Rolls a new draft theme and shows it; earlier tweaks to the previous draft are dropped. */
  randomize(scheme: ThemeScheme, random?: () => number) {
    const palette = generatePalette(scheme, random)
    const draft: SavedTheme = { id: draftThemeId, name: 'Random draft', ...palette }
    commit({ ...withOverrides(current, draftThemeId, {}), draft, themeId: draftThemeId })
  },
  /** Saves the colours on screen, including any customization, under `name`. Returns a reason when it cannot. */
  saveCurrent(name: string): string | null {
    const trimmed = name.trim()
    if (!trimmed) return 'Enter a name for the theme.'
    if (trimmed.length > maxThemeNameLength) return `Use at most ${maxThemeNameLength} characters.`
    if (themeList(current).some(theme => theme.id !== draftThemeId && theme.name.toLowerCase() === trimmed.toLowerCase())) return 'A theme with this name already exists.'
    if (current.saved.length >= maxSavedThemes) return `You can keep up to ${maxSavedThemes} saved themes. Delete one first.`
    const { theme, colors, radius, candleUpEdge, candleDownEdge } = resolveTheme(current)
    const id = `custom-${Date.now().toString(36)}-${Math.floor(Math.random() * 36 ** 4).toString(36)}`
    const saved: SavedTheme = { id, name: trimmed, scheme: theme.scheme, colors, radius, drawing: theme.drawing, candleUpEdge, candleDownEdge }
    const next = theme.id === draftThemeId ? { ...withOverrides(current, draftThemeId, {}), draft: null } : current
    commit({ ...next, saved: [...next.saved, saved], themeId: id })
    return null
  },
  deleteSaved(id: string) {
    if (!current.saved.some(theme => theme.id === id)) return
    const next = withOverrides(current, id, {})
    commit({ ...next, saved: next.saved.filter(theme => theme.id !== id), themeId: current.themeId === id ? defaultThemeId : current.themeId })
  },
}

function withOverrides(appearance: Appearance, themeId: string, overrides: ThemeOverrides): Appearance {
  const custom = { ...appearance.custom }
  if (overrides.colors || overrides.radius !== undefined) custom[themeId] = overrides
  else delete custom[themeId]
  return { ...appearance, custom }
}

export function useAppearance() {
  return useSyncExternalStore(appearanceStore.subscribe, appearanceStore.get, appearanceStore.get)
}

/** Section anchors inside Appearance. The app routes by hash, so these scroll instead of changing the URL. */
export type AppearanceSection = 'appearance' | 'theme-builder' | 'theme-customizer'

export function showAppearanceSection(section: AppearanceSection) {
  const target = document.getElementById(`${section}-heading`)
  target?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  target?.focus({ preventScroll: true })
}
