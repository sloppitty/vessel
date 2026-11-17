import { useId } from 'react'

export type LiveIndicatorState = 'off' | 'connecting' | 'live' | 'reconnecting' | 'polling' | 'stale'

const labels: Record<LiveIndicatorState, string> = {
  off: 'Off', connecting: 'Live', live: 'Live', reconnecting: 'Reconnecting', polling: 'Polling', stale: 'Stale',
}
const descriptions: Record<LiveIndicatorState, string> = {
  off: 'Live updates are off. Refresh updates the chart manually.',
  connecting: 'Connecting to live updates.',
  live: 'Receiving live venue updates.',
  reconnecting: 'Live updates were interrupted. Reconnecting.',
  polling: 'The live stream is unavailable. Refreshing every 15 seconds.',
  stale: 'The venue has sent no update for over a minute.',
}

/** Toggle for automatic chart updates with a status dot. Pressed means the owner wants live updates. */
export function LiveIndicator({ state, onToggle }: { state: LiveIndicatorState; onToggle: (on: boolean) => void }) {
  const describedBy = useId()
  const on = state !== 'off'
  return <button type="button" className="chart-live-toggle" data-state={state} aria-label="Live updates" aria-pressed={on}
    aria-describedby={describedBy} title={`Live updates · ${descriptions[state]}`} onClick={() => onToggle(!on)}>
    <i className="chart-live-dot" aria-hidden="true" />
    <span aria-hidden="true">{labels[state]}</span>
    <span id={describedBy} hidden>{descriptions[state]}</span>
  </button>
}
