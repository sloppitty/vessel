import { hasStructuredReview, type PlayStatus, type StructuredReview } from '@/api/plays'

/**
 * A review looks back at a trade: one that has filled (Open, Closed), ended without filling (Cancelled), or was declared
 * unresolved after filling.
 */
const reviewStatuses: readonly PlayStatus[] = ['open', 'closed', 'cancelled', 'unresolved']

/**
 * Whether the journal offers its Review tab. Draft and Planned have nothing to look back on yet. Paused
 * behaves like Planned: a play can only be paused before any fill, and a fill while paused opens it.
 * Retrospective plays exist to be reviewed. Review text or answers already written always keep the tab,
 * so no saved review is ever hidden.
 */
export function journalReviewVisible({ status, retrospective = false, review, structuredReview }: {
  status: PlayStatus
  retrospective?: boolean
  review: string
  structuredReview?: StructuredReview | undefined
}) {
  return retrospective || reviewStatuses.includes(status) || review.trim() !== '' || hasStructuredReview(structuredReview)
}
