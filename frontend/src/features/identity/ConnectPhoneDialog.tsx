import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { CheckCircle2, Smartphone, TriangleAlert } from 'lucide-react'
import { pairingCodeMessage, type PairingCreated, type PairingState, type SessionAuthApi } from '@/api/auth'
import { ApiError, errorMessage } from '@/api/errors'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Deadline, pairingPollMs } from './IdentityParts'
import { isLoopbackOrigin, pairingUrl } from './pairing-link'
import { PairingQr } from './PairingQr'
import { WalletSignFlow } from './WalletSignFlow'


type Phase =
  | { step: 'loopback' }
  | { step: 'confirm'; attempt: number }
  | { step: 'creating' }
  | { step: 'show'; pairing: PairingCreated; typed: boolean }
  | { step: 'approve'; pairing: PairingCreated; device: string; expiresAt: string; submitting: boolean; error: string | null; attempt: number }
  | { step: 'finishing'; pairing: PairingCreated; device: string }
  | { step: 'done'; device: string }
  | { step: 'ended'; title: string; message: string }

const ended = (title: string, message: string): Phase => ({ step: 'ended', title, message })

/** The end of a pairing the server reports, in the person's words. */
function endedBy(status: 'expired' | 'cancelled' | 'contested'): Phase {
  switch (status) {
    case 'expired': return ended('Request expired', 'Pairing requests last two minutes. Start a new one.')
    case 'cancelled': return ended('Request cancelled', 'This request was cancelled. Start a new one.')
    case 'contested': return ended('Another device used the code', 'Someone else scanned this code too, so Vessel cancelled it for safety. Start a new one and scan it yourself.')
  }
}

interface ConnectPhoneDialogProps {
  auth: SessionAuthApi
  /** A reason to refuse the signing address before asking the server (not linked to this journal). */
  accept: (address: string) => string | null
  /** The phone signed in: the sessions list reloads. Called once. */
  onConnected: () => void
  onClose: () => void
  onCloseAutoFocus?: (event: Event) => void
  /** The page's origin, which the QR link points at. Tests choose it. */
  origin?: string
}

/**
 * Settings → Sessions → Connect a phone. Step-up (`device.pair`), then a QR code for the phone, the phone's
 * confirmation code typed here and the new session. Closing cancels the pairing; the polling stops with the dialog.
 *
 * Nothing creates server state from an effect (React's development double mount would create it twice): the
 * pairing is created by the step-up flow finishing, or by Try again.
 */
