// Regressions from the PR #74 re-review: one expiry check per deadline, serialized polls, and the remember choice
// that an in-flight completion carries is the one the screen shows.
import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPublicAuthApi, createSessionAuthApi, type PairingClaim, type PairingCompletion, type PairingState } from '@/api/auth'
import { ApiError } from '@/api/errors'
import { secondWalletFixture, sessionFixture, walletFixture } from '@/test/identity-fixture'
import { ConnectPhoneDialog } from './ConnectPhoneDialog'
import { PairingScreen } from './PairingScreen'

vi.mock('./WalletSignFlow', () => ({
  WalletSignFlow: ({ onDone }: { onDone: () => void }) => <button onClick={onDone}>Finish step-up</button>,
}))

const pairingId = '7c1e9b52-4d8a-4f36-a0e7-2b5d9c3f1a84'
const future = (ms = 120_000) => new Date(Date.now() + ms).toISOString()
const claim = (expiresAt = future()): PairingClaim => ({
  code: '482913', expiresAt, journal: { displayName: 'Sam Rivera', wallet: walletFixture.address },
})
const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
const click = (name: string) => act(async () => { fireEvent.click(screen.getByRole('button', { name })) })
const deferred = <T,>() => {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}
const submit = () => act(async () => {
  fireEvent.change(screen.getByLabelText('Code shown on the phone'), { target: { value: '482913' } })
  fireEvent.click(screen.getByRole('button', { name: 'Approve' }))
})

function desktop(expiresAt = future()) {
  const auth = {
    ...createSessionAuthApi({ userId: sessionFixture.user.id, sessionId: sessionFixture.session.id }),
    createPairing: vi.fn(async () => ({ id: pairingId, secret: 's'.repeat(43), typedCode: '7KQ2M-HX9PD', expiresAt })),
    pairingStatus: vi.fn(async (): Promise<PairingState> => ({ status: 'claimed', expiresAt, device: 'Safari on iPhone' })),
    approvePairing: vi.fn(async () => undefined),
    cancelPairing: vi.fn(async () => undefined),
  }
  const handlers = { onConnected: vi.fn(), onClose: vi.fn() }
  const view = render(<ConnectPhoneDialog auth={auth} origin="https://vessel.test" accept={() => null} {...handlers} />)
  return { auth, ...handlers, ...view }
}

beforeEach(() => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-10-28T12:00:00Z'))
})
afterEach(() => { vi.useRealTimers() })

