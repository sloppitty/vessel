import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { LogOut, Monitor, Pencil, Plus, Smartphone, Trash2 } from 'lucide-react'
import { authErrorMessage, createPublicAuthApi, displayNameLimit, signInWalletLimit, walletLabelLimit, type SessionRow, type StepUpAction, type WalletDto } from '@/api/auth'
import { ApiError, errorMessage } from '@/api/errors'
import { SourceChip } from '@/components/accounts/AccountIdentity'
import { useNotifications } from '@/components/notifications/notifications'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { InfoTip } from '@/components/ui/info-tip'
import { Input } from '@/components/ui/input'
import { UserAvatar } from './IdentityParts'
import { cleanDisplayName, cleanLabel } from './profile-rules'
import { focusedControl, openDialog, returnFocus } from './return-focus'
import { signingMeaning } from './SignInScreen'
import type { ShellIdentity } from './types'
import { WalletSignFlow } from './WalletSignFlow'
import type { BrowserWallet } from './wallets/discovery'
import { sameAddress } from './wallets/provider'
import { Time } from '@/components/ui/time'

function ago(at: string, now = Date.now()) {
  const minutes = Math.round((now - Date.parse(at)) / 60_000)
  if (!Number.isFinite(minutes) || minutes < 2) return 'now'
  if (minutes < 60) return `${minutes} minutes ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`
  const days = Math.round(hours / 24)
  return `${days} day${days === 1 ? '' : 's'} ago`
}
const mobile = (agent: string | null) => !!agent && /\b(iOS|iPhone|iPad|Android)\b/i.test(agent)

/** `opener` is what had focus when the step-up opened, usually a control in the dialog underneath. */
type StepUpRequest = { action: StepUpAction; target?: WalletDto; opener: HTMLElement | null; resolve: (done: boolean) => void }

/**
 * Settings → Profile & sign-in in Wallet mode (mockup screens 8–9): display name, sign-in wallets and sessions.
 * Linking and removing a wallet need a fresh signature from a wallet already linked (step-up); a refused call
 * with `step-up-required` opens it and is retried once.
 */