export function ConnectPhoneDialog({ auth, accept, onConnected, onClose, onCloseAutoFocus, origin = window.location.origin }: ConnectPhoneDialogProps) {
  const [phase, setPhase] = useState<Phase>(() => isLoopbackOrigin(origin) ? { step: 'loopback' } : { step: 'confirm', attempt: 0 })
  // The pairing this dialog would cancel when it goes away: set once the server created it, cleared when it ended.
  const live = useRef<string | null>(null)
  const run = useRef({ id: 0, controller: new AbortController() })
  const begin = useCallback(() => {
    run.current.controller.abort()
    run.current = { id: run.current.id + 1, controller: new AbortController() }
    return run.current
  }, [])
  const connected = useRef(false)

  const cancel = useCallback(() => {
    const id = live.current
    live.current = null
    if (id) auth.cancelPairing(id).catch(() => undefined)
  }, [auth])
  // Unmount (a tab change, a sign-out) stops the run and cancels the open pairing like Cancel does.
  useEffect(() => () => { begin(); cancel() }, [begin, cancel])

  const close = () => { begin(); cancel(); onClose() }
  const finish = useCallback((next: Phase) => { cancel(); setPhase(next) }, [cancel])

  async function create() {
    const current = begin()
    setPhase({ step: 'creating' })
    try {
      const pairing = await auth.createPairing(current.controller.signal)
      if (run.current !== current) { auth.cancelPairing(pairing.id).catch(() => undefined); return }
      live.current = pairing.id
      setPhase({ step: 'show', pairing, typed: false })
    } catch (cause) {
      if (run.current !== current) return
      // The five-minute grant lapsed while the wallet signed: confirm again.
      if (cause instanceof ApiError && cause.code === 'step-up-required') setPhase(phase => ({ step: 'confirm', attempt: (phase.step === 'confirm' ? phase.attempt : 0) + 1 }))
      else setPhase(ended('Could not start', errorMessage(cause, 'The request could not be started. Try again.')))
    }
  }

  const pairing = phase.step === 'show' || phase.step === 'approve' || phase.step === 'finishing' ? phase.pairing : null
  const pairingId = pairing?.id ?? null
  const apply = useRef<(state: PairingState) => void>(() => {})
  useEffect(() => {
    apply.current = state => {
      switch (state.status) {
        case 'claimed':
          setPhase(phase => phase.step === 'show' ? { step: 'approve', pairing: phase.pairing, device: state.device ?? 'A new device', expiresAt: state.expiresAt, submitting: false, error: null, attempt: 0 }
            : phase.step === 'approve' && phase.expiresAt !== state.expiresAt ? { ...phase, expiresAt: state.expiresAt } : phase)
          break
        case 'approved':
          setPhase(phase => phase.step === 'approve' && !phase.submitting ? { step: 'finishing', pairing: phase.pairing, device: phase.device } : phase)
          break
        case 'completed':
          if (!connected.current) {
            connected.current = true
            live.current = null
            onConnected()
          }
          setPhase(phase => phase.step === 'show' || phase.step === 'approve' || phase.step === 'finishing'
            ? { step: 'done', device: 'device' in phase ? phase.device : state.device ?? 'Your phone' } : phase)
          break
        case 'expired': case 'cancelled': case 'contested':
          live.current = null
          setPhase(phase => phase.step === 'show' || phase.step === 'approve' || phase.step === 'finishing' ? endedBy(state.status as 'expired' | 'cancelled' | 'contested') : phase)
          break
      }
    }
  }, [onConnected])

  // Poll the pairing while it is open. A few failed reads in a row end it; a 404 means it is already gone.
  useEffect(() => {
    if (!pairingId) return
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    let failures = 0
    const tick = async () => {
      try {
        const state = await auth.pairingStatus(pairingId, controller.signal)
        if (controller.signal.aborted) return
        failures = 0
        apply.current(state)
        if (state.status !== 'pending' && state.status !== 'claimed' && state.status !== 'approved') return
      } catch (cause) {
        if (controller.signal.aborted) return
        if (cause instanceof ApiError && cause.status === 404) {
          live.current = null
          setPhase(ended('Request no longer available', 'This request is gone. Start a new one.'))
          return
        }
        if (++failures >= 5) {
          setPhase(ended('Vessel is unreachable', errorMessage(cause, 'The request could not be checked. Start a new one.')))
          return
        }
      }
      timer = setTimeout(() => { void tick() }, pairingPollMs)
    }
    timer = setTimeout(() => { void tick() }, pairingPollMs)
    return () => { controller.abort(); clearTimeout(timer) }
  }, [auth, pairingId])

  async function approve(code: string) {
    if (phase.step !== 'approve' || phase.submitting) return
    const { pairing: target } = phase
    setPhase({ ...phase, submitting: true, error: null })
    try {
      await auth.approvePairing(target.id, code)
      setPhase(phase => phase.step === 'approve' ? { step: 'finishing', pairing: phase.pairing, device: phase.device } : phase)
    } catch (cause) {
      const rejected = (error: string, attempt = 1) => setPhase(phase => phase.step === 'approve'
        ? { ...phase, submitting: false, error, attempt: phase.attempt + attempt } : phase)
      if (cause instanceof ApiError && cause.code === 'pairing-code-invalid') {
        if (cause.attemptsLeft === 0) finish(ended('Request cancelled', pairingCodeMessage(cause)))
        else rejected(pairingCodeMessage(cause))
      } else if (cause instanceof ApiError && (cause.code === 'pairing-expired' || cause.code === 'pairing-invalid')) {
        finish(cause.code === 'pairing-expired' ? endedBy('expired') : ended('Request no longer valid', cause.message))
      } else rejected(errorMessage(cause, 'The code could not be checked. Try again.'), 0)
    }
  }

  const expired = useCallback(() => {
    finish(endedBy('expired'))
  }, [finish])

  const description = {
    loopback: 'A phone needs a web address it can reach.',
    confirm: 'Sign with a wallet already linked to this journal before connecting a new device.',
    creating: 'Preparing the code…',
    show: 'Scan this code with the phone’s camera, or from Vessel’s sign-in screen on the phone.',
    approve: 'Type the code the phone shows to let it in.',
    finishing: 'Approved. The phone is signing in.',
    done: 'The phone has its own session now.',
    ended: 'Nothing was connected.',
  }[phase.step]
  return <Dialog open onOpenChange={next => { if (!next) close() }}>
    <DialogContent className="workspace-dialog identity-dialog" {...onCloseAutoFocus ? { onCloseAutoFocus } : {}}>
      <DialogHeader><DialogTitle>Connect a phone</DialogTitle><DialogDescription>{description}</DialogDescription></DialogHeader>
      {phase.step === 'loopback' && <>
        <div className="identity-callout is-warn" role="note"><TriangleAlert size={16} aria-hidden="true" />
          <span><strong>A phone can’t open this address.</strong> You are using Vessel at {new URL(origin).host}, which only this computer can reach.
            Open Vessel at its network or domain address, then try again.</span></div>
        <DialogFooter><Button variant="outline" onClick={close}>Close</Button></DialogFooter>
      </>}
      {phase.step === 'confirm' && <WalletSignFlow key={phase.attempt} purpose="Connect a phone" accept={accept}
        requestChallenge={(address, signal) => auth.stepUpChallenge({ action: 'device.pair', address }, signal)}
        complete={(challenge, signature, signal) => auth.stepUp({ challengeId: challenge.challengeId, signature }, signal)}
        onDone={() => { void create() }} onCancel={close} />}
      {phase.step === 'creating' && <p className="muted" role="status">Preparing the code…</p>}
      {phase.step === 'show' && <>
        <div className="pairing-show">
          <div className="pairing-qr-frame"><PairingQr value={pairingUrl(origin, phase.pairing.secret)} label="Pairing QR code" /></div>
          <Deadline key={phase.pairing.expiresAt} expiresAt={phase.pairing.expiresAt} onExpired={expired} />
          <Button variant="ghost" size="sm" aria-expanded={phase.typed} aria-controls="pairing-typed"
            onClick={() => setPhase({ ...phase, typed: !phase.typed })}>{phase.typed ? 'Hide the code' : 'Show a code to type'}</Button>
          {phase.typed && <p id="pairing-typed" className="pairing-typed identity-mono">
            <span className="sr-only">Code to type: </span>{phase.pairing.typedCode}</p>}
        </div>
        <DialogFooter><Button variant="outline" onClick={close}>Cancel</Button></DialogFooter>
      </>}
      {phase.step === 'approve' && <>
        <ApproveForm key={phase.attempt} device={phase.device} submitting={phase.submitting} error={phase.error}
          expiresAt={phase.expiresAt} onSubmit={code => { void approve(code) }} onExpired={expired} />
        <DialogFooter><Button variant="outline" onClick={close}>Cancel</Button></DialogFooter>
      </>}
      {phase.step === 'finishing' && <>
        <p className="muted" role="status"><span className="identity-spinner" aria-hidden="true" /> Waiting for {phase.device} to finish signing in…</p>
        <DialogFooter><Button variant="outline" onClick={close}>Cancel</Button></DialogFooter>
      </>}
      {phase.step === 'done' && <>
        <p className="pairing-done" role="status"><CheckCircle2 size={18} aria-hidden="true" /><span><strong>{phase.device} is connected.</strong> Sign it out from this list at any time.</span></p>
        <DialogFooter><Button onClick={close}>Done</Button></DialogFooter>
      </>}
      {phase.step === 'ended' && <>
        <div className="identity-callout is-warn" role="alert"><TriangleAlert size={16} aria-hidden="true" />
          <span><strong>{phase.title}.</strong> {phase.message}</span></div>
        <DialogFooter><Button variant="outline" onClick={close}>Close</Button>
          <Button onClick={() => setPhase({ step: 'confirm', attempt: 0 })}>Try again</Button></DialogFooter>
      </>}
    </DialogContent>
  </Dialog>
}

