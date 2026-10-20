import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { VenuesProvider, Venues, useVenues } from './venues'
import { systemFixture } from '@/test/system-fixture'
import { manualVenueId } from './system'

const venues = new Venues(systemFixture.venues)

describe('Venues', () => {
  it('identifies the manual descriptor by its reserved ID, not its name or capabilities', () => {
    expect(venues.isManual(manualVenueId)).toBe(true)
    expect(venues.isManual('hyperliquid')).toBe(false)
    expect(venues.isManual('unknown-venue')).toBe(false)
    const renamed = new Venues(systemFixture.venues.map(venue => ({ ...venue, name: 'Manual' })))
    expect(renamed.creatable().map(venue => venue.id)).toEqual(['hyperliquid', 'lighter-rh', manualVenueId])
    expect(renamed.isManual('hyperliquid')).toBe(false)
  })

  it('names venues and falls back to the ID for unknown ones', () => {
    expect(venues.name('hyperliquid')).toBe('Hyperliquid')
    expect(venues.name('manual')).toBe('Manual')
    expect(venues.name('unknown-venue')).toBe('unknown-venue')
  })

  it('answers capabilities from the API, with none for an unknown venue', () => {
    expect(venues.can('hyperliquid', 'sync')).toBe(true)
    expect(venues.can('hyperliquid', 'stablecoinWallet')).toBe(true)
    expect(venues.can('quantfury', 'sync')).toBe(false)
    expect(venues.can('manual', 'candles')).toBe(false)
    expect(venues.can('unknown-venue', 'sync')).toBe(false)
  })

  it('offers manual and venues that can read an account, not planned ones', () => {
    expect(venues.creatable().map(v => v.id)).toEqual(['hyperliquid', 'lighter-rh', 'manual'])
  })

  it('reports the bundled logo key, with null for a venue without one, an unknown key or an unknown venue', () => {
    expect(venues.logo('hyperliquid')).toBe('hyperliquid')
    expect(venues.logo('lighter-rh')).toBe('lighter-rh')
    expect(venues.logo('manual')).toBe('manual')
    expect(venues.logo('quantfury')).toBeNull()
    expect(venues.logo('unknown-venue')).toBeNull()
    const forged = new Venues([{ ...systemFixture.venues[0]!, logo: 'https://evil.example/logo.svg' as never }])
    expect(forged.logo('hyperliquid')).toBeNull()
  })

  it('reports the quote asset of a venue', () => {
    expect(venues.quote('hyperliquid')).toBe('USDC')
    expect(venues.quote('lighter-rh')).toBe('USDG')
    expect(venues.quote('manual')).toBeNull()
  })

  it('labels venue values with the descriptor quote and keeps the USD fallback', () => {
    expect(venues.valueUnit('hyperliquid')).toBe('USDC')
    expect(venues.valueUnit('lighter-rh')).toBe('USDG')
    expect(venues.valueUnit(manualVenueId)).toBe('USD')
    expect(venues.valueUnit('unknown-venue')).toBe('USD')
  })

  it('builds a trade link only for venue instruments with a safe contract ID', () => {
    expect(venues.tradeUrl('hyperliquid', 'BTC', 'venue')).toBe('https://app.hyperliquid.xyz/trade/BTC')
    expect(venues.tradeUrl('hyperliquid', 'BTC', 'manual')).toBeNull()
    expect(venues.tradeUrl('hyperliquid', null, 'venue')).toBeNull()
    expect(venues.tradeUrl('hyperliquid', '../x?y=1', 'venue')).toBeNull()
    expect(venues.tradeUrl('manual', 'BTC', 'venue')).toBeNull()
    expect(venues.tradeUrl('quantfury', 'BTC', 'venue')).toBeNull()
  })
  it('uses the native contract only where the descriptor requests it', () => {
    const native = new Venues([{ ...systemFixture.venues[0]!, tradeUrlTemplate: 'https://trade.example/{venueContractId}' }])
    expect(native.tradeUrl('hyperliquid', '1000PEPE', 'venue', 'kPEPE')).toBe('https://trade.example/kPEPE')
    expect(native.tradeUrl('hyperliquid', 'BTC', 'venue')).toBe('https://trade.example/BTC')
    expect(native.tradeUrl('hyperliquid', '1000PEPE', 'manual', 'kPEPE')).toBeNull()
    expect(native.tradeUrl('hyperliquid', '1000PEPE', 'venue', '../unsafe')).toBeNull()
    const canonical = new Venues([{ ...systemFixture.venues[0]!, id: 'risex', tradeUrlTemplate: 'https://trade.example/{instrument}' }])
    expect(canonical.tradeUrl('risex', '1000PEPE', 'venue', '42')).toBe('https://trade.example/1000PEPE')
  })
  it('replaces both known placeholders and refuses unsupported ones', () => {
    const both = new Venues([{ ...systemFixture.venues[0]!, tradeUrlTemplate: 'https://trade.example/{instrument}/{venueContractId}' }])
    expect(both.tradeUrl('hyperliquid', '1000PEPE', 'venue', 'kPEPE')).toBe('https://trade.example/1000PEPE/kPEPE')
    const unsupported = new Venues([{ ...systemFixture.venues[0]!, tradeUrlTemplate: 'https://trade.example/{instrument}/{unknown}' }])
    expect(unsupported.tradeUrl('hyperliquid', 'BTC', 'venue')).toBeNull()
  })
  it('defaults optional discovery and credential capabilities to false', () => {
    expect(venues.can('hyperliquid', 'accountDiscovery')).toBe(false)
    expect(venues.can('hyperliquid', 'readOnlyCredential')).toBe(false)
  })
  it('says the current book is not read only where an order-reading venue states it, never from its name', () => {
    const hyperliquid = systemFixture.venues[0]!
    const listed = new Venues([
      { ...hyperliquid, id: 'history-only', capabilities: { ...hyperliquid.capabilities, currentOrderBook: false } },
      { ...hyperliquid, id: 'book', capabilities: { ...hyperliquid.capabilities, currentOrderBook: true } },
      // An API that does not state the capability at all.
      { ...hyperliquid, id: 'unstated', capabilities: { sync: true, instruments: true, orders: true, candles: true, marketContext: true, stream: true, stablecoinWallet: true } },
      { ...hyperliquid, id: 'no-orders', capabilities: { ...hyperliquid.capabilities, orders: false, currentOrderBook: false } },
    ])
    expect(['history-only', 'book', 'unstated', 'no-orders', 'unknown'].map(id => listed.readsCurrentBook(id))).toEqual([false, true, true, true, true])
  })

  it('is provided by the application and empty without a provider', () => {
    function Probe() { return <p>{useVenues().name('hyperliquid')}</p> }
    const { unmount } = render(<Probe />)
    expect(screen.getByText('hyperliquid')).toBeInTheDocument()
    unmount()
    render(<VenuesProvider venues={systemFixture.venues}><Probe /></VenuesProvider>)
    expect(screen.getByText('Hyperliquid')).toBeInTheDocument()
  })
})
