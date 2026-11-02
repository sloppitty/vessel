// Regressions from the PR #74 review: the server owns each deadline, and a reply answers only for its own pairing.
import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createSessionAuthApi, type PairingCreated, type PairingState, type SessionAuthApi } from '@/api/auth'
import { ApiError } from '@/api/errors'
import { sessionFixture, walletFixture } from '@/test/identity-fixture'
import { ConnectPhoneDialog } from './ConnectPhoneDialog'
import { PairingScreen } from './PairingScreen'

// Keep the real pairing components. Wallet cryptography is not under review here.
vi.mock('./WalletSignFlow', () => ({
  WalletSignFlow: ({ onDone }: { onDone: () => void }) =>
    <button onClick={onDone}>Finish wallet step-up</button>,
}))

const origin = 'https://vessel.test'
const a = '7c1e9b52-4d8a-4f36-a0e7-2b5d9c3f1a84'
const b = '8c1e9b52-4d8a-4f36-a0e7-2b5d9c3f1a84'
const deadline = () => new Date(Date.now() + 120_000).toISOString()
const created = (id: string): PairingCreated => ({
  id, secret: 's'.repeat(43), typedCode: '7KQ2M-HX9PD', expiresAt: deadline(),
})
const state = (status: PairingState['status']): PairingState => ({
  status, expiresAt: deadline(), device: status === 'pending' ? null : 'Safari on iPhone',
})
const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
const click = (name: string) => act(async () => {
  fireEvent.click(screen.getByRole('button', { name }))
})
const submitCode = () => act(async () => {
  fireEvent.change(screen.getByLabelText('Code shown on the phone'), { target: { value: '482913' } })
  fireEvent.click(screen.getByRole('button', { name: 'Approve' }))
})

beforeEach(() => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-10-28T12:00:00Z'))
})
afterEach(() => { vi.useRealTimers() })

describe('pairing deadlines and stale replies', () => {
  it('does not expire the phone when the desktop approves just before its approval deadline', async () => {
    let approved = false
    const onSignedIn = vi.fn()
    const completePairing = vi.fn(async () => {
      if (!approved) return { status: 'waiting' as const }
      await new Promise(resolve => setTimeout(resolve, 100)) // Ordinary HTTP response latency.
      return { status: 'signed-in' as const, session: sessionFixture }
    })
    render(<PairingScreen
      api={{ completePairing } as unknown as Parameters<typeof PairingScreen>[0]['api']}
      source={{ kind: 'claimed', claim: {
        code: '482913', expiresAt: deadline(),
        journal: { displayName: 'Sam Rivera', wallet: walletFixture.address },
      } }}
      onSignedIn={onSignedIn} onBack={vi.fn()} />)

    await advance(118_000) // Last poll still says "waiting".
    await advance(1_000)
    approved = true // The server now allows another full two minutes for completion.
    await advance(1_000)
    await advance(100)

    expect(onSignedIn).toHaveBeenCalledWith(sessionFixture)
    expect(screen.queryByText('Request expired')).not.toBeInTheDocument()
  })

  it('refreshes desktop status instead of cancelling a phone that claimed just before the QR deadline', async () => {
    const pairing = created(a)
    let claimed = false
    const cancelPairing = vi.fn(async () => undefined)
    const auth: SessionAuthApi = {
      ...createSessionAuthApi({ userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }),
      createPairing: vi.fn(async () => pairing),
      pairingStatus: vi.fn(async () => claimed ? state('claimed') : {
        status: 'pending' as const, expiresAt: pairing.expiresAt, device: null,
      }),
      cancelPairing,
    }
    render(<ConnectPhoneDialog auth={auth} origin={origin} accept={() => null}
      onConnected={vi.fn()} onClose={vi.fn()} />)
    await click('Finish wallet step-up')
    await advance(118_000)
    await advance(1_000)
    claimed = true // The server has extended the deadline to t=239 seconds.
    await advance(1_000)

    expect(cancelPairing).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Code shown on the phone')).toBeVisible()
  })

  it('ignores an old approval refusal after Try again creates a replacement pairing', async () => {
    let rejectOldApproval!: (error: unknown) => void
    const oldApproval = new Promise<undefined>((_resolve, reject) => { rejectOldApproval = reject })
    let oldEnded = false
    const cancelPairing = vi.fn(async () => undefined)
    const auth: SessionAuthApi = {
      ...createSessionAuthApi({ userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }),
      createPairing: vi.fn().mockImplementationOnce(async () => created(a))
        .mockImplementationOnce(async () => created(b)),
      pairingStatus: vi.fn(async id => state(id === a && oldEnded ? 'cancelled' : 'claimed')),
      approvePairing: vi.fn(() => oldApproval),
      cancelPairing,
    }
    render(<ConnectPhoneDialog auth={auth} origin={origin} accept={() => null}
      onConnected={vi.fn()} onClose={vi.fn()} />)
    await click('Finish wallet step-up')
    await advance(2_000)
    await submitCode()
    oldEnded = true // A concurrent server cancellation is observed before approve's reply arrives.
    await advance(2_000)
    await click('Try again')
    await click('Finish wallet step-up')
    await advance(2_000)
    expect(screen.getByLabelText('Code shown on the phone')).toBeVisible()

    await act(async () => {
      rejectOldApproval(new ApiError('http', 'Old pairing is no longer valid.', 400, undefined, 'pairing-invalid'))
    })

    expect(cancelPairing).not.toHaveBeenCalledWith(b)
    expect(screen.getByLabelText('Code shown on the phone')).toBeVisible()
  })
})
