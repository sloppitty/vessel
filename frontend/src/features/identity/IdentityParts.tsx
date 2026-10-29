import { useCallback, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Check, ChevronRight, Clock } from 'lucide-react'
import { sourceHue } from '@/components/accounts/identity'
import { VesselMark } from '@/components/brand/VesselMark'
import { Button } from '@/components/ui/button'
import { clock, useCountdown } from './countdown'
import type { BrowserWallet } from './wallets/discovery'
import './identity.css'

/** The first screen's brand block, as on the token connection card. */
export function SignInBrand() {
  return <div className="connection-brand sign-in-brand" aria-hidden="true">
    <span className="shell-monogram"><VesselMark size={26} /></span><span>vessel<small>The trading journal</small></span>
  </div>
}

export type Step = 'connect' | 'sign' | 'create'
const stepLabels: Record<Step, string> = { connect: 'Connect', sign: 'Sign', create: 'Create' }

/** Connect · Sign (· Create): where the person is in sign-in. */
export function SignInSteps({ current, withCreate = false }: { current: Step; withCreate?: boolean }) {
  const steps: Step[] = withCreate ? ['connect', 'sign', 'create'] : ['connect', 'sign']
  const at = steps.indexOf(current)
  return <ol className="sign-in-steps" aria-label="Sign-in steps">
    {steps.map((step, index) => <li key={step} className={index < at ? 'is-done' : index === at ? 'is-current' : undefined}
      aria-current={index === at ? 'step' : undefined}>
      <i aria-hidden="true">{index < at ? <Check size={10} strokeWidth={3} /> : index + 1}</i>{stepLabels[step]}
      {index < at && <span className="sr-only"> (done)</span>}
    </li>)}
  </ol>
}

/** A wallet's announced icon, only as a bounded data URI in `<img>`; otherwise its initial. */
export function WalletMark({ wallet, size = 32 }: { wallet: Pick<BrowserWallet, 'icon' | 'name'>; size?: number }) {
  return <span className="wallet-mark" style={{ '--mark-size': `${size}px` } as CSSProperties} aria-hidden="true">
    {wallet.icon ? <img src={wallet.icon} alt="" width={size} height={size} decoding="async" referrerPolicy="no-referrer" />
      : <span>{[...wallet.name][0]?.toUpperCase() ?? 'W'}</span>}
  </span>
}

/** A person: an orb in the hue of their sign-in address, so it matches the address chip everywhere. */
export function UserAvatar({ name, address, size = 'md' }: { name: string; address: string; size?: 'md' | 'lg' }) {
  return <span className={`identity-avatar is-${size}`} style={{ '--source-hue': sourceHue(address) } as CSSProperties} aria-hidden="true">
    {[...name.trim()][0]?.toUpperCase() ?? '?'}
  </span>
}

export function WalletButton({ wallet, last, onChoose, disabled }: { wallet: BrowserWallet; last: boolean; onChoose: () => void; disabled?: boolean }) {
  return <button type="button" className={`wallet-option ${last ? 'is-last' : ''}`} onClick={onChoose} disabled={disabled}>
    <WalletMark wallet={wallet} />
    <span className="wallet-option-name"><strong>{wallet.name}</strong>
      <small>{last ? 'Last used on this browser' : wallet.fallback ? 'Injected by this browser' : 'Browser extension'}</small></span>
    {last && <span className="identity-pill is-brand">Last used</span>}
    <ChevronRight size={16} aria-hidden="true" />
  </button>
}

/** The exact server message, as the wallet will show it, with a countdown to its expiry. */
export function MessagePreview({ message, purpose, seconds }: { message: string; purpose: string; seconds: number | null }) {
  const [first = '', ...rest] = message.split('\n')
  const at = first.indexOf(' wants you to')
  return <figure className="message-preview">
    <figcaption><span><b>Message</b> · {purpose}</span>
      {seconds !== null && <span className="message-timer" role="timer" aria-label={`Expires in ${clock(seconds)}`}><Clock size={13} aria-hidden="true" />{clock(seconds)}</span>}</figcaption>
    <pre tabIndex={0} aria-label="Message to sign">{at > 0 ? <><span className="message-domain">{first.slice(0, at)}</span>{first.slice(at)}</> : first}{rest.length ? `\n${rest.join('\n')}` : ''}</pre>
  </figure>
}

export interface StateAction { label: string; onClick: () => void; primary?: boolean; disabled?: boolean }

/** One failure: what happened and the next step (mockup screen 6). */
export function StateCard({ icon, tone = 'plain', title, children, actions = [] }: {
  icon: ReactNode; tone?: 'plain' | 'warn' | 'bad' | 'info'; title: string; children: ReactNode; actions?: StateAction[]
}) {
  return <div className="sign-in-state" role="alert">
    <span className={`state-badge is-${tone}`} aria-hidden="true">{icon}</span>
    <h2>{title}</h2>
    <p>{children}</p>
    {actions.length > 0 && <div className="sign-in-actions">{actions.map(action =>
      // A disabled action is waiting (a countdown), so it reads as muted text in an outline, not a faded fill.
      <Button key={action.label} size="sm" variant={action.disabled ? 'outline' : action.primary ? 'default' : 'ghost'} onClick={action.onClick}
        disabled={action.disabled} className={action.disabled ? 'identity-waiting' : undefined}>{action.label}</Button>)}</div>}
  </div>
}

/** Both sides of a pairing read the server every two seconds. */
export const pairingPollMs = 2_000
/** Every pairing deadline is five minutes from the start of its state. */
const pairingDeadlineMs = 300_000

/**
 * Time left until a pairing deadline, measured from when the answer arrived; the server decides when it really ends.
 * `onExpired` runs once per deadline (the countdown repeats it every second at zero); key the component by the deadline.
 */
export function Deadline({ expiresAt, onExpired }: { expiresAt: string; onExpired: () => void }) {
  const [lifetime] = useState(() => Math.max(0, Math.min(pairingDeadlineMs, Date.parse(expiresAt) - Date.now() || 0)))
  const fired = useRef(false)
  const seconds = useCountdown(lifetime, useCallback(() => {
    if (fired.current) return
    fired.current = true
    onExpired()
  }, [onExpired]))
  return <span className="message-timer pairing-timer" role="timer" aria-label={`Expires in ${clock(seconds ?? 0)}`}>
    <Clock size={13} aria-hidden="true" />{clock(seconds ?? 0)}</span>
}
