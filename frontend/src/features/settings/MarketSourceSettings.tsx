import { useEffect, useId, useMemo, useState } from 'react'
import { Layers } from 'lucide-react'
import { errorMessage } from '@/api/errors'
import type { VenueMarketSources } from '@/api/market-sources'
import type { WorkspaceApi } from '@/api/workspace'
import { Button } from '@/components/ui/button'
import './market-source-settings.css'

type MarketSourceApi = Pick<WorkspaceApi, 'marketSources' | 'setMarketSource'>

/**
 * Which market sources the instrument selector offers, for venues with several. Turning one off only hides it from the
 * selector: Plays on its markets stay, and their imports, chart and monitoring carry on.
 */
export function MarketSourceSettings({ api }: { api: MarketSourceApi }) {
  const id = useId()
  const [attempt, setAttempt] = useState(0)
  const request = useMemo(() => ({ api, attempt }), [api, attempt])
  const [loaded, setLoaded] = useState<{ request: typeof request; venues: VenueMarketSources[] | null; error: string | null } | null>(null)
  const [saving, setSaving] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    request.api.marketSources(controller.signal).then(venues => {
      if (!controller.signal.aborted) setLoaded({ request, venues, error: null })
    }).catch(cause => {
      if (!controller.signal.aborted) setLoaded({ request, venues: null, error: errorMessage(cause, 'Market sources could not be loaded.') })
    })
    return () => controller.abort()
  }, [request])

  const current = loaded?.request === request ? loaded : null
  // No venue with several sources: nothing to choose.
  if (current?.venues?.length === 0) return null

  async function toggle(venueId: string, sourceId: string, enabled: boolean) {
    if (!current?.venues) return
    const key = `${venueId}/${sourceId}`
    setSaving(key)
    try {
      await api.setMarketSource(venueId, sourceId, enabled)
      setLoaded({ ...current, error: null, venues: current.venues.map(venue => venue.venueId !== venueId ? venue
        : { ...venue, sources: venue.sources.map(source => source.id === sourceId ? { ...source, enabled } : source) }) })
    } catch (cause) {
      setLoaded({ ...current, error: errorMessage(cause, 'The market source could not be saved.') })
    } finally {
      setSaving(null)
    }
  }

  return <section className="shell-panel settings-panel market-source-settings" aria-labelledby={`${id}-heading`}>
    <Layers size={23} aria-hidden="true" /><h2 id={`${id}-heading`}>Market sources</h2>
    <p>Third-party markets the instrument selector offers. Turning one off hides it from new Plays; saved Plays keep their market.</p>
    {!current && <p role="status">Loading market sources…</p>}
    {current?.error && <div className="workspace-alert" role="alert"><span>{current.error}</span>
      {!current.venues && <Button variant="ghost" size="sm" onClick={() => setAttempt(value => value + 1)}>Reload market sources</Button>}</div>}
    {current?.venues?.map(venue => <fieldset key={venue.venueId} disabled={saving !== null}>
      <legend>{venue.venueName}</legend>
      {!venue.available && <p className="muted">The venue&apos;s markets could not be read just now.</p>}
      {venue.available && venue.sources.length === 0 && <p className="muted">No extra sources listed.</p>}
      {venue.sources.map(source => <label key={source.id}>
        <input type="checkbox" checked={source.enabled} onChange={event => void toggle(venue.venueId, source.id, event.target.checked)} />
        <span>{source.name}</span><small>{source.markets} {source.markets === 1 ? 'market' : 'markets'}</small>
      </label>)}
    </fieldset>)}
  </section>
}
