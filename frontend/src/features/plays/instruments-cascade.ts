import type { VenueInstrument } from '@/api/workspace'

/** The one category set every venue maps to, in display order. The server normalizes each venue's labels to these keys. */
export const marketCategories = [
  ['crypto', 'Crypto'], ['stocks', 'Stocks'], ['commodities', 'Commodities'], ['indices', 'Indices'],
  ['fx', 'FX'], ['rates', 'Rates'], ['pre-ipo', 'Pre-IPO'], ['other', 'Other'],
] as const

/** A venue without a label for a market counts as Other, so its markets are never lost behind a category. */
export const categoryOf = (instrument: Pick<VenueInstrument, 'category'>) =>
  marketCategories.some(([key]) => key === instrument.category) ? instrument.category! : 'other'

/** The asset of a contract key, without a source prefix: GOLD for xyz:GOLD, BTC for BTC. */
export const assetName = (contractId: string) => /^[a-z0-9]{1,16}:(.+)$/.exec(contractId)?.[1] ?? contractId

/** The key of the venue's own market, for instruments with no separate source. */
export const mainSource = ''
export const sourceOf = (instrument: Pick<VenueInstrument, 'source'>) => instrument.source ?? mainSource

export interface Facet { key: string; label: string; count: number }
export interface CascadeView { categories: Facet[]; sources: Facet[]; instruments: VenueInstrument[] }

const counts = (items: readonly VenueInstrument[], key: (item: VenueInstrument) => string) => {
  const map = new Map<string, number>()
  for (const item of items) map.set(key(item), (map.get(key(item)) ?? 0) + 1)
  return map
}

/**
 * The cascade for any venue. Each column counts what the other leaves available, so a count is what clicking it shows.
 * A column with a single choice has nothing to narrow and is left out, so one catalogue of unlabelled markets is a plain list.
 */
export function cascade(matches: readonly VenueInstrument[], venueName: string, category: string | null, source: string | null): CascadeView {
  const inSource = source === null ? matches : matches.filter(item => sourceOf(item) === source)
  const inCategory = category === null ? matches : matches.filter(item => categoryOf(item) === category)
  const byCategory = counts(inSource, categoryOf), bySource = counts(inCategory, sourceOf)
  const allSources = new Map<string, string>()
  for (const item of matches) allSources.set(sourceOf(item), item.sourceName ?? item.source ?? venueName)
  const categories = marketCategories.flatMap(([key, label]) => byCategory.has(key) ? [{ key, label, count: byCategory.get(key)! }] : [])
  const sources = [...allSources].filter(([key]) => bySource.has(key)).sort(([a], [b]) => a === mainSource ? -1 : b === mainSource ? 1 : 0)
    .map(([key, label]) => ({ key, label: key === mainSource ? venueName : label, count: bySource.get(key)! }))
  return { categories, sources, instruments: category === null ? [...inSource] : inSource.filter(item => categoryOf(item) === category) }
}
