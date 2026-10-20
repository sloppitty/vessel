import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { FlaskConical } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import { isPaperSettings, type PaperSettings as Settings } from '@/api/paper'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import './paper-settings.css'

type PaperSettingsApi = Pick<WorkspaceApi, 'paperSettings' | 'savePaperSettings'>
type SettingsRequest = { api: PaperSettingsApi; attempt: number }
export interface PaperSettingsProps {
  api: PaperSettingsApi
  /** Successfully loaded or saved settings only, never an unsaved draft. */
  onChange?: (settings: Settings) => void
}
interface Loaded {
  request: SettingsRequest
  saved: Settings | null
  enabled: boolean
  interval: string
  error: string | null
  message: string | null
}

/** Paper execution is an independent, opt-in simulation; no real-monitor setting is changed here. */
export function PaperSettings({ api, onChange }: PaperSettingsProps) {
  const id = useId()
  const [attempt, setAttempt] = useState(0)
  const request = useMemo(() => ({ api, attempt }), [api, attempt])
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [savingFor, setSavingFor] = useState<SettingsRequest | null>(null)
  const [confirmingFor, setConfirmingFor] = useState<SettingsRequest | null>(null)
  const scope = useRef<{ request: SettingsRequest; controller: AbortController; saving: boolean } | null>(null)
  const changed = useRef(onChange)
  useEffect(() => { changed.current = onChange }, [onChange])

  useEffect(() => {
    const controller = new AbortController()
    scope.current = { request, controller, saving: false }
    void (async () => {
      try {
        if (typeof request.api.paperSettings !== 'function') throw new Error('Paper settings unavailable')
        const settings = await request.api.paperSettings(controller.signal)
        if (controller.signal.aborted) return
        if (!isPaperSettings(settings)) throw new Error('Invalid paper settings')
        setLoaded({ request, saved: settings, enabled: settings.enabled, interval: String(settings.checkIntervalMinutes), error: null, message: null })
        changed.current?.(settings)
      } catch (cause) {
        if (!controller.signal.aborted) setLoaded({ request, saved: null, enabled: false, interval: '60',
          error: errorMessage(cause, 'Paper settings could not be loaded. No changes have been made.'), message: null })
      }
    })()
    return () => controller.abort()
  }, [request])

  const current = loaded?.request === request ? loaded : null
  const saving = savingFor === request
  const draft = { enabled: current?.enabled ?? false, checkIntervalMinutes: Number(current?.interval ?? '60') }
  const valid = (current?.interval.trim().length ?? 0) > 0 && isPaperSettings(draft)
  const dirty = current?.saved !== null && current?.saved !== undefined &&
    (current.enabled !== current.saved.enabled || draft.checkIntervalMinutes !== current.saved.checkIntervalMinutes)
  const confirming = confirmingFor === request && current?.saved?.enabled === true && !draft.enabled

  async function save(confirmed = false) {
    const active = scope.current
    if (!current?.saved || !active || active.request !== request || active.controller.signal.aborted || active.saving || !dirty || !valid) return
    if (current.saved.enabled && !draft.enabled && !confirmed) { setConfirmingFor(request); return }
    setConfirmingFor(null)
    active.saving = true
    setSavingFor(request)
    setLoaded({ ...current, error: null, message: null })
    try {
      if (typeof api.savePaperSettings !== 'function') throw new Error('Saving paper settings unavailable')
      const settings = await api.savePaperSettings(draft)
      if (active.controller.signal.aborted) return
      if (!isPaperSettings(settings)) throw new Error('Invalid paper settings')
      setLoaded({ request, saved: settings, enabled: settings.enabled, interval: String(settings.checkIntervalMinutes), error: null,
        message: 'Paper settings saved.' })
      changed.current?.(settings)
    } catch (cause) {
      if (!active.controller.signal.aborted) setLoaded({ ...current,
        error: errorMessage(cause, 'Paper settings could not be saved. Reload to confirm the server settings.'), message: null })
    } finally {
      active.saving = false
      if (!active.controller.signal.aborted) setSavingFor(null)
    }
  }

  return <section className="shell-panel settings-panel paper-settings" aria-labelledby={`${id}-heading`}>
    <FlaskConical size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Paper trading</h2>
    <p>Opt in to simulated execution with virtual capital. No real orders are submitted. Paper checks use closed one-minute candles,
      with zero fees and slippage; ambiguous candle order is not guessed. These settings are independent of the real-account monitor.
      Defaults: disabled, checked every 60 minutes. Disabling stops monitoring and blocks new paper arming; paper history stays available.
      Re-enabling resumes suspended Plays at a fresh minute boundary without replaying disabled-period touches. Unevaluated history remains unscored.</p>
    {!current && <p role="status">Loading paper settings…</p>}
    {current?.saved && <p role="status">Saved paper monitoring: {current.saved.enabled ? 'enabled' : 'disabled'}.</p>}
    {current?.error && <div className="workspace-alert" role="alert"><span>{current.error}</span>
      <Button variant="ghost" size="sm" disabled={saving} onClick={() => setAttempt(value => value + 1)}>Reload paper settings</Button></div>}
    <form onSubmit={event => { event.preventDefault(); void save() }} noValidate>
      <fieldset disabled={!current?.saved || saving || confirming}>
        <legend className="sr-only">Paper execution settings</legend>
        <label className="paper-settings-switch"><input type="checkbox" role="switch" checked={current?.enabled ?? false}
          onChange={event => current && setLoaded({ ...current, enabled: event.target.checked, message: null })} />Enable paper trading</label>
        <label htmlFor={`${id}-interval`}>Paper check interval (minutes)</label>
        <Input id={`${id}-interval`} type="number" min={1} max={1440} step={1} value={current?.interval ?? '60'}
          aria-describedby={`${id}-interval-help`} aria-invalid={current?.saved ? !valid : undefined}
          onChange={event => current && setLoaded({ ...current, interval: event.target.value, message: null })} />
        <p id={`${id}-interval-help`} className="muted">A whole number from 1 to 1440. This does not change the real monitor interval.</p>
      </fieldset>
      <Button type="submit" disabled={!current?.saved || !dirty || !valid || saving || confirming} aria-busy={saving}>Save paper settings</Button>
      {current?.message && <p role="status">{current.message}</p>}
    </form>
    {confirming && <Dialog open onOpenChange={open => { if (!open) setConfirmingFor(null) }}>
      <DialogContent className="workspace-dialog">
        <DialogHeader><DialogTitle>Disable paper monitoring?</DialogTitle>
          <DialogDescription>Monitoring will stop for all armed and open paper Plays. Existing capital reservations remain held while open exposure is unknown.
            Re-enabling resumes suspended Plays prospectively at a fresh minute boundary; owner-paused Plays stay paused.
            Price touches while disabled are not retroactively simulated. Unevaluated history remains unscored after resumption.
            Paper history stays available.</DialogDescription></DialogHeader>
        <DialogFooter><Button type="button" variant="outline" onClick={() => setConfirmingFor(null)}>Cancel</Button>
          <Button type="button" variant="destructive" onClick={() => { void save(true) }}>Disable paper monitoring</Button></DialogFooter>
      </DialogContent>
    </Dialog>}
  </section>
}
