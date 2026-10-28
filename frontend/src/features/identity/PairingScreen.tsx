import { useCallback, useEffect, useRef, useState } from 'react'
import { Ban, CircleAlert, Clock, Plug, Users } from 'lucide-react'
import type { PairingClaim, PublicAuthApi, SessionDto } from '@/api/auth'
import { ApiError } from '@/api/errors'
import { SourceChip } from '@/components/accounts/AccountIdentity'
import { Button } from '@/components/ui/button'
import { checksumAddress } from '@/lib/eip55'
import { Deadline, pairingPollMs, SignInBrand, StateCard } from './IdentityParts'
import { groupedCode } from './pairing-link'

/** What the phone starts from: a secret it still has to claim, or a claim made already (the typed code). */
export type PairingSource = { kind: 'secret'; secret: string } | { kind: 'claimed'; claim: PairingClaim }

type Ended = { kind: 'expired' | 'cancelled' | 'contested' | 'unreachable' | 'other'; title: string; message: string }
type State =
  | { step: 'claiming' }
  | { step: 'waiting'; claim: PairingClaim }
  | { step: 'signing-in' }
  | ({ step: 'ended' } & Ended)

const endedBy = (status: 'expired' | 'cancelled' | 'contested'): State => ({ step: 'ended', kind: status, ...{
  expired: { title: 'Request expired', message: 'Pairing requests last five minutes. Start again on your computer.' },
  cancelled: { title: 'Request cancelled', message: 'The request was cancelled on your computer. Start again there if you still want to sign in.' },
  contested: { title: 'Another device used this code', message: 'Someone else used this code too, so Vessel cancelled it for safety. Start again on your computer.' },
}[status] })
const icons = { expired: <Clock size={16} />, cancelled: <Ban size={16} />, contested: <Users size={16} />, unreachable: <Plug size={16} />, other: <CircleAlert size={16} /> }

/** A failed claim or poll in the person's words; the API client already holds the copy for each code. */
function pairingFailure(cause: unknown): State {
  if (cause instanceof ApiError) {
    if (cause.code === 'pairing-contested') return endedBy('contested')
    if (cause.code === 'pairing-expired') return endedBy('expired')
    return cause.kind === 'unavailable' ? { step: 'ended', kind: 'unreachable', title: 'Vessel is unreachable', message: cause.message }
      : { step: 'ended', kind: 'other', title: 'Could not connect', message: cause.message }
  }
  return { step: 'ended', kind: 'other', title: 'Could not connect', message: 'The request could not be completed. Start again on your computer.' }
}

interface PairingScreenProps {
  api: PublicAuthApi
  source: PairingSource
  /** The server issued this device's session; the gate opens the workspace like after any sign-in. */
  onSignedIn: (session: SessionDto) => void
  /** Back to the wallet chooser. */
  onBack: () => void
}

/**
 * The phone's side of device pairing: claim the secret, show the confirmation code to type on the signed-in
 * desktop, then poll for the session. "Keep me signed in" is checked by default here (the desktop's stays off).
 *
 * The claim is not an abortable effect: React's development double mount would claim twice, and a second claim
 * contests (cancels) the pairing. One promise per screen is shared by both mounts, and only a mounted screen reads it.
 */
