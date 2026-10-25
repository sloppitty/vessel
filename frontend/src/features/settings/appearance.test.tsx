import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { readableInk } from '@/components/chart/ink'
import { AppearanceSettings, CurrentThemeName } from './AppearanceSettings'
import { applyAppearance, appearanceStore, initAppearance, parseAppearance, storageKey } from './appearance'
import { generatePalette, isReadable } from './generator'
import { contrastChecks, defaultThemeId, themes } from './themes'

const root = document.documentElement
const token = (name: string) => root.style.getPropertyValue(name)

beforeEach(() => {
  window.localStorage.clear()
  if (!document.querySelector('meta[name="theme-color"]')) {
    const meta = document.createElement('meta')
    meta.name = 'theme-color'
    document.head.append(meta)
  }
  initAppearance()
})

describe('Built-in themes', () => {
  it('offers dark and light themes with Graphite as the default', () => {
    expect(defaultThemeId).toBe('graphite')
    expect(themes.filter(theme => theme.scheme === 'dark').length).toBeGreaterThanOrEqual(5)
    expect(themes.filter(theme => theme.scheme === 'light').length).toBeGreaterThanOrEqual(2)
    expect(new Set(themes.map(theme => theme.id)).size).toBe(themes.length)
  })

  it.each(themes.map(theme => [theme.name, theme] as const))('%s passes every readability check', (_name, theme) => {
    expect(contrastChecks(theme.colors).filter(check => check.ratio < check.minimum)).toEqual([])
  })

  it.each(themes.map(theme => [theme.name, theme] as const))('%s keeps the unread badge and warning popups readable', (_name, theme) => {
    const checks = contrastChecks(theme.colors).filter(check => /Unread badge|warning popups/.test(check.label))
    expect(checks.map(check => check.label)).toEqual(['Unread badge', 'Text on warning popups', 'Warning mark on warning popups'])
    for (const check of checks) expect(check.ratio, check.label).toBeGreaterThanOrEqual(check.minimum)
  })

  it.each(themes.map(theme => [theme.name, theme] as const))('%s keeps Paper mode ink readable on the page, navigation and Paper strip', (_name, theme) => {
    const checks = contrastChecks(theme.colors).filter(check => check.label.startsWith('Paper mode ink'))
    expect(checks.map(check => check.label)).toEqual(['Paper mode ink on background', 'Paper mode ink on navigation', 'Paper mode ink on the Paper strip'])
    for (const check of checks) expect(check.ratio, check.label).toBeGreaterThanOrEqual(4.5)
  })

  it('applies Graphite tokens when nothing is stored', () => {
    expect(root.dataset.theme).toBe('graphite')
    expect(token('--background')).toBe('#0f1114')
    expect(token('--sidebar')).toBe('#13161a')
    expect(token('--radius')).toBe('0.4375rem')
    expect(root).toHaveClass('dark')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', '#0f1114')
    expect(token('--shadow-color')).toBe('#000000')
  })
})

describe('Resolved browser appearance', () => {
  it.each(themes)('$name synchronizes the scheme, dark controls and browser theme colour', theme => {
    root.classList.add('unrelated')
    appearanceStore.selectTheme(theme.id)
    expect(root.classList.contains('dark')).toBe(theme.scheme === 'dark')
    expect(root).toHaveClass('unrelated')
    root.classList.remove('unrelated')
    expect(root.style.colorScheme).toBe(theme.scheme)
    expect(root.dataset.scheme).toBe(theme.scheme)
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', theme.colors.background)
    expect(token('--shadow-color')).toBe(theme.scheme === 'dark' ? '#000000' : theme.colors.foreground)
  })

  it('updates the browser colour on customization, reset and reload', () => {
    appearanceStore.selectTheme('paper')
    appearanceStore.setColor('background', '#abcdef')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', '#abcdef')
    initAppearance()
    expect(root).not.toHaveClass('dark')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', '#abcdef')
    appearanceStore.resetTheme('paper')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', themes.find(theme => theme.id === 'paper')!.colors.background)
    appearanceStore.selectTheme('graphite')
    expect(root).toHaveClass('dark')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', '#0f1114')
  })

  it.each(['light', 'dark'] as const)('applies the %s draft and saved theme scheme', scheme => {
    appearanceStore.randomize(scheme, seeded(12))
    const background = token('--background')
    expect(root.classList.contains('dark')).toBe(scheme === 'dark')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', background)
    appearanceStore.saveCurrent('Saved scheme')
    const id = appearanceStore.get().themeId
    appearanceStore.selectTheme(scheme === 'dark' ? 'paper' : 'graphite')
    appearanceStore.selectTheme(id)
    initAppearance()
    expect(root.classList.contains('dark')).toBe(scheme === 'dark')
    expect(document.querySelector('meta[name="theme-color"]')).toHaveAttribute('content', background)
  })

  it('also applies safely to a document without a browser theme meta tag', () => {
    const other = document.implementation.createHTMLDocument()
    expect(() => applyAppearance(parseAppearance(null), other.documentElement)).not.toThrow()
    expect(other.documentElement.classList.contains('dark')).toBe(true)
  })
})

