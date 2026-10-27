import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Lock, Plug, ShieldCheck } from 'lucide-react'
import { createPublicAuthApi, createSessionAuthApi, type PublicAuthApi, type RegistrationPolicy, type SessionDto, type UserDto } from '@/api/auth'
import { ApiError, errorMessage } from '@/api/errors'
import { getSystem, type SystemInfo } from '@/api/system'
import { createWorkspaceApi, type WorkspaceApi } from '@/api/workspace'
import { clearCandleCache } from '@/features/market/candleCache'
import { scopePreferences } from '@/lib/preference-scope'
import { SignInBrand, StateCard } from './IdentityParts'
import { takePairingSecret } from './pairing-link'
import { PairingScreen, type PairingSource } from './PairingScreen'
import { SignInScreen } from './SignInScreen'
import { clearNotice, readNotice, reloadToSignIn, SessionController, takeLanding, workspaceNotices, type Notice, type SessionChannelFactory, type SessionCheck } from './session'
import type { ProfileRead, ShellIdentity } from './types'
import type { BrowserWallet } from './wallets/discovery'

/** `session.user` mirrors the controller's profile record (see `profiles`). */
type Ready = { session: SessionDto; system: SystemInfo; api: WorkspaceApi; controller: SessionController; walletName: string | null }
type GateState =
  | { kind: 'probing' }
  | { kind: 'sign-in' }
  /** A phone with no wallet: a scanned or linked pairing secret, or a typed code already claimed. */
  | { kind: 'pairing'; source: PairingSource }
  /** A pairing link opened on a device that already has a session: nothing is paired. */
  | { kind: 'already-signed-in'; session: SessionDto }
  | { kind: 'loading' }
  | { kind: 'ready'; ready: Ready }
  | { kind: 'failed'; message: string; retry: { session: SessionDto; walletName: string | null } | null }
  | { kind: 'suspended' }
  | { kind: 'signing-out'; ready: Ready }
  | { kind: 'sign-out-failed'; ready: Ready; message: string }

export type GateChildren = (system: SystemInfo, disconnect: () => void, api: WorkspaceApi, identity: ShellIdentity) => ReactNode

interface SignInGateProps {
  registration: RegistrationPolicy
  originAccepted: boolean
  children: GateChildren
  /** Tests inject the cross-tab channel, the full navigation and the public client. */
  channels?: SessionChannelFactory
  navigate?: (url: string) => void
  publicApi?: PublicAuthApi
}

/**
 * Wallet mode's first screen. It probes the session with the public call kind: a 200 opens the workspace, and a
 * 401 shows sign-in and never reloads (a teardown's reload lands here, so reloading again would loop). A load
 * marked by `reloadToSignIn` shows the chooser without probing.
 *
 * A teardown's notice stays stored until a screen shows it: the sign-in card, or the workspace when the probe
 * finds that another tab signed in meanwhile.
 *
 * A `#pair=` fragment (a phone opened the desktop's QR link) is read and removed from the address on the first
 * render. When the probe finds no session the pairing screen replaces the chooser; when it finds one, nothing is
 * paired and the device says it is already signed in.
 */
