import { createContext, createElement, useContext, useMemo, type ReactNode } from 'react'
import { manualVenueId, isTradeUrlTemplate, type VenueCapabilities, type VenueInfo } from './system'
import { isVenueLogoKey, type VenueLogoKey } from './venue-logos'

const instrumentPattern = /^[A-Za-z0-9_-]{1,32}$/

/** Lookups over the venues the API reported. Unknown venue IDs fall back to the ID and no capabilities. */
export class Venues {
  constructor(readonly list: readonly VenueInfo[]) {}

  find(id: string) { return this.list.find(venue => venue.id === id) }
  name(id: string) { return this.find(id)?.name ?? id }
  can(id: string, capability: keyof VenueCapabilities) { return this.find(id)?.capabilities[capability] === true }
  /**
   * False only when the venue reads orders but says its order reader does not return the current open book: stop
   * coverage there is "current book not read". An API that does not state the capability is taken at its word elsewhere.
   */
  readsCurrentBook(id: string) { const capabilities = this.find(id)?.capabilities; return !(capabilities?.orders && capabilities.currentOrderBook === false) }
  /** Quote asset of the venue's perpetuals, e.g. USDC. */
  quote(id: string) { return this.find(id)?.quoteAsset ?? null }
  isManual(id: string) { return id === manualVenueId }
  /** Unit of a venue-reported value: the venue's quote stablecoin, or USD for manual records and venues without one. */
  valueUnit(id: string) { return this.quote(id) ?? 'USD' }
  /** Bundled logo key of the venue, or null for the generic icon. */
  logo(id: string): VenueLogoKey | null { const key = this.find(id)?.logo; return isVenueLogoKey(key) ? key : null }
  /** Venues an account can be added at: manual, or an adapter that can read an account. */
  creatable() { return this.list.filter(venue => this.isManual(venue.id) || venue.capabilities.sync) }

  /** Where the owner places the planned orders. Vessel itself never sends orders. */
  tradeUrl(venueId: string, instrument: string | null, source: 'venue' | 'manual', venueContractId?: string | null) {
    const template = this.find(venueId)?.tradeUrlTemplate
    if (!isTradeUrlTemplate(template) || !instrument || source !== 'venue' || !instrumentPattern.test(instrument)) return null
    const native = venueContractId ?? instrument
    if (template.includes('{venueContractId}') && !instrumentPattern.test(native)) return null
    return template.replaceAll('{instrument}', encodeURIComponent(instrument)).replaceAll('{venueContractId}', encodeURIComponent(native))
  }
}

const VenuesContext = createContext(new Venues([]))

export function VenuesProvider({ venues, children }: { venues: readonly VenueInfo[]; children: ReactNode }) {
  const value = useMemo(() => new Venues(venues), [venues])
  return createElement(VenuesContext.Provider, { value }, children)
}

export const useVenues = () => useContext(VenuesContext)