export function PairingScreen({ api, source, onSignedIn, onBack }: PairingScreenProps) {
  const [state, setState] = useState<State>(() => source.kind === 'claimed' ? { step: 'waiting', claim: source.claim } : { step: 'claiming' })
  const [remember, setRemember] = useState(true)
  const rememberNow = useRef(remember)
  // A completion in flight may issue the session with the choice it carried, so the box holds still until it answers.
  const completing = useRef(false)
  const signedIn = useRef(onSignedIn)
  useEffect(() => { rememberNow.current = remember; signedIn.current = onSignedIn }, [remember, onSignedIn])

  const claim = useRef<Promise<PairingClaim> | null>(null)
  useEffect(() => {
    if (source.kind !== 'secret') return
    let alive = true
    claim.current ??= api.claimPairing({ secret: source.secret })
    claim.current.then(
      claimed => { if (alive) setState({ step: 'waiting', claim: claimed }) },
      (cause: unknown) => { if (alive) setState(pairingFailure(cause)) })
    return () => { alive = false }
  }, [api, source])

  // The server owns the deadline (the desktop's approval restarts it), so a countdown reaching zero only asks it now.
  const checkNow = useRef(() => {})
  const waiting = state.step === 'waiting'
  useEffect(() => {
    if (!waiting) return
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    let failures = 0
    let busy = false
    const tick = async () => {
      busy = true
      completing.current = true
      try {
        const result = await api.completePairing({ remember: rememberNow.current }, controller.signal)
        if (controller.signal.aborted) return
        failures = 0
        if (result.status === 'signed-in') { setState({ step: 'signing-in' }); signedIn.current(result.session); return }
        if (result.status !== 'waiting') { setState(endedBy(result.status)); return }
      } catch (cause) {
        if (controller.signal.aborted) return
        // A wait notice or an unreachable server is retried a few times; anything else ends the request.
        const retry = cause instanceof ApiError && (cause.status === 429 || cause.kind === 'unavailable' || (cause.status ?? 0) >= 500)
        if (!retry || ++failures >= 5) { setState(pairingFailure(cause)); return }
      } finally {
        busy = false
        completing.current = false
      }
      timer = setTimeout(() => { void tick() }, pairingPollMs)
    }
    timer = setTimeout(() => { void tick() }, pairingPollMs)
    checkNow.current = () => { if (!busy && !controller.signal.aborted) { clearTimeout(timer); void tick() } }
    return () => { controller.abort(); clearTimeout(timer); checkNow.current = () => {} }
  }, [api, waiting])

  const expired = useCallback(() => { checkNow.current() }, [])
  return <main className="connection-page sign-in-page">
    <section className="panel connection-card sign-in-card" aria-labelledby="pairing-title" aria-busy={state.step === 'claiming' || state.step === 'signing-in'}>
      <div className="sign-in-head"><SignInBrand /></div>
      {state.step === 'claiming' && <><h1 id="pairing-title" className="sr-only">Connecting this device</h1>
        <p className="muted" role="status">Connecting this device…</p></>}
      {state.step === 'waiting' && <>
        <h1 id="pairing-title">Confirm on your computer</h1>
        <p className="sign-in-lede">Signing in to <strong className="pairing-journal">{state.claim.journal.displayName}</strong>{' '}
          <SourceChip source={checksumAddress(state.claim.journal.wallet)} /></p>
        <div className="sign-in-body">
          <div className="pairing-confirm">
            <p className="pairing-digits"><span className="sr-only">Confirmation code: </span>{groupedCode(state.claim.code)}</p>
            <p className="pairing-instruction">Type this code in Vessel on your computer.</p>
          </div>
          <label className="sign-in-remember"><input type="checkbox" checked={remember} onChange={event => {
            if (completing.current) return
            rememberNow.current = event.target.checked
            setRemember(event.target.checked)
          }} />Keep me signed in on this device</label>
          <p className="muted pairing-wait" role="status"><span className="identity-spinner" aria-hidden="true" />Waiting for approval…
            <Deadline key={state.claim.expiresAt} expiresAt={state.claim.expiresAt} onExpired={expired} /></p>
          <div className="sign-in-actions"><Button size="lg" variant="outline" onClick={onBack}>Cancel</Button></div>
        </div>
      </>}
      {state.step === 'signing-in' && <><h1 id="pairing-title" className="sr-only">Signing in</h1>
        <p className="muted" role="status">Opening your journal…</p></>}
      {state.step === 'ended' && <><h1 id="pairing-title" className="sr-only">Could not connect this device</h1>
        <div className="sign-in-body"><StateCard icon={icons[state.kind]}
          tone={state.kind === 'contested' ? 'bad' : 'warn'} title={state.title} actions={[{ label: 'Back to sign-in', primary: true, onClick: onBack }]}>
          {state.message}</StateCard></div></>}
    </section>
  </main>
}
