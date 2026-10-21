import { InfoTip } from '@/components/ui/info-tip'
import './post-hoc-badge.css'

/**
 * Marks a planned Play whose plan was written after its first execution (D-17), from stored times only. It changes nothing
 * in Review or sizing (decision 9): the Play may still answer "Followed" and counts like any other.
 */
export function PostHocBadge() {
  return <InfoTip className="badge play-post-hoc" trigger="Plan written after first execution">
    This Play was planned after its first execution, so its plan was not written beforehand. It may still answer Followed and
    is scored like any other Play; this mark is the only difference.
  </InfoTip>
}
