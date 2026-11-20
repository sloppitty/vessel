import { useEffect, useId, useRef, useState } from 'react'
import { FlaskConical } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import { isPaperSettings, type PaperSettings as Settings } from '@/api/paper'
import type { WorkspaceApi } from '@/api/workspace'
import { useApiRead } from '@/lib/use-api-read'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import './paper-settings.css'

type PaperSettingsApi = Pick<WorkspaceApi, 'paperSettings' | 'savePaperSettings'>
export interface PaperSettingsProps {
  api: PaperSettingsApi
  /** Successfully loaded or saved settings only, never an unsaved draft. */
  onChange?: (settings: Settings) => void
}
/** The form over one read: edits, saves and the confirmation belong to it, and a new read starts a new form. */
interface Form {
  read: Settings
  saved: Settings
  enabled: boolean
  interval: string
  error: string | null
  message: string | null
}

const formOf = (read: Settings): Form =>
  ({ read, saved: read, enabled: read.enabled, interval: String(read.checkIntervalMinutes), error: null, message: null })

/** Paper execution is an independent, opt-in simulation; no real-monitor setting is changed here. */
export function PaperSettings({ api, onChange }: PaperSettingsProps) {
  const id = useId()
  const settings = useApiRead(api, null, (client, signal) => {
    if (typeof client.paperSettings !== 'function') throw new Error('Paper settings unavailable')
    return client.paperSettings(signal).then(settings => {
      if (!isPaperSettings(settings)) throw new Error('Invalid paper settings')
      return settings
    })
  }, { fallback: 'Paper settings could not be loaded. No changes have been made.' })
  const read = settings.data
  const [form, setForm] = useState<Form | null>(null)
  const [savingFor, setSavingFor] = useState<Settings | null>(null)
  const [confirmingFor, setConfirmingFor] = useState<Settings | null>(null)
  // The read whose form is on screen, and the one a save is running for, outside render.
  const shown = useRef<Settings | null>(null)
  const savingRead = useRef<Settings | null>(null)
  useEffect(() => { shown.current = read; return () => { shown.current = null } }, [read])
  const changed = useRef(onChange)
  useEffect(() => { changed.current = onChange }, [onChange])
  // Each loaded read is reported once.
  useEffect(() => { if (read) changed.current?.(read) }, [read])

  const current = read === null ? null : form?.read === read ? form : formOf(read)
  const saving = read !== null && savingFor === read
  const draft = { enabled: current?.enabled ?? false, checkIntervalMinutes: Number(current?.interval ?? '60') }
  const valid = (current?.interval.trim().length ?? 0) > 0 && isPaperSettings(draft)
  const dirty = !!current && (current.enabled !== current.saved.enabled || draft.checkIntervalMinutes !== current.saved.checkIntervalMinutes)
  const confirming = read !== null && confirmingFor === read && current?.saved.enabled === true && !draft.enabled

  async function save(confirmed = false) {
    if (!current || savingRead.current === current.read || !dirty || !valid) return
    if (current.saved.enabled && !draft.enabled && !confirmed) { setConfirmingFor(current.read); return }
    setConfirmingFor(null)
    savingRead.current = current.read
    setSavingFor(current.read)
    setForm({ ...current, error: null, message: null })
    // Only the form still on screen takes the answer.
    const live = () => shown.current === current.read
    try {
      if (typeof api.savePaperSettings !== 'function') throw new Error('Saving paper settings unavailable')
      const settings = await api.savePaperSettings(draft)
      if (!live()) return
      if (!isPaperSettings(settings)) throw new Error('Invalid paper settings')
      setForm({ ...formOf(settings), read: current.read, message: 'Paper settings saved.' })
      changed.current?.(settings)
    } catch (cause) {
      if (live()) setForm({ ...current,
        error: errorMessage(cause, 'Paper settings could not be saved. Reload to confirm the server settings.'), message: null })
    } finally {
      if (savingRead.current === current.read) savingRead.current = null
      if (live()) setSavingFor(null)
    }
  }

  return <section className="shell-panel settings-panel paper-settings" aria-labelledby={`${id}-heading`}>
    <FlaskConical size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Paper trading</h2>
    <p>Opt in to simulated execution with virtual capital. No real orders are submitted. Paper checks use closed one-minute candles,
      with zero fees and slippage; ambiguous candle order is not guessed. These settings are independent of the real-account monitor.
      Defaults: disabled, checked every 60 minutes. Disabling stops monitoring and blocks new paper arming; paper history stays available.
      Re-enabling resumes suspended Plays at a fresh minute boundary without replaying disabled-period touches. Unevaluated history remains unscored.</p>
    {settings.loading && <p role="status">Loading paper settings…</p>}
    {current && <p role="status">Saved paper monitoring: {current.saved.enabled ? 'enabled' : 'disabled'}.</p>}
    {(settings.error ?? current?.error) && <div className="workspace-alert" role="alert"><span>{settings.error ?? current?.error}</span>
      <Button variant="ghost" size="sm" disabled={saving} onClick={settings.reload}>Reload paper settings</Button></div>}
    <form onSubmit={event => { event.preventDefault(); void save() }} noValidate>
      <fieldset disabled={!current?.saved || saving || confirming}>
        <legend className="sr-only">Paper execution settings</legend>
        <label className="paper-settings-switch"><input type="checkbox" role="switch" checked={current?.enabled ?? false}
          onChange={event => current && setForm({ ...current, enabled: event.target.checked, message: null })} />Enable paper trading</label>
        <label htmlFor={`${id}-interval`}>Paper check interval (minutes)</label>
        <Input id={`${id}-interval`} type="number" min={1} max={1440} step={1} value={current?.interval ?? '60'}
          aria-describedby={`${id}-interval-help`} aria-invalid={current?.saved ? !valid : undefined}
          onChange={event => current && setForm({ ...current, interval: event.target.value, message: null })} />
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
            Unevaluated time before disabling is also skipped and remains unscored.
            Any position open at suspension keeps new entries on that account blocked after re-enabling until that exposure closes.
            Entries touched during the hold are declined, not replayed.
            Paper history stays available.</DialogDescription></DialogHeader>
        <DialogFooter><Button type="button" variant="outline" onClick={() => setConfirmingFor(null)}>Cancel</Button>
          <Button type="button" variant="destructive" onClick={() => { void save(true) }}>Disable paper monitoring</Button></DialogFooter>
      </DialogContent>
    </Dialog>}
  </section>
}
