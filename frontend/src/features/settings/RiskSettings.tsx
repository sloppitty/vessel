import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { Gauge } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import type { RiskSettings as Settings } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { riskLimitText } from './risk-limit'
import './risk-settings.css'

type RiskSettingsApi = Pick<WorkspaceApi, 'riskSettings' | 'updateRiskSettings'>

interface Loaded { api: RiskSettingsApi; attempt: number; saved: Settings | null; text: string; error: string | null; message: string | null }

/**
 * Soft risk limits (Phase 2, item 1): the open-risk limit as a percentage of the scope's balance. Blank until the owner
 * sets it; it is shown against open risk and never blocks anything. Saved on the server for this owner.
 */
export function RiskSettings({ api }: { api: RiskSettingsApi }) {
  const id = useId()
  const [attempt, setAttempt] = useState(0)
  const request = useMemo(() => ({ api, attempt }), [api, attempt])
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [saving, setSaving] = useState(false)
  const scope = useRef<AbortController | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    scope.current = controller
    request.api.riskSettings(controller.signal).then(settings => {
      if (!controller.signal.aborted) setLoaded({ ...request, saved: settings, text: settings.openRiskLimitPercent ?? '', error: null, message: null })
    }).catch(cause => {
      if (!controller.signal.aborted) setLoaded({ ...request, saved: null, text: '', error: errorMessage(cause, 'Risk limits could not be loaded.'), message: null })
    })
    return () => controller.abort()
  }, [request])

  const current = loaded?.api === request.api && loaded.attempt === request.attempt ? loaded : null
  const parsed = current ? riskLimitText(current.text) : undefined
  const valid = parsed !== undefined
  const dirty = !!current?.saved && valid && parsed !== current.saved.openRiskLimitPercent

  async function save() {
    const controller = scope.current
    if (!current?.saved || !controller || controller.signal.aborted || saving || !dirty) return
    setSaving(true)
    try {
      const settings = await api.updateRiskSettings({ openRiskLimitPercent: parsed ?? null })
      if (!controller.signal.aborted) setLoaded({ ...current, saved: settings, text: settings.openRiskLimitPercent ?? '', error: null,
        message: settings.openRiskLimitPercent === null ? 'Open-risk limit cleared.' : 'Open-risk limit saved.' })
    } catch (cause) {
      if (!controller.signal.aborted) setLoaded({ ...current, error: errorMessage(cause, 'The limit could not be saved. Reload to see the saved value.'), message: null })
    } finally {
      if (!controller.signal.aborted) setSaving(false)
    }
  }

  return <section className="shell-panel settings-panel risk-settings" aria-labelledby={`${id}-heading`}>
    <Gauge size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Risk limits</h2>
    <p>A soft limit is shown beside open risk and flagged when a Play would go over it. It never blocks planning or trading.</p>
    {!current && <p role="status">Loading risk limits…</p>}
    {current?.error && <div className="workspace-alert" role="alert"><span>{current.error}</span>
      <Button variant="ghost" size="sm" disabled={saving} onClick={() => setAttempt(value => value + 1)}>Reload risk limits</Button></div>}
    <form onSubmit={event => { event.preventDefault(); void save() }} noValidate>
      <fieldset disabled={!current?.saved || saving}>
        <legend className="sr-only">Soft risk limits</legend>
        <label htmlFor={`${id}-open`}>Open-risk soft limit (% of balance)</label>
        <Input id={`${id}-open`} inputMode="decimal" autoComplete="off" placeholder="Not set" value={current?.text ?? ''}
          aria-describedby={`${id}-open-help`} aria-invalid={current?.saved ? !valid : undefined}
          onChange={event => current && setLoaded({ ...current, text: event.target.value, message: null })} />
        <p id={`${id}-open-help`} className={valid ? 'muted' : 'risk-settings-error'}>{valid
          ? 'Above 0 and at most 100, with up to four decimals. Leave it blank for no limit.'
          : 'Enter a percentage above 0 and at most 100, with up to four decimals, or leave it blank.'}</p>
      </fieldset>
      <Button type="submit" disabled={!current?.saved || !dirty || saving} aria-busy={saving}>Save risk limits</Button>
      {current?.message && <p role="status">{current.message}</p>}
    </form>
  </section>
}
