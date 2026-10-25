import { useEffect, useState, type ComponentType } from 'react'
import { createPublicAuthApi, type AuthConfig } from '@/api/auth'
import { errorMessage } from '@/api/errors'
import { Button } from '@/components/ui/button'
import { SignInGate } from '@/features/identity/SignInGate'
import type { ApplicationShell as Shell } from '@/features/workspace/ApplicationShell'

type ShellComponent = ComponentType<Parameters<typeof Shell>[0]>

// Sign-in is always first, so it ships without the workspace. The workspace starts downloading as soon as this
// module runs, while the person signs in.
let loadedShell: ShellComponent | null = null
const shellModule: Promise<ShellComponent> = import('@/features/workspace/ApplicationShell')
  .then(module => module.ApplicationShell as ShellComponent)
  // A rejected native module URL can stay failed for the document's lifetime: offer a reload, not a retry.
  .catch(() => ShellLoadFallback)
  .then(component => (loadedShell = component))

function ShellLoadFallback() {
  return <main className="connection-page">
    <section className="panel connection-card" role="alert">
      <h1>The workspace could not be loaded.</h1>
      <p className="muted">The browser could not load or initialize the workspace code. This can follow a connection problem or app update. Reloading the browser may ask you to sign in again.</p>
      <Button variant="outline" onClick={() => window.location.reload()}>Reload browser</Button>
    </section>
  </main>
}

/**
 * Not React.lazy: a lazy component suspends once even when its module is already loaded, and React
 * then holds the Suspense fallback for its reveal throttle (about 300 ms) after every sign-in.
 */
function DeferredShell(props: Parameters<typeof Shell>[0]) {
  const [Loaded, setLoaded] = useState<ShellComponent | null>(() => loadedShell)
  useEffect(() => {
    if (Loaded) return
    let active = true
    void shellModule.then(component => { if (active) setLoaded(() => component) })
    return () => { active = false }
  }, [Loaded])
  if (!Loaded) return <main className="connection-page"><p className="muted" role="status">Loading your workspace…</p></main>
  return <Loaded {...props} />
}

/** Asks the server for its registration policy, then shows wallet sign-in. */
export function WorkspacePage() {
  const [config, setConfig] = useState<AuthConfig | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    createPublicAuthApi().config(window.location.origin, controller.signal).then(next => {
      if (!controller.signal.aborted) setConfig(next)
    }, (cause: unknown) => { if (!controller.signal.aborted) setError(errorMessage(cause, 'Vessel is unreachable.')) })
    return () => controller.abort()
  }, [attempt])
  if (config) return <SignInGate registration={config.registration} originAccepted={config.originAccepted}>
    {(system, disconnect, api, identity) => <DeferredShell system={system} disconnect={disconnect} api={api} identity={identity} />}
  </SignInGate>
  return <main className="connection-page">
    {error ? <section className="panel connection-card" role="alert">
      <h1>Vessel is unreachable</h1>
      <p className="muted">{error}</p>
      <Button variant="outline" onClick={() => { setError(null); setAttempt(value => value + 1) }}>Retry</Button>
    </section> : <p className="muted" role="status">Connecting to Vessel…</p>}
  </main>
}
