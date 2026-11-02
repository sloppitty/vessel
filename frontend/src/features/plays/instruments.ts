import { useEffect, useState } from 'react'
import type { BrokerAccount, InstrumentCatalog, VenueInstrument, WorkspaceApi } from '@/api/workspace'
import { errorMessage } from '@/api/errors'
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
  const [generation, setGeneration] = useState(0)
  const [result, setResult] = useState<{
    api: WorkspaceApi; accountId: string; generation: number
    catalog: InstrumentCatalog | null; error: string | null
  } | null>(null)
  const venueId = account?.venueId ?? ''
  const hasCatalogue = venues.can(venueId, 'instruments')
  const accountId = account?.isEnabled !== false && hasCatalogue ? account!.id : null
  const current = result?.api === api && result.accountId === accountId && result.generation === generation ? result : null

  useEffect(() => {
    if (accountId === null) return
    const controller = new AbortController()
    let active = true
    api.instruments(accountId, controller.signal).then(catalog => {
      if (!active) return
      const matches = catalog.venueId === venueId && catalog.scope === 'venue-perpetuals'
      setResult({ api, accountId, generation, catalog: matches ? catalog : null,
        error: matches ? null : 'The catalogue does not match the selected venue.' })
    }).catch(cause => {
      if (active) setResult({ api, accountId, generation, catalog: null,
        error: errorMessage(cause, 'The venue catalogue could not be loaded.') })
    })
    return () => { active = false; controller.abort() }
  }, [api, accountId, generation, venueId])

  return {
    accountId, catalog: current?.catalog ?? null, error: current?.error ?? null,
    loading: accountId !== null && current === null, retry: () => setGeneration(value => value + 1),
  }
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
