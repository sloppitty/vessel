import { ApiError } from './errors'
import { hasNoControlCharacters, isCount, isGuid, isListOf, isNonBlankText, isNullOr, isRecord, isText, isTimestamp } from './guards'
import { createApiCall, type AuthFailure, type SessionGeneration } from './request'

/** `GET /api/auth/config`: the server's registration policy. `originAccepted` is only a hint for the screen. */
export interface AuthConfig { registration: RegistrationPolicy; originAccepted: boolean }
export type RegistrationPolicy = 'closed' | 'invitation' | 'allow-list' | 'open'

export interface Challenge { challengeId: string; message: string; expiresAt: string }
export interface UserDto { id: string; displayName: string; role: 'member' | 'operator'; revision: number }
/** A sign-in wallet. `address` is EIP-55; `current` marks the wallet that authenticated this session. */
export interface WalletDto { id: string; address: string; label: string | null; addedAt: string; lastUsedAt: string | null; current: boolean }
export interface SessionDto {
  user: UserDto
  wallet: WalletDto
  session: { id: string; createdAt: string; idleExpiresAt: string; absoluteExpiresAt: string; remember: boolean }
  /** True only for the configured installation owner, whose journal predates wallet sign-in: the browser's unscoped preferences become theirs. */
  adoptsLegacyPreferences: boolean
}
export interface SessionRow {
  id: string; current: boolean; createdAt: string; lastSeenAt: string; remember: boolean
  /** Browser family and OS, e.g. `Firefox on Linux`, when the server could tell. */
  userAgent: string | null
  walletAddress: string | null
  /** `pairing`: a device connected by scanning a code from a signed-in session; shown as "Paired" in the list. */
  source: SessionSource
}
export type SessionSource = 'wallet' | 'pairing'
export type VerifyResult =
  | { status: 'signed-in'; session: SessionDto }
  | { status: 'registration-required'; registration: string; requiresInvitation: boolean; expiresAt: string }
export type StepUpAction = 'wallet.link' | 'wallet.remove' | 'device.pair'

/** Device pairing: `completed` and the terminal statuses only ever come from the desktop's status read. */
export type PairingStatus = 'pending' | 'claimed' | 'approved' | 'completed' | 'expired' | 'cancelled' | 'contested'
/** `POST /api/auth/pairings`: `secret` goes into the QR link's fragment, `typedCode` is the `XXXXX-XXXXX` fallback. */
export interface PairingCreated { id: string; secret: string; typedCode: string; expiresAt: string }
/** `device` is the claiming phone's browser and OS, present from `claimed` on. `expiresAt` is the current state's deadline. */
export interface PairingState { status: PairingStatus; expiresAt: string; device: string | null }
/** What the phone learns by claiming: the confirmation code to type on the desktop and the journal it would join. */
export interface PairingClaim { code: string; expiresAt: string; journal: { displayName: string; wallet: string } }
export type PairingClaimRequest = { secret: string } | { typedCode: string }
export type PairingCompletion =
  | { status: 'waiting' }
  | { status: 'signed-in'; session: SessionDto }
  | { status: 'expired' | 'cancelled' | 'contested' }

/** The tab's session: `X-Vessel-User` carries the user, `sessionId` names the session sign-out expects. */
export interface SessionIdentity { userId: string; sessionId: string; generation?: SessionGeneration }

export const displayNameLimit = 60
export const walletLabelLimit = 40
export const signInWalletLimit = 20

