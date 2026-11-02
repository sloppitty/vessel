import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ArrowLeft } from 'lucide-react'
import type { PairingClaim, PublicAuthApi } from '@/api/auth'
import { ApiError } from '@/api/errors'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { SignInBrand } from './IdentityParts'

/**
 * The fallback when scanning fails: the 10-character code under "Show a code to type" on the desktop. It is sent as
 * typed (any case, spaces or dashes); the server normalizes it, and a wrong one answers like an unknown secret.
 */
export function TypedCodeEntry({ api, onClaimed, onBack }: { api: PublicAuthApi; onClaimed: (claim: PairingClaim) => void; onBack: () => void }) {
  const [value, setValue] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (busy || !value.trim()) return
    setBusy(true); setError(null)
    try {
      const claim = await api.claimPairing({ typedCode: value })
      if (mounted.current) onClaimed(claim)
    } catch (cause) {
      if (!mounted.current) return
      setError(cause instanceof ApiError ? cause.message : 'The code could not be checked. Try again.')
      setBusy(false)
    }
  }
  return <>
    <div className="sign-in-head"><SignInBrand /></div>
    <h1 id="sign-in-title">Enter a code</h1>
    <p className="sign-in-lede">On your computer, open Settings, Profile &amp; sign-in, Connect a phone, then Show a code to type.</p>
    <form className="sign-in-body" onSubmit={event => { void submit(event) }} noValidate aria-busy={busy}>
      <div className="identity-field">
        <label htmlFor="typed-pairing-code">Code from your computer</label>
        <Input id="typed-pairing-code" className="identity-mono pairing-code-input" value={value} onChange={event => setValue(event.target.value)}
          placeholder="XXXXX-XXXXX" maxLength={40} autoComplete="off" autoCapitalize="characters" spellCheck={false} autoFocus disabled={busy}
          aria-invalid={error !== null} aria-describedby={error ? 'typed-pairing-error' : undefined} />
        {error && <p id="typed-pairing-error" className="error" role="alert">{error}</p>}
      </div>
      <div className="sign-in-actions">
        <Button type="submit" size="lg" className="sign-in-primary" disabled={busy || !value.trim()} aria-busy={busy}>{busy ? 'Checking…' : 'Continue'}</Button>
        <Button type="button" size="lg" variant="ghost" onClick={onBack} disabled={busy}><ArrowLeft size={15} aria-hidden="true" />Back</Button>
      </div>
    </form>
  </>
}
