import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { CalendarRange } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import type { ReportingSettings as Settings, WeekStart } from '@/api/reporting'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { NativeSelect } from '@/components/ui/native-select'
import './reporting-settings.css'

type ReportingSettingsApi = Pick<WorkspaceApi, 'reportingSettings' | 'updateReportingSettings'>

interface Draft { timeZone: string; weekStart: WeekStart }
interface Loaded { api: ReportingSettingsApi; attempt: number; saved: Settings | null; draft: Draft; error: string | null; message: string | null }

/** The browser's IANA zones for suggestions, UTC first; the server decides which it accepts. */
const zones = (() => {
  try { return ['UTC', ...Intl.supportedValuesOf('timeZone').filter(zone => zone !== 'UTC')] } catch { return ['UTC'] }
})()

/**
 * The reporting setting (Phase 2, item 2): one IANA zone and a week start, saved on the server for this owner. It sets the
 * week page's boundaries and the soft loss limits' days and weeks. It is not the display zone above, and Activity and
 * Review's rolling periods keep UTC days.
 */
export function ReportingSettings({ api }: { api: ReportingSettingsApi }) {
  const id = useId()
  const [attempt, setAttempt] = useState(0)
  const request = useMemo(() => ({ api, attempt }), [api, attempt])
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [saving, setSaving] = useState(false)
  const scope = useRef<AbortController | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    scope.current = controller
    request.api.reportingSettings(controller.signal).then(settings => {
      if (!controller.signal.aborted) setLoaded({ ...request, saved: settings, draft: { timeZone: settings.timeZone, weekStart: settings.weekStart },
        error: null, message: null })
    }).catch(cause => {
      if (!controller.signal.aborted) setLoaded({ ...request, saved: null, draft: { timeZone: '', weekStart: 'monday' },
        error: errorMessage(cause, 'The reporting setting could not be loaded.'), message: null })
    })
    return () => controller.abort()
  }, [request])

  const current = loaded?.api === request.api && loaded.attempt === request.attempt ? loaded : null
  const zone = current?.draft.timeZone.trim() ?? ''
  const dirty = !!current?.saved && zone !== '' &&
    (zone !== current.saved.timeZone || current.draft.weekStart !== current.saved.weekStart || current.saved.isDefault)
  // The browser's own IANA zone, if it reports one, as a one-click choice.
  const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone || null

  async function save() {
    const controller = scope.current
    if (!current?.saved || !controller || controller.signal.aborted || saving || !dirty) return
    setSaving(true)
    try {
      const settings = await api.updateReportingSettings({ timeZone: zone, weekStart: current.draft.weekStart })
      if (!controller.signal.aborted) setLoaded({ ...current, saved: settings, draft: { timeZone: settings.timeZone, weekStart: settings.weekStart },
        error: null, message: 'Reporting setting saved.' })
    } catch (cause) {
      if (!controller.signal.aborted) setLoaded({ ...current, error: errorMessage(cause, 'The setting could not be saved.'), message: null })
    } finally {
      if (!controller.signal.aborted) setSaving(false)
    }
  }

  const edit = (draft: Partial<Draft>) => current && setLoaded({ ...current, draft: { ...current.draft, ...draft }, message: null, error: null })
  return <section className="shell-panel settings-panel reporting-settings" aria-labelledby={`${id}-heading`}>
    <CalendarRange size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Reporting week</h2>
    <p>The time zone and first day of the week page and of the daily and weekly loss limits. It is saved for your account, not
      this browser, and does not change how times are displayed. Activity and Review&apos;s rolling periods keep UTC days.</p>
    {!current && <p role="status">Loading the reporting setting…</p>}
    {current?.error && <div className="workspace-alert" role="alert"><span>{current.error}</span>
      {!current.saved && <Button variant="ghost" size="sm" disabled={saving} onClick={() => setAttempt(value => value + 1)}>Reload the reporting setting</Button>}</div>}
    <form onSubmit={event => { event.preventDefault(); void save() }} noValidate>
      <fieldset disabled={!current?.saved || saving}>
        <legend className="sr-only">Reporting zone and week start</legend>
        <label htmlFor={`${id}-zone`}>Reporting time zone (IANA)</label>
        <div className="reporting-settings-zone">
          <Input id={`${id}-zone`} list={`${id}-zones`} autoComplete="off" spellCheck={false} placeholder="UTC" value={current?.draft.timeZone ?? ''}
            aria-describedby={`${id}-zone-help`} onChange={event => edit({ timeZone: event.target.value })} />
          {browserZone && browserZone !== zone && <Button type="button" variant="outline" size="sm" onClick={() => edit({ timeZone: browserZone })}>
            Use this browser&apos;s zone ({browserZone})</Button>}
        </div>
        <datalist id={`${id}-zones`}>{zones.map(item => <option key={item} value={item} />)}</datalist>
        <p id={`${id}-zone-help`} className="muted">An IANA name such as Europe/Madrid or UTC.{current?.saved?.isDefault ? ' Not set yet: weeks use UTC and start on Monday.' : ''}</p>
        <label htmlFor={`${id}-week`}>Weeks start on</label>
        <NativeSelect id={`${id}-week`} value={current?.draft.weekStart ?? 'monday'} onChange={event => edit({ weekStart: event.target.value as WeekStart })}>
          <option value="monday">Monday</option><option value="sunday">Sunday</option>
        </NativeSelect>
      </fieldset>
      <Button type="submit" disabled={!current?.saved || !dirty || saving} aria-busy={saving}>Save reporting week</Button>
      {current?.message && <p role="status">{current.message}</p>}
    </form>
  </section>
}
