import { createSessionAuthApi, type Challenge, type SessionDto, type SessionRow, type WalletDto } from '@/api/auth'
import type { SessionCredentials } from '@/api/request'
import type { ShellIdentity } from '@/features/identity/types'

/** Synthetic people and addresses only. */
export const walletFixture: WalletDto = {
  id: '6f0c9d3e-1b7a-4c2e-9a51-3d8e2f4b7c10', address: '0x7A3f9C2e5b1D4a8F0c6E2b9D3a7F1e5C8b2D4caa', label: 'Hot wallet',
  addedAt: '2026-10-17T09:41:12.000Z', lastUsedAt: '2026-10-17T09:41:12.000Z', current: true,
}

export const secondWalletFixture: WalletDto = {
  id: '0b5e2c71-8d4f-4a39-b6e0-7c1a9f3d2e58', address: '0xC41d82B07e9A3f5c6D0b1E2a4F8c7D3e9B5a0f3b', label: 'Ledger',
  addedAt: '2026-10-17T09:50:00.000Z', lastUsedAt: '2026-10-17T10:02:00.000Z', current: false,
}

export const sessionFixture: SessionDto = {
  user: { id: 'a3c1f6e2-5b8d-4e0a-9f27-1d6c4b8e3a90', displayName: 'Sam Rivera', role: 'member', revision: 1 },
  wallet: walletFixture,
  session: {
    id: '9d2e4f61-3a7b-4c85-b0e1-6f8a2d5c7b34', createdAt: '2026-10-17T09:41:12.000Z',
    idleExpiresAt: '2026-10-17T21:41:12.000Z', absoluteExpiresAt: '2026-10-18T09:41:12.000Z', remember: false,
  },
  adoptsLegacyPreferences: false,
}

/** The session transport of `sessionFixture`, for API clients under test. */
export const credentialsFixture: SessionCredentials = { kind: 'session', userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }

/** A signed-in shell identity whose callbacks do nothing. Tests of the session itself use a `SessionController`. */
export function shellIdentityFixture(overrides: Partial<ShellIdentity> = {}): ShellIdentity {
  return {
    session: sessionFixture, walletName: null, auth: createSessionAuthApi(credentialsFixture),
    registerExitState: () => () => {}, profileRead: () => ({ generation: 0, epoch: 0 }), onProfile: () => true,
    checkSession: async () => 'current', onSessionChange: () => () => {}, ...overrides,
  }
}

export const sessionRowsFixture: SessionRow[] = [
  { id: sessionFixture.session.id, current: true, createdAt: '2026-10-17T09:41:12.000Z', lastSeenAt: '2026-10-17T09:41:12.000Z', remember: false,
    userAgent: 'Firefox on Linux', walletAddress: walletFixture.address },
  { id: '1e7c3b9a-6d2f-4a58-8b04-c9e5f1a7d362', current: false, createdAt: '2026-10-14T08:00:00.000Z', lastSeenAt: '2026-10-15T08:00:00.000Z', remember: true,
    userAgent: 'Safari on iOS', walletAddress: secondWalletFixture.address },
]

/** A server-written sign-in message for `address`, issued at `issued` and valid for five minutes. */
export function challengeFixture(address = walletFixture.address, issued = new Date()): Challenge {
  const expires = new Date(issued.getTime() + 5 * 60_000)
  return {
    challengeId: '4b8e1d27-9c3a-4f60-a5e2-8d7c1b3f9e46',
    message: [
      'vessel.test wants you to sign in with your Ethereum account:', address, '',
      'Sign in to Vessel. This is not a transaction and costs no gas.', '',
      'URI: https://vessel.test/', 'Version: 1', 'Chain ID: 1', 'Nonce: 9fK2mQ7xLp3Rt8VwA4cHs1',
      `Issued At: ${issued.toISOString()}`, `Expiration Time: ${expires.toISOString()}`,
    ].join('\n'),
    expiresAt: expires.toISOString(),
  }
}
