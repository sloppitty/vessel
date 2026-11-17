import type { BrokerAccount, InstrumentCatalog, VenueInstrument, WorkspaceApi } from '@/api/workspace'
import { ApiError } from '@/api/errors'
import { useApiRead } from '@/lib/use-api-read'
import { useVenues } from '@/api/venues'

/** Trading pair label, e.g. BTC/USDC, or GOLD/USDC for a contract the venue labels. */
export const pairLabel = (instrument: Pick<VenueInstrument, 'contractId' | 'quoteAsset' | 'displayName'>) =>
  `${instrument.displayName ?? instrument.contractId}/${instrument.quoteAsset}`

/** The base and quote of a displayed instrument: a venue contract gets the venue's quote asset, a manual label stays whole. */
export function instrumentParts(quote: string | null, instrument: string, source: 'venue' | 'manual') {
  return { base: instrument, quote: source === 'venue' && quote && instrument ? quote : null }
}

/** BTC/USDC for a venue contract, given the venue's quote asset; manual labels stay as entered. */
export function instrumentLabel(quote: string | null, instrument: string, source: 'venue' | 'manual') {
  const parts = instrumentParts(quote, instrument, source)
  return parts.quote ? pairLabel({ contractId: parts.base, quoteAsset: parts.quote }) : parts.base
}

export interface InstrumentCatalogState {
  /** Null for manual or disabled accounts, which have no venue catalogue. */
  accountId: string | null
  catalog: InstrumentCatalog | null
  error: string | null
  loading: boolean
  retry: () => void
}

/** Venue catalogue of the account's perpetuals, read once per account and on retry. */
export function useInstrumentCatalog(api: WorkspaceApi, account: BrokerAccount | undefined): InstrumentCatalogState {
  const venues = useVenues()
  const venueId = account?.venueId ?? ''
  const hasCatalogue = venues.can(venueId, 'instruments')
  const accountId = account?.isEnabled !== false && hasCatalogue ? account!.id : null
  const read = useApiRead(api, JSON.stringify([accountId, venueId]), (client, signal) => client.instruments(accountId!, signal).then(catalog => {
    if (catalog.venueId !== venueId || catalog.scope !== 'venue-perpetuals') throw new ApiError('invalid-response', 'The catalogue does not match the selected venue.')
    return catalog
  }), { fallback: 'The venue catalogue could not be loaded.', enabled: accountId !== null })

  return { accountId, catalog: read.data, error: read.error, loading: read.loading, retry: read.reload }
}

/** Enough for any one category or source; the columns narrow the rest. */
const maxSuggestions = 500

/**
 * Contracts matching the query by name, pair, venue keyword or source: exact matches, then names that start with it, then
 * the rest. Ties keep the venue's catalogue order, which lists the major contracts first.
 */
export function searchInstruments(instruments: readonly VenueInstrument[], query: string) {
  const text = query.trim().toUpperCase().replace(/\s+/g, '')
  if (!text) return instruments.slice(0, maxSuggestions)
  const scored = instruments.flatMap(instrument => {
    const names = [instrument.contractId, instrument.displayName ?? ''].map(name => name.toUpperCase())
    const pair = pairLabel(instrument).toUpperCase()
    const terms = [...(instrument.keywords ?? []), instrument.source ?? '', instrument.sourceName ?? ''].map(term => term.toUpperCase().replace(/\s+/g, ''))
    const score = names.includes(text) || pair === text ? 0
      : names.some(name => name.startsWith(text)) || pair.startsWith(text) || terms.includes(text) ? 1
        : pair.includes(text) || names.some(name => name.includes(text)) || terms.some(term => term.includes(text)) ? 2 : -1
    return score < 0 ? [] : [{ instrument, score }]
  })
  return scored.sort((a, b) => a.score - b.score)
    .slice(0, maxSuggestions).map(item => item.instrument)
}
