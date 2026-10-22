import { candleIntervals, type CandleInterval } from './candles'
import { ApiError } from './errors'
import { isGuid, isRecord } from './guards'
import { createApiCall, type ApiCredentials, type AuthFailure } from './request'
import { authErrorMessage } from './auth'
import { isVenueLogoKey, type VenueLogoKey } from './venue-logos'

export const manualVenueId = 'manual'

export interface VenueCapabilities {
  paperExecution?: boolean
  sync: boolean; instruments: boolean; orders: boolean; candles: boolean
  marketContext: boolean; stream: boolean; stablecoinWallet: boolean
  accountDiscovery?: boolean; readOnlyCredential?: boolean; manualExecutions?: boolean
  /** The order reader returns the current open book. Without it stop coverage reads "current book not read". */
  currentOrderBook?: boolean
  /** The venue serves executions by time window, so a history gap can be backfilled (Phase 2, item 6b). */
  fillHistory?: boolean
}

/** One venue and what Vessel can do there. The browser checks capabilities, never venue names. */
export interface VenueInfo {
  id: string
  name: string
  status: 'planned' | 'candidate' | 'manual' | 'read-only'
  /** Account indices stay strings, including indices beyond JavaScript's safe integer range. */
  source: 'none' | 'evm-address' | 'account-index'
  capabilities: VenueCapabilities
  quoteAsset: string | null
  /** Uses canonical `{instrument}` and/or the catalogue's native `{venueContractId}`. */
  tradeUrlTemplate: string | null
  /** Optional venue page where the owner creates a read-only credential. */
  credentialSetupUrl?: string | null
  /** Candle intervals the venue serves natively. */
  intervals: CandleInterval[]
  /** Five significant figures, or each instrument's tick. */
  priceRule: 'significant-figures' | 'tick-size'
  /** Bundled logo key from the fixed set. A key this browser does not know is dropped, showing the generic icon. */
  logo?: VenueLogoKey
}

export interface SystemInfo {
  application: 'Vessel'
  stage: 'foundation' | 'core'
  owner: { id: string; displayName: string }
  marketScope: 'perpetuals'
  allowsConcurrentPlays: true
  venues: VenueInfo[]
}

const capabilityNames = ['sync', 'instruments', 'orders', 'candles', 'marketContext', 'stream', 'stablecoinWallet']

export function isHttpsUrl(value: unknown): value is string {
  if (typeof value !== 'string' || !value.startsWith('https://') || value.length > 2048) return false
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && !!url.hostname && !url.username && !url.password
  } catch { return false }
}

export function isTradeUrlTemplate(value: unknown): value is string {
  if (typeof value !== 'string' || !/\{(?:instrument|venueContractId)\}/.test(value)) return false
  const resolved = value.replaceAll('{instrument}', 'BTC').replaceAll('{venueContractId}', 'BTC')
  return !/[{}]/.test(resolved) && isHttpsUrl(resolved)
}

function isVenue(value: unknown): value is VenueInfo {
  return isRecord(value) && typeof value.id === 'string' && /^[a-z][a-z\d-]{0,63}$/.test(value.id) &&
    typeof value.name === 'string' && value.name.trim().length > 0 &&
    ['planned', 'candidate', 'manual', 'read-only'].includes(String(value.status)) &&
    ['none', 'evm-address', 'account-index'].includes(String(value.source)) &&
    isRecord(value.capabilities) && capabilityNames.every(name => typeof (value.capabilities as Record<string, unknown>)[name] === 'boolean') &&
    ['accountDiscovery', 'readOnlyCredential', 'manualExecutions', 'currentOrderBook', 'paperExecution', 'fillHistory'].every(name => {
      const flag = (value.capabilities as Record<string, unknown>)[name]
      return flag === undefined || typeof flag === 'boolean'
    }) &&
    (value.quoteAsset === null || typeof value.quoteAsset === 'string') &&
    (value.tradeUrlTemplate === null || isTradeUrlTemplate(value.tradeUrlTemplate)) &&
    (value.credentialSetupUrl == null || isHttpsUrl(value.credentialSetupUrl)) &&
    Array.isArray(value.intervals) && value.intervals.every(interval => candleIntervals.includes(interval as CandleInterval)) &&
    ['significant-figures', 'tick-size'].includes(String(value.priceRule))
}

function isSystemInfo(value: unknown): value is SystemInfo {
  if (!isRecord(value) || !isRecord(value.owner) || !Array.isArray(value.venues)) return false
  const venues: unknown[] = value.venues
  const ids = venues.map(venue => isRecord(venue) ? venue.id : null)
  return value.application === 'Vessel' && ['foundation', 'core'].includes(String(value.stage)) &&
    value.marketScope === 'perpetuals' && value.allowsConcurrentPlays === true &&
    isGuid(value.owner.id) &&
    typeof value.owner.displayName === 'string' && value.owner.displayName.trim().length > 0 &&
    venues.every(isVenue) && new Set(ids).size === ids.length && ids.includes(manualVenueId)
}

/**
 * Token stays in the caller's runtime memory; never put it in an environment variable or URL. A session call reports
 * its 401 and 403 answers to `onAuthFailure`, like every other session request.
 */
export async function getSystem(credentials: string | ApiCredentials, onAuthFailure?: (failure: AuthFailure) => void): Promise<SystemInfo> {
  const session = typeof credentials !== 'string' && credentials.kind !== 'bearer'
  const body = await createApiCall(credentials, onAuthFailure)('/api/system', {
    timeout: 8_000, validate: isSystemInfo, ...session ? { codeMessage: (code: string) => authErrorMessage(code) } : {},
    unavailable: 'Vessel API is unavailable. Check that the API is running and the proxy target is correct.',
    invalidJson: 'The API returned an invalid system response.',
    noContent: 'The API returned an invalid system response.',
    invalidData: 'The API returned an incompatible system contract.',
    ...session ? {} : { httpError: (response: Response) => {
      if (response.status === 401 || response.status === 403) {
        return new ApiError('unauthorized', 'The API rejected this token. Check the configured server token and try again.', response.status)
      }
      if (response.status === 429) {
        return new ApiError('unauthorized', 'Too many wrong tokens from this device. Check the configured server token; wrong tokens are refused for a few minutes.', response.status)
      }
      if (response.status >= 500) {
        return new ApiError('unavailable', 'Vessel API is unavailable. Check the server configuration and try again.', response.status)
      }
      return new ApiError('http', `The API request failed with HTTP ${response.status}.`, response.status)
    } },
  })
  return { ...body, venues: body.venues.map(withKnownLogo) }
}

/** Unknown or malformed logo keys keep the venue usable with a generic icon. */
function withKnownLogo(venue: VenueInfo): VenueInfo {
  const { logo, ...rest } = venue as VenueInfo & { logo?: unknown }
  return isVenueLogoKey(logo) ? { ...rest, logo } : rest
}
