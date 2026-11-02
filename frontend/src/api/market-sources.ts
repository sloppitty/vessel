import { isFilledText, isRecord, isText } from './guards'

/** One market source of a venue, such as a Hyperliquid builder DEX, and whether the owner's selector offers it. */
export interface MarketSource { id: string; name: string; markets: number; enabled: boolean }
/** A venue's sources; `available` is false when its catalogue could not be read just now. */
export interface VenueMarketSources { venueId: string; venueName: string; available: boolean; sources: MarketSource[] }

export const marketSourcesPath = '/api/settings/market-sources'
export const marketSourcePath = (venueId: string, sourceId: string) =>
  `${marketSourcesPath}/${encodeURIComponent(venueId)}/${encodeURIComponent(sourceId)}`

const isSource = (v: unknown): v is MarketSource => isRecord(v) && isFilledText(v.id, 16) && isText(v.name, 128) &&
  typeof v.markets === 'number' && Number.isInteger(v.markets) && v.markets >= 0 && typeof v.enabled === 'boolean'
export const isMarketSources = (v: unknown): v is VenueMarketSources[] => Array.isArray(v) && v.every(venue => isRecord(venue) &&
  isFilledText(venue.venueId, 64) && isText(venue.venueName, 128) && typeof venue.available === 'boolean' &&
  Array.isArray(venue.sources) && venue.sources.every(isSource))
