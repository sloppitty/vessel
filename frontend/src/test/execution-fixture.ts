import type { EntryProgress, PlayExecution } from '@/api/plays'

/**
 * The Play execution fields the API always sends (`PlayExecutionService.GetAsync`), at their empty values, for hand-built
 * fixtures that do not exercise them. `funding` is left out: the API omits it when null.
 */
export const executionDefaults = {
  earlierOrders: [], venuePosition: null, riskAtFirstFill: null, receipt: null,
  sharedFills: [], allocationPlays: [], sharedFillCount: 0, sharedFillsNextOffset: null,
} satisfies Partial<PlayExecution>

/** The totals' venue net and Play-local result before the Play is closed and settled. */
export const totalsDefaults = {
  venueNetUsd: null, venueNotAllocated: false,
  local: { netUsd: null, basis: 'executions', reason: 'Known once the Play is closed and its fills settle it.', signDiffers: false },
} satisfies Partial<PlayExecution['totals']>

/** An entry whose plan the server could not give. */
export const plannedUnknown = { plannedPrice: null, plannedQuantity: null, planRevision: null } satisfies Partial<EntryProgress>
