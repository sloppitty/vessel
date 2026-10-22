import { useState } from 'react'
import { Clock } from 'lucide-react'
import { InfoTip } from '@/components/ui/info-tip'
import { displayZones, displayZoneStore, useDisplayZone } from '@/lib/display-zone'
import { formatTime, zoneName } from '@/lib/time'

/** Where every page shows times (phase 1, 1.6e): the browser's zone, the default, or UTC. Display only. */
export function TimeSettings() {
  const zone = useDisplayZone()
  const [now] = useState(() => new Date())
  return <section className="shell-panel settings-panel" aria-labelledby="time-settings-heading">
    <Clock size={23} aria-hidden="true" />
    <h2 id="time-settings-heading">Times<InfoTip label="About displayed times">Every page shows times in this zone and names
      it; a time&apos;s tooltip shows the other zone. Activity and Review&apos;s rolling periods still filter by UTC days, the
      week page follows the reporting week below, and stored times never change.</InfoTip></h2>
    <fieldset className="settings-choice">
      <legend>Show times in</legend>
      {displayZones.map(option => <label key={option}>
        <input type="radio" name="display-zone" value={option} checked={zone === option} onChange={() => displayZoneStore.set(option)} />
        {option === 'local' ? `Local (${zoneName()})` : 'UTC'}
      </label>)}
    </fieldset>
    <p>Now: {formatTime(now, zone)}</p>
  </section>
}
