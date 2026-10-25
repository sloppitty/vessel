/**
 * Built-in themes. A theme only sets colours and corner radius; it never moves sections or changes behaviour.
 * Graphite is the approved default. The dark palettes come from the prototype's design studies.
 */
export type ThemeScheme = 'dark' | 'light'

/** Colours the owner can override in the customizer. Every other token is derived from these. */
export interface ThemeColors {
  background: string
  card: string
  secondary: string
  accent: string
  field: string
  foreground: string
  mutedForeground: string
  border: string
  primary: string
  primaryForeground: string
  ring: string
  positive: string
  negative: string
  warning: string
  candleUp: string
  candleDown: string
}

export type ThemeColorKey = keyof ThemeColors

export interface Theme {
  id: string
  name: string
  description: string
  scheme: ThemeScheme
  colors: ThemeColors
  /** Candle outlines; an overridden candle colour uses itself as its outline. */
  candleUpEdge: string
  candleDownEdge: string
  drawing: string
  /** Panel radius in pixels. */
  radius: number
}

export const defaultThemeId = 'graphite'

const darkSignals = { positive: '#a9d6b6', negative: '#f2b3ac', warning: '#edd49e' }

const dark = (id: string, name: string, description: string, colors: Omit<ThemeColors, keyof typeof darkSignals | 'candleUp' | 'candleDown'>, radius: number, drawing = '#8fb8ff'): Theme => ({
  id, name, description, scheme: 'dark', radius, drawing,
  colors: { ...colors, ...darkSignals, candleUp: colors.foreground, candleDown: '#050607' },
  candleUpEdge: colors.foreground, candleDownEdge: mix(colors.mutedForeground, colors.background, 0.88),
})

const light = (id: string, name: string, description: string, colors: Omit<ThemeColors, 'candleUp' | 'candleDown'>, radius: number, drawing: string): Theme => ({
  id, name, description, scheme: 'light', radius, drawing,
  // Hollow up candles and solid down candles keep red and green free for stops and targets.
  colors: { ...colors, candleUp: colors.card, candleDown: colors.foreground },
  candleUpEdge: colors.foreground, candleDownEdge: colors.foreground,
})

export const themes: readonly Theme[] = [
  dark('graphite', 'Graphite', 'Neutral charcoal with silver actions. The approved default.', {
    background: '#0f1114', card: '#171a1f', secondary: '#1e2229', accent: '#242932', field: '#111419',
    foreground: '#e9edf2', mutedForeground: '#abb3c0', border: '#394350', primary: '#c5cfdd', primaryForeground: '#11161d', ring: '#9ab5f2',
  }, 7),
  dark('carbon', 'Carbon', 'Near-black surfaces, crisp white actions and almost-square corners.', {
    background: '#090b0d', card: '#111519', secondary: '#181d22', accent: '#20272e', field: '#0c1014',
    foreground: '#e8edef', mutedForeground: '#aab5bf', border: '#404a54', primary: '#e8edef', primaryForeground: '#0c1014', ring: '#d5dce2',
  }, 4),
  dark('midnight', 'Midnight', 'Ink-blue panels with a cool blue primary action.', {
    background: '#0b1019', card: '#131d2c', secondary: '#1b283b', accent: '#24324a', field: '#0c1624',
    foreground: '#edf2fa', mutedForeground: '#adbbcf', border: '#3b5271', primary: '#96b7ff', primaryForeground: '#10182a', ring: '#96b7ff',
  }, 8),
  dark('forest', 'Forest', 'Deep green-black with pale sage actions and soft corners.', {
    background: '#0d1513', card: '#16221e', secondary: '#213029', accent: '#2b3d34', field: '#101b17',
    foreground: '#e7f0e9', mutedForeground: '#afc3b4', border: '#4c6758', primary: '#a8d6b9', primaryForeground: '#102418', ring: '#a8d6b9',
  }, 9, '#9fd3f0'),
  dark('petrol', 'Petrol', 'Dark blue-green with cyan accents.', {
    background: '#0c1518', card: '#14242a', secondary: '#1d3238', accent: '#29434b', field: '#0d1c21',
    foreground: '#e6f2f4', mutedForeground: '#adc9cc', border: '#496b72', primary: '#a1dfe2', primaryForeground: '#0d1c21', ring: '#a1dfe2',
  }, 6, '#c9b6f2'),
  dark('espresso', 'Espresso', 'Brown-black panels with warm copper accents.', {
    background: '#171310', card: '#241e19', secondary: '#322820', accent: '#40342a', field: '#1b1612',
    foreground: '#f4eee7', mutedForeground: '#cdbfb1', border: '#75614f', primary: '#efd4bf', primaryForeground: '#241e19', ring: '#efd4bf',
  }, 7),
  dark('aubergine', 'Aubergine', 'Muted plum-charcoal with lavender accents.', {
    background: '#17121a', card: '#231c29', secondary: '#302638', accent: '#3e3348', field: '#1c1622',
    foreground: '#f1eaf5', mutedForeground: '#c9b8d3', border: '#705c82', primary: '#d1b6e8', primaryForeground: '#1c1622', ring: '#d1b6e8',
  }, 9),
  light('paper', 'Paper', 'Neutral light gray with near-black actions.', {
    background: '#f3f4f6', card: '#ffffff', secondary: '#eceef2', accent: '#e1e5eb', field: '#ffffff',
    foreground: '#15191f', mutedForeground: '#505968', border: '#c5cbd4', primary: '#1f2a37', primaryForeground: '#ffffff', ring: '#2f5fb3',
    positive: '#17784a', negative: '#b42a2a', warning: '#835400',
  }, 7, '#2f6fde'),
  light('daylight', 'Daylight', 'Cool blue-white with a strong blue primary action.', {
    background: '#edf2f8', card: '#fbfdff', secondary: '#e4ebf5', accent: '#d7e1ef', field: '#ffffff',
    foreground: '#0f1b2d', mutedForeground: '#46566c', border: '#b9c7d9', primary: '#2353b0', primaryForeground: '#ffffff', ring: '#2353b0',
    positive: '#13774a', negative: '#b3262f', warning: '#7f5200',
  }, 8, '#7a3fc4'),
  light('sand', 'Sand', 'Warm parchment with brown actions.', {
    background: '#f4efe7', card: '#fffdf8', secondary: '#eee7dc', accent: '#e4dace', field: '#fffdf9',
    foreground: '#2a241d', mutedForeground: '#5f5548', border: '#d0c3b0', primary: '#6b4a2b', primaryForeground: '#fffaf2', ring: '#8a5a32',
    positive: '#2a6f42', negative: '#a5312a', warning: '#7c5408',
  }, 7, '#2f6fbf'),
]

