import { expect, it } from 'vitest'
import { generatePalette, isReadable } from './generator'
import { contrastRatio, mix, paperInk, sidebarColor } from './themes'

function seeded(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

it.each(['dark', 'light'] as const)('keeps generated %s Paper subtitle ink readable on the actual warning-washed navigation', scheme => {
  const failures = []
  for (let seed = 0; seed < 1000; seed++) {
    const { colors } = generatePalette(scheme, seeded(seed))
    // trading-mode.css: the top of the sidebar is 5% warning over --sidebar.
    const actualNavigation = mix(colors.warning, sidebarColor(colors), 0.05)
    const ratio = contrastRatio(paperInk(colors), actualNavigation)
    if (ratio < 4.5) failures.push({ seed, ratio, reportedReadable: isReadable(colors), colors })
  }
  expect(failures).toEqual([])
})