const codeCopy: Record<string, string> = {
  'request-invalid': 'Vessel could not read this request. Reload the page and try again.',
  'address-invalid': 'This wallet address is not valid here. Choose another wallet or address.',
  'challenge-invalid': 'This request is no longer valid. Start again.',
  'challenge-expired': 'Sign-in requests last five minutes. Start a new one.',
  'signature-invalid': 'The signature could not be checked. Try again.',
  'signature-unsupported': 'This signature format isn’t supported yet, as with Safe and other smart accounts. Use a regular wallet address.',
  'signature-mismatch': 'The signature came from a different address. Sign with the address you connected.',
  'registration-invalid': 'This journal request is no longer valid. Sign in again to start over.',
  'display-name-invalid': `Use 1 to ${displayNameLimit} printable characters for the display name.`,
  'label-invalid': `Use at most ${walletLabelLimit} printable characters for the label.`,
  'session-required': 'Your session ended. Sign in again.',
  'session-expired': 'Your session expired. Sign in again.',
  'session-revoked': 'Your session was signed out. Sign in again.',
  'session-user-mismatch': 'This tab belongs to another sign-in. Reload to continue.',
  'origin-rejected': 'This server does not accept sign-in from this address. Ask its operator to add it.',
  'csrf-rejected': 'The browser could not confirm this request came from Vessel. Reload and try again.',
  'registration-closed': 'This server is not admitting new journals.',
  'invitation-required': 'This server only admits invited wallets. Ask its operator for a code.',
  'invitation-invalid': 'This invitation code is not valid, has expired or was already used.',
  'user-suspended': 'The operator of this server has suspended this journal. Contact them to restore access.',
  'step-up-required': 'Confirm with a wallet already linked to this journal to continue.',
  'operator-required': 'Only an operator can do this.',
  'not-found': 'This item is no longer available.',
  'wallet-taken': 'This wallet already signs in to another journal.',
  'wallet-already-linked': 'This wallet is already linked to your journal.',
  'wallet-limit': `This journal already has the maximum of ${signInWalletLimit} sign-in wallets. Remove one before linking another.`,
  'last-wallet': 'You can’t remove your only sign-in wallet. Link another one first.',
  'revision-conflict': 'Your profile changed somewhere else. It has been reloaded; try again.',
  'session-changed': 'You signed in again in another tab.',
  'request-too-large': 'This request is too large.',
  'rate-limited': 'Too many attempts. Wait a moment before trying again.',
  'identity-unavailable': 'Sign-in is unavailable right now. Try again shortly.',
  'pairing-invalid': 'This code is not valid, was already used or has ended. Start again on your computer.',
  'pairing-expired': 'This code expired. Start again on your computer.',
  'pairing-code-invalid': 'That confirmation code is not right.',
  'pairing-contested': 'Another device used this code first, so it was cancelled. Start again on your computer.',
}

/** The wrong-code message with the attempts the server says are left; the server cancels the pairing at zero. */
export function pairingCodeMessage(error: ApiError): string {
  const base = authErrorMessage('pairing-code-invalid')!
  const left = error.attemptsLeft
  if (left === undefined) return base
  return left === 0 ? `${base} Too many wrong codes, so this request was cancelled.` : `${base} ${left} ${left === 1 ? 'attempt' : 'attempts'} left.`
}

/** Vessel's wording for an auth error code; the server sends stable codes and the browser owns the copy. */
export function authErrorMessage(code: string): string | undefined {
  return Object.hasOwn(codeCopy, code) ? codeCopy[code] : undefined
}

export const isAddress = (v: unknown): v is string => typeof v === 'string' && /^0x[\da-fA-F]{40}$/.test(v)
// Display names and labels are bounded by characters on the server; allow surrogate pairs here.
const isDisplayName = (v: unknown): v is string => isNonBlankText(v, displayNameLimit * 2) && hasNoControlCharacters(v)
const isLabel = (v: unknown) => v === null || isNonBlankText(v, walletLabelLimit * 2) && hasNoControlCharacters(v as string)
const isUtc = (v: unknown) => isTimestamp(v, 40)

