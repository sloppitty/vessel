import { InfoTip } from '@/components/ui/info-tip'
import { hasLossLimit, rText, type LossLimitsLoad } from './loss-limits'

/**
 * One line in capital context (Phase 2, item 10b): today's and this week's loss in R against the soft limits, the Plays
 * without R counted apart, a reached limit marked. Nothing shows while no limit is set; a failed read says so.
 */
export function LossLimitLine({ load }: { load: LossLimitsLoad | null }) {
  if (!load) return null
  if ('failed' in load) return <p className="capital-open-risk" data-testid="loss-limit-line">
    <span className="capital-open-risk-label">Loss limits</span> unavailable</p>
  const loss = load.value
  if (!hasLossLimit(loss)) return null
  const part = (label: string, window: typeof loss.day) => window.limitR === null ? null
    : <span key={label} className={window.breached ? 'capital-open-risk-over' : undefined}>
      {`${label} ${rText(window.netR)} of ${rText(window.limitR)}${window.breached ? ' (reached)' : ''}`}
      {window.withoutR > 0 && ` · ${window.withoutR} without R`}</span>
  const parts = [part('today', loss.day), part('this week', loss.week)].filter(Boolean)
  return <p className="capital-open-risk" data-testid="loss-limit-line">
    <InfoTip className="capital-open-risk-label" trigger="Loss limits">{loss.notice} Days and weeks follow {loss.timeZone}, weeks starting
      on {loss.weekStart === 'sunday' ? 'Sunday' : 'Monday'}.</InfoTip>
    {parts.map((item, index) => <span key={index}>{index > 0 ? ' · ' : ''}{item}</span>)}
  </p>
}
