import type { UnresolvedEvidence } from '@/api/plays'

/*
 * The execution facts kept with an Unresolved declaration (phase 1, 1.3a), as plain lines for History and Review's
 * exclusion drill-down. They describe the Play when it was declared; nothing here infers a result or a sign.
 */

/** At most this many linked orders are listed by name; the rest are counted. */
export const listedEvidenceOrders = 10

const order = (o: UnresolvedEvidence['orders'][number]) => `${o.role} order ${o.orderId} (${o.status}, filled ${o.filled})`

export function evidenceLines(evidence: UnresolvedEvidence): string[] {
  const listed = evidence.orders.slice(0, listedEvidenceOrders)
  const more = evidence.orderCount - listed.length
  return [
    `When declared: entered ${evidence.entered}, exited ${evidence.exited}, entered minus exited ${evidence.open}.`,
    evidence.orderCount === 0 ? 'No linked orders.'
      : `Linked orders: ${listed.map(order).join('; ')}${more > 0 ? `; and ${more} more` : ''}.`,
    ...evidence.problems,
  ]
}
