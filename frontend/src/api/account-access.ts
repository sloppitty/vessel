import { ApiError, rateLimitMessage } from './errors'

/** Only the development build allows plaintext LAN entry. The API enforces its own environment. */
export function credentialTransportAllowed(
  location: Pick<Location, 'protocol' | 'hostname'> = window.location,
  development = import.meta.env.DEV,
) {
  return location.protocol === 'https:' || location.protocol === 'http:' &&
    (development || ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname))
}

export const credentialTransportMessage = 'Use HTTPS before entering a read-only token. Token entry is disabled on plain HTTP outside localhost.'
export const credentialDevelopmentTransportMessage = 'Development only: this connection uses HTTP. The token is sent without transport encryption. Use only on a trusted test network; production requires HTTPS.'
export const credentialSaveMessage = 'The read-only token could not be verified or saved. Check its scope and expiry, and ask the operator to check credential storage. Public account reads remain available.'
export const credentialStorageMessage = 'Credential storage is not configured. Ask the Vessel operator to initialize a private key file, set Vessel__Credentials__KeyFile, and restart the API. Then refresh credential status. Public account reads remain available.'

/** Only local messages and HTTP status are safe here, never an upstream error body. */
export function credentialFailureMessage(cause: unknown, fallback = credentialSaveMessage): string {
  if (!(cause instanceof ApiError)) return fallback
  if (cause.kind === 'unavailable')
    return 'The credential request did not complete. Check the connection and retry. Refresh credential status before retrying a save.'
  switch (cause.status) {
    case 401: return 'Your session ended. Sign in again, then retry.'
    case 403: return 'The server could not confirm a secure connection for token entry. Use HTTPS; if this page already uses HTTPS, ask the operator to check the trusted reverse proxy.'
    case 404: return 'The account or credential endpoint is unavailable. Refresh the workspace and check that the frontend and API are on the same version.'
    case 409: return 'The account changed or is disabled. Refresh its settings and enable it before saving a token.'
    case 413: return 'The credential request is too large. Paste only the read-only token, up to 512 characters.'
    case 429: return rateLimitMessage(cause.retryAtUtc)
    case 502:
    case 503:
    case 504: return 'Credential verification or storage is unavailable. Retry later; if this persists, ask the operator to check the venue connection and server credential vault.'
    default: return fallback
  }
}

export interface DiscoveredAccount {
  sourceId: string
  name: string
  accountType: 'main' | 'subaccount'
  accountValueUsd: string | null
  collateralUsd?: string | null
  availableBalanceUsd?: string | null
  existingAccountId: string | null
  isEnabled: boolean | null
}

export interface AccountDiscovery {
  venueId: string
  address: string
  accounts: DiscoveredAccount[]
  notice: string
}

export interface AccountCredential {
  storageConfigured: boolean
  credential: {
    scope: 'single' | 'all'
    expiresAt: string
    lastVerifiedAt: string | null
    status: 'valid' | 'expiring' | 'expired' | 'unavailable'
  } | null
}

export const accountIndex = (value: unknown): value is string =>
  typeof value === 'string' && /^(0|[1-9]\d{0,18})$/.test(value) && BigInt(value) <= 9223372036854775807n