describe('Stored appearance', () => {
  it('drops unknown themes, malformed colours and out-of-range radii', () => {
    expect(parseAppearance('not json')).toEqual({ themeId: 'graphite', custom: {}, saved: [], draft: null })
    expect(parseAppearance(JSON.stringify({
      themeId: 'neon',
      custom: { paper: { colors: { background: '#FAFAFA', card: 'red', bogus: '#000000' }, radius: 99 }, neon: { radius: 3 } },
    }))).toEqual({ themeId: 'graphite', custom: { paper: { colors: { background: '#fafafa' } } }, saved: [], draft: null })
  })

  it('keeps overrides per theme and persists them', () => {
    appearanceStore.selectTheme('paper')
    appearanceStore.setColor('primary', '#123456')
    appearanceStore.setRadius(3)
    expect(token('--primary')).toBe('#123456')
    expect(root.style.colorScheme).toBe('light')

    appearanceStore.selectTheme('midnight')
    expect(token('--primary')).toBe('#96b7ff')
    appearanceStore.selectTheme('paper')
    expect(token('--primary')).toBe('#123456')
    expect(JSON.parse(window.localStorage.getItem(storageKey)!)).toEqual({ themeId: 'paper', custom: { paper: { colors: { primary: '#123456' }, radius: 3 } }, saved: [], draft: null })

    appearanceStore.setColor('primary', '#1F2A37')
    appearanceStore.setRadius(7)
    expect(appearanceStore.get().custom).toEqual({})
  })
})