export function SignInGate({ registration, originAccepted, children, channels, navigate, publicApi }: SignInGateProps) {
  const api = useMemo(() => publicApi ?? createPublicAuthApi(), [publicApi])
  const [landing] = useState(takeLanding)
  const [fragment] = useState(takePairingSecret)
  const [state, setState] = useState<GateState>(() => landing
    ? fragment ? { kind: 'pairing', source: { kind: 'secret', secret: fragment } } : { kind: 'sign-in' }
    : { kind: 'probing' })
  // Cleared once the sign-in card or the workspace has shown it.
  const [notice, setNotice] = useState<Notice | null>(readNotice)
  const controllerRef = useRef<SessionController | null>(null)
  // A late probe or sign-in answer after unmount never scopes preferences or opens a channel.
  const mounted = useRef(false)
  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
      controllerRef.current?.dispose()
      controllerRef.current = null
    }
  }, [])
  const signingIn = state.kind === 'sign-in'
  useEffect(() => { if (signingIn && notice) clearNotice() }, [signingIn, notice])
  const noticePresented = useCallback(() => { setNotice(null); clearNotice() }, [])

  /**
   * Probes the cookie's session and follows it: a 401 or suspension ends the tab like any session call, another
   * person reloads, and this tab's own session replaces the gate's copy (wallet, profile) without touching the
   * workspace, so a same-user adoption never leaves the old sign-in wallet on screen or in a removal decision.
   *
   * Each probe belongs to the controller and session generation it started under. A newer probe, an adoption or a
   * teardown aborts it, and an answer that arrives after any of them is dropped, a 401 included: it may describe the
   * session this tab has since left. The probe's profile applies only if no authoritative profile change (profile
   * epoch) happened since it started; the session and wallet still follow it. Within one generation the profile only
   * moves forward by `UserDto.revision`, so a probe answered before a save never restores the old name. A probe of a
   * newer generation than the known profile replaces it at any revision: after a restore and the mandatory session
   * reset the server's revision may be lower.
   */
  const checkSession = useCallback(async (current: SessionController): Promise<SessionCheck> => {
    probes.get(current)?.abort()
    const attempt = new AbortController()
    probes.set(current, attempt)
    const generation = current.generation
    const read = profileRead(current)
    const superseded = () => attempt.signal.aborted || current.generation !== generation || current.sessionGeneration.ended.aborted
    const settled = () => current.sessionGeneration.ended.aborted ? 'ended' : 'unknown'
    let fresh: SessionDto
    try { fresh = await api.session(AbortSignal.any([attempt.signal, current.sessionGeneration.ended])) } catch (cause) {
      if (superseded()) return settled()
      if (cause instanceof ApiError && (cause.status === 401 || cause.code === 'user-suspended')) {
        current.handleFailure({ status: cause.status === 401 ? 401 : 403, ...cause.code ? { code: cause.code } : {}, generation })
      }
      return settled()
    } finally { if (probes.get(current) === attempt) probes.delete(current) }
    if (superseded()) return settled()
    const check = current.observe(fresh)
    if (check === 'current') {
      const known = profiles.get(current)
      if (known && known.user.id === fresh.user.id && known.epoch === read.epoch &&
          (generation > known.generation || fresh.user.revision > known.user.revision)) {
        profiles.set(current, { user: fresh.user, generation, epoch: known.epoch + 1 })
      }
      // The updater, not a ref, checks that this controller still owns the gate; after unmount it does nothing.
      setState(state => {
        if (state.kind !== 'ready' || state.ready.controller !== current) return state
        const user = profiles.get(current)?.user ?? state.ready.session.user
        return { kind: 'ready', ready: {
          ...state.ready, session: { ...fresh, user },
          // The wallet's announced name belongs to the sign-in this tab made; another tab's wallet is unknown here.
          walletName: fresh.wallet.id === state.ready.session.wallet.id ? state.ready.walletName : null,
          system: { ...state.ready.system, owner: { ...state.ready.system.owner, displayName: user.displayName } } } }
      })
    }
    return check
  }, [api])
  const enter = useCallback(async (session: SessionDto, fresh: boolean, walletName: string | null) => {
    if (!mounted.current) return
    setState({ kind: 'loading' })
    controllerRef.current?.dispose()
    // The stores switch to this user before the workspace reads any preference.
    scopePreferences(session.user.id, session.adoptsLegacyPreferences)
    const controller: SessionController = new SessionController({
      session, ...channels ? { channels } : {}, ...navigate ? { navigate } : {},
      onSuspended: () => { controller.dispose(); setState({ kind: 'suspended' }) },
    })
    controllerRef.current = controller
    // The session was read before any adoption, so its profile belongs to generation 0.
    profiles.set(controller, { user: session.user, generation: 0, epoch: 0 })
    // Adopting another tab's session of this user changes which wallet signed this tab in. The workspace shows the
    // session it started with (generation 0), so an adoption while it loads is followed once it is on screen.
    controller.sessionGeneration.subscribe(() => { void checkSession(controller) })
    if (fresh) controller.announceSignIn()
    try {
      const credentials = controller.credentials()
      const system = await getSystem(credentials, controller.handleFailure)
      if (controllerRef.current !== controller) return
      const workspace = createWorkspaceApi(credentials, controller.handleFailure)
      controller.markLive()
      // An adoption probe answered while the workspace loaded may already have moved the profile.
      const user = profiles.get(controller)?.user ?? session.user
      setState({ kind: 'ready', ready: { session: { ...session, user }, system, api: workspace, controller, walletName } })
      if (controller.generation !== 0) void checkSession(controller)
    } catch (cause) {
      // A 401 or suspension already moved the gate; anything else may be retried.
      if (controllerRef.current !== controller || cause instanceof ApiError && (cause.status === 401 || cause.code === 'user-suspended')) return
      setState({ kind: 'failed', message: errorMessage(cause, 'The workspace could not be loaded.'), retry: { session, walletName } })
    }
  }, [channels, navigate, checkSession])

  const settleProbe = useCallback((result: { session: SessionDto } | { cause: unknown }) => {
    if ('session' in result) {
      if (fragment) setState({ kind: 'already-signed-in', session: result.session })
      else void enter(result.session, false, null)
      return
    }
    const { cause } = result
    if (cause instanceof ApiError && cause.status === 401) setState(fragment ? { kind: 'pairing', source: { kind: 'secret', secret: fragment } } : { kind: 'sign-in' })
    else if (cause instanceof ApiError && cause.code === 'user-suspended') setState({ kind: 'suspended' })
    else setState({ kind: 'failed', message: errorMessage(cause, 'Vessel is unreachable.'), retry: null })
  }, [enter, fragment])
  // Every probe, a manual Retry included, belongs to the gate: a newer one, unmount or a replaced client aborts it.
  const probing = useRef<AbortController | null>(null)
  const probe = useCallback(() => {
    probing.current?.abort()
    const attempt = new AbortController()
    probing.current = attempt
    api.session(attempt.signal).then(session => ({ session }), (cause: unknown) => ({ cause }))
      .then(result => { if (!attempt.signal.aborted) settleProbe(result) })
  }, [api, settleProbe])

  useEffect(() => () => {
    probing.current?.abort()
    probing.current = null
  }, [api])
  useEffect(() => {
    // The landing marker means the chooser is already showing.
    if (!landing) probe()
  }, [landing, probe])

  const ready = state.kind === 'ready' ? state.ready : null
  const signOut = useCallback((current: Ready) => {
    setState({ kind: 'signing-out', ready: current })
    clearCandleCache(current.api)
    current.controller.signOut(createSessionAuthApi(current.controller.credentials(), current.controller.handleFailure))
      .catch((cause: unknown) => setState({ kind: 'sign-out-failed', ready: current, message: errorMessage(cause, 'Sign-out did not complete.') }))
  }, [])
  const controller = ready?.controller ?? null
  const auth = useMemo(() => controller && createSessionAuthApi(controller.credentials(), controller.handleFailure), [controller])
  const identity = useMemo<ShellIdentity | null>(() => {
    if (!ready || !controller || !auth) return null
    return {
      session: ready.session, walletName: ready.walletName, auth,
      notice: notice ? workspaceNotices[notice] : null, noticePresented,
      registerExitState: clear => controller.registerExitState(clear),
      profileRead: () => profileRead(controller),
      onProfile: (user: UserDto, recovery?: ProfileRead) => {
        const known = profiles.get(controller)
        if (!known || known.user.id !== user.id) return false
        // A save's answer moves the profile forward. A conflict's recovery read rebases it, even to a lower revision,
        // but only if it started in this generation and no other authoritative change happened while it ran.
        if (recovery ? recovery.generation !== controller.generation || recovery.epoch !== known.epoch : user.revision < known.user.revision) return false
        profiles.set(controller, { user, generation: controller.generation, epoch: known.epoch + 1 })
        setState(current => {
          if (current.kind !== 'ready' || current.ready.controller !== controller) return current
          const latest = profiles.get(controller)?.user ?? user
          return { kind: 'ready', ready: {
            ...current.ready, session: { ...current.ready.session, user: latest }, system: { ...current.ready.system, owner: { ...current.ready.system.owner, displayName: latest.displayName } } } }
        })
        return true
      },
      checkSession: () => checkSession(controller),
      onSessionChange: listener => controller.sessionGeneration.subscribe(listener),
    }
  }, [ready, controller, auth, notice, noticePresented, checkSession])

  if (state.kind === 'ready' && identity) return children(state.ready.system, () => signOut(state.ready), state.ready.api, identity)
  if (state.kind === 'sign-in') return <SignInScreen api={api} registration={registration} originAccepted={originAccepted} notice={notice}
    onSignedIn={(session: SessionDto, wallet: BrowserWallet) => { setNotice(null); void enter(session, true, wallet.name) }}
    onPair={source => setState({ kind: 'pairing', source })} />
  if (state.kind === 'pairing') return <PairingScreen key={state.source.kind === 'secret' ? 'secret' : state.source.claim.code} api={api} source={state.source}
    onSignedIn={session => { setNotice(null); void enter(session, true, null) }} onBack={() => setState({ kind: 'sign-in' })} />
  return <main className="connection-page sign-in-page">
    <section className="panel connection-card sign-in-card" aria-labelledby="gate-title">
      <div className="sign-in-head"><SignInBrand /></div>
      {(state.kind === 'probing' || state.kind === 'loading') && <><h1 id="gate-title" className="sr-only">Vessel</h1>
        <p className="muted" role="status">{state.kind === 'probing' ? 'Checking your session…' : 'Loading your workspace…'}</p></>}
      {state.kind === 'signing-out' && <><h1 id="gate-title" className="sr-only">Signing out</h1><p className="muted" role="status">Signing out…</p></>}
      {state.kind === 'failed' && <><h1 id="gate-title" className="sr-only">Vessel</h1>
        <StateCard icon={<Plug size={16} />} title="Vessel is unreachable" actions={[{ label: 'Retry', primary: true, onClick: () => {
          if (state.retry) void enter(state.retry.session, false, state.retry.walletName)
          else { setState({ kind: 'probing' }); probe() }
        } }]}>{state.message}</StateCard></>}
      {state.kind === 'already-signed-in' && <><h1 id="gate-title" className="sr-only">Vessel</h1>
        <StateCard icon={<ShieldCheck size={16} />} tone="info" title="This device is already signed in" actions={[
          { label: 'Open Vessel', primary: true, onClick: () => { void enter(state.session, false, null) } }]}>
          The code was not used, so nothing was paired.</StateCard></>}
      {state.kind === 'suspended' && <><h1 id="gate-title" className="sr-only">Vessel</h1>
        <StateCard icon={<Lock size={16} />} tone="bad" title="Journal suspended" actions={[{ label: 'Use another wallet', onClick: () => reloadToSignIn(navigate) }]}>
          The operator of this server has suspended this journal. Contact them to restore access.</StateCard></>}
      {state.kind === 'sign-out-failed' && <><h1 id="gate-title" className="sr-only">Vessel</h1>
        <StateCard icon={<Plug size={16} />} tone="warn" title="Sign-out did not complete" actions={[
          { label: 'Try again', primary: true, onClick: () => signOut(state.ready) },
          { label: 'Reload', onClick: () => window.location.reload() },
        ]}>{state.message} Your session may still be active on this device.</StateCard></>}
    </section>
  </main>
}

/** The probe in flight for each controller: a newer one aborts it, and the controller's teardown ends it. */
const probes = new WeakMap<SessionController, AbortController>()

/**
 * Each controller's authoritative profile: the user, the session generation it was learned under and its profile
 * epoch. The epoch increases on every authoritative change (a newer generation's profile, a newer revision, a save, a
 * conflict rebase), so an asynchronous profile read applies its snapshot only if the generation and epoch it started
 * under are still current. Kept beside the controller, not in React state, so each decision sees the latest change.
 */
const profiles = new WeakMap<SessionController, { user: UserDto; generation: number; epoch: number }>()

function profileRead(controller: SessionController): ProfileRead {
  return { generation: controller.generation, epoch: profiles.get(controller)?.epoch ?? -1 }
}
