import { useCallback, useEffect, useReducer, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { AlertTriangle, ArrowLeft, ArrowLeftRight, Ban, CircleAlert, Clock, Keyboard, KeyRound, Lock, Plug, ScanLine, ShieldCheck, Ticket, Unplug, WalletMinimal, X } from 'lucide-react'
import { challengeAddress, displayNameLimit, type PublicAuthApi, type RegistrationPolicy, type SessionDto } from '@/api/auth'
import { ApiError } from '@/api/errors'
import { SourceChip } from '@/components/accounts/AccountIdentity'
import { Button } from '@/components/ui/button'
import { InfoTip } from '@/components/ui/info-tip'
import { checksumAddress } from '@/lib/eip55'
import { Input } from '@/components/ui/input'
import { cameraUsable } from './pairing-link'
import { PairingScanner } from './PairingScanner'
import type { PairingSource } from './PairingScreen'
import { TypedCodeEntry } from './TypedCodeEntry'
import { initialSignIn, signIn, type SignInError, type SignInEvent, type SignInState } from './sign-in-machine'
import { challengeLifetime, clock, useCountdown } from './countdown'
import { MessagePreview, SignInBrand, SignInSteps, StateCard, UserAvatar, WalletButton, WalletMark, type StateAction } from './IdentityParts'
import { readLastWallet, rememberWallet, type BrowserWallet } from './wallets/discovery'
import { requestAccount, sameAddress, signMessage, watchWallet } from './wallets/provider'
import { useBrowserWallets } from './wallets/useBrowserWallets'
import { cleanDisplayName } from './profile-rules'

export const signingMeaning = 'Vessel asks your wallet to sign a text message. The signature proves you control the address; it cannot move funds or approve spending. Vessel never signs, sends or approves anything at a venue.'

interface SignInScreenProps {
  api: PublicAuthApi
  registration: RegistrationPolicy
  originAccepted: boolean
  notice: string | null
  onSignedIn: (session: SessionDto, wallet: BrowserWallet) => void
  /** A phone with no wallet pairs with a signed-in desktop: a scanned secret or a typed code's claim opens the pairing screen. */
  onPair?: (source: PairingSource) => void
  /** Tests start from a given state; the app always starts at choose. */
  initialState?: SignInState
}

/** Wallet sign-in: choose → connecting → signing → verifying → signed in, or create a journal for a new wallet. */
export function SignInScreen({ api, registration, originAccepted, notice, onSignedIn, onPair, initialState = initialSignIn }: SignInScreenProps) {
  const [state, dispatchState] = useReducer(signIn, initialState)
  const [view, setView] = useState<'wallet' | 'scan' | 'type'>('wallet')
  const [remember, setRemember] = useState(false)
  const { wallets, settled } = useBrowserWallets()
  const [lastRdns] = useState(readLastWallet)
  // Every wallet or server step belongs to one run; a newer run (cancel, change, a switched account) discards it.
  const run = useRef({ id: 0, controller: new AbortController() })
  // Mirrors the reducer for handlers and wallet events; every dispatch goes through `dispatch` below.
  const stateRef = useRef(state)
  const dispatch = useCallback((event: SignInEvent) => {
    stateRef.current = signIn(stateRef.current, event)
    dispatchState(event)
  }, [])
  const begin = useCallback(() => {
    run.current.controller.abort()
    run.current = { id: run.current.id + 1, controller: new AbortController() }
    return run.current
  }, [])
  // Unmount replaces the run too, so a late wallet or server answer stops before any callback.
  useEffect(() => () => { begin() }, [begin])

  const prepare = useCallback(async (current: { id: number; controller: AbortController }, address: string) => {
    try {
      const challenge = await api.challenge(address, current.controller.signal)
      if (run.current !== current) return
      // Sign only a message for the address this wallet shared.
      if (!sameAddress(challengeAddress(challenge.message), address))
        throw new ApiError('invalid-response', 'Vessel returned a message for another address. Start again.')
      dispatch({ type: 'challenge', challenge })
    } catch (error) {
      if (run.current === current) dispatch({ type: 'failed', error })
    }
  }, [api, dispatch])

  const connect = useCallback(async (wallet: BrowserWallet) => {
    const current = begin()
    dispatch({ type: 'choose', wallet })
    try {
      const address = await requestAccount(wallet.provider)
      if (run.current !== current) return
      dispatch({ type: 'connected', address })
      await prepare(current, address)
    } catch (error) {
      if (run.current === current) dispatch({ type: 'failed', error, during: 'connect' })
    }
  }, [begin, dispatch, prepare])

  const restart = useCallback((event: SignInEvent) => {
    const current = begin()
    dispatch(event)
    const next = stateRef.current
    if (next.step === 'signing') void prepare(current, next.address)
    else if (next.step === 'connecting') void connect(next.wallet)
  }, [begin, connect, dispatch, prepare])

  const reset = useCallback(() => { begin(); dispatch({ type: 'reset' }) }, [begin, dispatch])

  async function sign() {
    if (state.step !== 'signing' || !state.challenge || state.waiting) return
    const { wallet, address, challenge } = state
    const current = begin()
    dispatch({ type: 'sign' })
    try {
      const signature = await signMessage(wallet.provider, challenge.message, address)
      if (run.current !== current) return
      dispatch({ type: 'signed' })
      const result = await api.verify({ challengeId: challenge.challengeId, signature, remember }, current.controller.signal)
      if (run.current !== current) return
      dispatch({ type: 'verified', result })
      if (result.status === 'signed-in') {
        rememberWallet(wallet)
        onSignedIn(result.session, wallet)
      }
    } catch (error) {
      if (run.current === current) dispatch({ type: 'failed', error })
    }
  }

  async function register(displayName: string, invitation: string) {
    if (state.step !== 'registration' || state.submitting) return
    const { wallet, registration: token } = state
    const current = begin()
    dispatch({ type: 'register' })
    try {
      const created = await api.register({ registration: token, displayName, remember,
        ...invitation.trim() ? { invitation: invitation.trim() } : {} }, current.controller.signal)
      if (run.current !== current) return
      dispatch({ type: 'registered', session: created.session })
      rememberWallet(wallet)
      onSignedIn(created.session, wallet)
    } catch (error) {
      if (run.current === current) dispatch({ type: 'registration-failed', error })
    }
  }

  // Watch the chosen wallet while it can still change what gets signed.
  const watched = state.step === 'connecting' || state.step === 'signing' ? state.wallet.provider : null
  useEffect(() => {
    if (!watched) return
    return watchWallet(watched, {
      accountChanged: address => {
        const current = stateRef.current
        if (current.step === 'signing' && !sameAddress(address, current.address)) begin()
        dispatch({ type: 'account-changed', address })
      },
      disconnected: () => {
        if (stateRef.current.step === 'connecting' || stateRef.current.step === 'signing') begin()
        dispatch({ type: 'disconnected' })
      },
    })
  }, [watched, begin, dispatch])

  const expire = useCallback(() => { begin(); dispatch({ type: 'expired' }) }, [begin, dispatch])

  const step = state.step === 'registration' ? 'create' : state.step === 'signing' || state.step === 'verifying' ? 'sign'
    : state.step === 'failed' && state.address ? 'sign' : 'connect'
  const pairingView = onPair && state.step === 'choose' && view !== 'wallet' ? view : null
  return <main className="connection-page sign-in-page">
    <section className="panel connection-card sign-in-card" aria-labelledby="sign-in-title" aria-busy={state.step === 'connecting' || state.step === 'verifying'}>
      {pairingView === 'scan' && onPair && <PairingScanner onSecret={secret => onPair({ kind: 'secret', secret })} onType={() => setView('type')} onBack={() => setView('wallet')} />}
      {pairingView === 'type' && onPair && <TypedCodeEntry api={api} onClaimed={claim => onPair({ kind: 'claimed', claim })} onBack={() => setView('wallet')} />}
      {!pairingView && <div className="sign-in-head"><SignInBrand /><SignInSteps current={step} withCreate={state.step === 'registration'} /></div>}
      {state.step === 'choose' && !pairingView && <ChooseWallet wallets={wallets} settled={settled} lastRdns={lastRdns} registration={registration}
        originAccepted={originAccepted} notice={notice} onChoose={wallet => { void connect(wallet) }}
        {...onPair ? { pairing: { camera: cameraUsable(), onScan: () => setView('scan'), onType: () => setView('type') } } : {}} />}
      {state.step === 'connecting' && <Pending wallet={state.wallet} title={`Open ${state.wallet.name} to connect`}
        detail="Approve the connection request in the wallet. Vessel only reads your address."
        actions={<><Button variant="ghost" onClick={reset}><ArrowLeft size={15} aria-hidden="true" />Other wallets</Button><Button variant="outline" onClick={reset}>Cancel</Button></>} />}
      {state.step === 'signing' && <Signing key={state.challenge?.challengeId ?? 'preparing'} state={state} remember={remember} onRemember={setRemember}
        onSign={() => { void sign() }} onCancel={reset} onExpired={expire} />}
      {state.step === 'verifying' && <Pending wallet={state.wallet} title="Checking the signature" detail="Vessel is verifying the signed message." />}
      {state.step === 'registration' && <CreateJournal state={state} onSubmit={(name, code) => { void register(name, code) }} onCancel={reset} />}
      {state.step === 'failed' && <Failure state={state} onReset={reset} onRetry={() => restart({ type: 'retry' })}
        onUseAddress={address => restart({ type: 'use-address', address })} onReconnect={() => { if (state.wallet) void connect(state.wallet) }} />}
      {state.step === 'signed-in' && <p className="muted" role="status">Opening your journal…</p>}
    </section>
  </main>
}

function ChooseWallet({ wallets, settled, lastRdns, registration, originAccepted, notice, onChoose, pairing }: {
  wallets: BrowserWallet[]; settled: boolean; lastRdns: string | null; registration: RegistrationPolicy; originAccepted: boolean
  notice: string | null; onChoose: (wallet: BrowserWallet) => void
  /** Present when pairing is offered: whether this browser can use a camera, and the two ways in. */
  pairing?: { camera: boolean; onScan: () => void; onType: () => void }
}) {
  return <>
    <h1 id="sign-in-title">Sign in to Vessel</h1>
    <p className="sign-in-lede">Use your wallet to prove it's you.{registration !== 'closed' && ' New wallets can create a journal.'}</p>
    <div className="sign-in-body">
      {notice && <p className="sign-in-notice" role="status">{notice}</p>}
      {!originAccepted && <div className="identity-callout is-warn" role="note"><AlertTriangle size={16} aria-hidden="true" />
        <span><strong>This address is not on the server's list.</strong> Sign-in will be refused here. Open Vessel at the address its operator configured.</span></div>}
      <div className="identity-separator" id="wallets-heading">In this browser</div>
      {wallets.length > 0 ? <div className="wallet-options" role="group" aria-labelledby="wallets-heading">
        {wallets.map(wallet => <WalletButton key={wallet.key} wallet={wallet} last={wallet.rdns !== null && wallet.rdns === lastRdns} onChoose={() => onChoose(wallet)} />)}
      </div> : settled ? <StateCard icon={<WalletMinimal size={16} />} title="No browser wallet found" actions={[{ label: 'Reload', primary: true, onClick: () => window.location.reload() }]}>
        Install a wallet extension, or open Vessel in your wallet app's browser, then reload this page.
      </StateCard> : <p className="muted wallet-searching" role="status">Looking for browser wallets…</p>}
      {pairing && <>
        <div className="identity-separator" id="pairing-heading">Signed in on a computer?</div>
        {pairing.camera
          ? <Button variant="outline" size="lg" onClick={pairing.onScan}><ScanLine size={16} aria-hidden="true" />Scan a code from your desktop</Button>
          : <div className="pairing-fallback" role="group" aria-labelledby="pairing-heading">
            <p className="muted">This browser can’t use the camera here. Scan the code from your computer with your phone’s camera app, or</p>
            <Button variant="outline" onClick={pairing.onType}><Keyboard size={16} aria-hidden="true" />Enter a code instead</Button>
          </div>}
      </>}
      <div className="sign-in-foot">
        <span className="sign-in-assure"><ShieldCheck size={15} aria-hidden="true" />No transaction, no gas, no approvals.
          <InfoTip label="What signing in means">{signingMeaning}</InfoTip></span>
        <InfoTip trigger="No wallet?" side="top">Install a browser wallet extension, or open Vessel inside your wallet app's browser on a phone.</InfoTip>
      </div>
    </div>
  </>
}

function Pending({ wallet, title, detail, actions }: { wallet: BrowserWallet; title: string; detail: string; actions?: ReactNode }) {
  return <>
    <div className="sign-in-pending" role="status">
      <div className="sign-in-pulse" aria-hidden="true"><span className="sign-in-ring" /><WalletMark wallet={wallet} size={64} /></div>
      <h1 id="sign-in-title" className="sign-in-pending-title">{title}</h1>
      <p>{detail}</p>
    </div>
    {actions && <div className="sign-in-actions is-split">{actions}</div>}
  </>
}

function Identity({ wallet, address, children }: { wallet: BrowserWallet; address: string; children?: ReactNode }) {
  return <div className="identity-row">
    <WalletMark wallet={wallet} />
    <span className="identity-row-meta"><strong title={wallet.name}>{wallet.name}</strong><span><SourceChip source={checksumAddress(address)} /></span></span>
    {children}
  </div>
}

function Signing({ state, remember, onRemember, onSign, onCancel, onExpired }: {
  state: Extract<SignInState, { step: 'signing' }>; remember: boolean; onRemember: (value: boolean) => void
  onSign: () => void; onCancel: () => void; onExpired: () => void
}) {
  const { wallet, address, challenge, waiting } = state
  const [lifetime] = useState(() => challenge ? challengeLifetime(challenge.message, challenge.expiresAt) : null)
  const seconds = useCountdown(lifetime, onExpired)
  return <>
    <h1 id="sign-in-title">Sign the message</h1>
    <p className="sign-in-lede">{wallet.name} will ask you to sign. Nothing is sent on-chain.</p>
    <div className="sign-in-body">
      <Identity wallet={wallet} address={address}><Button variant="ghost" size="sm" onClick={onCancel}>Change</Button></Identity>
      {challenge ? <MessagePreview message={challenge.message} purpose="Sign-In with Ethereum" seconds={seconds} />
        : <p className="muted" role="status">Preparing the message…</p>}
      {/* Locked from Sign until the flow ends, so what verify sends is what the box shows. */}
      <label className="sign-in-remember"><input type="checkbox" checked={remember} disabled={waiting} onChange={event => onRemember(event.target.checked)} />Keep me signed in on this device</label>
      <div className="sign-in-actions">
        <Button size="lg" className="sign-in-primary" onClick={onSign} disabled={!challenge || waiting} aria-busy={waiting}>
          {waiting ? <><span className="identity-spinner" aria-hidden="true" /><span className="identity-truncate">Waiting for {wallet.name}…</span></>
            : <span className="identity-truncate">Sign with {wallet.name}</span>}
        </Button>
        <Button size="lg" variant="outline" onClick={onCancel}>Cancel</Button>
      </div>
    </div>
  </>
}

function CreateJournal({ state, onSubmit, onCancel }: {
  state: Extract<SignInState, { step: 'registration' }>; onSubmit: (displayName: string, invitation: string) => void; onCancel: () => void
}) {
  const [name, setName] = useState('')
  const [code, setCode] = useState('')
  const [local, setLocal] = useState<{ field: 'displayName' | 'invitation'; message: string } | null>(null)
  const error = local ?? state.error
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const displayName = cleanDisplayName(name)
    if (displayName === undefined) setLocal({ field: 'displayName', message: `Use 1 to ${displayNameLimit} printable characters for the display name.` })
    else if (state.requiresInvitation && !code.trim()) setLocal({ field: 'invitation', message: 'Enter the invitation code from this server\'s operator.' })
    else { setLocal(null); onSubmit(displayName, code) }
  }
  const nameError = error?.field === 'displayName' ? error.message : null
  const codeError = error?.field === 'invitation' ? error.message : null
  return <>
    <h1 id="sign-in-title">Create your journal</h1>
    <p className="sign-in-lede">This wallet is new to this Vessel server.</p>
    <form className="sign-in-body" onSubmit={submit} noValidate aria-busy={state.submitting}>
      <div className="identity-row">
        <UserAvatar name={name || state.wallet.name} address={state.address} />
        <span className="identity-row-meta"><strong title={state.wallet.name}>Signed with {state.wallet.name}</strong>
          <span><SourceChip source={checksumAddress(state.address)} /><span className="identity-pill is-good">Verified</span></span></span>
      </div>
      <div className="identity-field">
        <label htmlFor="journal-name">Display name</label>
        <Input id="journal-name" value={name} onChange={event => setName(event.target.value)} maxLength={displayNameLimit * 2} autoComplete="nickname"
          disabled={state.submitting} aria-invalid={nameError !== null} aria-describedby={nameError ? 'journal-name-error' : undefined} />
        {nameError && <p id="journal-name-error" className="error" role="alert">{nameError}</p>}
      </div>
      {state.requiresInvitation && <div className="identity-field">
        <label htmlFor="journal-invitation">Invitation code <small>· this server is invite-only</small></label>
        <Input id="journal-invitation" className="identity-mono" value={code} onChange={event => setCode(event.target.value)} maxLength={64}
          autoComplete="off" spellCheck={false} autoCapitalize="characters" disabled={state.submitting}
          aria-invalid={codeError !== null} aria-describedby={codeError ? 'journal-invitation-error' : undefined} />
        {codeError && <p id="journal-invitation-error" className="error" role="alert">{codeError}</p>}
      </div>}
      <div className="identity-callout is-warn"><KeyRound size={16} aria-hidden="true" />
        <span><strong>This wallet is your key.</strong> There is no email or password reset. Link a second wallet in Settings once you're in.</span></div>
      {error && error.field === null && <p className="error" role="alert">{error.message}</p>}
      <div className="sign-in-actions">
        {/* A new rate limit restarts the wait; the deadline string is unique to each refusal. */}
        <CreateButton key={state.error?.retryAt ?? 'ready'} submitting={state.submitting} retryAt={state.error?.retryAt ?? null} />
        <Button type="button" size="lg" variant="ghost" onClick={onCancel} disabled={state.submitting}>Use another wallet</Button>
      </div>
    </form>
  </>
}

/** Create journal, held back until a rate limit's deadline passes (at most an hour). */
function CreateButton({ submitting, retryAt }: { submitting: boolean; retryAt: string | null }) {
  const [lifetime] = useState(() => retryAt ? Math.max(0, Math.min(60 * 60_000, Date.parse(retryAt) - Date.now() || 0)) : null)
  const [ready, setReady] = useState(lifetime === null || lifetime === 0)
  const seconds = useCountdown(lifetime, useCallback(() => setReady(true), []))
  const waiting = !ready && !!seconds
  return <Button type="submit" size="lg" variant={waiting ? 'outline' : 'default'} className={waiting ? 'sign-in-primary identity-waiting' : 'sign-in-primary'}
    disabled={submitting || waiting}>
    {submitting ? 'Creating…' : waiting ? `Create journal in ${clock(seconds)}` : 'Create journal'}</Button>
}

function RateLimited({ retryAt, onRetry, onReset }: { retryAt: string | null; onRetry: () => void; onReset: () => void }) {
  const [lifetime] = useState(() => retryAt ? Math.max(0, Math.min(60 * 60_000, Date.parse(retryAt) - Date.now())) : 60_000)
  const [ready, setReady] = useState(lifetime === 0)
  const seconds = useCountdown(lifetime, useCallback(() => setReady(true), []))
  return <StateCard icon={<AlertTriangle size={16} />} tone="warn" title="Too many attempts" actions={[
    { label: ready || !seconds ? 'Try again' : `Try again in ${clock(seconds)}`, primary: true, onClick: onRetry, disabled: !ready && !!seconds },
    { label: 'Other wallets', onClick: onReset },
  ]}>Wait a moment before trying again.</StateCard>
}

function Failure({ state, onReset, onRetry, onUseAddress, onReconnect }: {
  state: Extract<SignInState, { step: 'failed' }>; onReset: () => void; onRetry: () => void
  onUseAddress: (address: string) => void; onReconnect: () => void
}) {
  const other: StateAction = { label: 'Other wallets', onClick: onReset }
  const name = state.wallet?.name ?? 'The wallet'
  const error: SignInError = state.error
  const card = (): ReactNode => {
    switch (error.kind) {
      case 'declined': return error.during === 'connect'
        ? <StateCard icon={<X size={16} />} title="Connection declined" actions={[{ label: 'Try again', primary: true, onClick: onReconnect }, other]}>No address was shared. Try again or choose another wallet.</StateCard>
        : <StateCard icon={<X size={16} />} title="Signature declined" actions={[{ label: 'Try again', primary: true, onClick: onRetry }, other]}>Nothing was signed. Try again or choose another wallet.</StateCard>
      case 'pending': return <StateCard icon={<Clock size={16} />} tone="warn" title="Request already open" actions={[{ label: 'Try again', primary: true, onClick: state.address ? onRetry : onReconnect }, other]}>
        {name} already has a request waiting. Open the wallet to finish or reject it, then try again.</StateCard>
      case 'expired': return <StateCard icon={<Clock size={16} />} tone="warn" title="Request expired" actions={[{ label: 'Start again', primary: true, onClick: state.address ? onRetry : onReset }]}>Sign-in requests last five minutes. Start a new one.</StateCard>
      case 'switched': return <StateCard icon={<ArrowLeftRight size={16} />} tone="warn" title="Wallet switched account" actions={[{ label: 'Use new address', primary: true, onClick: () => onUseAddress(error.address) }, other]}>
        {name} now uses <SourceChip source={checksumAddress(error.address)} />. Sign with the address you connected, or start again with the new one.</StateCard>
      case 'disconnected': return <StateCard icon={<Unplug size={16} />} tone="warn" title="Wallet disconnected" actions={[{ label: 'Connect again', primary: true, onClick: onReconnect }, other]}>
        {name} ended the connection. Connect again or choose another wallet.</StateCard>
      case 'invitation': return <StateCard icon={<Ticket size={16} />} tone="info" title="Invitation needed" actions={[{ label: 'Start again', primary: true, onClick: onReset }]}>
        This server only admits invited wallets. Ask its operator for a code.</StateCard>
      case 'unsupported': return <StateCard icon={<Ban size={16} />} tone="bad" title="Signature not supported" actions={[other]}>
        This signature format isn't supported yet, as with Safe and other smart accounts. Use a regular wallet address.</StateCard>
      case 'rate-limited': return <RateLimited retryAt={error.retryAt} onRetry={state.address ? onRetry : onReset} onReset={onReset} />
      case 'suspended': return <StateCard icon={<Lock size={16} />} tone="bad" title="Journal suspended" actions={[other]}>
        The operator of this server has suspended this journal. Contact them to restore access.</StateCard>
      case 'closed': return <StateCard icon={<Ban size={16} />} title="Not admitting new journals" actions={[other]}>
        This server is not admitting new journals. Sign in with a wallet that already has a journal here.</StateCard>
      case 'unreachable': return <StateCard icon={<Plug size={16} />} title="Vessel is unreachable" actions={[{ label: 'Retry', primary: true, onClick: state.address ? onRetry : onReset }]}>
        The server didn't answer. Check your connection and try again.</StateCard>
      case 'other': return <StateCard icon={<CircleAlert size={16} />} tone="warn" title="Sign-in did not complete" actions={[{ label: 'Start again', primary: true, onClick: onReset }]}>{error.message}</StateCard>
    }
  }
  return <>
    <h1 id="sign-in-title" className="sr-only">Sign in to Vessel</h1>
    {state.wallet && state.address && <div className="sign-in-body"><Identity wallet={state.wallet} address={state.address} /></div>}
    <div className="sign-in-body">{card()}</div>
  </>
}