describe('Appearance settings', () => {
  it('switches theme, customizes a colour, warns about contrast and resets', async () => {
    render(<><AppearanceSettings /><p data-testid="name"><CurrentThemeName /></p></>)
    await userEvent.click(screen.getByRole('button', { name: /^Daylight/ }))
    expect(root.dataset.theme).toBe('daylight')
    expect(screen.getByTestId('name')).toHaveTextContent('Daylight')

    const hex = screen.getByRole('textbox', { name: 'Secondary text hex value' })
    await userEvent.clear(hex)
    await userEvent.type(hex, '#dddddd{Enter}')
    expect(token('--muted-foreground')).toBe('#dddddd')
    expect(screen.getByTestId('name')).toHaveTextContent('Daylight (customized)')
    expect(within(screen.getByRole('status', { name: 'Readability' })).getByText(/readability checks? fails?/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Reset Daylight' }))
    expect(token('--muted-foreground')).toBe('#46566c')
    // Passing checks show nothing; the result is only announced.
    expect(screen.getByRole('status', { name: 'Readability' })).not.toHaveClass('has-issues')
    expect(within(screen.getByRole('status', { name: 'Readability' })).getByText(/All \d+ readability checks pass/)).toHaveClass('sr-only')
  })

  it('jumps to the theme builder and the customizer from the panel header', async () => {
    const scroll = vi.fn()
    Element.prototype.scrollIntoView = scroll
    render(<AppearanceSettings />)
    await userEvent.click(screen.getByRole('button', { name: 'Theme builder' }))
    expect(screen.getByRole('heading', { name: 'Theme builder' })).toHaveFocus()
    await userEvent.click(screen.getByRole('button', { name: 'Customize colours' }))
    expect(screen.getByRole('heading', { name: 'Customize Graphite' })).toHaveFocus()
    expect(scroll).toHaveBeenCalledTimes(2)
  })

  it('ignores an incomplete hex value', async () => {
    render(<AppearanceSettings />)
    const hex = screen.getByRole('textbox', { name: 'Panels hex value' })
    await userEvent.clear(hex)
    await userEvent.type(hex, '#12{Enter}')
    expect(token('--card')).toBe('#171a1f')
    expect(hex).toHaveValue('#171a1f')
  })
})

describe('Chart level tags', () => {
  it('pick dark ink on pastel levels and white ink on deep ones', () => {
    expect(readableInk('#a9d6b6')).toBe('#151c20')
    expect(readableInk('#17784a')).toBe('#ffffff')
    expect(readableInk('rgb(180, 42, 42)')).toBe('#ffffff')
  })
})

/** Small seeded generator so random rolls are reproducible in tests. */
function seeded(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

describe('Random theme builder', () => {
  it.each(['dark', 'light'] as const)('always rolls readable %s palettes', scheme => {
    const random = seeded(scheme === 'dark' ? 7 : 11)
    for (let roll = 0; roll < 500; roll++) {
      const palette = generatePalette(scheme, random)
      expect(isReadable(palette.colors), JSON.stringify(palette.colors)).toBe(true)
      expect(palette.scheme).toBe(scheme)
    }
  })

  it('shows a roll as the draft, saves it by name and deletes it', () => {
    appearanceStore.randomize('light', seeded(3))
    const { draft } = appearanceStore.get()
    expect(appearanceStore.get().themeId).toBe('draft')
    expect(token('--background')).toBe(draft!.colors.background)
    expect(root.style.colorScheme).toBe('light')

    appearanceStore.setColor('primary', '#123456')
    expect(appearanceStore.saveCurrent('  ')).toBe('Enter a name for the theme.')
    expect(appearanceStore.saveCurrent('graphite')).toBe('A theme with this name already exists.')
    expect(appearanceStore.saveCurrent('Quiet Harbor')).toBeNull()

    const saved = appearanceStore.get().saved[0]!
    expect(saved).toMatchObject({ name: 'Quiet Harbor', scheme: 'light', colors: { ...draft!.colors, primary: '#123456' } })
    expect(appearanceStore.get()).toMatchObject({ themeId: saved.id, draft: null, custom: {} })
    expect(appearanceStore.saveCurrent('quiet harbor')).toBe('A theme with this name already exists.')

    // Saved themes survive a reload.
    initAppearance()
    expect(appearanceStore.get().saved).toEqual([saved])
    expect(token('--primary')).toBe('#123456')

    appearanceStore.deleteSaved(saved.id)
    expect(appearanceStore.get()).toMatchObject({ themeId: 'graphite', saved: [] })
    expect(token('--background')).toBe('#0f1114')
  })

  it('drops malformed or duplicate saved themes from storage', () => {
    appearanceStore.randomize('dark', seeded(5))
    appearanceStore.saveCurrent('One')
    const good = appearanceStore.get().saved[0]!
    const stored = {
      themeId: 'custom-missing', custom: { [good.id]: { radius: 2 }, 'custom-gone': { radius: 2 } },
      saved: [good, { ...good, id: 'custom-dupe', name: 'ONE' }, { ...good, id: 'custom-bad', name: 'Bad', colors: { ...good.colors, card: 'blue' } }, { ...good, id: 'evil id', name: 'Evil' }],
      draft: { ...good, id: 'custom-not-draft' },
    }
    expect(parseAppearance(JSON.stringify(stored))).toEqual({ themeId: 'graphite', custom: { [good.id]: { radius: 2 } }, saved: [good], draft: null })
  })

  it('rolls, names and saves a theme from Settings', async () => {
    render(<><AppearanceSettings /><p data-testid="name"><CurrentThemeName /></p></>)
    await userEvent.click(screen.getByRole('button', { name: 'Light' }))
    await userEvent.click(screen.getByRole('button', { name: 'Randomize' }))
    expect(root.dataset.scheme).toBe('light')
    expect(screen.getByTestId('name')).toHaveTextContent('Random draft')
    expect(screen.getByRole('status', { name: 'Readability' })).not.toHaveClass('has-issues')

    const name = screen.getByRole('textbox', { name: 'Theme name' })
    expect(name).not.toHaveValue('')
    await userEvent.clear(name)
    await userEvent.type(name, 'Paper{Enter}')
    expect(screen.getByRole('alert')).toHaveTextContent('A theme with this name already exists.')
    await userEvent.clear(name)
    await userEvent.type(name, 'Morning desk{Enter}')

    expect(screen.getByTestId('name')).toHaveTextContent('Morning desk')
    const mine = screen.getByRole('heading', { name: 'My themes' }).parentElement!
    expect(within(mine).getByRole('button', { name: /^Morning desk/ })).toHaveAttribute('aria-pressed', 'true')

    await userEvent.click(screen.getByRole('button', { name: 'Delete theme' }))
    await userEvent.click(screen.getByRole('button', { name: 'Delete Morning desk' }))
    expect(screen.queryByRole('heading', { name: 'My themes' })).not.toBeInTheDocument()
    expect(screen.getByTestId('name')).toHaveTextContent('Graphite')
  })
})