export function isUser(v: unknown): v is UserDto {
  return isRecord(v) && isGuid(v.id) && isDisplayName(v.displayName) && (v.role === 'member' || v.role === 'operator') && isCount(v.revision)
}
export function isWallet(v: unknown): v is WalletDto {
  return isRecord(v) && isGuid(v.id) && isAddress(v.address) && isLabel(v.label) && isUtc(v.addedAt) &&
    isNullOr(v.lastUsedAt, isUtc) && typeof v.current === 'boolean'
}
export function isSession(v: unknown): v is SessionDto {
  if (!isRecord(v) || !isRecord(v.session)) return false
  const s = v.session
  return isUser(v.user) && isWallet(v.wallet) && isGuid(s.id) && [s.createdAt, s.idleExpiresAt, s.absoluteExpiresAt].every(isUtc) &&
    typeof s.remember === 'boolean' && typeof v.adoptsLegacyPreferences === 'boolean'
}
function isSessionRow(v: unknown): v is SessionRow {
  return isRecord(v) && isGuid(v.id) && typeof v.current === 'boolean' && isUtc(v.createdAt) && isUtc(v.lastSeenAt) &&
    typeof v.remember === 'boolean' && isNullOr(v.userAgent, item => isNonBlankText(item, 80) && hasNoControlCharacters(item as string)) &&
    isNullOr(v.walletAddress, isAddress) && (v.source === 'wallet' || v.source === 'pairing')
}
function isConfig(v: unknown): v is AuthConfig {
  return isRecord(v) && ['closed', 'invitation', 'allow-list', 'open'].includes(String(v.registration)) && typeof v.originAccepted === 'boolean'
}
/** The server writes the message; the browser only checks it is a bounded sign-in text before showing it. */
function isChallenge(v: unknown): v is Challenge {
  return isRecord(v) && isGuid(v.challengeId) && isText(v.message, 2048) && v.message.includes(' wants you to sign in with your Ethereum account:\n') &&
    v.message.split('\n').every(hasNoControlCharacters) && isUtc(v.expiresAt)
}
function isVerifyResult(v: unknown): v is VerifyResult {
  if (!isRecord(v)) return false
  if (v.status === 'signed-in') return isSession(v.session)
  return v.status === 'registration-required' && isText(v.registration, 512) && /^[\x21-\x7e]+$/.test(v.registration) &&
    typeof v.requiresInvitation === 'boolean' && isUtc(v.expiresAt)
}
const pairingStatuses: readonly string[] = ['pending', 'claimed', 'approved', 'completed', 'expired', 'cancelled', 'contested']
export const pairingSecretPattern = /^[A-Za-z0-9_-]{43}$/
const isDevice = (v: unknown) => isNonBlankText(v, 80) && hasNoControlCharacters(v as string)
function isPairingCreated(v: unknown): v is PairingCreated {
  return isRecord(v) && isGuid(v.id) && typeof v.secret === 'string' && pairingSecretPattern.test(v.secret) &&
    typeof v.typedCode === 'string' && /^[0-9A-Z]{5}-[0-9A-Z]{5}$/.test(v.typedCode) && isUtc(v.expiresAt)
}
function isPairingState(v: unknown): v is PairingState {
  return isRecord(v) && typeof v.status === 'string' && pairingStatuses.includes(v.status) && isUtc(v.expiresAt) && isNullOr(v.device, isDevice)
}
function isPairingClaim(v: unknown): v is PairingClaim {
  return isRecord(v) && typeof v.code === 'string' && /^\d{6}$/.test(v.code) && isUtc(v.expiresAt) && isRecord(v.journal) &&
    isDisplayName(v.journal.displayName) && isAddress(v.journal.wallet)
}
function isPairingCompletion(v: unknown): v is PairingCompletion {
  if (!isRecord(v)) return false
  if (v.status === 'signed-in') return isSession(v.session)
  return v.status === 'waiting' || v.status === 'expired' || v.status === 'cancelled' || v.status === 'contested'
}
const isCreated = (v: unknown): v is { session: SessionDto } => isRecord(v) && isSession(v.session)
const none = (v: unknown): v is undefined => v === undefined

/** The address line of a server message, so the browser signs only a message for the address it connected. */
export function challengeAddress(message: string): string | null {
  const line = message.split('\n')[1]
  return isAddress(line) ? line : null
}

const authRequest = { timeout: 15_000, codeMessage: authErrorMessage,
  unavailable: 'Vessel is unreachable. Check your connection and try again.',
  notFound: 'This item is no longer available.',
  invalidJson: 'Vessel returned an invalid sign-in response.',
  invalidData: 'Vessel returned an incompatible sign-in response.',
  noContent: 'Vessel returned an incompatible sign-in response.' }
const json = (body: unknown) => ({ body: JSON.stringify(body) })
const withSignal = (signal?: AbortSignal) => signal ? { signal } : {}

/** The anonymous routes, plus the session probe. A 401 here is an answer, never a teardown. */
export function createPublicAuthApi() {
  const call = createApiCall({ kind: 'public' })
  return {
    config: (origin: string, signal?: AbortSignal) =>
      call(`/api/auth/config?${new URLSearchParams({ origin })}`, { ...authRequest, timeout: 8_000, validate: isConfig, ...withSignal(signal) }),
    /** `GET /api/auth/session` with the public kind: it is the only protected route that needs no `X-Vessel-User`. */
    session: (signal?: AbortSignal) => call('/api/auth/session', { ...authRequest, timeout: 8_000, validate: isSession, ...withSignal(signal) }),
    challenge: (address: string, signal?: AbortSignal) => {
      if (!isAddress(address)) return Promise.reject(new ApiError('http', codeCopy['address-invalid']!, 400, undefined, 'address-invalid'))
      return call('/api/auth/challenge', { ...authRequest, method: 'POST', ...json({ address }), validate: isChallenge, ...withSignal(signal) })
    },
    verify: (request: { challengeId: string; signature: string; remember: boolean }, signal?: AbortSignal) =>
      call('/api/auth/verify', { ...authRequest, method: 'POST', ...json(request), validate: isVerifyResult, ...withSignal(signal) }),
    register: (request: { registration: string; displayName: string; invitation?: string; remember: boolean }, signal?: AbortSignal) =>
      call('/api/auth/register', { ...authRequest, method: 'POST', ...json(request), validate: isCreated, ...withSignal(signal) }),
    /** The phone redeems the secret from the QR (or the typed code) for a confirmation code. A second claim contests the pairing. */
    claimPairing: (request: PairingClaimRequest, signal?: AbortSignal) =>
      call('/api/auth/pairings/claim', { ...authRequest, method: 'POST', ...json(request), validate: isPairingClaim, ...withSignal(signal) }),
    /** Polled by the phone with its binding cookie; `signed-in` sets the session cookie. */
    completePairing: (request: { remember: boolean }, signal?: AbortSignal) =>
      call('/api/auth/pairings/complete', { ...authRequest, method: 'POST', ...json(request), validate: isPairingCompletion, ...withSignal(signal) }),
  }
}
export type PublicAuthApi = ReturnType<typeof createPublicAuthApi>