describe('pairing polls, deadlines and the remember choice', () => {
  it('serializes the deadline-triggered phone check with an in-flight scheduled poll', async () => {
    const pending = deferred<PairingCompletion>()
    const completePairing = vi.fn(() => pending.promise)
    const api = { ...createPublicAuthApi(), completePairing }
    const onSignedIn = vi.fn()
    render(<PairingScreen api={api} source={{ kind: 'claimed', claim: claim(future(3_000)) }}
      onSignedIn={onSignedIn} onBack={vi.fn()} />)
    await advance(2_000)
    await advance(8_000)
    expect(completePairing).toHaveBeenCalledTimes(1)
    await act(async () => { pending.resolve({ status: 'signed-in', session: sessionFixture }) })
    await advance(10_000)
    expect(onSignedIn).toHaveBeenCalledTimes(1)
    expect(completePairing).toHaveBeenCalledTimes(1)
  })

  it('serializes a desktop poll across the deadline and keeps its newly claimed pairing', async () => {
    const { auth } = desktop(future(3_000))
    const pending = deferred<PairingState>()
    auth.pairingStatus.mockImplementation(() => pending.promise)
    await click('Finish step-up')
    await advance(2_000)
    await advance(8_000)
    expect(auth.pairingStatus).toHaveBeenCalledTimes(1)
    await act(async () => { pending.resolve({ status: 'claimed', expiresAt: future(), device: 'Safari on iPhone' }) })
    expect(auth.cancelPairing).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Code shown on the phone')).toBeVisible()
    await advance(1_000)
    expect(auth.pairingStatus).toHaveBeenCalledTimes(1)
    await advance(1_000)
    expect(auth.pairingStatus).toHaveBeenCalledTimes(2)
  })

  it('finishes a successful approval even if the poll already observed approved', async () => {
    const { auth, onConnected } = desktop()
    const approval = deferred<undefined>()
    auth.approvePairing.mockImplementation(() => approval.promise)
    await click('Finish step-up')
    await advance(2_000)
    await submit()
    auth.pairingStatus.mockResolvedValue({ status: 'approved', expiresAt: future(), device: 'Safari on iPhone' })
    await advance(2_000)
    await act(async () => { approval.resolve(undefined) })
    expect(screen.getByText(/Waiting for Safari on iPhone to finish/)).toBeVisible()
    auth.pairingStatus.mockResolvedValue({ status: 'completed', expiresAt: future(), device: 'Safari on iPhone' })
    await advance(2_000)
    expect(screen.getByText('Safari on iPhone is connected.')).toBeVisible()
    const reads = auth.pairingStatus.mock.calls.length
    await advance(10_000)
    expect(auth.pairingStatus).toHaveBeenCalledTimes(reads)
    expect(onConnected).toHaveBeenCalledTimes(1)
    expect(auth.cancelPairing).not.toHaveBeenCalled()
  })

  it('does not overwrite completed when the approval success arrives later', async () => {
    const { auth, onConnected } = desktop()
    const approval = deferred<undefined>()
    auth.approvePairing.mockImplementation(() => approval.promise)
    await click('Finish step-up')
    await advance(2_000)
    await submit()
    auth.pairingStatus.mockResolvedValue({ status: 'completed', expiresAt: future(), device: 'Safari on iPhone' })
    await advance(2_000)
    await act(async () => { approval.resolve(undefined) })
    expect(screen.getByText('Safari on iPhone is connected.')).toBeVisible()
    expect(onConnected).toHaveBeenCalledTimes(1)
    expect(auth.cancelPairing).not.toHaveBeenCalled()
  })

  it('ends finishing from server expiry without sending DELETE', async () => {
    const { auth } = desktop()
    await click('Finish step-up')
    await advance(2_000)
    await submit()
    auth.pairingStatus.mockResolvedValue({ status: 'expired', expiresAt: future(-1), device: 'Safari on iPhone' })
    await advance(2_000)
    expect(screen.getByRole('alert')).toHaveTextContent('Request expired')
    const reads = auth.pairingStatus.mock.calls.length
    await advance(10_000)
    expect(auth.pairingStatus).toHaveBeenCalledTimes(reads)
    expect(auth.cancelPairing).not.toHaveBeenCalled()
  })

  it('drops an in-flight poll and approval after close and unmount, cancelling only once', async () => {
    const { auth, unmount, onConnected, onClose } = desktop()
    const approval = deferred<undefined>()
    const read = deferred<PairingState>()
    await click('Finish step-up')
    await advance(2_000)
    auth.approvePairing.mockImplementation(() => approval.promise)
    await submit()
    auth.pairingStatus.mockImplementation(() => read.promise)
    await advance(2_000)
    await click('Cancel')
    unmount()
    await act(async () => {
      approval.resolve(undefined)
      read.resolve({ status: 'completed', expiresAt: future(), device: 'Safari on iPhone' })
    })
    await advance(10_000)
    expect(auth.cancelPairing).toHaveBeenCalledExactlyOnceWith(pairingId)
    expect(onClose).toHaveBeenCalledTimes(1)
    expect(onConnected).not.toHaveBeenCalled()
    expect(auth.pairingStatus).toHaveBeenCalledTimes(2)
  })

  it('issues the lifetime shown by the checkbox when approval races an in-flight completion', async () => {
    const approved = deferred<undefined>()
    const completePairing = vi.fn(async ({ remember }: { remember: boolean }): Promise<PairingCompletion> => {
      // The poll has been sent but has not processed the desktop's approval yet.
      await approved.promise
      return { status: 'signed-in', session: { ...sessionFixture, session: { ...sessionFixture.session, remember } } }
    })
    const onSignedIn = vi.fn()
    render(<PairingScreen api={{ ...createPublicAuthApi(), completePairing }}
      source={{ kind: 'claimed', claim: claim() }} onSignedIn={onSignedIn} onBack={vi.fn()} />)
    await advance(2_000)
    expect(completePairing).toHaveBeenCalledTimes(1)
    const checkbox = screen.getByRole<HTMLInputElement>('checkbox', { name: 'Keep me signed in on this device' })
    // A locked checkbox would be safe too, as it could not display an unsent choice.
    if (!checkbox.disabled) await act(async () => { fireEvent.click(checkbox) })
    const displayedRemember = checkbox.checked
    await act(async () => { approved.resolve(undefined) })

    expect(onSignedIn).toHaveBeenCalledWith(expect.objectContaining({
      session: expect.objectContaining({ remember: displayedRemember }),
    }))
  })

  it('keeps two valid phones waiting within the shared 60-per-minute completion limit despite clock skew', async () => {
    // Two users behind one NAT. Only the first phone's clock is ahead.
    // Both actual server claim deadlines are still two minutes away.
    const skewedDeadline = future(-30_000)
    let permits = 60
    const completePairing = vi.fn(async (): Promise<PairingCompletion> => {
      if (--permits < 0) throw new ApiError('http', 'Too many attempts. Wait a moment before trying again.', 429, undefined, 'rate-limited')
      return { status: 'waiting' }
    })
    const api = { ...createPublicAuthApi(), completePairing }
    render(<PairingScreen api={api} source={{ kind: 'claimed', claim: claim(skewedDeadline) }}
      onSignedIn={vi.fn()} onBack={vi.fn()} />)
    render(<PairingScreen api={api} source={{ kind: 'claimed', claim: {
      ...claim(), code: '123456', journal: { displayName: 'Alex Chen', wallet: secondWalletFixture.address },
    } }}
      onSignedIn={vi.fn()} onBack={vi.fn()} />)

    // Flush React at each second, as separate real timer events would.
    for (let second = 0; second < 52; second++) await advance(1_000)

    // Two 2-second pollers plus one immediate expiry check each fit comfortably in this budget.
    expect.soft(screen.queryAllByRole('heading', { name: 'Confirm on your computer' })).toHaveLength(2)
    expect(completePairing.mock.calls.length).toBeLessThanOrEqual(56)
  })
})
