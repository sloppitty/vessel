import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { VenueBadge, VenueLogo } from './VenueBadge'
import { isVenueLogoKey, venueLogoKeys } from '@/api/venue-logos'
import { venueLogos } from './venue-logos'

describe('VenueBadge', () => {
  it('shows the bundled logo next to the descriptor name, with a decorative image', () => {
    const { container } = render(<VenueBadge venueId="lighter-rh" />)
    expect(screen.getByText('Lighter · Robinhood')).toBeInTheDocument()
    const image = container.querySelector('[data-logo="lighter-rh"] img')!
    expect(image).toHaveAttribute('alt', '')
    expect(image.getAttribute('src')).toBe(venueLogos['lighter-rh'].src)
    expect(container.querySelector('[data-logo]')).toHaveAttribute('aria-hidden', 'true')
  })

  it('keeps the venue name as the accessible name and tooltip of the compact variant', () => {
    render(<VenueBadge venueId="hyperliquid" compact />)
    const badge = screen.getByRole('img', { name: 'Hyperliquid' })
    expect(badge).toHaveAttribute('title', 'Hyperliquid')
    expect(screen.queryByText('Hyperliquid')).not.toBeInTheDocument()
  })

  it('uses an icon for manual records and the generic icon for venues without a known logo', () => {
    const { container } = render(<><VenueLogo venueId="manual" /><VenueBadge venueId="quantfury" /><VenueBadge venueId="unknown-venue" /></>)
    expect(container.querySelector('[data-logo="manual"] svg')).not.toBeNull()
    expect(container.querySelectorAll('[data-logo="generic"] svg')).toHaveLength(2)
    expect(container.querySelectorAll('img')).toHaveLength(0)
    expect(screen.getByText('Quantfury')).toBeInTheDocument()
    expect(screen.getByText('unknown-venue')).toBeInTheDocument()
  })

  it('accepts only the fixed logo set', () => {
    expect(venueLogoKeys).toEqual(['hyperliquid', 'lighter', 'lighter-rh', 'risex', 'pacifica', 'manual'])
    expect(isVenueLogoKey('lighter')).toBe(true)
    for (const key of ['Lighter', 'binance', 'toString', '__proto__', '', null, 3]) expect(isVenueLogoKey(key)).toBe(false)
  })

  it('bundles same-origin logo files, never remote URLs', () => {
    for (const key of venueLogoKeys) {
      const logo = venueLogos[key]
      if ('src' in logo) expect(logo.src).not.toMatch(/^(https?:)?\/\//)
    }
  })
})