export function ProfileSettings({ identity, onSignOut }: { identity: ShellIdentity; onSignOut: () => void }) {
  const { auth } = identity
  const { notify } = useNotifications()
  const [wallets, setWallets] = useState<WalletDto[] | null>(null)
  const [sessions, setSessions] = useState<SessionRow[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  // The lists on screen were loaded under an older session of this tab: their `current` marks may be wrong, so
  // nothing destructive is offered until the reload under the adopted session lands.
  const [stale, setStale] = useState(false)
  const reload = useCallback(() => setReloadKey(key => key + 1), [])
  const [stepUp, setStepUp] = useState<StepUpRequest | null>(null)
  const [linking, setLinking] = useState(false)
  const [removing, setRemoving] = useState<WalletDto | null>(null)
  // These dialogs open from state, so they remember where focus goes back to.
  const linkButton = useRef<HTMLButtonElement>(null)
  const linkRefused = useRef(false)
  const walletsHeading = useRef<HTMLHeadingElement>(null)
  const removeOpener = useRef<HTMLElement | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([auth.wallets(controller.signal), auth.sessions(controller.signal)]).then(([nextWallets, nextSessions]) => {
      if (controller.signal.aborted) return
      setWallets(nextWallets); setSessions(nextSessions); setLoadError(null); setStale(false)
    }, (cause: unknown) => { if (!controller.signal.aborted) setLoadError(errorMessage(cause, 'Sign-in wallets and sessions could not be loaded.')) })
    return () => controller.abort()
  }, [auth, reloadKey])

  // Adopting another tab's session of this user moves which wallet is current: the lists reload, their destructive
  // actions wait for it, and a removal prepared under the old session (its dialog or its step-up) is dropped instead
  // of acting on a stale row.
  const { onSessionChange } = identity
  const pendingStepUp = useRef<StepUpRequest | null>(null)
  useEffect(() => { pendingStepUp.current = stepUp }, [stepUp])
  useEffect(() => onSessionChange(() => {
    setStale(true)
    setRemoving(null)
    const pending = pendingStepUp.current
    if (pending?.action === 'wallet.remove') { pending.resolve(false); setStepUp(null) }
    reload()
  }), [onSessionChange, reload])

  const requestStepUp = useCallback((action: StepUpAction, target?: WalletDto) => new Promise<boolean>(resolve => {
    setStepUp({ action, ...target ? { target } : {}, opener: focusedControl(), resolve })
  }), [])
  const finishStepUp = (done: boolean) => { stepUp?.resolve(done); setStepUp(null) }

  /** Runs `action`; a `step-up-required` refusal opens the step-up and retries once. */
  async function withStepUp<T>(action: StepUpAction, target: WalletDto | undefined, run: () => Promise<T>): Promise<T | null> {
    try { return await run() } catch (cause) {
      if (!(cause instanceof ApiError && cause.code === 'step-up-required')) throw cause
      if (!await requestStepUp(action, target)) return null
      return await run()
    }
  }

  async function remove(target: WalletDto) {
    // Checked again at confirmation against the current list and session: the dialog may predate an adoption.
    const wallet = stale ? undefined : wallets?.find(row => row.id === target.id)
    if (!wallet || wallet.current || wallet.id === identity.session.wallet.id) {
      setRemoving(null)
      notify({ tone: 'error', message: wallet ? 'This wallet signs in this session now. Remove it from another session.' : 'The wallet list changed. Review it and try again.' })
      reload()
      return
    }
    try {
      const done = await withStepUp('wallet.remove', wallet, () => auth.removeWallet(wallet.id))
      if (done === null) return
      // The row is about to go; land on the list instead.
      removeOpener.current = walletsHeading.current
      setRemoving(null)
      // The server revoked this wallet's sessions. Whether that included this tab is the server's answer, not this
      // row's: another tab may have moved this tab onto a session another wallet signed in.
      if (await identity.checkSession() === 'ended') return
      notify({ tone: 'success', message: 'Sign-in wallet removed.' })
      reload()
    } catch (cause) {
      if (cause instanceof ApiError && cause.code === 'last-wallet') setRemoving(null)
      notify({ tone: 'error', message: errorMessage(cause, 'The wallet could not be removed.') })
      reload()
    }
  }

  const linked = wallets ?? []
  const walletLimitReached = linked.length >= signInWalletLimit
  const others = (sessions ?? []).filter(row => !row.current)
  return <div className="profile-settings">
    <ProfilePanel identity={identity} />
    {loadError && <div className="workspace-alert" role="alert"><span>{loadError}</span><Button variant="ghost" size="sm" onClick={reload}>Try again</Button></div>}
    <div className="profile-split">
      <section className="shell-panel profile-section" aria-labelledby="wallets-title">
        <header>
          <h2 id="wallets-title" ref={walletsHeading} tabIndex={-1}>Sign-in wallets <InfoTip label="About sign-in wallets">Any of these wallets can sign in to this journal. They are not venue accounts and don't import trading history. {signingMeaning}</InfoTip></h2>
          <Button ref={linkButton} variant="outline" size="sm" onClick={() => { linkRefused.current = false; setLinking(true) }} disabled={!wallets || walletLimitReached}
            aria-describedby={walletLimitReached ? 'wallet-limit' : undefined}><Plus size={15} aria-hidden="true" />Link wallet</Button>
        </header>
        {walletLimitReached && <p id="wallet-limit" className="field-help">{authErrorMessage('wallet-limit')}</p>}
        {wallets === null && !loadError ? <p className="muted profile-loading" role="status">Loading sign-in wallets…</p>
          : <ul className="profile-rows">{linked.map(wallet => <WalletRow key={wallet.id} wallet={wallet} only={linked.length === 1} stale={stale} auth={auth}
            onRemove={() => { removeOpener.current = focusedControl(); setRemoving(wallet) }} onChanged={reload} />)}</ul>}
      </section>
      <section className="shell-panel profile-section" aria-labelledby="sessions-heading">
        <header>
          <h2 id="sessions-heading" tabIndex={-1}>Sessions</h2>
          <Button variant="outline" size="sm" disabled={stale || !others.length} onClick={() => {
            auth.revokeOtherSessions().then(() => { notify({ tone: 'success', message: 'Other sessions signed out.' }); reload() },
              (cause: unknown) => notify({ tone: 'error', message: errorMessage(cause, 'Other sessions could not be signed out.') }))
          }}>Sign out other sessions</Button>
        </header>
        {sessions === null && !loadError ? <p className="muted profile-loading" role="status">Loading sessions…</p>
          : <ul className="profile-rows">{(sessions ?? []).map(row => <li key={row.id} className="profile-row">
            <span className="profile-device" aria-hidden="true">{mobile(row.userAgent) ? <Smartphone size={16} /> : <Monitor size={16} />}</span>
            <span className="profile-row-meta"><strong>{row.userAgent ?? 'Unknown browser'}</strong>
              <small>Signed in <Time at={row.createdAt} style="date" /> · active {ago(row.lastSeenAt)}{row.remember ? ' · kept signed in' : ''}</small></span>
            {row.current ? <span className="identity-pill is-brand">This browser</span>
              : <Button variant="ghost" size="sm" disabled={stale} aria-label={`Sign out ${row.userAgent ?? 'unknown browser'} session`} onClick={() => {
                auth.revokeSession(row.id).then(() => { notify({ tone: 'success', message: 'Session signed out.' }); reload() },
                  (cause: unknown) => { notify({ tone: 'error', message: errorMessage(cause, 'The session could not be signed out.') }); reload() })
              }}>Sign out</Button>}
          </li>)}</ul>}
      </section>
    </div>
    <div className="profile-sign-out"><Button variant="outline" onClick={onSignOut}><LogOut size={15} aria-hidden="true" />Sign out</Button></div>

    {removing && <Dialog open onOpenChange={next => { if (!next) setRemoving(null) }}>
      <DialogContent className="workspace-dialog" onCloseAutoFocus={event => returnFocus(() => removeOpener.current, () => walletsHeading.current)(event)}>
        <DialogHeader><DialogTitle>Remove sign-in wallet?</DialogTitle>
          <DialogDescription>This wallet will no longer sign in to this journal, and its sessions end. You'll confirm with a linked wallet.</DialogDescription></DialogHeader>
        <p><SourceChip source={removing.address} /> {removing.label}</p>
        <DialogFooter><Button variant="outline" onClick={() => setRemoving(null)}>Cancel</Button>
          <Button variant="destructive" disabled={stale} onClick={() => { void remove(removing) }}>Remove wallet</Button></DialogFooter>
      </DialogContent>
    </Dialog>}
    {linking && <LinkWalletDialog identity={identity} wallets={linked} onStepUp={() => requestStepUp('wallet.link')}
      onCloseAutoFocus={event => returnFocus(() => linkRefused.current || linkButton.current?.disabled ? null : linkButton.current, () => walletsHeading.current)(event)}
      onLimit={message => { linkRefused.current = true; setLinking(false); notify({ tone: 'error', message }); reload() }}
      onClose={() => setLinking(false)} onLinked={() => { setLinking(false); notify({ tone: 'success', message: 'Wallet linked. It can now sign in to this journal.' }); reload() }} />}
    {stepUp && <StepUpDialog identity={identity} wallets={linked} request={stepUp} onFinish={finishStepUp} />}
  </div>
}

