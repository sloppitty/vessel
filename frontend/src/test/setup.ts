import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'
import { resetPreferenceScope } from '@/lib/preference-scope'

// Browser storage never leaks between tests: preferences, the device notice and the last-used wallet start empty.
afterEach(() => {
  cleanup(); vi.unstubAllGlobals(); vi.unstubAllEnvs()
  localStorage.clear(); sessionStorage.clear(); resetPreferenceScope()
})
// jsdom has no layout or native scrolling; browser verification covers geometry.
Object.defineProperty(HTMLElement.prototype, 'scrollTo', { configurable: true, value: vi.fn() })
Object.defineProperty(window, 'scrollTo', { configurable: true, value: vi.fn() })
// jsdom has no object URLs; evidence thumbnails only need a stable string.
Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: vi.fn(() => 'blob:test-image') })
Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() })
// jsdom has no canvas. Feature tests inject a fake adapter; the real renderer is checked in a browser.
vi.mock('@/components/chart/lightweight', () => ({
  createLightweightAdapter: () => ({ setCandles: vi.fn(), setOverlays: vi.fn(), setDrawings: vi.fn(), setDrawingTool: vi.fn(), setPricePicker: vi.fn(), setIndicators: vi.fn(), setMarkers: vi.fn(), setFocus: vi.fn(), setTimeZone: vi.fn(), capture: vi.fn().mockResolvedValue(null), destroy: vi.fn() }),
}))
