import { useId, useState } from 'react'
import { Gauge } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import type { RiskSettings as Settings } from '@/api/risk'
import type { WorkspaceApi } from '@/api/workspace'
import { useApiRead } from '@/lib/use-api-read'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { riskLimitText } from './risk-limit'
import { lossLimitsChanged } from '@/features/plays/loss-limits'
import './risk-settings.css'

type RiskSettingsApi = Pick<WorkspaceApi, 'riskSettings' | 'updateRiskSettings'>

/** The three limits as typed: the open-risk percentage and the daily and weekly loss limits in R (Phase 2, items 1 and 10b). */
interface Texts { open: string; daily: string; weekly: string }
/** The form over one read: edits and saves belong to it, and a new read starts a new form. */
interface Form { read: Settings; saved: Settings; text: Texts; error: string | null; message: string | null }

const texts = (settings: Settings): Texts => ({
  open: settings.openRiskLimitPercent ?? '', daily: settings.dailyLossLimitR ?? '', weekly: settings.weeklyLossLimitR ?? '',
})
const formOf = (read: Settings): Form => ({ read, saved: read, text: texts(read), error: null, message: null })

/**
 * Soft risk limits: the open-risk limit as a percentage of the scope's balance (Phase 2, item 1), and daily and weekly loss
 * limits in R (item 10b), by the reporting week's days and weeks. Each is blank until the owner sets it; they are flagged
 * and never block anything. Saved on the server for this owner.
 */
export function RiskSettings({ api }: { api: RiskSettingsApi }) {
  const id = useId()
  const settings = useApiRead(api, null, (client, signal) => client.riskSettings(signal), { fallback: 'Risk limits could not be loaded.' })
  const [form, setForm] = useState<Form | null>(null)
  const [saving, setSaving] = useState(false)

  const current = settings.data === null ? null : form?.read === settings.data ? form : formOf(settings.data)
  const parsed = current ? { open: riskLimitText(current.text.open), daily: riskLimitText(current.text.daily), weekly: riskLimitText(current.text.weekly) } : null
  const valid = !!parsed && parsed.open !== undefined && parsed.daily !== undefined && parsed.weekly !== undefined
  const dirty = !!current?.saved && valid && (parsed.open !== current.saved.openRiskLimitPercent ||
    parsed.daily !== current.saved.dailyLossLimitR || parsed.weekly !== current.saved.weeklyLossLimitR)

  async function save() {
    if (!current || saving || !dirty || !parsed) return
    setSaving(true)
    try {
      const settings = await api.updateRiskSettings({ openRiskLimitPercent: parsed.open ?? null, dailyLossLimitR: parsed.daily ?? null,
        weeklyLossLimitR: parsed.weekly ?? null })
      lossLimitsChanged()
      // A form replaced by another read or API meanwhile ignores this answer.
      setForm({ ...current, saved: settings, text: texts(settings), error: null, message: 'Risk limits saved.' })
    } catch (cause) {
      setForm({ ...current, error: errorMessage(cause, 'The limits could not be saved. Reload to see the saved values.'), message: null })
    } finally {
      setSaving(false)
    }
  }

  const field = (key: keyof Texts, label: string, help: string, invalid: string) => {
    const ok = parsed?.[key] !== undefined
    return <>
      <label htmlFor={`${id}-${key}`}>{label}</label>
      <Input id={`${id}-${key}`} inputMode="decimal" autoComplete="off" placeholder="Not set" value={current?.text[key] ?? ''}
        aria-describedby={`${id}-${key}-help`} aria-invalid={current?.saved ? !ok : undefined}
        onChange={event => current && setForm({ ...current, text: { ...current.text, [key]: event.target.value }, message: null })} />
      <p id={`${id}-${key}-help`} className={ok ? 'muted' : 'risk-settings-error'}>{ok ? help : invalid}</p>
    </>
  }
  return <section className="shell-panel settings-panel risk-settings" aria-labelledby={`${id}-heading`}>
    <Gauge size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Risk limits</h2>
    <p>Soft limits are shown beside open risk and the day&apos;s and week&apos;s losses, and flagged when reached. They never block
      planning or trading.</p>
    {settings.loading && <p role="status">Loading risk limits…</p>}
    {(settings.error ?? current?.error) && <div className="workspace-alert" role="alert"><span>{settings.error ?? current?.error}</span>
      <Button variant="ghost" size="sm" disabled={saving} onClick={settings.reload}>Reload risk limits</Button></div>}
    <form onSubmit={event => { event.preventDefault(); void save() }} noValidate>
      <fieldset disabled={!current?.saved || saving}>
        <legend className="sr-only">Soft risk limits</legend>
        {field('open', 'Open-risk soft limit (% of balance)', 'Above 0 and at most 100, with up to four decimals. Leave it blank for no limit.',
          'Enter a percentage above 0 and at most 100, with up to four decimals, or leave it blank.')}
        {field('daily', 'Daily loss limit (R)', "The day's estimated R of closed real Plays, in the reporting week's time zone. Above 0 and at most 100.",
          'Enter an R above 0 and at most 100, with up to four decimals, or leave it blank.')}
        {field('weekly', 'Weekly loss limit (R)', "The week's, by the reporting week's first day. Above 0 and at most 100.",
          'Enter an R above 0 and at most 100, with up to four decimals, or leave it blank.')}
      </fieldset>
      <Button type="submit" disabled={!current?.saved || !dirty || saving} aria-busy={saving}>Save risk limits</Button>
      {current?.message && <p role="status">{current.message}</p>}
    </form>
  </section>
}