function ProfilePanel({ identity }: { identity: ShellIdentity }) {
  const { user } = identity.session
  const [name, setName] = useState(user.displayName)
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const { notify } = useNotifications()
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const displayName = cleanDisplayName(name)
    if (displayName === undefined) { setError(`Use 1 to ${displayNameLimit} printable characters for the display name.`); return }
    setPending(true); setError(null)
    try {
      identity.onProfile(await identity.auth.updateProfile(displayName, user.revision))
      notify({ tone: 'success', message: 'Display name saved.' })
    } catch (cause) {
      setError(errorMessage(cause, 'The display name could not be saved.'))
      if (cause instanceof ApiError && cause.code === 'revision-conflict') {
        // Another tab changed the profile, or a restore lowered it: take the server's revision so the next save applies.
        // A late answer is dropped if the profile changed authoritatively, or the session generation, since it started.
        const read = identity.profileRead()
        createPublicAuthApi().session().then(fresh => {
          if (fresh.user.id === user.id && identity.onProfile(fresh.user, read)) setName(fresh.user.displayName)
        }, () => undefined)
      }
    } finally { setPending(false) }
  }
  return <section className="shell-panel profile-section" aria-labelledby="profile-title">
    <header><h2 id="profile-title">Profile</h2></header>
    <form className="profile-inline-form" onSubmit={event => { void save(event) }} aria-busy={pending}>
      <div className="identity-field"><label htmlFor="profile-name">Display name</label>
        <Input id="profile-name" value={name} onChange={event => setName(event.target.value)} maxLength={displayNameLimit * 2} autoComplete="nickname"
          disabled={pending} aria-invalid={error !== null} aria-describedby={error ? 'profile-name-error' : undefined} /></div>
      <Button type="submit" variant="outline" disabled={pending || name.trim() === user.displayName}>Save</Button>
    </form>
    {error && <p id="profile-name-error" className="error profile-error" role="alert">{error}</p>}
  </section>
}