function ApproveForm({ device, submitting, error, expiresAt, onSubmit, onExpired }: {
  device: string; submitting: boolean; error: string | null; expiresAt: string; onSubmit: (code: string) => void; onExpired: () => void
}) {
  const [value, setValue] = useState('')
  const digits = value.replace(/\D/g, '')
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (digits.length === 6) onSubmit(digits)
  }
  return <form className="sign-flow" onSubmit={submit} noValidate aria-busy={submitting}>
    <div className="identity-row">
      <span className="profile-device" aria-hidden="true"><Smartphone size={16} /></span>
      <span className="identity-row-meta"><strong>{device} wants to connect</strong>
        <span><Deadline key={expiresAt} expiresAt={expiresAt} onExpired={onExpired} /></span></span>
    </div>
    <div className="identity-field">
      <label htmlFor="pairing-code">Code shown on the phone</label>
      <Input id="pairing-code" className="identity-mono pairing-code-input" value={value} onChange={event => setValue(event.target.value)}
        inputMode="numeric" autoComplete="off" maxLength={8} spellCheck={false} autoFocus disabled={submitting}
        aria-invalid={error !== null} aria-describedby={error ? 'pairing-code-error' : undefined} />
      {error && <p id="pairing-code-error" className="error" role="alert">{error}</p>}
    </div>
    <div className="sign-in-actions"><Button type="submit" className="sign-in-primary" disabled={digits.length !== 6 || submitting} aria-busy={submitting}>
      {submitting ? 'Checking…' : 'Approve'}</Button></div>
  </form>
}
