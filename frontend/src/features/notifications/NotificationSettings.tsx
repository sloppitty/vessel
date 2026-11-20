import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Bell, Save, Send } from 'lucide-react'
import type { WorkspaceApi } from '@/api/workspace'
import { ApiError, errorMessage } from '@/api/errors'
import { useApiRead } from '@/lib/use-api-read'
import {
  notificationPreferenceCategories, notificationSeverities, type NotificationPreferenceFields, type NotificationPreferences,
} from '@/api/notifications'
import { Button } from '@/components/ui/button'
import { NativeSelect } from '@/components/ui/native-select'
import { useNotifications } from '@/components/notifications/notifications'
import type { InAppNotifications } from './useInAppNotifications'
import { categoryLabels, severityLabels } from './notification-list'
import { workerStatus } from './unread'
import './notification-settings.css'
import { useDisplayZone } from '@/lib/display-zone'

const fieldsOf = (preferences: NotificationPreferences): NotificationPreferenceFields =>
  ({ enabled: preferences.enabled, minimumSeverity: preferences.minimumSeverity, categories: preferences.categories })

/** The form over one read: edits and saves belong to it, and a new read starts a new form. */
interface Form { read: NotificationPreferences; preferences: NotificationPreferences; draft: NotificationPreferenceFields }

const sameFields = (a: NotificationPreferenceFields, b: NotificationPreferenceFields) => a.enabled === b.enabled &&
  a.minimumSeverity === b.minimumSeverity && a.categories.length === b.categories.length && a.categories.every(item => b.categories.includes(item))

/**
 * In-app notification preferences: the channel switch, minimum severity and categories, saved with the revision they
 * were read at. Changes apply to events handled afterwards. The test request travels the whole delivery path.
 */