function WalletRow({ wallet, only, stale, auth, onRemove, onChanged }: {
  wallet: WalletDto; only: boolean; stale: boolean; auth: ShellIdentity['auth']; onRemove: () => void; onChanged: () => void
}) {
  const [editing, setEditing] = useState(false)
  const [label, setLabel] = useState(wallet.label ?? '')
  const [error, setError] = useState<string | null>(null)
  const name = wallet.label ?? 'Wallet'
  // Leaving the form by Cancel, Escape or a save puts focus back on this row's Rename control.
  const renameButton = useRef<HTMLButtonElement>(null)
  const refocus = useRef(false)
  useEffect(() => {
    if (editing || !refocus.current) return
    refocus.current = false
    renameButton.current?.focus()
  }, [editing])
  const leave = () => { refocus.current = true; setEditing(false) }
  const cancel = () => { leave(); setLabel(wallet.label ?? ''); setError(null) }
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const cleaned = cleanLabel(label)
    if (cleaned === undefined) { setError(`Use at most ${walletLabelLimit} printable characters for the label.`); return }
    try {
      await auth.updateWallet(wallet.id, cleaned)
      leave(); setError(null); onChanged()
    } catch (cause) { setError(errorMessage(cause, 'The label could not be saved.')) }
  }
  return <li className="profile-row">
    <UserAvatar name={name} address={wallet.address} />
    <span className="profile-row-meta">
      {editing ? <form className="profile-label-form" onSubmit={event => { void save(event) }}
        onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); cancel() } }}>
        <Input aria-label={`Label for ${wallet.address}`} value={label} onChange={event => setLabel(event.target.value)} maxLength={walletLabelLimit * 2} autoFocus
          aria-invalid={error !== null} aria-describedby={error ? `label-error-${wallet.id}` : undefined} />
        <Button type="submit" size="sm">Save</Button><Button type="button" size="sm" variant="ghost" onClick={cancel}>Cancel</Button>
      </form> : <strong>{name} <SourceChip source={wallet.address} /></strong>}
      {error && <span id={`label-error-${wallet.id}`} className="error" role="alert">{error}</span>}
      <small>Added <Time at={wallet.addedAt} style="date" /> · {wallet.current ? 'used for this session' : wallet.lastUsedAt ? `last used ${ago(wallet.lastUsedAt)}` : 'not used yet'}</small>
    </span>
    {wallet.current && <span className="identity-pill is-brand">This session</span>}
    {!editing && <span className="profile-row-actions">
      <Button ref={renameButton} variant="ghost" size="sm" aria-label={`Rename ${name}`} title="Rename" onClick={() => setEditing(true)}><Pencil size={14} aria-hidden="true" /></Button>
      {/* As on mockup screen 8, the wallet that signed this session is removed from another session. */}
      {/* After an adoption the old marks may be wrong, so Remove waits for the reloaded list. */}
      {!only && !wallet.current && !stale && <Button variant="ghost" size="sm" className="profile-danger" onClick={onRemove}><Trash2 size={14} aria-hidden="true" />Remove</Button>}
    </span>}
  </li>
}

const stepUpCopy: Record<StepUpAction, (target?: WalletDto) => ReactNode> = {
  'wallet.link': () => 'Sign with a wallet already linked to this journal before adding another.',
  'wallet.remove': target => <>Sign with a wallet already linked to this journal to remove {target ? <SourceChip source={target.address} /> : 'this wallet'}.</>,
}

function linkedOnly(wallets: readonly WalletDto[]) {
  return (address: string) => wallets.some(wallet => sameAddress(wallet.address, address)) ? null
    : 'This address isn’t linked to your journal. Switch to a linked wallet, then try again.'
}

/** A fresh signature from a wallet already linked: the grant lasts five minutes and covers one action. */
function StepUpDialog({ identity, wallets, request, onFinish }: {
  identity: ShellIdentity; wallets: readonly WalletDto[]; request: StepUpRequest; onFinish: (done: boolean) => void
}) {
  const accept = useMemo(() => linkedOnly(wallets), [wallets])
  // Back to the control that asked, or else to the dialog underneath.
  const onCloseAutoFocus = useMemo(() => returnFocus(() => request.opener, openDialog), [request.opener])
  return <Dialog open onOpenChange={next => { if (!next) onFinish(false) }}>
    <DialogContent className="workspace-dialog identity-dialog" onCloseAutoFocus={onCloseAutoFocus}>
      <DialogHeader><DialogTitle>Confirm with a linked wallet</DialogTitle>
        <DialogDescription>{stepUpCopy[request.action](request.target)}</DialogDescription></DialogHeader>
      <WalletSignFlow purpose="Confirm" accept={accept}
        requestChallenge={(address, signal) => identity.auth.stepUpChallenge({ action: request.action, ...request.target ? { target: request.target.id } : {}, address }, signal)}
        complete={(challenge, signature, signal) => identity.auth.stepUp({ challengeId: challenge.challengeId, signature }, signal)}
        onDone={() => onFinish(true)} onCancel={() => onFinish(false)} />
    </DialogContent>
  </Dialog>
}