export const isHexColor = (value: unknown): value is string => typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value)

function channels(hex: string) { return [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16)) }

/** `weight` of `a` mixed with the rest of `b`, as a hex colour. */
export function mix(a: string, b: string, weight: number) {
  const x = channels(a), y = channels(b)
  return `#${x.map((value, i) => Math.round(value * weight + y[i]! * (1 - weight)).toString(16).padStart(2, '0')).join('')}`
}

/** The navigation surface (`--sidebar`): halfway between the page and the panels. */
export const sidebarColor = (colors: Pick<ThemeColors, 'background' | 'card'>) => mix(colors.background, colors.card, 0.5)

type PaperColors = Pick<ThemeColors, 'background' | 'card' | 'mutedForeground' | 'warning'>
/** Paper mode's ink (`--paper-ink` in styles.css): caution softened toward secondary text. */
export const paperInk = (colors: PaperColors) => mix(colors.warning, colors.mutedForeground, 0.72)
/** Where Paper mode writes that ink: the page, the navigation and the Paper strip (`--paper-wash`, 9% warning, over the page). */
export const paperSurfaces = (colors: PaperColors) =>
  ({ background: colors.background, navigation: sidebarColor(colors), 'the Paper strip': mix(colors.warning, colors.background, 0.09) })

export function luminance(hex: string) {
  const [r, g, b] = channels(hex).map(value => value / 255).map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4)
  return r! * 0.2126 + g! * 0.7152 + b! * 0.0722
}

/** WCAG contrast ratio between two hex colours. */
export function contrastRatio(a: string, b: string) {
  const x = luminance(a), y = luminance(b)
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05)
}

export interface ContrastCheck { label: string; ratio: number; minimum: number }

/** Pairs that must stay readable. 4.5 is WCAG AA for body text. */
export function contrastChecks(colors: ThemeColors): ContrastCheck[] {
  const surfaces = { background: colors.background, panels: colors.card, insets: colors.secondary, fields: colors.field }
  const inks = { Text: colors.foreground, 'Secondary text': colors.mutedForeground, Positive: colors.positive, Negative: colors.negative, Warning: colors.warning }
  const checks: ContrastCheck[] = []
  for (const [ink, color] of Object.entries(inks)) {
    for (const [surface, background] of Object.entries(surfaces)) checks.push({ label: `${ink} on ${surface}`, ratio: contrastRatio(color, background), minimum: 4.5 })
  }
  checks.push({ label: 'Primary button', ratio: contrastRatio(colors.primaryForeground, colors.primary), minimum: 4.5 })
  checks.push({ label: 'Focus ring on panels', ratio: contrastRatio(colors.ring, colors.card), minimum: 3 })
  // The unread badge draws `--destructive-foreground` (the page background) on `--destructive` (the negative colour).
  checks.push({ label: 'Unread badge', ratio: contrastRatio(colors.background, colors.negative), minimum: 4.5 })
  // A warning popup tints the panel with 7% warning; its text and mark must stay readable on that tint.
  const warningPopup = mix(colors.warning, colors.card, 0.07)
  checks.push({ label: 'Text on warning popups', ratio: contrastRatio(colors.foreground, warningPopup), minimum: 4.5 })
  checks.push({ label: 'Warning mark on warning popups', ratio: contrastRatio(colors.warning, warningPopup), minimum: 3 })
  // Paper mode's chrome (sidebar subtitle, header mark, Paper strip title) is written in the derived Paper ink.
  const ink = paperInk(colors)
  for (const [surface, background] of Object.entries(paperSurfaces(colors))) checks.push({ label: `Paper mode ink on ${surface}`, ratio: contrastRatio(ink, background), minimum: 4.5 })
  return checks
}