export function NotificationSettings({ api, inApp }: { api: WorkspaceApi; inApp: InAppNotifications }) {
  const { notify } = useNotifications()
  const zone = useDisplayZone()
  // A reread keeps the shown preferences until it answers.
  const read = useApiRead(api, null, (client, signal) => client.notificationPreferences(signal),
    { fallback: 'Notification preferences could not be loaded.', keepPrevious: 'always' })
  const [form, setForm] = useState<Form | null>(null)
  // Commands belong to the API that started them: a replaced API's answer never reaches the new one's section.
  const [savingFor, setSavingFor] = useState<WorkspaceApi | null>(null)
  const [conflict, setConflict] = useState<{ api: WorkspaceApi; message: string } | null>(null)
  const [testingFor, setTestingFor] = useState<WorkspaceApi | null>(null)
  const [cooldown, setCooldown] = useState<{ api: WorkspaceApi; until: number } | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const id = useId()
  const { subscribe } = inApp
  const liveApi = useRef(api)
  useEffect(() => { liveApi.current = api })
  const saving = savingFor === api
  const testing = testingFor === api

  // Saving anywhere resets the notification feed; reread (unless edits are pending) so a save carries the current revision.
  const quiet = useRef(true)
  const reload = read.reload
  useEffect(() => subscribe(change => { if (change.type === 'reset' && quiet.current) reload() }), [subscribe, reload])
  const cooldownUntil = cooldown?.api === api ? cooldown.until : null
  useEffect(() => {
    if (cooldownUntil === null) return
    const timer = setInterval(() => {
      const at = Date.now()
      setNow(at)
      if (at >= cooldownUntil) setCooldown(null)
    }, 1000)
    return () => clearInterval(timer)
  }, [cooldownUntil])

  const current = read.data === null ? null : form?.read === read.data ? form : { read: read.data, preferences: read.data, draft: fieldsOf(read.data) }
  const preferences = current?.preferences ?? null
  const draft = current?.draft ?? null
  const setDraft = (next: NotificationPreferenceFields) => { if (current) setForm({ ...current, draft: next }) }
  const dirty = preferences !== null && draft !== null && !sameFields(draft, preferences)
  const online = inApp.status.known ? inApp.status.workerOnline : preferences?.workerOnline ?? true
  const heartbeat = inApp.status.known ? inApp.status.heartbeatAtUtc : preferences?.heartbeatAtUtc ?? null
  useEffect(() => { quiet.current = !dirty && !saving })
  const wait = cooldownUntil === null ? 0 : Math.max(0, Math.ceil((cooldownUntil - now) / 1000))

  async function save(event: FormEvent) {
    event.preventDefault()
    if (!current) return
    setSavingFor(api); setConflict(null)
    try {
      const saved = await api.saveNotificationPreferences({ ...current.draft, expectedRevision: current.preferences.revision })
      if (liveApi.current !== api) return
      setForm({ read: current.read, preferences: saved, draft: fieldsOf(saved) })
      notify({ tone: 'success', message: 'Notification preferences saved.' })
    } catch (cause) {
      if (liveApi.current !== api) return
      if (cause instanceof ApiError && cause.status === 409) { setConflict({ api, message: cause.message }); reload() }
      else notify({ tone: 'error', message: errorMessage(cause, 'Notification preferences could not be saved.') })
    } finally {
      setSavingFor(current => current === api ? null : current)
    }
  }
  async function test() {
    setTestingFor(api)
    try {
      await api.requestTestNotification()
      if (liveApi.current === api) notify({ tone: 'success', message: 'Test notification requested. It arrives like any other notification.' })
    } catch (cause) {
      if (liveApi.current !== api) return
      if (cause instanceof ApiError && cause.status === 429) {
        const at = cause.retryAtUtc ? Date.parse(cause.retryAtUtc) : Date.now() + 10_000
        setNow(Date.now()); setCooldown({ api, until: at })
      }
      notify({ tone: cause instanceof ApiError && cause.status === 429 ? 'warning' : 'error',
        message: errorMessage(cause, 'The test notification could not be requested.') })
    } finally {
      setTestingFor(current => current === api ? null : current)
    }
  }
  const toggle = (category: typeof notificationPreferenceCategories[number], on: boolean) => draft && setDraft({
    ...draft, categories: on ? [...draft.categories.filter(item => item !== category), category] : draft.categories.filter(item => item !== category),
  })

  return <section className="shell-panel settings-panel notification-settings" aria-labelledby={`${id}-heading`}>
    <Bell size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Notifications</h2>
    {read.error ? <div className="workspace-alert" role="alert"><span>{read.error}</span><Button variant="ghost" size="sm" onClick={reload}>Retry</Button></div>
      : !preferences || !draft ? <p role="status">Loading notification preferences…</p>
      : <form onSubmit={event => { void save(event) }}>
        {conflict?.api === api && <div className="workspace-alert" role="alert"><span>{conflict.message}</span></div>}
        <fieldset className="notification-settings-fields" disabled={saving}>
          <legend className="sr-only">In-app notification preferences</legend>
          <label className="notification-settings-switch"><input type="checkbox" role="switch" checked={draft.enabled}
            onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />In-app notifications</label>
          <label className="notification-settings-severity">Minimum severity
            <NativeSelect density="compact" value={draft.minimumSeverity} disabled={!draft.enabled}
              onChange={event => setDraft({ ...draft, minimumSeverity: event.target.value as typeof draft.minimumSeverity })}>
              {notificationSeverities.map(item => <option key={item} value={item}>{severityLabels[item]}</option>)}
            </NativeSelect></label>
          <fieldset className="notification-settings-categories" disabled={!draft.enabled}>
            <legend>Categories</legend>
            {notificationPreferenceCategories.map(item => <label key={item}><input type="checkbox" checked={draft.categories.includes(item)}
              onChange={event => toggle(item, event.target.checked)} />{categoryLabels[item]}</label>)}
          </fieldset>
        </fieldset>
        <dl>
          <div><dt>Status</dt><dd role="status">{workerStatus(online, heartbeat, zone)}</dd></div>
          {preferences.subscriptionStatus === 'lagging' &&
            <div><dt>Gap</dt><dd>Some events aged out before they could become notifications.</dd></div>}
          {preferences.failedDeliveries > 0 &&
            <div><dt>Failed</dt><dd>{preferences.failedDeliveries === 1 ? '1 event' : `${preferences.failedDeliveries} events`} could not become notifications.</dd></div>}
        </dl>
        <div className="notification-settings-actions">
          <Button type="submit" disabled={!dirty || saving} aria-busy={saving}><Save size={15} />Save notifications</Button>
          <Button type="button" variant="outline" disabled={testing || wait > 0} aria-busy={testing} onClick={() => { void test() }}>
            <Send size={15} />{wait > 0 ? `Send test notification (wait ${wait} s)` : 'Send test notification'}</Button>
        </div>
      </form>}
  </section>
}