/** Mockup screen 9: a step-up from a linked wallet, then a signature from the new wallet with its own statement. */
function LinkWalletDialog({ identity, wallets, onStepUp, onClose, onLinked, onLimit, onCloseAutoFocus }: {
  identity: ShellIdentity; wallets: readonly WalletDto[]; onStepUp: () => Promise<boolean>; onClose: () => void; onLinked: () => void
  onLimit: (message: string) => void
  onCloseAutoFocus: (event: Event) => void
}) {
  const [phase, setPhase] = useState<'confirm' | 'link'>('confirm')
  const [label, setLabel] = useState('')
  const [labelError, setLabelError] = useState<string | null>(null)
  // The new wallet's announced name is the suggested label until the person types their own.
  const labelEdited = useRef(false)
  const suggestLabel = useCallback((wallet: BrowserWallet) => {
    if (!labelEdited.current) setLabel(cleanLabel(wallet.name) ?? '')
  }, [])
  const accept = useMemo(() => linkedOnly(wallets), [wallets])
  const [confirmedWith, setConfirmedWith] = useState<string | null>(null)
  return <Dialog open onOpenChange={next => { if (!next) onClose() }}>
    <DialogContent className="workspace-dialog identity-dialog" onCloseAutoFocus={onCloseAutoFocus}>
      <DialogHeader><DialogTitle>Link another wallet</DialogTitle>
        <DialogDescription>{phase === 'confirm' ? 'First, sign with a wallet already linked to this journal.'
          : `Confirmed${confirmedWith ? ` with ${confirmedWith}` : ''}. Now sign with the wallet you want to add.`}</DialogDescription></DialogHeader>
      {phase === 'confirm' ? <WalletSignFlow key="confirm" purpose="Confirm" accept={accept}
        requestChallenge={(address, signal) => {
          setConfirmedWith(wallets.find(wallet => sameAddress(wallet.address, address))?.label ?? null)
          return identity.auth.stepUpChallenge({ action: 'wallet.link', address }, signal)
        }}
        complete={(challenge, signature, signal) => identity.auth.stepUp({ challengeId: challenge.challengeId, signature }, signal)}
        onDone={() => setPhase('link')} onCancel={onClose} />
        : <WalletSignFlow key="link" purpose="Link wallet" onStepUp={onStepUp}
          accept={address => wallets.some(wallet => sameAddress(wallet.address, address)) ? 'This wallet is already linked to your journal. Switch to the wallet you want to add.' : null}
          requestChallenge={async (address, signal) => {
            try { return await identity.auth.walletChallenge(address, signal) } catch (cause) {
              if (!signal.aborted && cause instanceof ApiError && cause.code === 'wallet-limit') onLimit(cause.message)
              throw cause
            }
          }}
          complete={async (challenge, signature, signal) => {
            const cleaned = cleanLabel(label)
            if (cleaned === undefined) {
              setLabelError(`Use at most ${walletLabelLimit} printable characters for the label.`)
              throw new ApiError('http', `Use at most ${walletLabelLimit} printable characters for the label.`, 400, undefined, 'label-invalid')
            }
            try {
              await identity.auth.linkWallet({ challengeId: challenge.challengeId, signature, ...cleaned ? { label: cleaned } : {} }, signal)
            } catch (cause) {
              if (!signal.aborted && cause instanceof ApiError && cause.code === 'wallet-limit') onLimit(cause.message)
              // The server decides; its refusal shows on the field too.
              if (cause instanceof ApiError && cause.code === 'label-invalid') setLabelError(cause.message)
              throw cause
            }
          }}
          onDone={onLinked} onCancel={onClose} onWallet={suggestLabel}>
          <div className="identity-field"><label htmlFor="link-label">Label <small>· optional</small></label>
            <Input id="link-label" value={label} onChange={event => { labelEdited.current = true; setLabel(event.target.value); setLabelError(null) }} maxLength={walletLabelLimit * 2}
              aria-invalid={labelError !== null} aria-describedby={labelError ? 'link-label-error' : undefined} />
            {labelError && <p id="link-label-error" className="error" role="alert">{labelError}</p>}</div>
        </WalletSignFlow>}
    </DialogContent>
  </Dialog>
}
