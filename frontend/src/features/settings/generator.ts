import { contrastChecks, contrastRatio, luminance, paperInk, paperSurfaces, type ThemeColors, type ThemeScheme } from './themes'

/**
 * Random but coherent palettes: one neutral base hue for surfaces and text, one accent hue for actions,
 * and conventional green/red/amber signals. Inks are nudged until every readability check passes.
 */
export interface GeneratedPalette { scheme: ThemeScheme; colors: ThemeColors; radius: number; drawing: string; candleUpEdge: string; candleDownEdge: string }

type Random = () => number

const between = (random: Random, min: number, max: number) => min + random() * (max - min)
const pick = <T,>(random: Random, items: readonly T[]) => items[Math.floor(random() * items.length)]!

export function hsl(hue: number, saturation: number, lightness: number) {
  const h = ((hue % 360) + 360) % 360
  const s = Math.min(100, Math.max(0, saturation)) / 100
  const l = Math.min(100, Math.max(0, lightness)) / 100
  const k = (n: number) => (n + h / 30) % 12
  const a = s * Math.min(l, 1 - l)
  const channel = (n: number) => Math.round(255 * (l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)))))
  return `#${[0, 8, 4].map(n => channel(n).toString(16).padStart(2, '0')).join('')}`
}

interface Hsl { h: number; s: number; l: number }

/**
 * Moves `color` lighter (dark schemes) or darker (light schemes) until it reaches `minimum` on every surface and passes
 * any colour derived from it (`derived`).
 */
function readable(color: Hsl, surfaces: readonly string[], minimum: number, scheme: ThemeScheme, derived: (hex: string) => boolean = () => true) {
  let { l } = color
  for (let step = 0; step < 100; step++) {
    const hex = hsl(color.h, color.s, l)
    if (surfaces.every(surface => contrastRatio(hex, surface) >= minimum) && derived(hex)) return hex
    l += scheme === 'dark' ? 1 : -1
  }
  return scheme === 'dark' ? '#ffffff' : '#000000'
}

const accentOffsets = [0, 30, -30, 150, 180, 210] as const
const radii = [2, 4, 6, 7, 8, 10, 12] as const

export function generatePalette(scheme: ThemeScheme, random: Random = Math.random): GeneratedPalette {
  const base = between(random, 0, 360)
  const tint = between(random, 6, 30)
  const accentHue = base + pick(random, accentOffsets) + between(random, -12, 12)
  const accentSaturation = between(random, 45, 85)
  const dark = scheme === 'dark'
  const floor = dark ? between(random, 4, 9) : between(random, 93, 97)
  const surface = (offset: number, saturation = tint) => hsl(base, saturation, dark ? floor + offset : floor - offset)

  const background = surface(0)
  const card = dark ? surface(4) : hsl(base, tint * 0.5, Math.min(100, floor + 4))
  const secondary = surface(dark ? 7 : 3)
  const accent = surface(dark ? 11 : 8)
  const field = dark ? surface(2) : hsl(base, tint * 0.3, 100)
  const border = surface(dark ? 22 : 17, tint * 0.9)
  const surfaces = [background, card, secondary, field]

  const foreground = readable({ h: base, s: tint * 0.5, l: dark ? 90 : 12 }, surfaces, 7, scheme)
  const mutedForeground = readable({ h: base, s: tint * 0.6, l: dark ? 68 : 38 }, surfaces, 4.5, scheme)
  const ring = readable({ h: accentHue, s: accentSaturation, l: dark ? 70 : 42 }, [card, background], 3, scheme)
  const positive = readable({ h: between(random, 128, 155), s: between(random, 40, 65), l: dark ? 70 : 30 }, surfaces, 4.5, scheme)
  const negative = readable({ h: between(random, -8, 10), s: between(random, 60, 80), l: dark ? 76 : 42 }, surfaces, 4.5, scheme)
  // Paper mode's ink mixes warning with secondary text, so the warning also has to carry that mix.
  const paperReadable = (hex: string) => {
    const colors = { background, card, mutedForeground, warning: hex }
    return Object.values(paperSurfaces(colors)).every(surface => contrastRatio(paperInk(colors), surface) >= 4.5)
  }
  const warning = readable({ h: between(random, 34, 48), s: between(random, 65, 90), l: dark ? 74 : 28 }, surfaces, 4.5, scheme, paperReadable)

  // Either an accent-coloured or a neutral primary button, with whichever ink reads better on it.
  const primary = random() < 0.65 ? ring : dark ? foreground : hsl(base, tint, 16)
  const inks = [hsl(base, tint * 0.4, dark ? 8 : 99), dark ? '#ffffff' : '#0b0d10', '#ffffff', '#000000']
  const primaryForeground = inks.find(ink => contrastRatio(ink, primary) >= 4.5) ?? (luminance(primary) > 0.18 ? '#000000' : '#ffffff')

  const candleUp = dark ? foreground : card
  const candleDown = dark ? hsl(base, tint, 2) : foreground
  const colors: ThemeColors = {
    background, card, secondary, accent, field, foreground, mutedForeground, border,
    primary, primaryForeground, ring, positive, negative, warning, candleUp, candleDown,
  }
  return {
    scheme, colors, radius: pick(random, radii), drawing: ring,
    candleUpEdge: foreground, candleDownEdge: dark ? mutedForeground : foreground,
  }
}

const adjectives = ['Quiet', 'Deep', 'Soft', 'Bright', 'Hidden', 'Northern', 'Velvet', 'Silver', 'Copper', 'Misty', 'Still', 'Electric', 'Dusky', 'Warm', 'Cold'] as const
const nouns = ['Harbor', 'Orchid', 'Canyon', 'Lagoon', 'Ember', 'Glacier', 'Meadow', 'Comet', 'Reef', 'Dune', 'Pine', 'Signal', 'Tide', 'Slate', 'Aurora'] as const

export const suggestThemeName = (random: Random = Math.random) => `${pick(random, adjectives)} ${pick(random, nouns)}`

/** True when every readability check passes; generated palettes always should. */
export const isReadable = (colors: ThemeColors) => contrastChecks(colors).every(check => check.ratio >= check.minimum)
