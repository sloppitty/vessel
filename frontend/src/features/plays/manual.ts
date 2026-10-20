import { isManualCorrectionBatch, isManualExecutionCorrection, isManualExecutionInput, newDiaryRequestId, type ManualCorrectionBatch, type ManualExecutionCorrection, type ManualExecutionInput, type ManualExecutionRecord } from '@/api/diary'

/** Kept with the shell's Plays session, including the identity of an uncertain write. */
export interface ManualExecutionDraft {
  executionId: string | null
  role: 'entry' | 'exit'
  entryId: string
  occurredAtUtc: string
  side: 'buy' | 'sell' | ''
  quantity: string
  price: string
  fee: string
  feeCurrency: string
  source: string
  reason: string
  /** Other existing executions corrected with this one, under its reason, all or nothing (phase 1, 1.3b). */
  together?: ManualExecutionDraft[]
  attempt?: ManualExecutionInput | ManualExecutionCorrection | ManualCorrectionBatch
}
export const emptyManualDraft = (): ManualExecutionDraft => ({
  executionId: null, role: 'entry', entryId: '', occurredAtUtc: '', side: '', quantity: '', price: '',
  fee: '', feeCurrency: '', source: '', reason: '',
})
export const hasManualDraft = (draft?: ManualExecutionDraft) => !!draft &&
  !!(draft.executionId || draft.entryId || draft.occurredAtUtc || draft.side || draft.quantity || draft.price || draft.fee || draft.feeCurrency || draft.source || draft.reason || draft.attempt ||
    draft.together?.length)
export function manualCorrectionDraft(record: ManualExecutionRecord): ManualExecutionDraft {
  return { ...emptyManualDraft(), ...record, executionId: record.id, entryId: record.entryId ?? '', fee: record.fee ?? '',
    feeCurrency: record.feeCurrency ?? '' }
}

/** The UTC time the owner typed, completed to a full UTC timestamp; unqualified input is UTC. */
const utc = (time: string) => !time || /(?:Z|\+00:00)$/.test(time) ? time : `${time.length === 16 ? `${time}:00` : time}Z`

/**
 * Several corrections with one reason, sent as one batch (phase 1, 1.3b). Each row keeps its role, entry and side; the
 * server checks only the final ledger as a whole. A frozen attempt is resent unchanged.
 */
export function manualBatchRequest(draft: ManualExecutionDraft, version: number) {
  const rows = [draft, ...draft.together ?? []]
  const batch: ManualCorrectionBatch = draft.attempt && 'corrections' in draft.attempt ? draft.attempt : {
    requestId: newDiaryRequestId(), expectedVersion: version, reason: draft.reason,
    corrections: rows.map(row => ({
      executionId: row.executionId ?? '', role: row.role, entryId: row.role === 'entry' ? row.entryId : null,
      occurredAtUtc: utc(row.occurredAtUtc), side: row.side as 'buy' | 'sell', quantity: row.quantity, price: row.price,
      fee: row.fee === '' ? null : row.fee, feeCurrency: row.feeCurrency === '' ? null : row.feeCurrency, source: row.source,
    })),
  }
  if (batch.corrections.some(row => Number.isFinite(Date.parse(row.occurredAtUtc)) && Date.parse(row.occurredAtUtc) > Date.now()))
    return { error: 'Execution time must be a nonfuture UTC timestamp.' } as const
  if (!isManualCorrectionBatch(batch)) return { error: 'Enter positive decimal quantity and price, a UTC time and a source for every execution, and one reason for the corrections. Leave both fee fields blank for unknown, or provide an amount and currency (including known zero).' } as const
  return { batch } as const
}

export function manualRequest(draft: ManualExecutionDraft, version: number, direction: 'long' | 'short', entryIds: string[]) {
  const openingSide = direction === 'long' ? 'buy' : 'sell'
  if (draft.role === 'entry' && !entryIds.includes(draft.entryId)) return { error: 'Choose an entry from the saved plan.' } as const
  if (draft.side !== (draft.role === 'entry' ? openingSide : openingSide === 'buy' ? 'sell' : 'buy')) {
    return { error: `This ${direction} play needs ${draft.role === 'entry' ? openingSide : openingSide === 'buy' ? 'sell' : 'buy'} for an ${draft.role}.` } as const
  }
  // Unqualified input is UTC; keep explicit UTC suffixes and fractional precision intact.
  const occurredAtUtc = utc(draft.occurredAtUtc)
  if (Number.isFinite(Date.parse(occurredAtUtc)) && Date.parse(occurredAtUtc) > Date.now()) return { error: 'Execution time must be a nonfuture UTC timestamp.' } as const
  const input: ManualExecutionInput | ManualExecutionCorrection = draft.attempt && !('corrections' in draft.attempt) ? draft.attempt : {
    requestId: newDiaryRequestId(), expectedVersion: version, role: draft.role, entryId: draft.role === 'entry' ? draft.entryId : null,
    occurredAtUtc, side: draft.side, quantity: draft.quantity, price: draft.price,
    fee: draft.fee === '' ? null : draft.fee, feeCurrency: draft.feeCurrency === '' ? null : draft.feeCurrency,
    source: draft.source, ...(draft.executionId ? { reason: draft.reason } : {}),
  }
  if (!isManualExecutionInput(input)) return { error: 'Enter positive decimal quantity and price, a UTC time and a source. Leave both fee fields blank for unknown, or provide an amount and currency (including known zero).' } as const
  if (draft.executionId && !isManualExecutionCorrection(input)) return { error: 'Give a reason for the correction.' } as const
  return { input } as const
}