/** Session-bound auth routes: profile, sessions, wallets and step-up. */
export function createSessionAuthApi(identity: SessionIdentity, onUnauthorized?: (failure: AuthFailure) => void) {
  const call = createApiCall({ kind: 'session', ...identity }, onUnauthorized)
  const id = (value: string) => {
    if (!isGuid(value)) throw new ApiError('invalid-response', 'The identifier is invalid.')
    return value
  }
  return {
    /** Revokes only when the cookie's session is `expectedSessionId`; otherwise 409 `session-changed`. */
    signOut: (expectedSessionId: string) =>
      call('/api/auth/sign-out', { ...authRequest, method: 'POST', ...json({ expectedSessionId }), validate: none }),
    sessions: (signal?: AbortSignal) =>
      call('/api/auth/sessions', { ...authRequest, validate: (v): v is SessionRow[] => isListOf(v, isSessionRow, 200), ...withSignal(signal) }),
    revokeSession: (sessionId: string) => call(`/api/auth/sessions/${id(sessionId)}`, { ...authRequest, method: 'DELETE', validate: none }),
    revokeOtherSessions: () => call('/api/auth/sessions/revoke-others', { ...authRequest, method: 'POST', validate: none }),
    updateProfile: (displayName: string, expectedRevision: number) =>
      call('/api/auth/profile', { ...authRequest, method: 'PATCH', ...json({ displayName, expectedRevision }), validate: isUser }),
    wallets: (signal?: AbortSignal) =>
      call('/api/auth/wallets', { ...authRequest, validate: (v): v is WalletDto[] => isListOf(v, isWallet, 200), ...withSignal(signal) }),
    stepUpChallenge: (request: { action: StepUpAction; target?: string; address: string }, signal?: AbortSignal) =>
      call('/api/auth/step-up/challenge', { ...authRequest, method: 'POST', ...json(request), validate: isChallenge, ...withSignal(signal) }),
    stepUp: (request: { challengeId: string; signature: string }, signal?: AbortSignal) =>
      call('/api/auth/step-up', { ...authRequest, method: 'POST', ...json(request), validate: none, ...withSignal(signal) }),
    walletChallenge: (address: string, signal?: AbortSignal) =>
      call('/api/auth/wallets/challenge', { ...authRequest, method: 'POST', ...json({ address }), validate: isChallenge, ...withSignal(signal) }),
    linkWallet: (request: { challengeId: string; signature: string; label?: string }, signal?: AbortSignal) =>
      call('/api/auth/wallets', { ...authRequest, method: 'POST', ...json(request), validate: isWallet, ...withSignal(signal) }),
    updateWallet: (walletId: string, label: string | null) =>
      call(`/api/auth/wallets/${id(walletId)}`, { ...authRequest, method: 'PATCH', ...json({ label }), validate: isWallet }),
    removeWallet: (walletId: string) => call(`/api/auth/wallets/${id(walletId)}`, { ...authRequest, method: 'DELETE', validate: none }),
    /** Needs a live `device.pair` grant (403 `step-up-required` otherwise); consumes it. */
    createPairing: (signal?: AbortSignal) =>
      call('/api/auth/pairings', { ...authRequest, method: 'POST', validate: isPairingCreated, ...withSignal(signal) }),
    pairingStatus: (pairingId: string, signal?: AbortSignal) =>
      call(`/api/auth/pairings/${id(pairingId)}`, { ...authRequest, validate: isPairingState, ...withSignal(signal) }),
    approvePairing: (pairingId: string, code: string) =>
      call(`/api/auth/pairings/${id(pairingId)}/approve`, { ...authRequest, method: 'POST', ...json({ code }), validate: none }),
    cancelPairing: (pairingId: string) =>
      call(`/api/auth/pairings/${id(pairingId)}`, { ...authRequest, method: 'DELETE', validate: none }),
  }
}
export type SessionAuthApi = ReturnType<typeof createSessionAuthApi>
